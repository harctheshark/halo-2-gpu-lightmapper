// 1.0 payload packing: a forward-only reader for one LZMA stream in the classic ".lzma" (LZMA_Alone) layout:
//   1 byte properties (lc/lp/pb), 4 bytes dictionary size (LE), 8 bytes uncompressed size (LE, all ones = unknown,
//   the stream then ends with the end marker), then the range-coded data.
// Written after the algorithm description in the public-domain LZMA specification (Igor Pavlov, "LZMA specification",
// 7-Zip / LZMA SDK). No code was copied; .NET Framework 4.8 has no LZMA reader of its own.
//
// Why: the engine folder holds several programs that are almost the same file. Zip's deflate cannot see a repeat that
// is megabytes away; one LZMA stream over all of them can (31.4 MB of zip entries pack to about 4 MB).
// Every byte this class produces is md5-checked against the manifest by the extractor, so a decoding fault can never
// reach a bake: it fails the extraction with a named file.
using System;
using System.IO;

namespace H2GpuLightmapper.Core.Payload
{
    public sealed class LzmaAloneStream : Stream
    {
        const int NumBitModelTotalBits = 11;
        const int NumMoveBits = 5;
        const uint TopValue = 1u << 24;
        const ushort ProbInit = 1 << (NumBitModelTotalBits - 1);

        const int NumStates = 12;
        const int NumPosBitsMax = 4;
        const int NumLenToPosStates = 4;
        const int EndPosModelIndex = 14;
        const int NumFullDistances = 1 << (EndPosModelIndex >> 1);
        const int NumAlignBits = 4;
        const int MatchMinLen = 2;

        /// <summary>The largest dictionary this reader accepts (the builder uses far less).</summary>
        public const uint MaxDictSize = 256u << 20;

        readonly Stream _in;
        readonly bool _leaveOpen;
        readonly byte[] _inBuf = new byte[1 << 16];
        int _inPos, _inLen;

        // range decoder
        uint _range, _code;

        // properties
        readonly int _lc, _lp, _pb;
        readonly uint _dictSize;
        readonly bool _sizeKnown;
        readonly long _unpackSize;

        // window
        readonly byte[] _win;
        uint _winPos;
        bool _winFull;
        long _total;

        // model
        readonly ushort[] _lit;
        readonly ushort[] _isMatch = new ushort[NumStates << NumPosBitsMax];
        readonly ushort[] _isRep = new ushort[NumStates];
        readonly ushort[] _isRepG0 = new ushort[NumStates];
        readonly ushort[] _isRepG1 = new ushort[NumStates];
        readonly ushort[] _isRepG2 = new ushort[NumStates];
        readonly ushort[] _isRep0Long = new ushort[NumStates << NumPosBitsMax];
        readonly ushort[] _posSlot = new ushort[NumLenToPosStates << 6];
        readonly ushort[] _posDec = new ushort[1 + NumFullDistances - EndPosModelIndex];
        readonly ushort[] _align = new ushort[1 << NumAlignBits];
        readonly LenDecoder _len = new LenDecoder();
        readonly LenDecoder _repLen = new LenDecoder();

        uint _rep0, _rep1, _rep2, _rep3;
        int _state;
        int _pendingLen;      // bytes of the current match still to copy
        bool _finished;

        sealed class LenDecoder
        {
            public ushort Choice = ProbInit, Choice2 = ProbInit;
            public readonly ushort[] Low = new ushort[(1 << NumPosBitsMax) << 3];
            public readonly ushort[] Mid = new ushort[(1 << NumPosBitsMax) << 3];
            public readonly ushort[] High = new ushort[1 << 8];
            public LenDecoder() { Fill(Low); Fill(Mid); Fill(High); }
        }

        static void Fill(ushort[] p) { for (int i = 0; i < p.Length; i++) p[i] = ProbInit; }

        /// <summary>Reads the 13-byte header and primes the range decoder. <paramref name="input"/> is read forward only.</summary>
        public LzmaAloneStream(Stream input, bool leaveOpen = false)
        {
            if (input == null) throw new ArgumentNullException("input");
            _in = input;
            _leaveOpen = leaveOpen;

            var hdr = new byte[13];
            for (int i = 0; i < 13; i++) hdr[i] = NextByte();
            int d = hdr[0];
            if (d >= 9 * 5 * 5) throw new InvalidDataException("LZMA: bad properties byte " + d);
            _lc = d % 9; d /= 9;
            _lp = d % 5;
            _pb = d / 5;
            uint dict = (uint)(hdr[1] | (hdr[2] << 8) | (hdr[3] << 16)) | ((uint)hdr[4] << 24);
            if (dict < 4096) dict = 4096;
            if (dict > MaxDictSize) throw new InvalidDataException("LZMA: dictionary of " + dict + " bytes is over the limit");
            _dictSize = dict;
            ulong size = 0;
            for (int i = 0; i < 8; i++) size |= (ulong)hdr[5 + i] << (8 * i);
            _sizeKnown = size != ulong.MaxValue;
            if (_sizeKnown && size > long.MaxValue) throw new InvalidDataException("LZMA: bad size field");
            _unpackSize = _sizeKnown ? (long)size : -1;

            _win = new byte[_dictSize];
            _lit = new ushort[0x300 << (_lc + _lp)];
            Fill(_lit); Fill(_isMatch); Fill(_isRep); Fill(_isRepG0); Fill(_isRepG1); Fill(_isRepG2); Fill(_isRep0Long);
            Fill(_posSlot); Fill(_posDec); Fill(_align);

            // range decoder: one zero byte, then the first four code bytes
            if (NextByte() != 0) throw new InvalidDataException("LZMA: bad first data byte");
            _range = 0xFFFFFFFF;
            _code = 0;
            for (int i = 0; i < 4; i++) _code = (_code << 8) | NextByte();
            if (_code == _range) throw new InvalidDataException("LZMA: bad start of data");
        }

        /// <summary>Uncompressed bytes produced so far.</summary>
        public long TotalOut { get { return _total; } }
        /// <summary>The size the header declares, or -1 when the stream ends with an end marker.</summary>
        public long DeclaredSize { get { return _unpackSize; } }
        public uint DictionarySize { get { return _dictSize; } }

        byte NextByte()
        {
            if (_inPos == _inLen)
            {
                _inLen = _in.Read(_inBuf, 0, _inBuf.Length);
                _inPos = 0;
                if (_inLen <= 0) { _inLen = 0; throw new EndOfStreamException("LZMA: the compressed data ends early"); }
            }
            return _inBuf[_inPos++];
        }

        void Normalize()
        {
            if (_range < TopValue)
            {
                _range <<= 8;
                _code = (_code << 8) | NextByte();
            }
        }

        uint DecodeBit(ushort[] probs, int index)
        {
            uint v = probs[index];
            uint bound = (_range >> NumBitModelTotalBits) * v;
            uint bit;
            if (_code < bound)
            {
                v += ((1u << NumBitModelTotalBits) - v) >> NumMoveBits;
                _range = bound;
                bit = 0;
            }
            else
            {
                v -= v >> NumMoveBits;
                _code -= bound;
                _range -= bound;
                bit = 1;
            }
            probs[index] = (ushort)v;
            Normalize();
            return bit;
        }

        uint DecodeBit(ref ushort prob)
        {
            uint v = prob;
            uint bound = (_range >> NumBitModelTotalBits) * v;
            uint bit;
            if (_code < bound)
            {
                v += ((1u << NumBitModelTotalBits) - v) >> NumMoveBits;
                _range = bound;
                bit = 0;
            }
            else
            {
                v -= v >> NumMoveBits;
                _code -= bound;
                _range -= bound;
                bit = 1;
            }
            prob = (ushort)v;
            Normalize();
            return bit;
        }

        uint DecodeDirectBits(int numBits)
        {
            uint res = 0;
            for (; numBits > 0; numBits--)
            {
                _range >>= 1;
                _code -= _range;
                uint t = 0u - (_code >> 31);
                _code += _range & t;
                if (_code == _range) throw new InvalidDataException("LZMA: corrupt data (direct bits)");
                Normalize();
                res = (res << 1) + t + 1;
            }
            return res;
        }

        uint BitTree(ushort[] probs, int baseIndex, int numBits)
        {
            uint m = 1;
            for (int i = 0; i < numBits; i++) m = (m << 1) + DecodeBit(probs, baseIndex + (int)m);
            return m - (1u << numBits);
        }

        uint BitTreeReverse(ushort[] probs, int baseIndex, int numBits)
        {
            uint m = 1, sym = 0;
            for (int i = 0; i < numBits; i++)
            {
                uint bit = DecodeBit(probs, baseIndex + (int)m);
                m = (m << 1) + bit;
                sym |= bit << i;
            }
            return sym;
        }

        uint DecodeLen(LenDecoder ld, int posState)
        {
            if (DecodeBit(ref ld.Choice) == 0) return BitTree(ld.Low, posState << 3, 3);
            if (DecodeBit(ref ld.Choice2) == 0) return 8 + BitTree(ld.Mid, posState << 3, 3);
            return 16 + BitTree(ld.High, 0, 8);
        }

        uint DecodeDistance(uint len)
        {
            uint lenState = len;
            if (lenState > NumLenToPosStates - 1) lenState = NumLenToPosStates - 1;
            uint posSlot = BitTree(_posSlot, (int)lenState << 6, 6);
            if (posSlot < 4) return posSlot;
            int numDirectBits = (int)((posSlot >> 1) - 1);
            uint dist = (2 | (posSlot & 1)) << numDirectBits;
            if (posSlot < EndPosModelIndex)
                dist += BitTreeReverse(_posDec, (int)(dist - posSlot), numDirectBits);
            else
            {
                dist += DecodeDirectBits(numDirectBits - NumAlignBits) << NumAlignBits;
                dist += BitTreeReverse(_align, 0, NumAlignBits);
            }
            return dist;
        }

        byte WindowByte(uint dist)   // dist >= 1: the byte "dist" positions back
        {
            return _win[dist <= _winPos ? _winPos - dist : _dictSize - dist + _winPos];
        }

        void Put(byte b)
        {
            _win[_winPos++] = b;
            if (_winPos == _dictSize) { _winPos = 0; _winFull = true; }
            _total++;
        }

        bool DistanceOk(uint dist) { return dist <= _winPos || _winFull; }

        byte DecodeLiteral()
        {
            uint prevByte = (_winPos != 0 || _winFull) ? WindowByte(1) : 0u;
            uint symbol = 1;
            uint litState = (((uint)_total & ((1u << _lp) - 1)) << _lc) + (prevByte >> (8 - _lc));
            int p = (int)(0x300 * litState);
            if (_state >= 7)
            {
                uint matchByte = WindowByte(_rep0 + 1);
                do
                {
                    uint matchBit = (matchByte >> 7) & 1;
                    matchByte <<= 1;
                    uint bit = DecodeBit(_lit, p + (int)(((1 + matchBit) << 8) + symbol));
                    symbol = (symbol << 1) | bit;
                    if (matchBit != bit) break;
                }
                while (symbol < 0x100);
            }
            while (symbol < 0x100) symbol = (symbol << 1) | DecodeBit(_lit, p + (int)symbol);
            return (byte)(symbol - 0x100);
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            if (buffer == null) throw new ArgumentNullException("buffer");
            if (offset < 0 || count < 0 || offset + count > buffer.Length) throw new ArgumentOutOfRangeException();
            int produced = 0;
            while (produced < count)
            {
                if (_pendingLen > 0)
                {
                    uint dist = _rep0 + 1;
                    while (_pendingLen > 0 && produced < count)
                    {
                        byte b = WindowByte(dist);
                        Put(b);
                        buffer[offset + produced++] = b;
                        _pendingLen--;
                    }
                    continue;
                }
                if (_finished) break;
                if (_sizeKnown && _total == _unpackSize) { _finished = true; break; }

                int posState = (int)((uint)_total & ((1u << _pb) - 1));
                if (DecodeBit(_isMatch, (_state << NumPosBitsMax) + posState) == 0)
                {
                    byte b = DecodeLiteral();
                    Put(b);
                    buffer[offset + produced++] = b;
                    _state = _state < 4 ? 0 : (_state < 10 ? _state - 3 : _state - 6);
                    continue;
                }

                uint len;
                if (DecodeBit(_isRep, _state) != 0)
                {
                    if (_winPos == 0 && !_winFull) throw new InvalidDataException("LZMA: corrupt data (repeat with an empty window)");
                    if (DecodeBit(_isRepG0, _state) == 0)
                    {
                        if (DecodeBit(_isRep0Long, (_state << NumPosBitsMax) + posState) == 0)
                        {
                            _state = _state < 7 ? 9 : 11;
                            if (!DistanceOk(_rep0 + 1)) throw new InvalidDataException("LZMA: corrupt data (distance)");
                            byte b = WindowByte(_rep0 + 1);
                            Put(b);
                            buffer[offset + produced++] = b;
                            continue;
                        }
                    }
                    else
                    {
                        uint dist;
                        if (DecodeBit(_isRepG1, _state) == 0) dist = _rep1;
                        else
                        {
                            if (DecodeBit(_isRepG2, _state) == 0) dist = _rep2;
                            else { dist = _rep3; _rep3 = _rep2; }
                            _rep2 = _rep1;
                        }
                        _rep1 = _rep0;
                        _rep0 = dist;
                    }
                    len = DecodeLen(_repLen, posState);
                    _state = _state < 7 ? 8 : 11;
                }
                else
                {
                    _rep3 = _rep2; _rep2 = _rep1; _rep1 = _rep0;
                    len = DecodeLen(_len, posState);
                    _state = _state < 7 ? 7 : 10;
                    _rep0 = DecodeDistance(len);
                    if (_rep0 == 0xFFFFFFFF)
                    {
                        // end marker
                        if (_code != 0) throw new InvalidDataException("LZMA: corrupt data (end marker)");
                        if (_sizeKnown && _total != _unpackSize) throw new InvalidDataException("LZMA: the data ends before the declared size");
                        _finished = true;
                        break;
                    }
                    if (_rep0 >= _dictSize) throw new InvalidDataException("LZMA: corrupt data (distance over the dictionary)");
                }
                if (!DistanceOk(_rep0 + 1)) throw new InvalidDataException("LZMA: corrupt data (distance)");
                long total = (long)len + MatchMinLen;
                if (_sizeKnown && _total + total > _unpackSize) throw new InvalidDataException("LZMA: corrupt data (match runs past the declared size)");
                _pendingLen = (int)total;
            }
            return produced;
        }

        /// <summary>Reads exactly <paramref name="count"/> bytes or throws.</summary>
        public void ReadExact(byte[] buffer, int offset, int count)
        {
            while (count > 0)
            {
                int r = Read(buffer, offset, count);
                if (r <= 0) throw new EndOfStreamException("LZMA: the packed data is shorter than the manifest says");
                offset += r; count -= r;
            }
        }

        public override bool CanRead { get { return true; } }
        public override bool CanSeek { get { return false; } }
        public override bool CanWrite { get { return false; } }
        public override long Length { get { throw new NotSupportedException(); } }
        public override long Position { get { return _total; } set { throw new NotSupportedException(); } }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) { throw new NotSupportedException(); }
        public override void SetLength(long value) { throw new NotSupportedException(); }
        public override void Write(byte[] buffer, int offset, int count) { throw new NotSupportedException(); }

        protected override void Dispose(bool disposing)
        {
            if (disposing && !_leaveOpen) _in.Dispose();
            base.Dispose(disposing);
        }
    }
}
