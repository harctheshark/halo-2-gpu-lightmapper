// S2.2 preflight on a (possibly foreign) H2EK root: FINAL_DESIGN section 8 + the constraints found by S0.2 (ASCII paths:
// pwsh decodes `& py` captures as OEM 437) and S0.4 (no '{' '}' in the root: gpu_bake builds $OUT / $SKW with -f format
// strings that contain $W). Every check is read-only except the optional write probe (WORK\.write_probe, created and
// deleted) and EnsureBoundsDb (copies the shipped DB into ROOT only when ROOT has none; never overwrites).
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using H2GpuLightmapper.Core.Payload;
using H2GpuLightmapper.Core.Run;
using H2GpuLightmapper.Core.Util;
using H2GpuLightmapper.DllSlots;
using Json = H2GpuLightmapper.Core.Util.Json;
using H2Lm.Proc;

namespace H2GpuLightmapper.Core.Preflight
{
    public enum CheckLevel { Ok, Info, Warn, Fail }

    public sealed class PreflightItem
    {
        public string Id;
        public CheckLevel Level;
        public string Message;
        public string Detail;
        public override string ToString() { return Level.ToString().ToUpperInvariant() + " " + Id + ": " + Message + (Detail != null ? " [" + Detail + "]" : ""); }
    }

    public sealed class PreflightReport
    {
        public string Root, Work;
        public List<PreflightItem> Items = new List<PreflightItem>();
        public ToolIdentity Tool;
        public SlotBasePlan SlotPlan;
        public GpuCheckResult Gpu;
        /// <summary>gpucheck_plain on a hybrid PC (null otherwise).</summary>
        public GpuCheckResult GpuPlain;
        /// <summary>Which kernel set the bake will run on this GPU (Unknown when the GPU was not checked or was refused).</summary>
        public PrecisionMode Precision;
        public bool Ok { get { return !Items.Any(i => i.Level == CheckLevel.Fail); } }
        public IEnumerable<PreflightItem> Failures { get { return Items.Where(i => i.Level == CheckLevel.Fail); } }
        public PreflightItem Get(string id) { return Items.FirstOrDefault(i => i.Id == id); }

        internal PreflightItem Add(string id, CheckLevel l, string msg, string detail = null)
        {
            var it = new PreflightItem { Id = id, Level = l, Message = msg, Detail = detail };
            Items.Add(it);
            return it;
        }

        public string ToJson()
        {
            var a = Items.Select(i => (object)new JObj().Add("id", i.Id).Add("level", i.Level.ToString().ToLowerInvariant()).Add("message", i.Message).Add("detail", i.Detail)).ToList();
            return Json.Write(new JObj().Add("root", Root).Add("work", Work).Add("ok", Ok).Add("tool", Tool == null ? null : Tool.StatusLine)
                .Add("slotBase", SlotPlan == null ? null : SlotPlan.SlotBase).Add("gpu", Gpu == null ? null : Gpu.Line)
                .Add("precision", Precision == PrecisionMode.Unknown ? null : GpuCheck.PrecisionText(Precision, true)).Add("items", a), true);
        }
    }

    public sealed class PreflightOptions
    {
        /// <summary>Create and delete WORK\.write_probe (off for the read-only production tests).</summary>
        public bool ProbeWrite = true;
        /// <summary>X1: the payload's gpu_bake.ps1 handles H2LM_ROOT at a drive root (MANIFEST features.driveRoot): an H2EK at "D:\" is then
        /// an info, not a failure. Default false: the finding-23 refusal.</summary>
        public bool AllowDriveRoot;
        /// <summary>Run tool_fast.exe with no arguments, hidden, for up to <see cref="ToolSmokeTimeoutMs"/>.</summary>
        public bool RunToolSmoke = true;
        public int ToolSmokeTimeoutMs = 10000;
        /// <summary>Working dir of the smoke run (default WORK\standalone\smoke: the tool writes debug.txt + reports\ there).</summary>
        public string ToolSmokeCwd;
        /// <summary>Custom quality above 50M photons needs 12 GB free instead of 8 GB.</summary>
        public bool Custom;
        public long CustomPhotons;
        public long MinFreeBytes = 8L << 30;
        public long MinFreeBytesBigCustom = 12L << 30;
        /// <summary>INST component (for the path budget of its extraction); null = skip that entry.</summary>
        public string InstDirName;
        public int InstMaxPathLen;
        public string ScenarioRel, Bsp;
        /// <summary>DLL-slot alias candidates (see ShellPaths); null LocalAppData = the real one.</summary>
        public string AliasLocalAppData, AliasVolumeRoot;
        /// <summary>The x86 system folder (SysWOW64 on 64-bit Windows); null = the real one.</summary>
        public string SystemX86Dir;
        /// <summary>gpucheck.exe to run (null = skip), or its JSON + exit code already captured.</summary>
        public string GpuCheckExe;
        public string GpuCheckJson;
        public int GpuCheckExitCode;
        /// <summary>S1.6: on a HYBRID machine also run gpucheck_plain.exe (no Optimus / AMD exports, like the production
        /// fullgpu 43a600c1 that Low / Super use until N3 passes): the adapter Windows gives an exe without the exports.</summary>
        public string GpuCheckPlainExe;
        public string GpuCheckPlainJson;
        public int GpuCheckPlainExitCode;
        /// <summary>Compatibility mode (DF64): the engine this bake runs has it (its gpu_bake.ps1 validates GPULM_COMPAT; see
        /// EngineSwitches.ReadsEnvVar). True: a GPU without FP64 is accepted and runs the FP32-only kernels. False (default; every engine up to
        /// 0.6.3): a GPU without FP64 is refused in plain words (<see cref="GpuCheck.NoFp64Plain"/>).</summary>
        public bool CompatEngine;
        /// <summary>The bake's fullgpu carries the NvOptimus / AmdPowerXpress exports (the N3 guarded Custom engine): the plain
        /// verdict on a hybrid PC is then advice only. Default false: Low / Super run the production fullgpu without them.</summary>
        public bool FullgpuSelectsDgpu;
        /// <summary>The runtime pwsh.exe for the execution-policy / language-mode check (null = skip).</summary>
        public string PwshExe;
        public ChildEnvironment PwshEnv;
    }

    public static class PreflightChecks
    {
        public const string BoundsDbName = "h2v_scenery_bounds.db";
        public const string BoundsDbMd5 = "6814f29a";   // the Osoyoos project's (md5 prefix); the payload ships this copy

        /// <summary>Characters refused anywhere in ROOT / WORK (beyond non-ASCII): '{' '}' break gpu_bake's -f format strings,
        /// '[' ']' are PowerShell wildcards in every -Path / Test-Path call gpu_bake makes.</summary>
        public static readonly char[] ForbiddenPathChars = { '{', '}', '[', ']' };

        public static PreflightReport Run(string root, PreflightOptions o = null)
        {
            o = o ?? new PreflightOptions();
            var rep = new PreflightReport();
            try { rep.Root = SafeFs.Norm(root); }
            catch (Exception e) { rep.Root = root; rep.Add("root.path", CheckLevel.Fail, "not a usable folder path: " + e.Message); return rep; }
            root = rep.Root;
            rep.Work = ShellPaths.WorkOf(root);
            if (!Directory.Exists(root)) { rep.Add("root.exists", CheckLevel.Fail, "the folder does not exist: " + root); return rep; }
            if (ShellPaths.IsDriveRoot(root))
            {
                if (o.AllowDriveRoot) rep.Add("path.root", CheckLevel.Info, "the H2EK is at the root of a drive; this build's bake scripts handle that (H2LM_ROOT=" + root + ")", root);
                else rep.Add("path.root", CheckLevel.Fail, ShellPaths.DriveRootMessage, root);
            }

            // ---- tool_fast
            rep.Tool = ToolFast.Identify(root);
            rep.Add("tool.build", rep.Tool.Verdict == ToolVerdict.Supported ? CheckLevel.Ok : CheckLevel.Fail, rep.Tool.StatusLine, rep.Tool.Md5);

            // ---- H2EK layout and prerequisites (none is ever shipped)
            rep.Add("h2ek.tags", Directory.Exists(Path.Combine(root, "tags")) ? CheckLevel.Ok : CheckLevel.Fail,
                Directory.Exists(Path.Combine(root, "tags")) ? "tags\\ present" : "tags\\ is missing -- this is not an H2EK folder");
            // R1 fix (V6 finding): gpu_bake runs every tool_fast with "-data_dir data"; without ROOT\data the tool exits 3 at once
            // ("data directory override does not exist") and gpu_bake reports it as "PHASE 1: injection FAILED"
            rep.Add("h2ek.data", Directory.Exists(Path.Combine(root, "data")) ? CheckLevel.Ok : CheckLevel.Fail,
                Directory.Exists(Path.Combine(root, "data")) ? "data\\ present"
                    : "data\\ is missing from the H2EK folder -- tool_fast refuses to start without it (every capture would fail as 'injection FAILED'); "
                      + "verify the H2EK files in Steam, or create an empty folder named data in the H2EK folder");
            rep.Add("h2ek.fbxsdk", File.Exists(Path.Combine(root, "libfbxsdk.dll")) ? CheckLevel.Ok : CheckLevel.Fail,
                File.Exists(Path.Combine(root, "libfbxsdk.dll")) ? "libfbxsdk.dll present" : "libfbxsdk.dll is missing from the H2EK folder (tool_fast cannot start) -- verify the H2EK files in Steam");
            string x86 = o.SystemX86Dir ?? Environment.GetFolderPath(Environment.SpecialFolder.SystemX86);
            var missingVc = new[] { "msvcp140.dll", "vcruntime140.dll", "vcomp140.dll" }
                .Where(d => !File.Exists(Path.Combine(x86, d)) && !File.Exists(Path.Combine(root, d))).ToList();
            rep.Add("prereq.vcx86", missingVc.Count == 0 ? CheckLevel.Ok : CheckLevel.Fail,
                missingVc.Count == 0 ? "x86 Visual C++ 2015-2022 runtime present"
                    : "missing " + string.Join(", ", missingVc) + " (x86) -- install the Visual C++ 2015-2022 x86 redistributable (an H2EK prerequisite)", x86);
            bool d3dx = File.Exists(Path.Combine(x86, "d3dx11_43.dll")) || File.Exists(Path.Combine(root, "d3dx11_43.dll"));
            rep.Add("prereq.d3dx11", d3dx ? CheckLevel.Ok : CheckLevel.Fail,
                d3dx ? "d3dx11_43.dll (x86) present" : "d3dx11_43.dll (x86) is missing -- install the DirectX End-User Runtime (June 2010), an H2EK prerequisite");

            // ---- file system
            CheckPathText(rep, root);
            CheckVolume(rep, root, o);
            CheckSlot(rep, o);
            CheckBudget(rep, o);
            if (o.ProbeWrite) CheckWritable(rep);
            CheckBoundsDb(rep);

            // ---- runtime checks that start processes (all hidden)
            if (o.RunToolSmoke && rep.Tool.Verdict == ToolVerdict.Supported) ToolSmoke(rep, o);
            if (o.GpuCheckJson != null) AddGpu(rep, GpuCheck.Parse(o.GpuCheckJson, o.GpuCheckExitCode), o.CompatEngine);
            else if (o.GpuCheckExe != null) AddGpu(rep, GpuCheck.Run(o.GpuCheckExe), o.CompatEngine);
            if (rep.Gpu != null && rep.Gpu.Ok && rep.Gpu.Hybrid && (o.GpuCheckPlainJson != null || o.GpuCheckPlainExe != null))
            {
                var plain = o.GpuCheckPlainJson != null ? GpuCheck.Parse(o.GpuCheckPlainJson, o.GpuCheckPlainExitCode) : GpuCheck.Run(o.GpuCheckPlainExe);
                rep.GpuPlain = plain;
                // Compatibility mode: a plain adapter without FP64 is only acceptable when the adapter the exports pick has none either (then
                // both run the DF64 kernels). When the discrete GPU has FP64 and Windows would hand the export-less fullgpu the iGPU without
                // it, the bake would silently run slowly in Compatibility mode on the weaker GPU: that stays the Graphics settings refusal.
                bool plainNoFp64 = plain.Raw != null && GpuCheck.NoFp64(plain);
                bool bothNoFp64 = plainNoFp64 && o.CompatEngine && rep.Gpu.CompatAccepted;
                if (bothNoFp64) { plain.Ok = true; plain.CompatAccepted = true; plain.Notes.AddRange(plain.Reasons); plain.Reasons.Clear(); }
                if (plain.Raw != null && (!plain.Ok || (plainNoFp64 && !bothNoFp64)))
                {
                    // review fix (finding 17): Low and Super run the PRODUCTION fullgpu, which has no NvOptimusEnablement /
                    // AmdPowerXpressRequestHighPerformance exports -- exactly like gpucheck_plain. Its verdict is therefore the one
                    // that decides (FAIL), unless the bake runs an fullgpu with the exports (the N3 guarded Custom engine). gpu_bake
                    // copies fullgpu into each worker folder and runs it from there, and Windows keys the preference by exe path.
                    string farms = Path.Combine(rep.Work, "farm_w2NN", "fullgpu.exe");
                    string plainWhy = plainNoFp64 ? GpuCheck.Fp64Detail(plain) + (o.CompatEngine ? "; the bake would run slowly in Compatibility mode on it instead of on the discrete GPU" : "")
                                                  : string.Join("; ", plain.Reasons);
                    string msg = "This PC has two GPUs and Windows gives programs without a GPU preference the " + (plain.DefaultAdapter ?? "other adapter")
                        + ", which cannot run the lightmapper (" + plainWhy + "). The bake runs fullgpu.exe from its worker folder ("
                        + farms + ", e.g. " + Path.Combine(rep.Work, "farm_w200", "fullgpu.exe") + "). Open Windows Settings > System > Display > Graphics, add the fullgpu.exe of every "
                        + "farm_w2NN folder this app uses (the bake names the exact path) and choose 'High performance'. (The app never changes this setting.)";
                    if (o.FullgpuSelectsDgpu) rep.Add("gpu.hybrid", CheckLevel.Info, "two GPUs; this bake's fullgpu carries the high-performance GPU exports, so Windows gives it the discrete GPU", plain.Line);
                    else rep.Add("gpu.hybrid", CheckLevel.Fail, msg, plain.Line);
                }
                else if (plain.Raw != null)
                    rep.Add("gpu.hybrid", CheckLevel.Info, "two GPUs; programs without a preference get " + (plain.DefaultAdapter ?? "?") + ", which is suitable", plain.Line);
            }
            if (o.PwshExe != null) CheckPwshPolicy(rep, o);
            return rep;
        }

        private static void CheckPathText(PreflightReport rep, string root)
        {
            foreach (var p in new[] { rep.Root, rep.Work })
            {
                string bad = new string(p.Where(c => c > 0x7E || c < 0x20).Distinct().ToArray());
                if (bad.Length > 0)
                {
                    rep.Add("path.ascii", CheckLevel.Fail, "the H2EK path has non-ASCII characters (" + bad + "): the bake scripts read the paths back through the OEM console code page and would garble them -- move or copy the H2EK to a plain-ASCII folder", p);
                    return;
                }
                var forb = p.Where(c => ForbiddenPathChars.Contains(c)).Distinct().ToArray();
                if (forb.Length > 0)
                {
                    rep.Add("path.chars", CheckLevel.Fail, "the H2EK path contains " + string.Join(" ", forb.Select(c => "'" + c + "'")) + ", which the bake script cannot handle (format strings / wildcards) -- rename the folder", p);
                    return;
                }
            }
            bool ansi = AnsiCodePage.RoundTrips(rep.Root, AnsiCodePage.System) && AnsiCodePage.RoundTrips(rep.Work, AnsiCodePage.System);
            rep.Add("path.ascii", ansi ? CheckLevel.Ok : CheckLevel.Fail, ansi ? "plain ASCII path" : "the path does not survive the ANSI code page (tool_fast and the DLL are ANSI)", rep.Root);
        }

        private static void CheckVolume(PreflightReport rep, string root, PreflightOptions o)
        {
            string vol = Path.GetPathRoot(root);
            var fs = new StringBuilder(64);
            uint serial, maxComp, flags;
            if (!GetVolumeInformationW(vol, null, 0, out serial, out maxComp, out flags, fs, fs.Capacity))
                rep.Add("fs.ntfs", CheckLevel.Fail, "cannot read the volume information of " + vol + " (error " + Marshal.GetLastWin32Error() + ")");
            else
                rep.Add("fs.ntfs", fs.ToString() == "NTFS" ? CheckLevel.Ok : CheckLevel.Fail,
                    fs.ToString() == "NTFS" ? "NTFS volume" : "the H2EK is on a " + fs + " volume; the lightmapper needs NTFS (it uses junctions) -- move the H2EK to an NTFS drive", vol);
            ulong free, total, totalFree;
            if (GetDiskFreeSpaceExW(root, out free, out total, out totalFree))
            {
                long need = o.Custom && o.CustomPhotons > 50000000 ? o.MinFreeBytesBigCustom : o.MinFreeBytes;
                rep.Add("fs.free", (long)free >= need ? CheckLevel.Ok : CheckLevel.Fail,
                    string.Format("{0:0.0} GB free on {1} ({2:0} GB needed{3})", free / 1073741824.0, vol, need / 1073741824.0, need > o.MinFreeBytes ? " for Custom above 50M photons" : ""));
            }
            else rep.Add("fs.free", CheckLevel.Warn, "cannot read the free space of " + vol);
        }

        private static void CheckSlot(PreflightReport rep, PreflightOptions o)
        {
            try
            {
                var plan = o.AliasLocalAppData == null && o.AliasVolumeRoot == null
                    ? SlotAliasPlanner.Plan(rep.Work)
                    : SlotAliasPlanner.Plan(rep.Work, o.AliasLocalAppData ?? ShellPaths.Default().LocalAppData, o.AliasVolumeRoot);
                rep.SlotPlan = plan;
                rep.Add("path.slot", CheckLevel.Ok, plan.IsAlias ? "the work folder path is long; the estimator DLL will reach it through the short alias " + plan.SlotBase : "the work folder fits the estimator DLL's 60-character path slot", plan.SlotBase);
            }
            catch (Exception e)
            {
                rep.Add("path.slot", CheckLevel.Fail, "the work folder path is too long for the estimator DLL and no short alias is possible: " + e.Message + " -- move the H2EK to a shorter path");
            }
        }

        private static void CheckBudget(PreflightReport rep, PreflightOptions o)
        {
            var e = PathBudget.Estimate(rep.Root, o.InstDirName, o.InstMaxPathLen, o.ScenarioRel, o.Bsp);
            var l = PathBudget.Longest(e);
            bool real = !string.IsNullOrEmpty(o.ScenarioRel) && !string.IsNullOrEmpty(o.Bsp);
            bool instOver = e.Any(x => x.What == "INST extraction" && x.Length >= PathBudget.Limit);
            var level = l.Length < PathBudget.Limit ? CheckLevel.Ok : (real || instOver) ? CheckLevel.Fail : CheckLevel.Warn;
            rep.Add("path.budget", level,
                l.Length < PathBudget.Limit ? "longest expected path " + l.Length + " characters (limit " + PathBudget.Limit + ")"
                    : (real || instOver ? "paths would reach " : "maps with long names may not fit: paths could reach ") + l.Length + " characters (" + l.What + "; limit " + PathBudget.Limit
                      + ") -- move the H2EK to a shorter path" + (real ? " or use a shorter scenario path" : ""),
                l.Example);
        }

        private static void CheckWritable(PreflightReport rep)
        {
            try
            {
                Directory.CreateDirectory(rep.Work);
                string probe = Path.Combine(rep.Work, ".write_probe");
                using (var fs = new FileStream(probe, FileMode.Create, FileAccess.Write, FileShare.None, 4096, FileOptions.DeleteOnClose))
                    fs.WriteByte(1);
                SafeFs.TryDeleteFile(probe);
                rep.Add("fs.writable", CheckLevel.Ok, "the H2EK folder is writable");
            }
            catch (Exception e)
            {
                rep.Add("fs.writable", CheckLevel.Fail, "cannot write in " + rep.Work + ": " + e.Message + (rep.Root.IndexOf("Program Files", StringComparison.OrdinalIgnoreCase) >= 0 ? " -- an H2EK under Program Files is read-only for normal users; copy it elsewhere" : ""));
            }
        }

        private static void CheckBoundsDb(PreflightReport rep)
        {
            string p = Path.Combine(rep.Root, BoundsDbName);
            if (File.Exists(p))
            {
                string m = SafeFs.Md5File(p);
                rep.Add("boundsdb", m.StartsWith(BoundsDbMd5, StringComparison.OrdinalIgnoreCase) ? CheckLevel.Ok : CheckLevel.Info,
                    m.StartsWith(BoundsDbMd5, StringComparison.OrdinalIgnoreCase) ? "scenery bounds DB present" : "an existing scenery bounds DB is kept as is (md5 " + m.Substring(0, 8) + ")", m);
            }
            else rep.Add("boundsdb", CheckLevel.Info, "no scenery bounds DB in the H2EK folder: the shipped one (" + BoundsDbMd5 + ") will be copied in before the first bake (never overwritten)");
        }

        // ------------------------------------------------------------------ processes

        public sealed class ToolSmokeResult
        {
            public int ExitCode;
            public bool TimedOut;
            public string Output;
            public double Seconds;
            public CheckLevel Level;
            public string Message;
        }

        /// <summary>tool_fast.exe with no arguments, CREATE_NO_WINDOW, in a job; it must start (print its usage, exit 1) and
        /// not die with a loader error such as 0xC0000135. Every tool run writes debug.txt + reports\debug_full.txt into its
        /// working folder, so the default cwd is WORK\standalone\smoke (never ROOT).</summary>
        public static ToolSmokeResult RunToolSmoke(string root, string cwd = null, int timeoutMs = 10000)
        {
            string exe = Path.Combine(root, "tool_fast.exe");
            if (cwd == null) { cwd = Path.Combine(ShellPaths.StandaloneOf(ShellPaths.WorkOf(root)), "smoke"); Directory.CreateDirectory(cwd); }
            var env = ChildEnvironment.FromCurrentProcess();
            foreach (var p in ChildEnvironment.StrippedPrefixes) env.RemovePrefix(p);
            var r = ProcessRunner.RunCapture(exe, new string[0], cwd ?? root, env, timeoutMs);
            var t = new ToolSmokeResult { ExitCode = r.ExitCode, TimedOut = r.TimedOut, Seconds = r.Seconds };
            t.Output = string.Join("\n", r.StdoutLines.Concat(r.StderrLines).Take(40));
            string why = ProcessRunner.Describe(r.ExitCode);
            bool printed = r.StdoutLines.Concat(r.StderrLines).Any(l => l.Trim().Length > 0);
            if (r.TimedOut) { t.Level = CheckLevel.Warn; t.Message = "tool_fast.exe did not finish within " + timeoutMs / 1000 + " s with no arguments (it was stopped)"; }
            else if (why != null && unchecked((uint)r.ExitCode) >= 0xC0000000u) { t.Level = CheckLevel.Fail; t.Message = "tool_fast.exe failed to start: " + why + " (exit " + ProcessRunner.FormatExit(r.ExitCode) + ") -- check the H2EK prerequisites (VC++ x86, DirectX June 2010)"; }
            else if (!printed) { t.Level = CheckLevel.Warn; t.Message = "tool_fast.exe started but printed nothing (exit " + ProcessRunner.FormatExit(r.ExitCode) + ")"; }
            else { t.Level = CheckLevel.Ok; t.Message = "tool_fast.exe starts (usage printed, exit " + ProcessRunner.FormatExit(r.ExitCode) + ", " + t.Seconds.ToString("0.0") + " s)"; }
            return t;
        }

        private static void ToolSmoke(PreflightReport rep, PreflightOptions o)
        {
            try
            {
                var t = RunToolSmoke(rep.Root, o.ToolSmokeCwd, o.ToolSmokeTimeoutMs);
                rep.Add("tool.smoke", t.Level, t.Message, t.Output.Length > 300 ? t.Output.Substring(0, 300) : t.Output);
            }
            catch (Exception e) { rep.Add("tool.smoke", CheckLevel.Fail, "tool_fast.exe could not be started: " + e.Message); }
        }

        private static void AddGpu(PreflightReport rep, GpuCheckResult g, bool compatEngine)
        {
            rep.Gpu = g;
            if (!g.Ran || g.Raw == null) { rep.Add("gpu", CheckLevel.Warn, "the GPU check could not run" + (g.Error != null ? ": " + g.Error : "")); return; }
            // Compatibility mode (DF64): decided from the adapter's own FP64 fields, so it holds for gpucheck 1.2.0 (verdict ok + compat_mode) and for
            // an older gpucheck (verdict not ok, FP64 reasons only) alike. An engine WITHOUT the mode keeps the plain refusal in both cases.
            rep.Precision = GpuCheck.ApplyEngine(g, compatEngine);
            if (!g.Ok)
            {
                rep.Add("gpu", CheckLevel.Fail, GpuCheck.RefusalText(g), g.Line);
                return;
            }
            rep.Add("gpu", CheckLevel.Ok, g.Line);
            if (rep.Precision == PrecisionMode.Compat) rep.Add("gpu.precision", CheckLevel.Info, GpuCheck.PrecisionCompat, string.Join("; ", g.Notes));
            else rep.Add("gpu.precision", CheckLevel.Info, GpuCheck.PrecisionFull);
            foreach (var a in g.Advice) rep.Add("gpu.advice", CheckLevel.Warn, a + " (advice only; the app never changes system settings)");
        }

        /// <summary>Execution policy and language mode as the bundled pwsh sees them (S0.2): a Group-Policy AllSigned /
        /// Restricted overrides -ExecutionPolicy Bypass, and Constrained Language Mode (Smart App Control / WDAC) cannot run
        /// gpu_bake.ps1.</summary>
        private static void CheckPwshPolicy(PreflightReport rep, PreflightOptions o)
        {
            const string script = "Get-ExecutionPolicy -List | ForEach-Object { 'POLICY ' + $_.Scope + '=' + $_.ExecutionPolicy }; 'LANG ' + $ExecutionContext.SessionState.LanguageMode";
            ProcessRunner.Result r;
            try
            {
                r = ProcessRunner.RunCapture(o.PwshExe, new[] { "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-Command", script },
                    Path.GetDirectoryName(o.PwshExe), o.PwshEnv, 30000);
            }
            catch (Exception e) { rep.Add("pwsh.policy", CheckLevel.Fail, "the bundled PowerShell could not start: " + e.Message); return; }
            var pol = r.StdoutLines.Where(l => l.StartsWith("POLICY ", StringComparison.Ordinal)).Select(l => l.Substring(7)).ToList();
            string lang = r.StdoutLines.Where(l => l.StartsWith("LANG ", StringComparison.Ordinal)).Select(l => l.Substring(5).Trim()).FirstOrDefault();
            if (r.ExitCode != 0 || lang == null) { rep.Add("pwsh.policy", CheckLevel.Fail, "the bundled PowerShell failed (exit " + ProcessRunner.FormatExit(r.ExitCode) + "): " + string.Join(" / ", r.StderrLines.Take(3))); return; }
            var gpo = pol.Where(p => p.StartsWith("MachinePolicy=", StringComparison.OrdinalIgnoreCase) || p.StartsWith("UserPolicy=", StringComparison.OrdinalIgnoreCase))
                         .Where(p => { string v = p.Substring(p.IndexOf('=') + 1); return v != "Undefined" && v != "Bypass" && v != "Unrestricted" && v != "RemoteSigned"; }).ToList();
            if (gpo.Count > 0) rep.Add("pwsh.policy", CheckLevel.Fail, "a Group Policy forces the PowerShell execution policy (" + string.Join(", ", gpo) + "), which blocks the unsigned bake script", string.Join("; ", pol));
            else if (lang != "FullLanguage") rep.Add("pwsh.policy", CheckLevel.Fail, "PowerShell runs in " + lang + " (Smart App Control / application control policy); the bake script needs FullLanguage", string.Join("; ", pol));
            else rep.Add("pwsh.policy", CheckLevel.Ok, "PowerShell: FullLanguage, execution policy not forced", string.Join("; ", pol));
        }

        /// <summary>
        /// Read-only: does Windows' per-app graphics preference (Settings > Display > Graphics, stored as
        /// HKCU\Software\Microsoft\DirectX\UserGpuPreferences "&lt;exe path&gt;" = "GpuPreference=2;") give
        /// <paramref name="exePath"/> the high-performance GPU? Used when gpu.hybrid failed: a bake may go ahead when every
        /// worker folder's fullgpu.exe it will run has that preference.
        /// </summary>
        public static bool HighPerformancePreferenceSet(string exePath)
        {
            try
            {
                using (var k = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\DirectX\UserGpuPreferences", false))
                {
                    if (k == null) return false;
                    string full = Path.GetFullPath(exePath);
                    foreach (var name in k.GetValueNames())
                    {
                        if (!string.Equals(name, full, StringComparison.OrdinalIgnoreCase)) continue;
                        var v = k.GetValue(name) as string;
                        return v != null && v.Replace(" ", "").IndexOf("GpuPreference=2;", StringComparison.OrdinalIgnoreCase) >= 0;
                    }
                }
            }
            catch (Exception e) when (e is System.Security.SecurityException || e is IOException || e is UnauthorizedAccessException || e is ArgumentException) { }
            return false;
        }

        // ------------------------------------------------------------------ the one write preflight makes

        public enum BoundsDbAction { Copied, AlreadyPresent, NoShippedCopy }

        public sealed class BoundsDbResult
        {
            public BoundsDbAction Action;
            public string Path, Md5, Message;
        }

        /// <summary>FINAL_DESIGN section 1/8: copy the shipped h2v_scenery_bounds.db into ROOT only if ROOT has none; an
        /// existing file is NEVER overwritten (whatever its md5). The copy goes through a temp file and a no-replace rename,
        /// so a concurrent writer can never be clobbered either.</summary>
        public static BoundsDbResult EnsureBoundsDb(string root, string shippedDb)
        {
            string dst = Path.Combine(root, BoundsDbName);
            if (File.Exists(dst))
            {
                string m = SafeFs.Md5File(dst);
                return new BoundsDbResult { Action = BoundsDbAction.AlreadyPresent, Path = dst, Md5 = m, Message = "kept the existing " + BoundsDbName + " (" + m.Substring(0, 8) + ")" };
            }
            if (shippedDb == null || !File.Exists(shippedDb))
                return new BoundsDbResult { Action = BoundsDbAction.NoShippedCopy, Path = dst, Message = "the payload has no " + BoundsDbName };
            string tmp = dst + ".h2lm_tmp_" + Guid.NewGuid().ToString("N").Substring(0, 8);
            string md5 = SafeFs.CopyVerified(shippedDb, tmp);
            try { SafeFs.MoveNoReplace(tmp, dst); }
            catch
            {
                SafeFs.TryDeleteFile(tmp);
                if (File.Exists(dst)) return new BoundsDbResult { Action = BoundsDbAction.AlreadyPresent, Path = dst, Md5 = SafeFs.Md5File(dst), Message = "another process created " + BoundsDbName + " first; kept it" };
                throw;
            }
            return new BoundsDbResult { Action = BoundsDbAction.Copied, Path = dst, Md5 = md5, Message = "copied the shipped " + BoundsDbName + " (" + md5.Substring(0, 8) + ") into " + root };
        }

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool GetVolumeInformationW(string root, StringBuilder name, int nameSize, out uint serial, out uint maxComp, out uint flags, StringBuilder fs, int fsSize);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool GetDiskFreeSpaceExW(string dir, out ulong freeToCaller, out ulong total, out ulong totalFree);
    }
}
