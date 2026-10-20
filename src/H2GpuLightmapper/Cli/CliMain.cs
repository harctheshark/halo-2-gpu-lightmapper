// S2.5 the same-exe CLI (FINAL_DESIGN section 4): "H2 GPU Lightmapper.exe --cli <verb> ...". It never creates a window: output
// goes to inherited pipes / a redirect file, or the parent console (AttachConsole), never AllocConsole (S0.1 ConsoleHost).
// Exit codes: 0 ok, 2 refused before any write (bad arguments too), 3 failed, 4 cancelled.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using H2GpuLightmapper.App;
using H2GpuLightmapper.Core.Discovery;
using H2GpuLightmapper.Core.Payload;
using H2GpuLightmapper.Core.Preflight;
using H2GpuLightmapper.Core.Run;
using H2GpuLightmapper.Core.Safety;
using H2GpuLightmapper.Core.Util;
using H2GpuLightmapper.Pipeline;
using H2Lm.Proc;
using Json = H2GpuLightmapper.Core.Util.Json;

namespace H2GpuLightmapper.Cli
{
    public static class CliMain
    {
        public const int ExitOk = 0, ExitRefused = 2, ExitFailed = 3, ExitCancelled = 4;

        private static readonly string[] HostValues = { "--payload-dir", "--localappdata", "--alias-localappdata", "--alias-volume-root", "--rt-pwsh", "--rt-py" };
        // --dev-custom: accepted and IGNORED since 0.6.1 (Custom needs no switch; old scripts keep working)
        private static readonly string[] HostSwitches = { "--dev-custom", "--dev-custom-unguarded", "--dev-probes" };

        public const string Usage =
@"H2 GPU Lightmapper -- command line (the same exe as the window; no window opens)

  ""H2 GPU Lightmapper.exe"" --cli bake --h2ek DIR --scenario S --bsp B
        [--quality low|super|custom] [--photons N --rays M] [--mode standard|probes|lightmap]
        [--ao [--ao-strength X] [--ao-exponent Y]] [--reuse-capture] [--no-install] [--yes]
        [--ao-fallback stop|install] [--probes-fallback stop|install] [--install-retries N [--install-retry-wait-s S]]
        [--responsive] [--exact-vertex-lighting]
        [--dry-run [--json] [--probe-points N] [--assume-vram-mb N [--assume-other-vram-mb N]]]
  ""H2 GPU Lightmapper.exe"" --cli restore --h2ek DIR --scenario S --bsp B [--list | --from BACKUP | --undo-last-install] [--dry-run]
  ""H2 GPU Lightmapper.exe"" --cli restore --h2ek DIR --recover
  ""H2 GPU Lightmapper.exe"" --cli cleanup-alias --h2ek DIR   (removes the H2GLM\<k6> short-path link to that H2EK's work folder)
  ""H2 GPU Lightmapper.exe"" --cli doctor [--h2ek DIR] [--json] [--repair] [--no-gpu] [--no-extract]
  ""H2 GPU Lightmapper.exe"" --cli scan-scenery-fix --h2ek DIR [--scenario S]   (Scan and Build Scenery Fix Dependencies: adds the scenery
        models the database SceneryFixDependencies.db in the H2EK folder does not know yet; every bake also does this for its own scenario)
  ""H2 GPU Lightmapper.exe"" --cli cancel --h2ek DIR          (cancels the bake running on that H2EK, in any window)
  ""H2 GPU Lightmapper.exe"" --cli status --h2ek DIR

  Defaults (reset every launch): --quality super, --mode standard, AO off (strength 1.0, exponent 0.5), a new capture, install.
  AO and light probes need the direct tag writer's stage; when a map falls back to the tool writer the bake stops (exit 3),
  or with --ao-fallback install (AO) / --probes-fallback install (light probes: they are RESET to zero, like tool lightmaps)
  the lightmap is installed WITHOUT them (the production write-back on the same capture); each dropped layer needs its flag.
  Light probes (Standard on a probe BSP, --mode probes) need a build whose payload passed gate T4 for its engine (MANIFEST
  features.probes; dev: --dev-probes); without it, on a probe BSP use --mode lightmap, which resets that BSP's probes to zero
  like tool lightmaps.
  Restore picks the newest backup that differs from the live set; --list shows them all, --from NAME picks one.
  --reuse-capture with changed tags since the capture needs --yes (the changed tags are listed).
  --install-retries N (0-20, default 0): when the shell's install finds the lightmap files in use (Sapien / Guerilla holds one;
  lmw_install exit 2, nothing changed), wait S seconds (--install-retry-wait-s, 1-3600, default 30) and retry, up to N times --
  the window asks ""Retry?"" instead. With 0 the run fails and the stage is kept. Every mode installs through the shell: Lightmap
  Only / Standard bake stage-only and the shell runs the lmw_install command gpu_bake's [4b] would run. Only the tool write-back
  fallback (a map whose direct tag writer makes no stage: gpu_bake -WriteBack -SkipCapture on the same capture) installs inside
  gpu_bake, without a retry.
  A bake that installs is refused (exit 2) while a lightmap file it replaces is open in another program (Sapien / Guerilla).
  --no-install (stage only) fails (exit 3) unless the direct tag writer wrote a stage in this run (tagw_report.json 'written').
  --recover: a tool write-back marker whose lightmap set is proven unchanged (md5 = the copy saved before the write-back) is set
  aside as .verified_<time> and nothing is restored; otherwise the newest differing backup is restored.
  Custom (EXPERIMENTAL): photons 1,000,000-100,000,000 (default 50,000,000), rays (MC samples) 1-64 (default 16). It runs the N3
  guarded fullgpu (engine_custom) and needs no switch; it is validated only at Super's own values (20,000,000 / 8). Other values are
  untested and can bake for hours (100,000,000 photons / 64 rays took 1.4 hours on cyclotron on an RTX 4090) and need much more VRAM.
  A payload without engine_custom refuses Custom (exit 2) unless --dev-custom-unguarded.
  --responsive = the window's Advanced settings ""Keep PC responsive - Slower Bakes!"": every gpu_bake of the bake gets -Responsive (the GPU work
  is split into short pieces so the desktop, videos and games stay smooth; the lightmap is identical, the bake takes longer). Off = the
  argv is exactly what it was before the option existed. The AO trace and the light probe bake run through driver scripts; they get
  GPULM_RESPONSIVE=1 in their environment.
  --exact-vertex-lighting = the window's Advanced settings ""2004 Exact Vertex Lighting?"": every gpu_bake of the bake gets -ExactVertexLighting
  (the tag writer re-runs the CPU's 7-probe per-vertex visibility gate, so the vertex-lit records match the 2004 tool more closely; the tag writer takes
  about 2 minutes longer on cyclotron; the lightmap bitmaps are unchanged). Off = the argv is exactly what it was before the option existed.
  --assume-other-vram-mb N (dry run, with --assume-vram-mb): video memory other programs are assumed to hold. A real run measures it with
  gpucheck and sizes the parts (-MaxCallsPerPart) for what is left when it is more than a normal desktop's 3 GiB.
  --dry-run prints the exact child argv / environment / working folder of every step and starts nothing, writes nothing.
  Exit codes: 0 ok, 2 refused before any write, 3 failed, 4 cancelled.

  dev: --worker N --probe-worker M (200-299) [--dev-custom-unguarded] --dev-probes --dev-no-fixes
       --dev-skip-preflight (tool_fast's identity is still checked) --dev-skip-gpucheck
       --reuse-unrecorded --lock-wait-min N --lock-poll-s N --cancel-file F
       --payload-dir DIR --localappdata DIR --alias-localappdata DIR --alias-volume-root DIR --rt-pwsh DIR --rt-py DIR";

        public static int Run(string[] args)
        {
            string mode = Ui.ConsoleHost.Attach();
            try { return Dispatch(args); }
            catch (Exception e)
            {
                Console.Error.WriteLine("H2 GPU Lightmapper: internal error: " + e);
                return ExitFailed;
            }
            finally { Console.Out.Flush(); Console.Error.Flush(); GC.KeepAlive(mode); }
        }

        private static int Dispatch(string[] args)
        {
            int start = Array.IndexOf(args, "--cli") + 1;
            string verb = start < args.Length ? args[start] : null;
            switch (verb)
            {
                case "bake": return Bake(args, start);
                case "restore": return Restore(args, start);
                case "doctor": return Doctor(args, start);
                case "scan-scenery-fix": return ScanSceneryFix(args, start);
                case "cancel": return Cancel(args, start);
                case "status": return Status(args, start);
                case "cleanup-alias": return CleanupAlias(args, start);
                case null: case "help": case "--help": case "-h": case "/?":
                    Console.WriteLine(Usage);
                    return verb == null ? ExitRefused : ExitOk;
                default:
                    Console.Error.WriteLine("unknown verb '" + verb + "'");
                    Console.WriteLine(Usage);
                    return ExitRefused;
            }
        }

        private static CliArgs ParseFor(string[] args, int start, IEnumerable<string> values, IEnumerable<string> switches)
        {
            return CliArgs.Parse(args, start, new HashSet<string>(values.Concat(HostValues)), new HashSet<string>(switches.Concat(HostSwitches)));
        }

        public static HostConfig HostConfigFrom(CliArgs a)
        {
            return new HostConfig
            {
                PayloadDir = a.Get("--payload-dir"), LocalAppData = a.Get("--localappdata"), AliasLocalAppData = a.Get("--alias-localappdata"),
                AliasVolumeRoot = a.Get("--alias-volume-root"), RtPwshDir = a.Get("--rt-pwsh"), RtPyDir = a.Get("--rt-py"),
                DevCustomUnguarded = a.Has("--dev-custom-unguarded"), DevProbes = a.Has("--dev-probes"),
            };
        }

        private static int ArgErrors(CliArgs a)
        {
            if (a.Errors.Count == 0) return -1;
            foreach (var e in a.Errors) Console.Error.WriteLine("argument error: " + e);
            Console.Error.WriteLine("(--cli help for the usage)");
            return ExitRefused;
        }

        // ------------------------------------------------------------------ bake

        public static BakeOptions BakeOptionsFrom(CliArgs a)
        {
            var o = BakeOptions.Defaults();
            o.Root = a.Get("--h2ek");
            o.ScenarioRel = a.Get("--scenario");
            o.Bsp = a.Get("--bsp");
            string q = a.Choice("--quality", "super", "low", "super", "custom");
            o.Quality = q == "low" ? Quality.Low : q == "custom" ? Quality.Custom : Quality.Super;
            var p = a.GetLong("--photons");
            var r = a.GetInt("--rays");
            if ((p.HasValue || r.HasValue) && o.Quality != Quality.Custom) a.Errors.Add("--photons / --rays are only valid with --quality custom");
            if (p.HasValue) o.Photons = p.Value;
            if (r.HasValue) o.Rays = r.Value;
            string m = a.Choice("--mode", "standard", "standard", "probes", "lightmap");
            o.Mode = m == "probes" ? BakeMode.Probes : m == "lightmap" ? BakeMode.Lightmap : BakeMode.Standard;
            o.Ao = a.Has("--ao");
            var s = a.GetDouble("--ao-strength");
            var x = a.GetDouble("--ao-exponent");
            if ((s.HasValue || x.HasValue) && !o.Ao) a.Errors.Add("--ao-strength / --ao-exponent need --ao");
            if (s.HasValue) o.AoStrength = s.Value;
            if (x.HasValue) o.AoExponent = x.Value;
            o.ReuseCapture = a.Has("--reuse-capture");
            o.StageOnly = a.Has("--no-install");
            o.AcceptChangedTags = a.Has("--yes");
            o.AoFallback = a.Choice("--ao-fallback", "stop", "stop", "install") == "install" ? AoFallback.InstallWithoutAo : AoFallback.Stop;
            o.ProbesFallback = a.Choice("--probes-fallback", "stop", "stop", "install") == "install" ? AoFallback.InstallWithoutAo : AoFallback.Stop;
            o.Worker = a.GetInt("--worker");
            o.ProbeWorker = a.GetInt("--probe-worker");
            o.Fixes = !a.Has("--dev-no-fixes");
            o.Responsive = a.Has("--responsive");
            o.ExactVertexLighting = a.Has("--exact-vertex-lighting");
            o.ReuseUnrecorded = a.Has("--reuse-unrecorded");
            if (o.ReuseUnrecorded && !o.ReuseCapture) a.Errors.Add("--reuse-unrecorded needs --reuse-capture");
            if (o.ReuseUnrecorded && !o.Worker.HasValue && o.Mode != BakeMode.Probes) a.Errors.Add("--reuse-unrecorded needs --worker N (the farm whose capture is reused)");
            if (string.IsNullOrWhiteSpace(o.Root)) a.Errors.Add("--h2ek DIR is required");
            if (string.IsNullOrWhiteSpace(o.ScenarioRel)) a.Errors.Add("--scenario S is required");
            if (string.IsNullOrWhiteSpace(o.Bsp)) a.Errors.Add("--bsp B is required");
            return o;
        }

        private static int Bake(string[] args, int start)
        {
            var a = ParseFor(args, start + 1,
                new[] { "--h2ek", "--scenario", "--bsp", "--quality", "--photons", "--rays", "--mode", "--ao-strength", "--ao-exponent", "--ao-fallback", "--probes-fallback",
                        "--worker", "--probe-worker", "--probe-points", "--assume-vram-mb", "--assume-other-vram-mb", "--lock-wait-min", "--lock-poll-s", "--cancel-file", "--install-retries", "--install-retry-wait-s" },
                new[] { "--ao", "--reuse-capture", "--no-install", "--yes", "--responsive", "--exact-vertex-lighting", "--dry-run", "--json", "--dev-no-fixes", "--dev-skip-preflight", "--dev-skip-gpucheck", "--reuse-unrecorded" });
            var o = BakeOptionsFrom(a);
            var so = new SessionOptions
            {
                DryRun = a.Has("--dry-run"), SkipPreflight = a.Has("--dev-skip-preflight"), SkipGpuCheck = a.Has("--dev-skip-gpucheck"),
                AssumeProbePoints = a.GetInt("--probe-points"), AssumeVramMB = a.GetInt("--assume-vram-mb"), AssumeOtherVramUseMB = a.GetInt("--assume-other-vram-mb"), CancelFile = a.Get("--cancel-file"),
            };
            bool json = a.Has("--json");
            var lw = a.GetDouble("--lock-wait-min");
            var lp = a.GetDouble("--lock-poll-s");
            if (lw.HasValue) so.LockWait = TimeSpan.FromMinutes(Math.Max(0, lw.Value));
            if (lp.HasValue) so.LockPoll = TimeSpan.FromSeconds(Math.Max(0.1, lp.Value));
            if (json && !so.DryRun) a.Errors.Add("--json is only for --dry-run");
            if (so.AssumeProbePoints.HasValue && !so.DryRun) a.Errors.Add("--probe-points is only for --dry-run (a real run queries the scenario)");
            if (so.AssumeProbePoints.HasValue && so.AssumeProbePoints.Value < 0) a.Errors.Add("--probe-points must be >= 0");
            if (so.AssumeVramMB.HasValue && !so.DryRun) a.Errors.Add("--assume-vram-mb is only for --dry-run (a real run asks gpucheck for the adapter's memory)");
            if (so.AssumeVramMB.HasValue && so.AssumeVramMB.Value < 0) a.Errors.Add("--assume-vram-mb must be >= 0");
            if (so.AssumeOtherVramUseMB.HasValue && !so.DryRun) a.Errors.Add("--assume-other-vram-mb is only for --dry-run (a real run measures it with gpucheck)");
            if (so.AssumeOtherVramUseMB.HasValue && !so.AssumeVramMB.HasValue) a.Errors.Add("--assume-other-vram-mb needs --assume-vram-mb");
            if (so.AssumeOtherVramUseMB.HasValue && so.AssumeOtherVramUseMB.Value < 0) a.Errors.Add("--assume-other-vram-mb must be >= 0");
            var retry = InstallRetryFrom(a, o);
            var host = AppHost.Create(HostConfigFrom(a));
            int ae = ArgErrors(a);
            if (ae >= 0) return ae;

            var s = new BakeSession(host, o, so);
            TextWriter info = json ? Console.Error : Console.Out;
            s.Info += m => info.WriteLine(m);
            s.Line += l => Console.Out.WriteLine(l.Kind == LineKind.Shell ? "=== " + l.Text : l.IsErr ? "ERR| " + l.Text : l.Text);
            s.Ask = p => p.Kind == PromptKind.InstallRetry ? retry.Answer(p, () => { var r = s.Run; return r != null && r.CancelRequested; }) : CliAnswer(o, p);
            string lastStep = null;
            s.StatusChanged += st =>
            {
                if (st.Step != lastStep) { lastStep = st.Step; Console.Out.WriteLine("=== [step] " + st.Step + (st.Phase != null ? "  (phase " + st.Phase + ")" : "")); }
            };
            BakePlan planned = null;
            s.Planned += p => planned = p;
            ConsoleCancelEventHandler cc = (sender, e) => { e.Cancel = true; Console.Error.WriteLine("Ctrl+C: cancelling (a tag write in progress finishes first)"); s.RequestCancel(); };
            Console.CancelKeyPress += cc;
            int code;
            try { code = s.Execute(); }
            finally { Console.CancelKeyPress -= cc; }

            if (so.DryRun)
            {
                if (code == ExitOk && planned != null)
                {
                    if (json) Console.Out.WriteLine(Json.Write(planned.ToJson(true), true));
                    else Console.Out.Write(planned.ToText(true, true));
                }
                else if (json) Console.Out.WriteLine(Json.Write(new JObj().Add("schema", "h2lm-bake-plan/1").Add("dryRun", true).Add("refused", s.Reason).Add("exitCode", (long)code), true));
                return code;
            }
            Console.Out.WriteLine("RESULT " + ResultWord(code) + " (exit " + code + ")" + (s.Reason != null ? ": " + s.Reason : ""));
            if (s.Run != null) Console.Out.WriteLine("run folder: " + s.Run.RunDir);
            return code;
        }

        public static string ResultWord(int code)
        {
            return code == ExitOk ? "DONE" : code == ExitRefused ? "REFUSED" : code == ExitCancelled ? "CANCELLED" : "FAILED";
        }

        /// <summary>Non-interactive answers: only explicit flags say yes.</summary>
        private static bool CliAnswer(BakeOptions o, Prompt p)
        {
            Console.Out.WriteLine("=== question: " + p.Title + " -- " + p.Message.Replace("\n", " "));
            foreach (var d in p.Details.Take(30)) Console.Out.WriteLine("===     " + d);
            bool yes = p.Kind == PromptKind.ChangedTags ? o.AcceptChangedTags : BakeSession.FlagsAllowInstallWithoutLayer(o, p);
            Console.Out.WriteLine("=== answer: " + (yes ? "yes (flag)" : "no (the CLI never answers yes without a flag)"));
            return yes;
        }

        public const int MaxInstallRetries = 20, DefaultInstallRetryWaitS = 30, MaxInstallRetryWaitS = 3600;

        /// <summary>
        /// V6 (gate T6): the CLI's answer to "the lightmap files are in use -- Retry the install?" (lmw_install exit 2 in the shell's
        /// install, nothing changed). Only the explicit flag says yes: --install-retries N retries up to N times, each after
        /// --install-retry-wait-s S seconds (a cancel ends the wait at once; BakeSession then stops the run as CANCELLED).
        /// </summary>
        public sealed class InstallRetryPolicy
        {
            public int Retries, WaitSeconds = DefaultInstallRetryWaitS;
            public int Used { get; private set; }
            public Action<TimeSpan> Sleep = t => System.Threading.Thread.Sleep(t);

            public bool Answer(Prompt p, Func<bool> cancelled)
            {
                Console.Out.WriteLine("=== question: " + p.Title + " -- " + p.Message.Replace("\n", " "));
                if (Used >= Retries)
                {
                    Console.Out.WriteLine("=== answer: no (" + (Retries == 0 ? "the CLI retries only with --install-retries N" : "--install-retries " + Retries + " used up") + ")");
                    return false;
                }
                Used++;
                Console.Out.WriteLine("=== answer: yes (--install-retries " + Retries + ": retry " + Used + " of " + Retries + " in " + WaitSeconds + " s)");
                var until = DateTime.UtcNow.AddSeconds(WaitSeconds);
                while (DateTime.UtcNow < until)
                {
                    if (cancelled != null && cancelled()) { Console.Out.WriteLine("=== the retry wait ended: a cancel was requested"); return true; }
                    var left = until - DateTime.UtcNow;
                    Sleep(left < TimeSpan.FromMilliseconds(250) ? (left > TimeSpan.Zero ? left : TimeSpan.Zero) : TimeSpan.FromMilliseconds(250));
                }
                return true;
            }
        }

        public static InstallRetryPolicy InstallRetryFrom(CliArgs a, BakeOptions o)
        {
            var r = new InstallRetryPolicy();
            var n = a.GetInt("--install-retries");
            var w = a.GetInt("--install-retry-wait-s");
            if (n.HasValue && (n.Value < 0 || n.Value > MaxInstallRetries)) a.Errors.Add("--install-retries must be 0-" + MaxInstallRetries);
            if (w.HasValue && (w.Value < 1 || w.Value > MaxInstallRetryWaitS)) a.Errors.Add("--install-retry-wait-s must be 1-" + MaxInstallRetryWaitS);
            if (w.HasValue && !n.HasValue) a.Errors.Add("--install-retry-wait-s needs --install-retries N");
            if (n.HasValue && n.Value > 0 && o.StageOnly) a.Errors.Add("--install-retries is for an install (not with --no-install)");
            if (n.HasValue) r.Retries = Math.Max(0, Math.Min(MaxInstallRetries, n.Value));
            if (w.HasValue) r.WaitSeconds = Math.Max(1, Math.Min(MaxInstallRetryWaitS, w.Value));
            return r;
        }

        // ------------------------------------------------------------------ restore

        /// <summary>The H2EK folder from --h2ek, or null with an error printed (a drive root is refused: finding 23).</summary>
        private static string RootFrom(string arg)
        {
            string root = ShellPaths.NormalizeRoot(arg);      // X1: a drive root stays "D:\" (these commands never start gpu_bake)
            if (!Directory.Exists(Path.Combine(root, "tags"))) { Console.Error.WriteLine("not an H2EK folder (no tags\\): " + root); return null; }
            return root;
        }

        private static int Restore(string[] args, int start)
        {
            var a = ParseFor(args, start + 1, new[] { "--h2ek", "--scenario", "--bsp", "--from" }, new[] { "--list", "--undo-last-install", "--recover", "--dry-run" });
            string root = a.Get("--h2ek"), scen = a.Get("--scenario"), bsp = a.Get("--bsp"), from = a.Get("--from");
            bool list = a.Has("--list"), undo = a.Has("--undo-last-install"), recover = a.Has("--recover"), dry = a.Has("--dry-run");
            if (string.IsNullOrWhiteSpace(root)) a.Errors.Add("--h2ek DIR is required");
            if (!recover && (string.IsNullOrWhiteSpace(scen) || string.IsNullOrWhiteSpace(bsp))) a.Errors.Add("--scenario and --bsp are required (or --recover)");
            if ((list ? 1 : 0) + (undo ? 1 : 0) + (recover ? 1 : 0) + (from != null ? 1 : 0) > 1) a.Errors.Add("--list, --from, --undo-last-install and --recover exclude each other");
            var host = AppHost.Create(HostConfigFrom(a));
            int ae = ArgErrors(a);
            if (ae >= 0) return ae;
            root = RootFrom(root);
            if (root == null) return ExitRefused;
            string work = ShellPaths.WorkOf(root);

            // review fix (findings 1 + 2): nothing restores while a bake runs on this H2EK or a live holder has the GPU lock --
            // checked BEFORE --recover too (the old order acted on a running bake's marker); the action holds the lock throughout
            string what = recover ? "the recovery restore" : undo ? "undo last install" : "restore previous lightmap";
            if (!list && !dry)
            {
                string why = TagWriteGate.Blocked(work, what);
                if (why != null) { Console.Error.WriteLine("refused: " + why); return ExitRefused; }
            }
            if (recover) return Recover(host, root, work, dry);

            string rel = BakeOptions.NormalizeScenario(root, scen);
            if (rel == null) { Console.Error.WriteLine("the scenario is not a path inside " + root + "\\tags: " + scen); return ExitRefused; }
            rel = BakeOptions.CanonicalScenario(root, rel) ?? rel;
            var set = TagSet.For(root, rel, BakeOptions.CanonicalBsp(TagSet.For(root, rel, bsp).TagDir, bsp) ?? bsp);

            if (undo)
            {
                var last = LastInstall(work, rel, set.Bsp);
                if (last == null) { Console.Out.WriteLine("no install of " + rel + " / " + bsp + " by this app is recorded (runs\\*\\status.json installBackupDir, written after the install ran)"); return ExitRefused; }
                Console.Out.WriteLine("undo the install of run " + last.RunTag + ": lmw_install restore --backup-dir " + last.InstallBackupDir + " --target-dir " + set.TagDir);
                if (dry) return ExitOk;
                if (!host.HasPayload) { Console.Error.WriteLine(host.PayloadError); return ExitRefused; }
                host.Installer.StartupEnsure(root);
                var rt = host.Resolve(root);
                var env = BakeEnv.Build(rt);
                InstallResult rr;
                try
                {
                    using (TagWriteGate.Enter(work, what, rel))
                        rr = TagInstaller.Restore(new TagRestoreRequest
                        {
                            PyExe = rt.PyExe, LmwInstallPy = rt.LmwInstallPy, Root = root, Work = work, BackupDir = last.InstallBackupDir, TargetDir = set.TagDir,
                            Marker = last.Marker != null && File.Exists(last.Marker) ? last.Marker : null, Env = env,
                        });
                }
                catch (SafetyException e) { Console.Error.WriteLine("refused: " + e.Message); return ExitRefused; }
                foreach (var l in rr.Lines) Console.Out.WriteLine("  " + l);
                Console.Out.WriteLine("RESULT " + rr.Outcome + ": " + rr.Message);
                return rr.Outcome == InstallOutcome.Installed ? ExitOk : rr.Outcome == InstallOutcome.Refused ? ExitRefused : ExitFailed;
            }

            var cands = TagRestore.Candidates(work, set);
            if (list)
            {
                if (cands.Count == 0) Console.Out.WriteLine("no _TAGBAK_ui backup of " + set.Prefix + " yet");
                var live = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                var pick = TagRestore.Newest(work, set);
                foreach (var c in cands)
                    Console.Out.WriteLine(c.Describe() + (TagRestore.IsNoOp(set, c, live) ? "   (= the live set: nothing to restore)" : "")
                                          + (pick != null && pick.Dir == c.Dir ? "   <- the default" : "") + "   [" + string.Join(", ", c.Files.Select(f => f.Key)) + "]");
                return ExitOk;
            }
            RestoreCandidate chosen;
            if (from != null)
            {
                chosen = TagRestore.ByName(work, set, from.Trim());
                if (chosen == null) { Console.Error.WriteLine("no backup named '" + from + "' for " + set.Prefix + " (--list shows them)"); return ExitRefused; }
            }
            else
            {
                chosen = TagRestore.Newest(work, set);
                if (chosen == null)
                {
                    Console.Out.WriteLine("RESULT NothingToRestore: " + (cands.Count == 0 ? "no _TAGBAK_ui backup of " + set.Prefix + " exists yet" : "every backup of " + set.Prefix + " equals the live set (--list shows them)"));
                    return ExitRefused;
                }
            }
            Console.Out.WriteLine("restore " + chosen.Dir + " -> " + set.TagDir + " (" + string.Join(", ", chosen.Files.Select(f => f.Key)) + ")");
            if (!chosen.Verified) Console.Out.WriteLine("WARNING: " + chosen.Name + " is a legacy backup without recorded md5s: it is restored as found (NOT VERIFIED)");
            if (dry) return ExitOk;
            RestoreResult res;
            try
            {
                using (var lease = TagWriteGate.Enter(work, what, rel))
                    res = TagRestore.Restore(work, set, chosen, DateTime.Now, lease.LockContent);
            }
            catch (SafetyException e) { Console.Error.WriteLine("refused: " + e.Message); return ExitRefused; }
            Console.Out.WriteLine("RESULT " + res.Outcome + ": " + res.Message);
            return res.Outcome == RestoreOutcome.Restored ? ExitOk : res.Outcome == RestoreOutcome.Failed ? ExitFailed : ExitRefused;
        }

        /// <summary>
        /// The newest run of this map whose status.json records an install backup that still exists AND that install really
        /// happened in that run (review fix, finding 4): status.json names the backup only once the install ran, and the backup's
        /// _install_manifest.json 'made' time (or, for the tool write-back's flat copy, its files' creation time) is at or after
        /// the run's start -- the shared OUT\tag_backup_before_writeback of an older install never matches a later run.
        /// </summary>
        public static RunStatus LastInstall(string work, string scenarioRel, string bsp)
        {
            string runs = RunFolder.RunsDir(work);
            if (!Directory.Exists(runs)) return null;
            return Directory.GetDirectories(runs).Select(d => { try { return RunStatus.Load(d); } catch (Exception) { return null; } })
                .Where(s => s != null && string.Equals(s.Scenario, scenarioRel, StringComparison.OrdinalIgnoreCase) && string.Equals(s.Bsp, bsp, StringComparison.OrdinalIgnoreCase)
                            && s.InstallBackupDir != null && Directory.Exists(s.InstallBackupDir) && InstalledSince(s.InstallBackupDir, s.StartedUtc))
                .OrderByDescending(s => s.StartedUtc).FirstOrDefault();
        }

        public static bool InstalledSince(string backupDir, DateTime startedUtc)
        {
            var startLocal = startedUtc.ToLocalTime();
            var floor = new DateTime(startLocal.Year, startLocal.Month, startLocal.Day, startLocal.Hour, startLocal.Minute, startLocal.Second, DateTimeKind.Local);
            try
            {
                var man = Json.ReadObjFile(Path.Combine(backupDir, "_install_manifest.json"));
                DateTime made;
                if (man != null && DateTime.TryParseExact(man.Str("made") ?? "", "yyyyMMdd_HHmmss", CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out made))
                    return made >= floor;
            }
            catch (Exception) { }
            // the tool write-back's backup: a flat copy made by that write-back (Copy-Item creates the files)
            var files = Directory.GetFiles(backupDir).Where(f => !Path.GetFileName(f).StartsWith("_", StringComparison.Ordinal) && Path.GetFileName(f).IndexOf(".older_", StringComparison.OrdinalIgnoreCase) < 0).ToList();
            return files.Count > 0 && files.All(f => File.GetCreationTimeUtc(f) >= floor.ToUniversalTime());
        }

        private static int Recover(AppHost host, string root, string work, bool dry)
        {
            var items = StartupRecovery.Scan(work);
            if (items.Count == 0) { Console.Out.WriteLine("nothing to recover on " + work); return ExitOk; }
            ResolvedRuntime rt = null;
            ChildEnvironment env = null;
            int bad = 0;
            foreach (var it in items)
            {
                Console.Out.WriteLine(it.ToString());
                if (dry) continue;
                if (it.InUse) { Console.Out.WriteLine("  left alone: it belongs to a bake that is still running (" + it.InUseWhy + ")"); bad++; continue; }
                switch (it.Kind)
                {
                    case RecoveryKind.InterruptedRun: StartupRecovery.MarkInterrupted(work, it); Console.Out.WriteLine("  marked INTERRUPTED"); break;
                    case RecoveryKind.StaleOwnLock: Console.Out.WriteLine(StartupRecovery.ReleaseStaleOwnLock(work) ? "  released" : "  left (it changed)"); break;
                    case RecoveryKind.InstallMarker:
                        {
                            if (!host.HasPayload) { Console.Error.WriteLine(host.PayloadError); return ExitRefused; }
                            if (it.Unparsed) { Console.Out.WriteLine("  the marker cannot be read: restore by hand"); bad++; break; }
                            if (rt == null) { host.Installer.StartupEnsure(root); rt = host.Resolve(root); env = BakeEnv.Build(rt); }
                            InstallResult rr;
                            try
                            {
                                using (TagWriteGate.Enter(work, "the recovery restore"))
                                {
                                    var req = StartupRecovery.RestoreRequestFor(it, root, work);
                                    req.PyExe = rt.PyExe; req.LmwInstallPy = rt.LmwInstallPy; req.Env = env;
                                    rr = TagInstaller.Restore(req);
                                }
                            }
                            catch (SafetyException e) { Console.Out.WriteLine("  refused: " + e.Message); bad++; break; }
                            foreach (var l in rr.Lines) Console.Out.WriteLine("  " + l);
                            Console.Out.WriteLine("  " + rr.Outcome + ": " + rr.Message);
                            if (rr.Outcome != InstallOutcome.Installed) bad++;
                            break;
                        }
                    default:
                        {
                            // R1 fix (V6 finding K2): FIRST check whether the tool write-back changed anything at all -- killed
                            // before the tool saved, the live set still equals the copy gpu_bake made before [4]; then the marker
                            // is set aside (.verified_<ts>) and nothing is restored (Newest would otherwise pick an OLDER backup)
                            StartupRecovery.UnchangedResult un;
                            try
                            {
                                using (TagWriteGate.Enter(work, "the recovery check"))
                                    un = StartupRecovery.ClearToolMarkerIfUnchanged(work, root, it, DateTime.Now);
                            }
                            catch (SafetyException e) { Console.Out.WriteLine("  refused: " + e.Message); bad++; break; }
                            if (un.Cleared) { Console.Out.WriteLine("  verified unchanged: " + un.Message); break; }
                            Console.Out.WriteLine("  not unchanged: " + un.Message);
                            // review fix (finding 6): the tool write-back's text marker names the set: restore that set's newest
                            // differing _TAGBAK_ui backup (TagRestore renames the marker .restored_<ts> once the whole set is back)
                            var set = StartupRecovery.SetFor(it, root);
                            var c = set == null ? null : TagRestore.Newest(work, set);
                            if (set == null || c == null)
                            {
                                Console.Out.WriteLine("  -> " + (set == null ? "the marker does not name a lightmap set this app can find" : "no differing _TAGBAK_ui backup of " + set.Prefix)
                                                      + ": restore by hand, or --cli restore --h2ek \"" + root + "\" --scenario S --bsp B --list / --from NAME");
                                bad++;
                                break;
                            }
                            Console.Out.WriteLine("  restoring " + set.Prefix + " from " + c.Dir + (c.Verified ? "" : " (a legacy backup: NOT VERIFIED)"));
                            RestoreResult res;
                            try
                            {
                                using (var lease = TagWriteGate.Enter(work, "the recovery restore", set.ScenarioRel))
                                    res = TagRestore.Restore(work, set, c, DateTime.Now, lease.LockContent);
                            }
                            catch (SafetyException e) { Console.Out.WriteLine("  refused: " + e.Message); bad++; break; }
                            Console.Out.WriteLine("  " + res.Outcome + ": " + res.Message);
                            if (res.Outcome != RestoreOutcome.Restored) bad++;
                            break;
                        }
                }
            }
            return bad == 0 ? ExitOk : ExitFailed;
        }

        // ------------------------------------------------------------------ cleanup-alias (review fix, finding 12)

        /// <summary>
        /// Removes the DLL-slot alias junction(s) H2GLM\&lt;k6&gt; (in %LOCALAPPDATA% or on the volume root) that point at this
        /// H2EK's work folder -- RemoveDirectory on the junction ONLY (SlotAlias.RemoveAlias), never anything behind it. The
        /// work folder holds junctions into the live tags, so a recursive delete through the alias would reach them; delete the
        /// alias only with this verb or Explorer. The next bake recreates it when it is needed.
        /// </summary>
        private static int CleanupAlias(string[] args, int start)
        {
            var a = ParseFor(args, start + 1, new[] { "--h2ek" }, new[] { "--dry-run" });
            string root = a.Get("--h2ek");
            bool dry = a.Has("--dry-run");
            if (string.IsNullOrWhiteSpace(root)) a.Errors.Add("--h2ek DIR is required");
            var host = AppHost.Create(HostConfigFrom(a));
            int ae = ArgErrors(a);
            if (ae >= 0) return ae;
            root = RootFrom(root);
            if (root == null) return ExitRefused;
            string work = ShellPaths.WorkOf(root);
            string lad = host.Paths.AliasLocalAppData ?? host.Paths.LocalAppData;
            string k6 = DllSlots.SlotAliasPlanner.Key6(work);
            var dirs = new List<string>();
            if (!string.IsNullOrEmpty(lad)) dirs.Add(Path.Combine(lad, DllSlots.SlotAliasPlanner.AliasFolderName, k6));
            dirs.Add(Path.Combine(host.Paths.AliasVolumeRoot ?? Path.GetPathRoot(work), DllSlots.SlotAliasPlanner.AliasFolderName, k6));
            int removed = 0, found = 0;
            foreach (var d in dirs.Distinct(StringComparer.OrdinalIgnoreCase))
            {
                string target;
                if (!DllSlots.Junction.PathExists(d)) continue;
                if (!DllSlots.Junction.TryGetTarget(d, out target)) { Console.Out.WriteLine(d + ": not a junction -- left untouched"); continue; }
                if (!DllSlots.Junction.SamePathText(target, work)) { Console.Out.WriteLine(d + ": a junction to " + target + ", not this work folder -- left untouched"); continue; }
                found++;
                if (dry) { Console.Out.WriteLine("would remove the alias " + d + " -> " + target); continue; }
                try
                {
                    using (TagWriteGate.Enter(work, "removing the alias"))
                        DllSlots.SlotAlias.RemoveAlias(d, work);
                    removed++;
                    Console.Out.WriteLine("removed the alias " + d + " (the link only; " + work + " is untouched)");
                }
                catch (SafetyException e) { Console.Error.WriteLine("refused: " + e.Message); return ExitRefused; }
            }
            if (found == 0) Console.Out.WriteLine("no alias of " + work + " exists (" + string.Join("; ", dirs) + ")");
            Console.Out.WriteLine("RESULT " + (dry ? "DRY RUN" : "OK") + ": " + (dry ? found : removed) + " alias(es)");
            return ExitOk;
        }

        // ------------------------------------------------------------------ scan-scenery-fix (SF1)

        /// <summary>--cli scan-scenery-fix --h2ek DIR [--scenario S]: the "Scan and Build Scenery Fix Dependencies" action. Without
        /// --scenario every render_model under DIR\tags is scanned; missing models are added to DIR\SceneryFixDependencies.db
        /// (timestamped backup of the old file, append-only). Exit 0 done, 2 refused, 3 the scan did not complete.</summary>
        private static int ScanSceneryFix(string[] args, int start)
        {
            var a = ParseFor(args, start + 1, new[] { "--h2ek", "--scenario" }, new string[0]);
            string root = a.Get("--h2ek"), scen = a.Get("--scenario");
            if (string.IsNullOrWhiteSpace(root)) a.Errors.Add("--h2ek DIR is required");
            var host = AppHost.Create(HostConfigFrom(a));
            int ae = ArgErrors(a);
            if (ae >= 0) return ae;
            if (!host.HasPayload) { Console.Error.WriteLine("REFUSED: " + (host.PayloadError ?? "no payload")); return ExitRefused; }
            root = RootFrom(root);
            if (root == null) return ExitRefused;
            string work = ShellPaths.WorkOf(root);
            string why = TagWriteGate.Blocked(work, "the scenery fix scan");
            if (why != null) { Console.Error.WriteLine("REFUSED: " + why); return ExitRefused; }
            ResolvedRuntime rt;
            try
            {
                var q = host.Installer.StartupEnsure(root);
                if (!q.Ok) { Console.Error.WriteLine("REFUSED: the runtime could not be prepared: " + q); return ExitRefused; }
                rt = host.Resolve(root);
            }
            catch (Exception e) when (e is IOException || e is InvalidDataException || e is UnauthorizedAccessException)
            { Console.Error.WriteLine("REFUSED: the runtime could not be prepared: " + e.Message); return ExitRefused; }
            if (rt.ScanSceneryPy == null) { Console.Error.WriteLine("REFUSED: this payload has no scan_scenery_bounds.py"); return ExitRefused; }
            var env = BakeEnv.Build(rt);
            SceneryScanResult r;
            using (TagWriteGate.Enter(work, "the scenery fix scan"))
            {
                r = SceneryFixDeps.Run(rt.PyExe, rt.ScanSceneryPy, root, scen, rt.Work, env,
                                       string.IsNullOrEmpty(scen) ? SceneryFixDeps.FullScanTimeoutMs : SceneryFixDeps.ScenarioScanTimeoutMs,
                                       Path.Combine(ShellPaths.StandaloneOf(rt.Work), "scenery_fix_last_scan.json"));
            }
            foreach (var l in SceneryFixDeps.Summarize(r)) Console.Out.WriteLine(l);
            Console.Out.WriteLine("RESULT " + (r.Ran ? "OK" : "FAILED") + ": " + r.Models + " model(s), " + r.Added.Count + " added, " + r.Updated.Count + " updated, " + r.Conflicts.Count + " conflict(s)");
            return r.Ran ? ExitOk : ExitFailed;
        }

        // ------------------------------------------------------------------ doctor

        private static int Doctor(string[] args, int start)
        {
            var a = ParseFor(args, start + 1, new[] { "--h2ek" }, new[] { "--json", "--repair", "--no-gpu", "--no-extract" });
            var host = AppHost.Create(HostConfigFrom(a));
            string root = a.Get("--h2ek");
            bool json = a.Has("--json"), repair = a.Has("--repair"), noGpu = a.Has("--no-gpu"), noExtract = a.Has("--no-extract");
            int ae = ArgErrors(a);
            if (ae >= 0) return ae;
            var doc = new JObj().Add("schema", "h2lm-doctor/1").Add("exe", AppHost.ExeVersion).Add("appVersion", host.AppVersion)
                                .Add("payload", host.HasPayload ? host.Source.Describe : null).Add("payloadError", host.PayloadError)
                                .Add("customEnabled", host.CustomAvailable).Add("customGuarded", host.HasGuardedCustomEngine).Add("customStatus", "experimental")
                                .Add("localAppData", host.Paths.AppDir);
            var lines = new List<string>
            {
                "H2 GPU Lightmapper " + host.AppVersion + " (exe " + AppHost.ExeVersion + ")",
                "  payload: " + (host.HasPayload ? host.Source.Describe : "NONE -- " + host.PayloadError),
                "  per-user folder: " + host.Paths.AppDir,
                "  Custom quality: " + (host.CustomAvailable ? "Experimental (" + (host.HasGuardedCustomEngine ? "guarded engine" : "UNGUARDED, dev") + ")" : "not available: this payload has no guarded Custom engine"),
            };
            bool ok = host.HasPayload;
            if (root == null)
            {
                var st = host.LoadSettings();
                var cands = H2ekDiscovery.Discover(new DiscoveryOptions { Remembered = st.Roots });
                lines.Add("  H2EK folders found: " + cands.Count);
                foreach (var c in cands) lines.Add("    " + c.Root + "   (" + c.Source + ": " + c.Label + (c.HasToolFast ? "" : "; no tool_fast.exe") + ")");
                doc.Add("roots", cands.Select(c => (object)new JObj().Add("root", c.Root).Add("source", c.Source.ToString()).Add("label", c.Label).Add("toolFast", c.HasToolFast)).ToList());
                return Emit(json, doc, lines, ok ? ExitOk : ExitRefused);
            }
            string fullRoot = Path.GetFullPath(root.Trim().Trim('"'));
            string drvWhy = null;
            bool drvRoot = ShellPaths.IsDriveRoot(fullRoot);
            bool drvOk = drvRoot && host.HasPayload && host.DriveRootEnabled(out drvWhy);       // X1: only a payload with the P1b gpu_bake
            if (drvRoot && !drvOk) { lines.Add("  " + ShellPaths.DriveRootMessage + " (" + fullRoot + ")" + (drvWhy != null ? ": " + drvWhy : "")); doc.Add("root", fullRoot).Add("ok", false); return Emit(json, doc, lines, ExitRefused); }
            root = ShellPaths.NormalizeRoot(fullRoot);
            string work = ShellPaths.WorkOf(root);
            doc.Add("root", root).Add("work", work);
            lines.Add("  root: " + root);
            ResolvedRuntime rt = null;
            ChildEnvironment env = null;
            if (host.HasPayload && !noExtract)
            {
                try
                {
                    var q = host.Installer.StartupEnsure(root);
                    lines.Add("  runtime quick check: " + (q.Ok ? "ok" : q.ToString()));
                    if (repair)
                    {
                        // review fix (finding 2): never rewrite the runtime under a running bake
                        string why = TagWriteGate.Blocked(work, "repairing the runtime");
                        if (why != null) { lines.Add("  repair: refused -- " + why); ok = false; }
                        else
                        {
                            CheckReport r;
                            using (TagWriteGate.Enter(work, "repairing the runtime")) r = host.Installer.RepairEverything(root);
                            lines.Add("  repair (full md5 of everything): " + (r.Ok ? "ok" : r.ToString()));
                            ok &= r.Ok;
                        }
                    }
                    ok &= q.Ok;
                    doc.Add("runtimeOk", q.Ok);
                    rt = host.Resolve(root);
                    env = BakeEnv.Build(rt);
                }
                catch (Exception e) { ok = false; lines.Add("  runtime: FAILED: " + e.Message); doc.Add("runtimeError", e.Message); }
            }
            var inst = host.HasPayload ? host.Manifest.InstComponent : null;
            var pf = PreflightChecks.Run(root, new PreflightOptions
            {
                ProbeWrite = true, RunToolSmoke = true, InstDirName = inst == null ? null : inst.DirName, InstMaxPathLen = inst == null ? 0 : inst.MaxPathLen,
                AliasLocalAppData = host.Paths.AliasLocalAppData, AliasVolumeRoot = host.Paths.AliasVolumeRoot, AllowDriveRoot = drvOk,
                GpuCheckExe = !noGpu && rt != null ? rt.GpuCheckExe : null,
                GpuCheckPlainExe = !noGpu && rt != null && inst != null ? InstLayout.FindByName(inst, rt.InstDir, InstLayout.GpuCheckPlain) : null,
                PwshExe = rt == null ? null : rt.PwshExe, PwshEnv = env,
            });
            foreach (var it in pf.Items) lines.Add("  " + it);
            ok &= pf.Ok;
            doc.Add("preflight", Json.ParseObj(pf.ToJson()));
            if (Directory.Exists(work))
            {
                var rec = StartupRecovery.Scan(work);
                foreach (var it in rec) lines.Add("  RECOVERY " + it);
                doc.Add("recovery", rec.Select(it => (object)new JObj().Add("kind", it.Kind.ToString()).Add("message", it.Message).Add("path", it.Path).Add("inUse", it.InUse).Add("inUseWhy", it.InUseWhy)).ToList());
                var k = CurrentRun.Inspect(work, out CurrentRun cr);
                lines.Add("  current run: " + k + (cr != null ? " " + cr.RunTag : ""));
                doc.Add("currentRun", k.ToString());
                var li = GpuLock.Inspect(GpuLock.PathFor(work));
                lines.Add("  GPU lock: " + (li.Exists ? "held: " + li.Content : "free"));
                doc.Add("gpuLock", li.Exists ? li.Content : null);
            }
            doc.Add("ok", ok);
            lines.Add("RESULT " + (ok ? "OK" : "PROBLEMS FOUND"));
            return Emit(json, doc, lines, ok ? ExitOk : ExitRefused);
        }

        private static int Emit(bool json, JObj doc, List<string> lines, int code)
        {
            if (json) Console.Out.WriteLine(Json.Write(doc, true));
            else foreach (var l in lines) Console.Out.WriteLine(l);
            return code;
        }

        // ------------------------------------------------------------------ cancel / status

        private static string RootArg(CliArgs a)
        {
            string root = a.Get("--h2ek");
            if (string.IsNullOrWhiteSpace(root)) { a.Errors.Add("--h2ek DIR is required"); return null; }
            return ShellPaths.NormalizeRoot(root);
        }

        private static int Cancel(string[] args, int start)
        {
            var a = ParseFor(args, start + 1, new[] { "--h2ek" }, new string[0]);
            string root = RootArg(a);
            int ae = ArgErrors(a);
            if (ae >= 0) return ae;
            var cur = CurrentRun.Read(ShellPaths.WorkOf(root));
            if (cur == null || cur.RunDir == null || !Directory.Exists(cur.RunDir)) { Console.Out.WriteLine("no bake is running on " + root); return ExitRefused; }
            SafeFs.WriteAllTextAtomic(Path.Combine(cur.RunDir, BakeSession.CancelRequestFile),
                "cancel requested " + DateTime.Now.ToString("s", CultureInfo.InvariantCulture) + " by pid " + ProcessInfo.CurrentPid + "\r\n");
            Console.Out.WriteLine("cancel requested for run " + cur.RunTag + " (a tag write in progress finishes first)");
            return ExitOk;
        }

        private static int Status(string[] args, int start)
        {
            var a = ParseFor(args, start + 1, new[] { "--h2ek" }, new string[0]);
            string root = RootArg(a);
            int ae = ArgErrors(a);
            if (ae >= 0) return ae;
            string work = ShellPaths.WorkOf(root);
            var k = CurrentRun.Inspect(work, out CurrentRun cur);
            Console.Out.WriteLine("current run: " + k + (cur != null ? " " + cur.RunTag + " (" + cur.RunDir + ")" : ""));
            if (cur != null && cur.RunDir != null && File.Exists(Path.Combine(cur.RunDir, "status.json")))
                Console.Out.WriteLine(Json.ReadAllTextShared(Path.Combine(cur.RunDir, "status.json")));
            return ExitOk;
        }
    }
}
