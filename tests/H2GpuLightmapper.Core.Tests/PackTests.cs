// 1.0 payload packing: the shell's LZMA reader (LzmaAloneStream) and component zips that keep some files inside one
// LZMA "pack" entry. No GPU, no bake, no process: everything runs in the sandbox.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using H2GpuLightmapper.Core.Payload;
using H2GpuLightmapper.Core.Util;
using static H2GpuLightmapper.Core.Tests.Program;

namespace H2GpuLightmapper.Core.Tests
{
    /// <summary>A minimal LZMA writer for the tests: every byte is coded as a literal (no matches), lc=3 lp=0 pb=2, the
    /// size in the header, no end marker. Valid LZMA, no compression. Streams with matches come from the xz-made fixtures.</summary>
    internal static class TestLzma
    {
        public const string PackName = "__pack__.lzma";

        public static byte[] EncodeLiterals(byte[] data)
        {
            var ms = new MemoryStream();
            ms.WriteByte(93);                                   // (pb * 5 + lp) * 9 + lc
            ms.Write(BitConverter.GetBytes((uint)(1 << 16)), 0, 4);
            ms.Write(BitConverter.GetBytes((ulong)data.Length), 0, 8);

            ulong low = 0; uint range = 0xFFFFFFFF; byte cache = 0; long cacheSize = 1;
            Action shiftLow = () =>
            {
                if ((uint)low < 0xFF000000u || (uint)(low >> 32) != 0)
                {
                    byte temp = cache;
                    do { ms.WriteByte((byte)(temp + (byte)(low >> 32))); temp = 0xFF; } while (--cacheSize != 0);
                    cache = (byte)((uint)low >> 24);
                }
                cacheSize++;
                low = ((uint)low & 0x00FFFFFFu) << 8;
            };
            var isMatch = new ushort[12 << 4];
            var lit = new ushort[0x300 << 3];
            for (int i = 0; i < isMatch.Length; i++) isMatch[i] = 1024;
            for (int i = 0; i < lit.Length; i++) lit[i] = 1024;
            Action<ushort[], int, int> bit = (probs, idx, b) =>
            {
                uint p = probs[idx];
                uint bound = (range >> 11) * p;
                if (b == 0) { range = bound; p += (2048 - p) >> 5; }
                else { low += bound; range -= bound; p -= p >> 5; }
                probs[idx] = (ushort)p;
                while (range < (1u << 24)) { range <<= 8; shiftLow(); }
            };
            byte prev = 0;
            for (int pos = 0; pos < data.Length; pos++)
            {
                bit(isMatch, pos & 3, 0);                       // state 0 (a literal after a literal), posState = pos & 3
                int baseIdx = 0x300 * (prev >> 5);
                int symbol = 1;
                byte v = data[pos];
                for (int i = 7; i >= 0; i--)
                {
                    int b = (v >> i) & 1;
                    bit(lit, baseIdx + symbol, b);
                    symbol = (symbol << 1) | b;
                }
                prev = v;
            }
            for (int i = 0; i < 5; i++) shiftLow();
            return ms.ToArray();
        }
    }

    internal static class PackTests
    {
        public static IEnumerable<KeyValuePair<string, Action>> All()
        {
            yield return T("pack: LZMA reader decodes the xz-made reference vectors (matches, repeats, window wrap, lc/lp/pb)", Vectors);
            yield return T("pack: LZMA reader: sized header without end marker; damaged and cut streams never pass silently", SizedAndDamaged);
            yield return T("pack: packed files extract, verify, and read back straight from the payload", ExtractPacked);
            yield return T("pack: tampered packed file -> named by verify -> Repair restores it from the pack", TamperPacked);
            yield return T("pack: refusals: packed without a pack entry, zip entry for a packed file, pack longer than the manifest, wrong md5", Refusals);
            yield return T("pack: upgrade: a folder unpacked from the plain archive is accepted by the packed archive of the same files, and only then", UpgradeSameContent);
        }

        /// <summary>The builder's folder-name rule: "&lt;prefix&gt;-&lt;h8&gt;", h8 = first 8 hex of md5 over the sorted "&lt;path&gt; &lt;md5&gt;\n" lines.</summary>
        static string Hash8(CompSpec c)
        {
            var sb = new System.Text.StringBuilder();
            foreach (var f in c.Files.OrderBy(x => x.Path, StringComparer.Ordinal)) sb.Append(f.Path).Append(' ').Append(SafeFs.Md5Hex(f.Data)).Append('\n');
            return SafeFs.Md5Hex(new System.Text.UTF8Encoding(false).GetBytes(sb.ToString())).Substring(0, 8);
        }

        private static void UpgradeSameContent()
        {
            // the hash rule itself, against a value computed by the builder's Python formula
            var probe = new PayloadComponent { Files = new List<PayloadFile> {
                new PayloadFile { Path = "b/x.txt", Md5 = "0123456789abcdef0123456789abcdef" },
                new PayloadFile { Path = "a.exe", Md5 = "ffffffffffffffffffffffffffffffff" },
                new PayloadFile { Path = "B/y", Md5 = "00000000000000000000000000000000" } } };
            Assert.Eq("98ec621c", PayloadExtractor.ContentHash8(probe), "ContentHash8 == the builder's content hash");

            string parent = C.Sb.Dir("upgrade_rt");
            // "0.6.12": plain zip, folder named by the content hash
            var plain = TestPayload.Standard(C, "g");
            var pwA = plain.First(c => c.Name == "rt-tool-test");
            pwA.DirName = "tool-test-" + Hash8(pwA);
            var srcA = new DirectoryPayloadSource(TestPayload.Build(Path.Combine(C.Sb.Root, "payload_upA"), plain));
            var cA = PayloadManifest.Load(srcA).Components.First(c => c.Name == "rt-tool-test");
            Assert.True(!PayloadExtractor.EnsureExtracted(srcA, cA, parent).AlreadyPresent, "the plain archive unpacks");
            string dir = Path.Combine(parent, cA.DirName);

            // "1.0": the same files, packed: another zip md5, the same folder name
            var packed = TestPayload.Standard(C, "g");
            var pwB = packed.First(c => c.Name == "rt-tool-test");
            foreach (var f in pwB.Files) if (f.Path.EndsWith(".exe", StringComparison.Ordinal) || f.Path.EndsWith(".dll", StringComparison.Ordinal)) f.Packed = true;
            pwB.DirName = "tool-test-" + Hash8(pwB);
            var srcB = new DirectoryPayloadSource(TestPayload.Build(Path.Combine(C.Sb.Root, "payload_upB"), packed));
            var cB = PayloadManifest.Load(srcB).Components.First(c => c.Name == "rt-tool-test");
            Assert.Eq(cA.DirName, cB.DirName, "same files -> same folder name");
            Assert.True(cA.ZipMd5 != cB.ZipMd5, "the two archives differ");
            Assert.True(PayloadExtractor.EnsureExtracted(srcB, cB, parent).AlreadyPresent, "the packed archive accepts the folder the plain one unpacked");
            Assert.True(PayloadExtractor.QuickCheck(dir, cB).Ok, "quick check ok against the packed manifest");
            Assert.True(PayloadExtractor.FullVerify(dir, cB).Ok, "full md5 verify ok against the packed manifest");

            // a marker that counts other bytes is not this component
            string marker = Path.Combine(dir, PayloadExtractor.CompleteMarker);
            string saved = File.ReadAllText(marker);
            File.WriteAllText(marker, saved.Replace("\"totalBytes\": " + cA.TotalBytes, "\"totalBytes\": " + (cA.TotalBytes + 1)));
            Assert.True(!PayloadExtractor.IsMarkedComplete(dir, cB), "another byte total in the marker: not accepted");
            Assert.Throws<IOException>(() => PayloadExtractor.EnsureExtracted(srcB, cB, parent), "... and the folder is in the way, as before");
            File.WriteAllText(marker, saved);
            Assert.True(PayloadExtractor.IsMarkedComplete(dir, cB), "marker restored");

            // a folder name that does NOT carry the content hash gets no such trust (the old rule: the zip md5 must match)
            string parent2 = C.Sb.Dir("upgrade_rt2");
            var plain2 = TestPayload.Standard(C, "h");
            var srcC = new DirectoryPayloadSource(TestPayload.Build(Path.Combine(C.Sb.Root, "payload_upC"), plain2));
            var cC = PayloadManifest.Load(srcC).Components.First(c => c.Name == "rt-tool-test");
            PayloadExtractor.EnsureExtracted(srcC, cC, parent2);
            var packed2 = TestPayload.Standard(C, "h");
            foreach (var f in packed2.First(c => c.Name == "rt-tool-test").Files) if (f.Path.EndsWith(".exe", StringComparison.Ordinal)) f.Packed = true;
            var srcD = new DirectoryPayloadSource(TestPayload.Build(Path.Combine(C.Sb.Root, "payload_upD"), packed2));
            var cD = PayloadManifest.Load(srcD).Components.First(c => c.Name == "rt-tool-test");
            Assert.Eq(cC.DirName, cD.DirName, "the fixed test folder name");
            Assert.Throws<IOException>(() => PayloadExtractor.EnsureExtracted(srcD, cD, parent2), "no content hash in the folder name: refused as before");
        }

        static byte[] ReadAll(Stream s, int[] chunkSizes)
        {
            var ms = new MemoryStream();
            int k = 0;
            while (true)
            {
                var buf = new byte[chunkSizes[k++ % chunkSizes.Length]];
                int r = s.Read(buf, 0, buf.Length);
                if (r <= 0) break;
                ms.Write(buf, 0, r);
            }
            return ms.ToArray();
        }

        private static void Vectors()
        {
            string dir = C.Fixture("lzma");
            var lines = File.ReadAllLines(Path.Combine(dir, "vectors.txt")).Where(l => l.Trim().Length > 0).ToList();
            Assert.True(lines.Count >= 9, "the vector list is there (" + lines.Count + ")");
            foreach (var line in lines)
            {
                var parts = line.Split(new[] { ' ' }, 4);
                string name = parts[0]; long size = long.Parse(parts[1]); string md5 = parts[2];
                foreach (var chunks in new[] { new[] { 1 << 16 }, new[] { 1, 7, 4096, 3, 65536 }, new[] { 1 } })
                {
                    if (chunks.Length == 1 && chunks[0] == 1 && size > 60000) continue;   // byte-at-a-time only on the small ones
                    using (var fs = File.OpenRead(Path.Combine(dir, name + ".lzma")))
                    using (var lz = new LzmaAloneStream(fs))
                    {
                        byte[] got = ReadAll(lz, chunks);
                        Assert.Eq(size, got.LongLength, name + ": size");
                        Assert.Eq(md5, SafeFs.Md5Hex(got), name + ": md5");
                        Assert.Eq(0, lz.Read(new byte[8], 0, 8), name + ": stays at the end");
                        Assert.Eq(size, lz.TotalOut, name + ": TotalOut");
                    }
                }
            }
        }

        private static void SizedAndDamaged()
        {
            var rnd = new Random(77);
            var data = new byte[70000];
            rnd.NextBytes(data);
            for (int i = 0; i < 20000; i++) data[i] = (byte)(i % 7);
            foreach (var d in new[] { new byte[0], new byte[] { 0x41 }, data })
            {
                var packed = TestLzma.EncodeLiterals(d);
                using (var lz = new LzmaAloneStream(new MemoryStream(packed)))
                {
                    Assert.Eq((long)d.Length, lz.DeclaredSize, "the header carries the size");
                    var got = ReadAll(lz, new[] { 5000, 1, 33 });
                    Assert.True(got.SequenceEqual(d), "literal-only stream of " + d.Length + " bytes round-trips");
                }
            }

            // a cut stream: an exception, never a short "success"
            byte[] text = File.ReadAllBytes(Path.Combine(C.Fixture("lzma"), "longdist.lzma"));
            var cut = text.Take(text.Length / 2).ToArray();
            Assert.Throws<EndOfStreamException>(() => { using (var lz = new LzmaAloneStream(new MemoryStream(cut))) ReadAll(lz, new[] { 4096 }); }, "cut stream");

            // a damaged stream: either an exception or different bytes (the extractor's md5 check then refuses the file)
            string md5 = File.ReadAllLines(Path.Combine(C.Fixture("lzma"), "vectors.txt")).First(l => l.StartsWith("longdist ")).Split(' ')[2];
            int silent = 0;
            foreach (int at in new[] { 13, 40, 5000, 100000, text.Length - 3 })
            {
                var bad = (byte[])text.Clone();
                bad[at] ^= 0x40;
                try
                {
                    using (var lz = new LzmaAloneStream(new MemoryStream(bad)))
                        if (SafeFs.Md5Hex(ReadAll(lz, new[] { 4096 })) == md5) silent++;
                }
                catch (InvalidDataException) { }
                catch (EndOfStreamException) { }
            }
            Assert.Eq(0, silent, "a flipped bit never yields the original data");

            // bad headers
            Assert.Throws<InvalidDataException>(() => new LzmaAloneStream(new MemoryStream(new byte[] { 225, 0, 0, 1, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0 })), "properties byte out of range");
            var huge = (byte[])text.Clone();
            huge[1] = 0; huge[2] = 0; huge[3] = 0; huge[4] = 0x40;   // a 1 GiB dictionary
            Assert.Throws<InvalidDataException>(() => new LzmaAloneStream(new MemoryStream(huge)), "dictionary over the limit");
        }

        // ---------------------------------------------------------------- packed payloads

        static List<CompSpec> PackedSpec(string tag)
        {
            var comps = TestPayload.Standard(C, tag);
            var rnd = new Random(4321);
            var exe = new byte[300000];
            rnd.NextBytes(exe);
            var exe2 = (byte[])exe.Clone();
            for (int i = 0; i < exe2.Length; i += 5003) exe2[i] ^= 0x11;
            var inst = comps.First(c => c.Target == "inst");
            inst.Files.Add(new FileSpec("engine_v373/v041-fullgpu/fullgpu.exe", exe) { Packed = true });
            inst.Files.Add(new FileSpec("engine_custom/v041-fullgpu/fullgpu.exe", exe2) { Packed = true });
            inst.Files.Add(new FileSpec("engine_v373/empty.bin", new byte[0]) { Packed = true });
            foreach (var f in inst.Files) if (f.Path.EndsWith("gpulm_estimator.dll", StringComparison.Ordinal)) f.Packed = true;   // the slot DLLs too
            var pw = comps.First(c => c.Name == "rt-tool-test");
            foreach (var f in pw.Files) if (f.Path.EndsWith(".exe", StringComparison.Ordinal) || f.Path.EndsWith(".dll", StringComparison.Ordinal)) f.Packed = true;
            return comps;
        }

        private static string _dir;
        private static PayloadInstaller _inst;
        private static string _root;

        private static PayloadInstaller Inst
        {
            get
            {
                if (_inst != null) return _inst;
                _dir = TestPayload.Build(Path.Combine(C.Sb.Root, "payload_pack"), PackedSpec("k"));
                var paths = ShellPaths.ForSandbox(C.Sb.Dir("lad_pack"), C.Short.Dir("LP"), C.Short.Dir("VP"));
                _inst = new PayloadInstaller(new DirectoryPayloadSource(_dir), paths);
                _root = C.Sb.Dir("lr_pack");
                return _inst;
            }
        }

        private static void ExtractPacked()
        {
            var inst = Inst;
            var m = inst.Manifest;
            var ic = m.InstComponent;
            Assert.Eq(TestLzma.PackName, ic.PackEntry, "the INST component names its pack entry");
            Assert.True(ic.PackedFiles.Count() >= 5, "packed files listed (" + ic.PackedFiles.Count() + ")");
            using (var z = System.IO.Compression.ZipFile.OpenRead(Path.Combine(_dir, "engine-test.zip")))
            {
                Assert.True(z.GetEntry(TestLzma.PackName) != null, "the zip holds the pack entry");
                Assert.True(z.GetEntry("engine_v373/v041-fullgpu/fullgpu.exe") == null, "a packed file is not a zip entry");
                Assert.True(z.GetEntry("engine_v373/fake_gpu_bake.py") != null, "an unpacked file still is");
            }

            inst.EnsureRuntimes();
            var r = inst.EnsureInst(_root);
            foreach (var f in ic.Files)
            {
                string p = Path.Combine(r.InstDir, f.Path.Replace('/', '\\'));
                Assert.True(File.Exists(p), "extracted: " + f.Path);
                Assert.Eq(f.Size, new FileInfo(p).Length, "size of " + f.Path);
                if (!f.IsDllSlot) Assert.Eq(f.Md5, SafeFs.Md5File(p), "md5 of " + f.Path);
            }
            Assert.True(inst.QuickCheck(_root).Ok, "quick check ok");
            var v = inst.VerifyBeforeBake(_root);
            Assert.True(v.Ok, "full verify ok: " + v);

            // straight from the payload, nothing extracted: a packed file and a plain zip entry
            var src = new DirectoryPayloadSource(_dir);
            var pf = ic.Find("engine_custom/v041-fullgpu/fullgpu.exe");
            Assert.Eq(pf.Md5, SafeFs.Md5Hex(PayloadExtractor.ReadFileBytes(src, ic, pf)), "ReadFileBytes of a packed file");
            var zf = ic.Find("engine_v373/fake_gpu_bake.py");
            Assert.Eq(zf.Md5, SafeFs.Md5Hex(PayloadExtractor.ReadFileBytes(src, ic, zf)), "ReadFileBytes of a zip entry");
            var ef = ic.Find("engine_v373/empty.bin");
            Assert.Eq(0, PayloadExtractor.ReadFileBytes(src, ic, ef).Length, "an empty packed file");
            // the slot layout of a packed DLL can be worked out from the payload copy
            var dll = ic.Files.First(f => f.IsDllSlot);
            Assert.True(inst.LayoutFor(dll) != null, "slot layout of a packed DLL");
        }

        private static void TamperPacked()
        {
            var inst = Inst;
            inst.EnsureRuntimes();
            var r = inst.EnsureInst(_root);
            string p = Path.Combine(r.InstDir, @"engine_custom\v041-fullgpu\fullgpu.exe");
            var b = File.ReadAllBytes(p);
            b[123456] ^= 0x01;
            File.WriteAllBytes(p, b);
            PayloadExtractor.StampMtime(p);
            var v = inst.VerifyBeforeBake(_root);
            Assert.True(!v.Ok, "full verify catches the changed packed file");
            var ic = v.Components.First(x => x.Component.Target == "inst");
            Assert.Eq("engine_custom/v041-fullgpu/fullgpu.exe", ic.Result.BadFiles.Single(), "bad file named");
            var after = inst.Repair(_root, v);
            Assert.True(after.Ok, "repaired: " + after);
            Assert.True(inst.VerifyBeforeBake(_root).Ok, "verify ok after the repair");
            Assert.Eq(inst.Manifest.InstComponent.Find("engine_custom/v041-fullgpu/fullgpu.exe").Md5, SafeFs.Md5File(p), "the file is the payload's again");
            Assert.True(!Directory.GetFiles(Path.GetDirectoryName(p)).Any(x => x.Contains(".repair_")), "no repair temp left");
        }

        private static void Refusals()
        {
            string parent = C.Sb.Dir("refuse_pack");

            // a file flagged packed while the component names no pack entry: the manifest does not load
            string d1 = TestPayload.Build(Path.Combine(C.Sb.Root, "payload_pk1"), TestPayload.Standard(C, "q"), tweak: m =>
            {
                var f0 = (JObj)((JObj)m.Arr("components")[0]).Arr("files")[0];
                f0["packed"] = true;
            });
            Assert.Throws<InvalidDataException>(() => PayloadManifest.Load(new DirectoryPayloadSource(d1)), "packed without a pack entry");

            // the manifest says "packed" but the file is an ordinary zip entry (and there is no pack in the zip)
            string d2 = TestPayload.Build(Path.Combine(C.Sb.Root, "payload_pk2"), TestPayload.Standard(C, "r"), tweak: m =>
            {
                var c0 = (JObj)m.Arr("components")[0];
                ((JObj)c0.Arr("files")[0])["packed"] = true;
                c0["packEntry"] = TestLzma.PackName;
            });
            var src2 = new DirectoryPayloadSource(d2);
            var m2 = PayloadManifest.Load(src2);
            Assert.Throws<InvalidDataException>(() => PayloadExtractor.EnsureExtracted(src2, m2.Components[0], parent), "zip entry for a packed file");
            Assert.True(!Directory.Exists(Path.Combine(parent, m2.Components[0].DirName)), "no final dir after the refusal");

            // the pack holds one file more than the manifest lists
            string d3 = TestPayload.Build(Path.Combine(C.Sb.Root, "payload_pk3"), PackedSpec("s"), tweak: m =>
            {
                var ci = m.Arr("components").Cast<JObj>().First(c => (string)c["target"] == "inst");
                var files = ci.Arr("files");
                int last = -1;
                for (int i = 0; i < files.Count; i++) if (((JObj)files[i]).Has("packed")) last = i;
                files.RemoveAt(last);
                ci["fileCount"] = (long)files.Count;
            });
            var src3 = new DirectoryPayloadSource(d3);
            var m3 = PayloadManifest.Load(src3);
            Assert.Throws<InvalidDataException>(() => PayloadExtractor.EnsureExtracted(src3, m3.InstComponent, parent), "pack longer than the manifest");
            Assert.True(!Directory.Exists(Path.Combine(parent, m3.InstComponent.DirName)), "no final dir after the long pack");

            // a packed file whose manifest md5 is wrong
            string d4 = TestPayload.Build(Path.Combine(C.Sb.Root, "payload_pk4"), PackedSpec("t"), tweak: m =>
            {
                var ci = m.Arr("components").Cast<JObj>().First(c => (string)c["target"] == "inst");
                var pf = ci.Arr("files").Cast<JObj>().First(f => (string)f["path"] == "engine_v373/v041-fullgpu/fullgpu.exe");
                pf["md5"] = "0123456789abcdef0123456789abcdef";
            });
            var src4 = new DirectoryPayloadSource(d4);
            var m4 = PayloadManifest.Load(src4);
            Assert.Throws<InvalidDataException>(() => PayloadExtractor.EnsureExtracted(src4, m4.InstComponent, parent), "md5 mismatch of a packed file");
            Assert.True(!Directory.Exists(Path.Combine(parent, m4.InstComponent.DirName)), "no final dir after the md5 refusal");
            Assert.Throws<InvalidDataException>(() => PayloadExtractor.ReadFileBytes(src4, m4.InstComponent, m4.InstComponent.Find("engine_v373/v041-fullgpu/fullgpu.exe")), "ReadFileBytes checks the md5 too");

            // the builder's pack index: optional, but when present it must agree with the manifest
            var noIdx = PackedSpec("u");
            noIdx.First(c => c.Target == "inst").NoPackIndex = true;
            var src5 = new DirectoryPayloadSource(TestPayload.Build(Path.Combine(C.Sb.Root, "payload_pk5"), noIdx));
            var m5 = PayloadManifest.Load(src5);
            Assert.True(PayloadExtractor.EnsureExtracted(src5, m5.InstComponent, parent).Files == m5.InstComponent.FileCount, "a pack without an index extracts");
            var badIdx = PackedSpec("w");
            badIdx.First(c => c.Target == "inst").BadPackIndex = true;
            var src6 = new DirectoryPayloadSource(TestPayload.Build(Path.Combine(C.Sb.Root, "payload_pk6"), badIdx));
            var m6 = PayloadManifest.Load(src6);
            Assert.Throws<InvalidDataException>(() => PayloadExtractor.EnsureExtracted(src6, m6.InstComponent, parent), "a pack index that differs from the manifest");
        }
    }
}
