// S2.4 the shared GPU lock WORK\.gpu_bake.lock (FINAL_DESIGN sections 2 and 5): the same file research bakes and the
// PowerShell launcher (launcher\run_bake.ps1) take. Rules copied from run_bake.ps1:
//   * acquire = create the file exclusively (it must not exist); any other create error is a hard error;
//   * stale = LastWriteTime older than 60 min AND no process named fullgpu / tool_fast is running -> remove it, retry;
//   * wait in 20 s steps, give up after LockWaitMinutes (240);
//   * release only OUR lock (the content names this run).
// Additions (compatible with run_bake's readers): our content carries the shell pid + start time, so a lock left by a DEAD
// standalone shell (whose job object already killed its bake tree) is stale at once; and the holder refreshes the file's
// mtime (Heartbeat) so the 60-min rule never fires on a live standalone bake during a long fullgpu-free phase.
using System;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using H2GpuLightmapper.Core.Util;

namespace H2GpuLightmapper.Core.Safety
{
    public enum LockAcquire { Acquired, TimedOut, Cancelled }

    public sealed class LockInfo
    {
        public bool Exists;
        public string Content;
        public DateTime LastWriteUtc;
        public bool IsStandalone;
        public string RunTag;
        public int Pid;
        public long StartTicks;
        public double AgeMinutes(DateTime utcNow) { return (utcNow - LastWriteUtc).TotalMinutes; }
    }

    public sealed class GpuLock
    {
        public const string FileName = ".gpu_bake.lock";
        public static readonly TimeSpan StaleAge = TimeSpan.FromMinutes(60);
        public static readonly string[] BusyProcesses = { "fullgpu", "tool_fast" };
        private static readonly Regex Ours = new Regex(@"^H2LM BAKE (?<tag>\S+) .* pid (?<pid>\d+) start (?<start>\d+)\s*$", RegexOptions.Compiled);

        private readonly IProcessProbe _probe;
        private readonly Func<DateTime> _utcNow;
        public string LockPath { get; private set; }
        public string RunTag { get; private set; }
        public string Scenario { get; private set; }
        public string Content { get; private set; }
        public bool Held { get; private set; }
        /// <summary>Messages about stale locks removed while acquiring (for bake.log).</summary>
        public event Action<string> Log;

        public static string PathFor(string work) { return Path.Combine(work, FileName); }

        public GpuLock(string lockPath, string runTag, string scenario, IProcessProbe probe = null, Func<DateTime> utcNow = null)
        {
            if (string.IsNullOrWhiteSpace(runTag) || runTag.IndexOfAny(new[] { ' ', '\r', '\n' }) >= 0) throw new ArgumentException("runTag must be one word");
            LockPath = lockPath; RunTag = runTag; Scenario = scenario ?? "";
            _probe = probe ?? SystemProcessProbe.Instance;
            _utcNow = utcNow ?? (() => DateTime.UtcNow);
            Content = string.Format(CultureInfo.InvariantCulture, "H2LM BAKE {0} {1} {2:yyyy-MM-ddTHH:mm:ss} pid {3} start {4}",
                RunTag, Scenario.Replace(' ', '_'), DateTime.Now, ProcessInfo.CurrentPid, ProcessInfo.CurrentStartUtcTicks);
        }

        /// <summary>One attempt: create the lock file exclusively and write our content through the same handle.</summary>
        public bool TryAcquireOnce()
        {
            if (Held) return true;
            try
            {
                using (var fs = new FileStream(LockPath, FileMode.CreateNew, FileAccess.Write, FileShare.Read))
                {
                    var b = new UTF8Encoding(false).GetBytes(Content + "\r\n");
                    fs.Write(b, 0, b.Length);
                    fs.Flush(true);
                }
                Held = true;
                return true;
            }
            catch (IOException)
            {
                // only "it already exists" means somebody holds the lock (run_bake's rule); anything else is an error
                if (!File.Exists(LockPath)) throw;
                return false;
            }
        }

        /// <summary>run_bake's loop: try; if held, remove a stale lock and retry; else report the holder and wait.</summary>
        public LockAcquire Acquire(TimeSpan maxWait, TimeSpan poll, CancellationToken ct, Action<string, TimeSpan> onWaiting = null)
        {
            DateTime t0 = _utcNow();
            while (true)
            {
                ct.ThrowIfCancellationRequested();
                if (TryAcquireOnce()) return LockAcquire.Acquired;
                var info = Inspect(LockPath);
                if (info.Exists && IsStale(info, _probe, _utcNow()))
                {
                    if (RemoveIfUnchanged(info)) { var h = Log; if (h != null) h(string.Format(CultureInfo.InvariantCulture, "removed a stale GPU lock ({0:0} min old, {1}): {2}", info.AgeMinutes(_utcNow()), info.IsStandalone ? "its shell is gone" : "nothing running", info.Content)); }
                    continue;
                }
                if (!info.Exists) continue;
                var waited = _utcNow() - t0;
                if (waited > maxWait) return LockAcquire.TimedOut;
                if (onWaiting != null) onWaiting(info.Content, waited);
                if (ct.WaitHandle.WaitOne(poll)) return LockAcquire.Cancelled;
            }
        }

        /// <summary>No waiting (the tag-write gate): take the lock now, removing a STALE one first (run_bake's rules); false
        /// with <paramref name="holder"/> when a live holder has it.</summary>
        public bool TryAcquireNow(out LockInfo holder)
        {
            holder = null;
            for (int i = 0; i < 3; i++)
            {
                if (TryAcquireOnce()) return true;
                var info = Inspect(LockPath);
                if (!info.Exists) continue;
                if (IsStale(info, _probe, _utcNow()))
                {
                    if (RemoveIfUnchanged(info)) { var h = Log; if (h != null) h("removed a stale GPU lock: " + info.Content); }
                    continue;
                }
                holder = info;
                return false;
            }
            holder = Inspect(LockPath);
            return false;
        }

        /// <summary>Removes the lock only when it is still exactly the file we judged (same content, same mtime).</summary>
        private bool RemoveIfUnchanged(LockInfo seen)
        {
            var now = Inspect(LockPath);
            if (!now.Exists || now.Content != seen.Content || now.LastWriteUtc != seen.LastWriteUtc) return false;
            try { File.Delete(LockPath); return true; } catch (IOException) { return false; } catch (UnauthorizedAccessException) { return false; }
        }

        /// <summary>True when the lock file names THIS run (our exact content).</summary>
        public bool IsOurs()
        {
            var i = Inspect(LockPath);
            return i.Exists && i.Content == Content;
        }

        /// <summary>Releases the lock only when the file still names this run. Returns false (and leaves the file) otherwise.</summary>
        public bool Release()
        {
            bool ours = IsOurs();
            Held = false;
            if (!ours) return false;
            try { File.Delete(LockPath); return true; } catch (IOException) { return false; }
        }

        /// <summary>Refreshes the lock file's mtime while it names this run.</summary>
        public bool Heartbeat()
        {
            if (!Held || !IsOurs()) return false;
            File.SetLastWriteTimeUtc(LockPath, DateTime.UtcNow);
            return true;
        }

        public static LockInfo Inspect(string lockPath)
        {
            var i = new LockInfo();
            try
            {
                if (!File.Exists(lockPath)) return i;
                i.LastWriteUtc = File.GetLastWriteTimeUtc(lockPath);
                i.Content = Json.ReadAllTextShared(lockPath).Trim();
                i.Exists = true;
                var m = Ours.Match(i.Content);
                if (m.Success)
                {
                    i.IsStandalone = true;
                    i.RunTag = m.Groups["tag"].Value;
                    i.Pid = int.Parse(m.Groups["pid"].Value, CultureInfo.InvariantCulture);
                    i.StartTicks = long.Parse(m.Groups["start"].Value, CultureInfo.InvariantCulture);
                }
            }
            catch (FileNotFoundException) { i.Exists = false; }
            catch (IOException) { i.Exists = File.Exists(lockPath); }
            return i;
        }

        /// <summary>run_bake's rule (older than 60 min and neither fullgpu nor tool_fast running), or a standalone lock whose
        /// shell process no longer exists.</summary>
        public static bool IsStale(LockInfo i, IProcessProbe probe, DateTime utcNow)
        {
            if (!i.Exists) return false;
            if (i.IsStandalone && i.Pid > 0 && !probe.IsAlive(i.Pid, i.StartTicks)) return true;
            return i.AgeMinutes(utcNow) > StaleAge.TotalMinutes && !probe.AnyRunning(BusyProcesses);
        }
    }
}
