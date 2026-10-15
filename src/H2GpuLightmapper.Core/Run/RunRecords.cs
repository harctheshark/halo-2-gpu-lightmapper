// S2.3 run records: RUN\status.json (state, phase, reason...) and WORK\standalone\current_run.json (which shell owns the
// running bake), so a second window can re-attach read-only (tail bake.log + poll status.json) and startup recovery can
// find a run whose shell died (power loss, the 8 h auto-shutdown, a crash). Both are written atomically (temp + replace).
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using H2GpuLightmapper.Core.Payload;
using H2GpuLightmapper.Core.Util;

namespace H2GpuLightmapper.Core.Run
{
    public enum RunState { Preparing, Waiting, Running, Done, Failed, Cancelled, Interrupted }

    /// <summary>RUN\status.json. Keys: schema, runTag, state (PREPARING|WAITING|RUNNING|DONE|FAILED|CANCELLED|INTERRUPTED), step,
    /// phase, phaseTitle, inTagWrite, cancelRequested, cancelDeferred, startedUtc, updatedUtc, endedUtc, exitCode, reason,
    /// waitingFor, shellPid, shellStartUtc, scenario, bsp, worker, backupDir, installBackupDir, marker, notes[], guardFails[],
    /// extra{} (free-form, for S3.x).</summary>
    public sealed class RunStatus
    {
        public string RunDir, RunTag, Scenario, Bsp;
        public int Worker;
        public RunState State = RunState.Preparing;
        public string Step, Phase, PhaseTitle, Reason, WaitingFor, BackupDir, InstallBackupDir, Marker;
        public bool InTagWrite, CancelRequested, CancelDeferred;
        public DateTime StartedUtc = DateTime.UtcNow, UpdatedUtc = DateTime.UtcNow;
        public DateTime? EndedUtc;
        public int? ExitCode;
        public int ShellPid = ProcessInfo.CurrentPid;
        public long ShellStartTicks = ProcessInfo.CurrentStartUtcTicks;
        public List<string> Notes = new List<string>();
        public List<string> GuardFails = new List<string>();
        public JObj Extra = new JObj();

        public string PathOf { get { return System.IO.Path.Combine(RunDir, "status.json"); } }
        public bool IsFinal { get { return State == RunState.Done || State == RunState.Failed || State == RunState.Cancelled || State == RunState.Interrupted; } }

        public JObj ToJObj()
        {
            return new JObj().Add("schema", 1).Add("runTag", RunTag).Add("state", State.ToString().ToUpperInvariant()).Add("step", Step)
                .Add("phase", Phase).Add("phaseTitle", PhaseTitle).Add("inTagWrite", InTagWrite).Add("cancelRequested", CancelRequested)
                .Add("cancelDeferred", CancelDeferred).Add("startedUtc", StartedUtc).Add("updatedUtc", UpdatedUtc).Add("endedUtc", EndedUtc)
                .Add("exitCode", ExitCode).Add("reason", Reason).Add("waitingFor", WaitingFor).Add("shellPid", ShellPid)
                .Add("shellStartUtc", ProcessInfo.FormatTicks(ShellStartTicks)).Add("scenario", Scenario).Add("bsp", Bsp).Add("worker", Worker)
                .Add("backupDir", BackupDir).Add("installBackupDir", InstallBackupDir).Add("marker", Marker)
                .Add("notes", Notes).Add("guardFails", GuardFails).Add("extra", Extra);
        }

        public void Save()
        {
            UpdatedUtc = DateTime.UtcNow;
            string text;
            lock (this) text = Json.Write(ToJObj(), true);
            SafeFs.WriteAllTextAtomic(PathOf, text);
        }

        public static RunStatus Load(string runDir)
        {
            var j = Json.ReadObjFile(System.IO.Path.Combine(runDir, "status.json"));
            if (j == null) return null;
            var s = new RunStatus { RunDir = runDir };
            s.RunTag = j.Str("runTag"); s.Scenario = j.Str("scenario"); s.Bsp = j.Str("bsp"); s.Worker = j.Int("worker") ?? 0;
            RunState st;
            if (Enum.TryParse(j.Str("state") ?? "", true, out st)) s.State = st;
            s.Step = j.Str("step"); s.Phase = j.Str("phase"); s.PhaseTitle = j.Str("phaseTitle"); s.Reason = j.Str("reason");
            s.WaitingFor = j.Str("waitingFor"); s.BackupDir = j.Str("backupDir"); s.InstallBackupDir = j.Str("installBackupDir"); s.Marker = j.Str("marker");
            s.InTagWrite = j.Bool("inTagWrite") == true; s.CancelRequested = j.Bool("cancelRequested") == true; s.CancelDeferred = j.Bool("cancelDeferred") == true;
            s.ExitCode = j.Int("exitCode"); s.ShellPid = j.Int("shellPid") ?? 0;
            s.ShellStartTicks = ParseTicks(j.Str("shellStartUtc"));
            DateTime d;
            if (DateTime.TryParse(j.Str("startedUtc"), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out d)) s.StartedUtc = d;
            if (DateTime.TryParse(j.Str("endedUtc"), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out d)) s.EndedUtc = d;
            if (j.Arr("notes") != null) foreach (var n in j.Arr("notes")) s.Notes.Add(Convert.ToString(n));
            if (j.Arr("guardFails") != null) foreach (var n in j.Arr("guardFails")) s.GuardFails.Add(Convert.ToString(n));
            if (j.Obj("extra") != null) s.Extra = j.Obj("extra");
            return s;
        }

        internal static long ParseTicks(string s)
        {
            DateTime d;
            if (s != null && DateTime.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out d)) return d.ToUniversalTime().Ticks;
            return 0;
        }
    }

    public enum AttachKind { None, LiveOtherShell, LiveThisShell, Stale }

    /// <summary>WORK\standalone\current_run.json: {runDir, runTag, shellPid, shellStartUtc, startedUtc}.</summary>
    public sealed class CurrentRun
    {
        public string RunDir, RunTag;
        public int ShellPid;
        public long ShellStartTicks;
        public DateTime StartedUtc;

        public static string PathFor(string work) { return System.IO.Path.Combine(ShellPaths.StandaloneOf(work), "current_run.json"); }

        public static void Write(string work, string runDir, string runTag)
        {
            var j = new JObj().Add("schema", 1).Add("runDir", runDir).Add("runTag", runTag).Add("shellPid", ProcessInfo.CurrentPid)
                              .Add("shellStartUtc", ProcessInfo.FormatTicks(ProcessInfo.CurrentStartUtcTicks)).Add("startedUtc", DateTime.UtcNow);
            SafeFs.WriteAllTextAtomic(PathFor(work), Json.Write(j, true));
        }

        public static CurrentRun Read(string work)
        {
            JObj j;
            try { j = Json.ReadObjFile(PathFor(work)); } catch (Exception) { return null; }
            if (j == null) return null;
            var c = new CurrentRun { RunDir = j.Str("runDir"), RunTag = j.Str("runTag"), ShellPid = j.Int("shellPid") ?? 0, ShellStartTicks = RunStatus.ParseTicks(j.Str("shellStartUtc")) };
            DateTime d;
            if (DateTime.TryParse(j.Str("startedUtc"), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out d)) c.StartedUtc = d;
            return c;
        }

        /// <summary>Removes current_run.json only when it still names <paramref name="runDir"/> (a single small file of ours).</summary>
        public static void Clear(string work, string runDir)
        {
            var c = Read(work);
            if (c != null && runDir != null && !string.Equals(SafeFs.Norm(c.RunDir), SafeFs.Norm(runDir), StringComparison.OrdinalIgnoreCase)) return;
            SafeFs.TryDeleteFile(PathFor(work));
        }

        /// <summary>Re-attach decision for a window that opens on this WORK.</summary>
        public static AttachKind Inspect(string work, out CurrentRun cur, IProcessProbe probe = null)
        {
            probe = probe ?? SystemProcessProbe.Instance;
            cur = Read(work);
            if (cur == null) return AttachKind.None;
            if (cur.ShellPid == ProcessInfo.CurrentPid && (cur.ShellStartTicks == 0 || cur.ShellStartTicks == ProcessInfo.CurrentStartUtcTicks)) return AttachKind.LiveThisShell;
            return probe.IsAlive(cur.ShellPid, cur.ShellStartTicks) ? AttachKind.LiveOtherShell : AttachKind.Stale;
        }
    }

    /// <summary>Reads a growing log from a remembered offset (the re-attach view), never locking the writer out.</summary>
    public sealed class LogTail
    {
        private readonly string _path;
        private long _pos;
        private readonly Decoder _dec = new UTF8Encoding(false).GetDecoder();
        private readonly StringBuilder _partial = new StringBuilder();
        public LogTail(string path, long startOffset = 0) { _path = path; _pos = startOffset; }
        public long Position { get { return _pos; } }

        /// <summary>Complete new lines since the last call.</summary>
        public List<string> ReadNewLines()
        {
            var lines = new List<string>();
            if (!File.Exists(_path)) return lines;
            using (var fs = new FileStream(_path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            {
                if (fs.Length < _pos) _pos = 0;
                fs.Seek(_pos, SeekOrigin.Begin);
                var buf = new byte[65536];
                int n;
                while ((n = fs.Read(buf, 0, buf.Length)) > 0)
                {
                    _pos += n;
                    var chars = new char[_dec.GetCharCount(buf, 0, n)];
                    _dec.GetChars(buf, 0, n, chars, 0);
                    foreach (char c in chars)
                    {
                        if (c == '\n') { lines.Add(_partial.ToString().TrimEnd('\r')); _partial.Clear(); }
                        else _partial.Append(c);
                    }
                }
            }
            return lines;
        }
    }

    /// <summary>WORK\standalone\runs\&lt;yyyyMMdd_HHmmss&gt;_&lt;leaf&gt;_&lt;bsp&gt;\ (FINAL_DESIGN section 2).</summary>
    public static class RunFolder
    {
        public static string RunsDir(string work) { return System.IO.Path.Combine(ShellPaths.StandaloneOf(work), "runs"); }

        public static string Create(string work, string scenarioRel, string bsp, DateTime now)
        {
            string leaf = ScenarioLeaf(scenarioRel);
            string tag = now.ToString("yyyyMMdd_HHmmss", CultureInfo.InvariantCulture) + "_" + Sanitize(leaf) + "_" + Sanitize(bsp ?? "");
            Directory.CreateDirectory(RunsDir(work));
            string dir = SafeFs.UniqueChild(RunsDir(work), tag);
            Directory.CreateDirectory(dir);
            return dir;
        }

        public static string ScenarioLeaf(string scenarioRel)
        {
            string s = (scenarioRel ?? "").Replace('/', '\\').TrimEnd('\\');
            if (s.EndsWith(".scenario", StringComparison.OrdinalIgnoreCase)) s = s.Substring(0, s.Length - 9);
            int i = s.LastIndexOf('\\');
            return i < 0 ? s : s.Substring(i + 1);
        }

        private static string Sanitize(string s)
        {
            var sb = new StringBuilder();
            foreach (char c in s) sb.Append(System.IO.Path.GetInvalidFileNameChars() is char[] bad && Array.IndexOf(bad, c) >= 0 ? '_' : c);
            return sb.ToString();
        }
    }
}
