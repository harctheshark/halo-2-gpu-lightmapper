// S2.4 lmw_install wrapper (FINAL_DESIGN section 5 "Install"): the ONLY way the shell writes live tags is
//   py INST\engine_v373\v043-tagwriter\lmw_install.py install --stage S --target-dir T --backup-dir RUN\tag_backup_before_writeback
//      --auto-backup-root WORK\_TAGBAK_auto --marker OUT\writeback_in_progress.txt
// and its 'restore' verb. lmw_install's contract: exit 0 installed + verified; 2 refused before any change (Sapien /
// Guerilla holds a file, stage invalid): offer Retry, the stage is kept; 1 failed AFTER a replace (the marker stays):
// offer the automatic restore. The shell adds the guards lm_tagwriter only has for the dev PC: the stage is never under
// ROOT\tags, the target is a folder inside ROOT\tags, nothing touches the denylisted farm_w149.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using H2GpuLightmapper.Core.Run;
using H2GpuLightmapper.Core.Util;
using H2Lm.Proc;

namespace H2GpuLightmapper.Core.Safety
{
    public enum InstallOutcome { Installed, Refused, FailedAfterChange, Error, Cancelled }

    public sealed class InstallRequest
    {
        public string PyExe, LmwInstallPy;
        public string Root, Work;
        public string Stage, TargetDir, BackupDir, AutoBackupRoot, Marker;
        public bool DryRun;
        public ChildEnvironment Env;
        public int TimeoutMs = 30 * 60 * 1000;
    }

    public sealed class TagRestoreRequest
    {
        public string PyExe, LmwInstallPy;
        public string Root, Work;
        public string BackupDir, TargetDir, Marker;
        public ChildEnvironment Env;
        public int TimeoutMs = 30 * 60 * 1000;
    }

    public sealed class InstallResult
    {
        public InstallOutcome Outcome;
        public int ExitCode = -1;
        public List<string> Lines = new List<string>();
        public string Message;
        /// <summary>exit 2: nothing changed; the stage is kept -- "close Sapien / Guerilla, then Retry".</summary>
        public bool OfferRetry { get { return Outcome == InstallOutcome.Refused; } }
        /// <summary>exit 1: the live set may be half-installed -- offer lmw_install restore from BackupDir.</summary>
        public bool OfferRestore { get { return Outcome == InstallOutcome.FailedAfterChange; } }
        public string BackupDir, TargetDir, Marker;
    }

    public static class TagInstaller
    {
        public static void Validate(InstallRequest r)
        {
            if (string.IsNullOrEmpty(r.PyExe) || !File.Exists(r.PyExe)) throw new SafetyException("the runtime py.exe is missing: " + r.PyExe);
            if (string.IsNullOrEmpty(r.LmwInstallPy) || !File.Exists(r.LmwInstallPy)) throw new SafetyException("lmw_install.py is missing: " + r.LmwInstallPy);
            if (string.IsNullOrEmpty(r.Root) || string.IsNullOrEmpty(r.Work)) throw new SafetyException("Root and Work are required");
            if (!Directory.Exists(r.Stage)) throw new SafetyException("the stage folder does not exist: " + r.Stage);
            Guards.AssertStageNotUnderTags(r.Stage, r.Root);
            Guards.AssertTargetUnderTags(r.TargetDir, r.Root);
            foreach (var p in new[] { r.Stage, r.BackupDir, r.Marker, r.AutoBackupRoot }.Where(x => !string.IsNullOrEmpty(x)))
                Guards.AssertNotDeniedFarm(p, r.Work);
            foreach (var p in new[] { r.BackupDir, r.Marker, r.AutoBackupRoot }.Where(x => !string.IsNullOrEmpty(x)))
                Guards.AssertStageNotUnderTags(p, r.Root);
            if (string.IsNullOrEmpty(r.BackupDir) || string.IsNullOrEmpty(r.Marker)) throw new SafetyException("BackupDir and Marker are required");
        }

        public static List<string> InstallArgs(InstallRequest r)
        {
            var a = new List<string> { r.LmwInstallPy, "install", "--stage", r.Stage, "--target-dir", r.TargetDir, "--backup-dir", r.BackupDir, "--marker", r.Marker };
            if (!string.IsNullOrEmpty(r.AutoBackupRoot)) { a.Add("--auto-backup-root"); a.Add(r.AutoBackupRoot); }
            if (r.DryRun) a.Add("--dry-run");
            return a;
        }

        /// <summary>Runs the install. With a BakeRun the process is logged to bake.log and marked as a TAG WRITE (a cancel
        /// waits for it); without one (recovery, CLI restore) it runs directly.</summary>
        public static InstallResult Install(InstallRequest r, BakeRun run = null)
        {
            Validate(r);
            var res = new InstallResult { BackupDir = r.BackupDir, TargetDir = r.TargetDir, Marker = r.Marker };
            int exit; bool cancelled;
            Exec(r.PyExe, InstallArgs(r), r.Work, r.Env, r.TimeoutMs, run, "lmw_install install", res.Lines, out exit, out cancelled);
            return Map(res, exit, cancelled, "install");
        }

        public static void Validate(TagRestoreRequest r)
        {
            if (string.IsNullOrEmpty(r.PyExe) || !File.Exists(r.PyExe)) throw new SafetyException("the runtime py.exe is missing: " + r.PyExe);
            if (string.IsNullOrEmpty(r.LmwInstallPy) || !File.Exists(r.LmwInstallPy)) throw new SafetyException("lmw_install.py is missing: " + r.LmwInstallPy);
            if (!Directory.Exists(r.BackupDir)) throw new SafetyException("the backup folder does not exist: " + r.BackupDir);
            Guards.AssertTargetUnderTags(r.TargetDir, r.Root);
            Guards.AssertStageNotUnderTags(r.BackupDir, r.Root);
            foreach (var p in new[] { r.BackupDir, r.Marker }.Where(x => !string.IsNullOrEmpty(x))) Guards.AssertNotDeniedFarm(p, r.Work);
        }

        /// <summary>lmw_install.py restore: md5-verified atomic per-file copy back; files the install CREATED are renamed
        /// aside (never deleted); the marker is renamed to .restored_&lt;ts&gt;.</summary>
        public static InstallResult Restore(TagRestoreRequest r, BakeRun run = null)
        {
            Validate(r);
            var a = new List<string> { r.LmwInstallPy, "restore", "--backup-dir", r.BackupDir, "--target-dir", r.TargetDir };
            if (!string.IsNullOrEmpty(r.Marker)) { a.Add("--marker"); a.Add(r.Marker); }
            var res = new InstallResult { BackupDir = r.BackupDir, TargetDir = r.TargetDir, Marker = r.Marker };
            int exit; bool cancelled;
            Exec(r.PyExe, a, r.Work, r.Env, r.TimeoutMs, run, "lmw_install restore", res.Lines, out exit, out cancelled);
            return Map(res, exit, cancelled, "restore");
        }

        private static void Exec(string py, List<string> args, string cwd, ChildEnvironment env, int timeoutMs, BakeRun run, string name,
            List<string> lines, out int exit, out bool cancelled)
        {
            if (run != null)
            {
                Action<ParsedLine> h = l => { if (l.Kind != LineKind.Shell) lock (lines) lines.Add(l.Text); };
                run.Line += h;
                try
                {
                    var oc = run.RunProcess(new ProcSpec { Name = name, FileName = py, Args = args, Cwd = cwd, Env = env, TagWrite = true, TimeoutMs = timeoutMs });
                    exit = oc.ExitCode; cancelled = oc.Cancelled;
                }
                finally { run.Line -= h; }
            }
            else
            {
                var r = ProcessRunner.RunCapture(py, args, cwd, env, timeoutMs, (l, err) => { lock (lines) lines.Add(l); });
                exit = r.TimedOut ? -1 : r.ExitCode; cancelled = false;
            }
        }

        private static InstallResult Map(InstallResult res, int exit, bool cancelled, string verb)
        {
            res.ExitCode = exit;
            string last = res.Lines.LastOrDefault(l => l.TrimStart().StartsWith("INSTALL:", StringComparison.Ordinal));
            if (cancelled) { res.Outcome = InstallOutcome.Cancelled; res.Message = verb + " was cancelled"; }
            else if (exit == 0) { res.Outcome = InstallOutcome.Installed; res.Message = last ?? verb + " ok"; }
            else if (exit == 2) { res.Outcome = InstallOutcome.Refused; res.Message = (last ?? verb + " refused") + " -- nothing was changed; the stage is kept (close Sapien / Guerilla, then Retry)"; }
            else if (exit == 1) { res.Outcome = InstallOutcome.FailedAfterChange; res.Message = (last ?? verb + " failed") + " -- the lightmap set may be half-written; restore it from " + res.BackupDir; }
            else { res.Outcome = InstallOutcome.Error; res.Message = verb + " ended with exit " + ProcessRunner.FormatExit(exit) + (last != null ? ": " + last : ""); }
            return res;
        }
    }
}
