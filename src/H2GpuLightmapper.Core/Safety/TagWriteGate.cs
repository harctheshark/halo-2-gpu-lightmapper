// Review fix (finding 2): the ONE gate every tag-writing (or runtime-rewriting) action OUTSIDE a bake passes -- "Restore
// previous lightmap" (TagRestore), "Undo last install" and the recovery restore (lmw_install restore), "Repair runtime",
// the alias cleanup. It refuses while a bake runs on this WORK (current_run.json names a live shell) and otherwise takes the
// shared WORK\.gpu_bake.lock for the duration (run_bake's stale rules; a live holder -- another window's bake, the PowerShell
// launcher, a research bake -- refuses it). A bake that starts meanwhile WAITS for the lock before it writes anything.
// Inside a bake (the run already holds the lock) the restore helpers are called directly, never through this gate.
using System;
using System.Globalization;
using System.IO;
using H2GpuLightmapper.Core.Run;
using H2GpuLightmapper.Core.Util;

namespace H2GpuLightmapper.Core.Safety
{
    public sealed class TagWriteLease : IDisposable
    {
        private GpuLock _lk;
        internal TagWriteLease(GpuLock lk) { _lk = lk; }
        public string LockContent { get { return _lk == null ? null : _lk.Content; } }
        /// <summary>Releases the GPU lock (only while it still names this lease).</summary>
        public void Dispose()
        {
            var lk = _lk;
            _lk = null;
            if (lk != null) { try { lk.Release(); } catch (IOException) { } catch (UnauthorizedAccessException) { } }
        }
    }

    public static class TagWriteGate
    {
        /// <summary>The reason <paramref name="what"/> may not run now, or null (read-only: takes nothing).</summary>
        public static string Blocked(string work, string what, IProcessProbe probe = null)
        {
            probe = probe ?? SystemProcessProbe.Instance;
            CurrentRun cur;
            var ak = CurrentRun.Inspect(work, out cur, probe);
            if (ak == AttachKind.LiveOtherShell || ak == AttachKind.LiveThisShell)
                return "a bake is running on this H2EK (run " + (cur == null ? "?" : cur.RunTag) + "): " + what + " waits until it has ended";
            var li = GpuLock.Inspect(GpuLock.PathFor(work));
            if (li.Exists && !GpuLock.IsStale(li, probe, DateTime.UtcNow))
                return "another bake holds the GPU lock (" + li.Content + "): " + what + " waits until it has ended";
            return null;
        }

        /// <summary>
        /// Refuses (<see cref="SafetyException"/>) while a bake runs on <paramref name="work"/> or a live holder has the GPU lock;
        /// otherwise holds the lock until the lease is disposed. <paramref name="what"/> names the action in the refusal.
        /// </summary>
        public static TagWriteLease Enter(string work, string what, string scenario = null, IProcessProbe probe = null)
        {
            if (string.IsNullOrEmpty(work)) throw new ArgumentException("work is required");
            probe = probe ?? SystemProcessProbe.Instance;
            string why = Blocked(work, what, probe);
            if (why != null) throw new SafetyException(why);
            Directory.CreateDirectory(work);
            string tag = "tagwrite-" + DateTime.Now.ToString("yyyyMMddHHmmss", CultureInfo.InvariantCulture);
            var lk = new GpuLock(GpuLock.PathFor(work), tag, (scenario ?? what).Replace('\r', ' ').Replace('\n', ' '), probe);
            LockInfo holder;
            if (!lk.TryAcquireNow(out holder))
                throw new SafetyException("another bake holds the GPU lock (" + (holder == null ? "?" : holder.Content) + "): " + what + " waits until it has ended");
            // a bake may have registered between the check and the lock: it has not written anything yet (it needs the lock),
            // but refuse anyway so the two never interleave
            CurrentRun cur;
            var ak = CurrentRun.Inspect(work, out cur, probe);
            if (ak == AttachKind.LiveOtherShell || ak == AttachKind.LiveThisShell)
            {
                lk.Release();
                throw new SafetyException("a bake started on this H2EK (run " + (cur == null ? "?" : cur.RunTag) + "): " + what + " waits until it has ended");
            }
            return new TagWriteLease(lk);
        }
    }
}
