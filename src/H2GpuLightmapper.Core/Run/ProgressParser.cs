using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using H2Lm.Proc;

namespace H2GpuLightmapper.Core.Run
{
    /// <summary>The UI step list (FINAL_DESIGN section 4.9), in order.</summary>
    public enum StepId { Preflight, Backup, Capture, Derive, GpuBake, TagWriter, AO, ProbeCapture, Probes, Install, Verify }

    public enum LineKind { Plain, Phase, Note, GuardFail, Error, Install, Shell }

    public sealed class ParsedLine
    {
        public string Text;
        public bool IsErr;
        public LineKind Kind;
        public string PhaseId, PhaseTitle;
        public DateTime Utc;
    }

    /// <summary>
    /// Reads gpu_bake.ps1's output as it streams: phase banners (`Phase n t` prints "[n] t" at column 0: 1, 2, 3, 4a, 4b,
    /// 4, 4c), `Note` lines ("      *** msg"), pipeline_guard FAIL lines ("GUARD: *** FAIL ..."), lmw_install lines
    /// ("INSTALL: ..."), and pwsh error blocks on stderr. Tracks whether a TAG WRITE is in progress ([4b] the direct
    /// install, [4] the tool write-back) so a cancel can be deferred until it ends.
    /// </summary>
    public sealed class ProgressParser
    {
        private static readonly Regex Banner = new Regex(@"^\[(\d[a-c]?)\]\s+(.*)$", RegexOptions.Compiled);
        private static readonly Regex ReasonLine = new Regex(@"^\|\s+\S", RegexOptions.Compiled);
        private static readonly Regex Squiggle = new Regex(@"^\|\s+~+$", RegexOptions.Compiled);
        private readonly Queue<string> _errTail = new Queue<string>();
        private readonly Queue<string> _outTail = new Queue<string>();

        public string PhaseId { get; private set; }
        public string PhaseTitle { get; private set; }
        public bool InTagWrite { get; private set; }
        public List<string> Notes { get; } = new List<string>();
        public List<string> GuardFails { get; } = new List<string>();
        public List<string> ErrorLines { get; } = new List<string>();
        public List<string> PhasesSeen { get; } = new List<string>();
        /// <summary>Fired when a banner starts a phase (old id, new id).</summary>
        public event Action<string, string> PhaseChanged;

        public static bool IsTagWritePhase(string id) { return id == "4b" || id == "4"; }

        /// <summary>gpu_bake phase -> UI step (a probe-worker capture maps phases 1-3 to ProbeCapture).</summary>
        public static StepId? StepFor(string phaseId, bool probeWorker = false)
        {
            switch (phaseId)
            {
                case "1": return probeWorker ? StepId.ProbeCapture : StepId.Capture;
                case "2": return probeWorker ? StepId.ProbeCapture : StepId.Derive;
                case "3": return probeWorker ? StepId.ProbeCapture : StepId.GpuBake;
                case "4a": return StepId.TagWriter;
                case "4b": case "4": return StepId.Install;
                case "4c": return StepId.Verify;
                default: return null;
            }
        }

        public ParsedLine Feed(string raw, bool isErr)
        {
            string text = LineDecoder.StripAnsi(raw ?? "");
            var p = new ParsedLine { Text = text, IsErr = isErr, Kind = LineKind.Plain, Utc = DateTime.UtcNow };
            string t = text.Trim();
            if (t.Length > 0)
            {
                var q = isErr ? _errTail : _outTail;
                q.Enqueue(t);
                while (q.Count > 40) q.Dequeue();
            }
            var m = isErr ? null : Banner.Match(text);
            if (m != null && m.Success)
            {
                string old = PhaseId;
                PhaseId = m.Groups[1].Value;
                PhaseTitle = m.Groups[2].Value.Trim();
                InTagWrite = IsTagWritePhase(PhaseId);
                PhasesSeen.Add(PhaseId);
                p.Kind = LineKind.Phase; p.PhaseId = PhaseId; p.PhaseTitle = PhaseTitle;
                var h = PhaseChanged;
                if (h != null) h(old, PhaseId);
                return p;
            }
            if (t.StartsWith("GUARD: *** FAIL", StringComparison.Ordinal)) { p.Kind = LineKind.GuardFail; GuardFails.Add(t); return p; }
            if (t.StartsWith("*** ", StringComparison.Ordinal)) { p.Kind = LineKind.Note; Notes.Add(t.Substring(4)); return p; }
            if (t.StartsWith("INSTALL:", StringComparison.Ordinal)) { p.Kind = LineKind.Install; return p; }
            if (isErr && t.Length > 0) { p.Kind = LineKind.Error; if (ErrorLines.Count < 200) ErrorLines.Add(t); }
            return p;
        }

        /// <summary>The process ended: no tag write can still be in progress.</summary>
        public void ProcessEnded() { InTagWrite = false; }

        /// <summary>
        /// run_bake.ps1's rule (S0.2): pwsh prints a thrown error as "Exception: file:line / Line | / n | code / | ~~~ / | message";
        /// the reason is the LAST trimmed stderr line matching ^\|\s+\S that is not a ^\|\s+~+$ underline, minus the "| ".
        /// Falls back to the last GUARD FAIL, then the last stderr line, then the last stdout lines.
        /// </summary>
        public string FailureReason()
        {
            var msg = _errTail.Where(l => ReasonLine.IsMatch(l) && !Squiggle.IsMatch(l)).Select(l => Regex.Replace(l, @"^\|\s+", "")).ToList();
            if (msg.Count > 0) return msg[msg.Count - 1];
            if (GuardFails.Count > 0) return GuardFails[GuardFails.Count - 1];
            if (_errTail.Count > 0) return _errTail.Last();
            return string.Join(" / ", _outTail.Skip(Math.Max(0, _outTail.Count - 3)));
        }
    }

    /// <summary>The last N log lines for the UI (FINAL_DESIGN: a colour-coded live log of the last 5k lines).</summary>
    public sealed class LogRing
    {
        private readonly ParsedLine[] _buf;
        private int _start, _count;
        private long _total;
        public LogRing(int capacity = 5000) { _buf = new ParsedLine[capacity]; }
        public long Total { get { lock (_buf) return _total; } }

        public void Add(ParsedLine l)
        {
            lock (_buf)
            {
                _total++;
                if (_count < _buf.Length) { _buf[(_start + _count) % _buf.Length] = l; _count++; }
                else { _buf[_start] = l; _start = (_start + 1) % _buf.Length; }
            }
        }

        public List<ParsedLine> Snapshot()
        {
            lock (_buf)
            {
                var r = new List<ParsedLine>(_count);
                for (int i = 0; i < _count; i++) r.Add(_buf[(_start + i) % _buf.Length]);
                return r;
            }
        }
    }
}
