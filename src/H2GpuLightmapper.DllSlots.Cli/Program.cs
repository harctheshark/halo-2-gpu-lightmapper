using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace H2GpuLightmapper.DllSlots.Cli
{
    /// <summary>
    /// h2lm-slots.exe -- exit 0 ok, 1 verify mismatch, 2 refused (bad input / unsafe), 3 unexpected error. JSON on stdout.
    ///   layout  &lt;dll&gt;                                         identify a pristine or patched estimator DLL
    ///   check-base &lt;base&gt;                                    validate a slot base (ends with '\', &lt;= 60 ANSI bytes)
    ///   patch   &lt;pristine-src&gt; &lt;dst&gt; &lt;base&gt; [--overwrite]   write a slot-patched COPY (never in place)
    ///   verify  &lt;dll&gt; &lt;base&gt;                                  slots hold base and nothing else differs from pristine
    ///   plan    &lt;work-dir&gt; [--localappdata P] [--volume-root R] pure: which slot base WORK gets
    ///   ensure-alias &lt;work-dir&gt; [--localappdata P] [--volume-root R]   create / reuse the junction
    ///   remove-junction &lt;path&gt; --expect-target &lt;dir&gt;         RemoveDirectory on a junction only
    /// Every verb takes [--cp N] (ANSI code page; default = the system ACP).
    /// </summary>
    internal static class Program
    {
        private static int Main(string[] argv)
        {
            try
            {
                var pos = new List<string>();
                var opt = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                for (int i = 0; i < argv.Length; i++)
                {
                    if (argv[i] == "--overwrite") opt["overwrite"] = "1";
                    else if (argv[i].StartsWith("--", StringComparison.Ordinal) && i + 1 < argv.Length) opt[argv[i].Substring(2)] = argv[++i];
                    else pos.Add(argv[i]);
                }
                int? cp = opt.TryGetValue("cp", out var cps) ? int.Parse(cps, CultureInfo.InvariantCulture) : (int?)null;
                string verb = pos.Count > 0 ? pos[0] : "";
                switch (verb)
                {
                    case "layout":
                        {
                            Need(pos, 2);
                            var b = System.IO.File.ReadAllBytes(pos[1]);
                            var id = DllSlotPatcher.Identify(b, null, cp);
                            if (id == null) { Console.WriteLine("{\"md5\":\"" + DllSlotPatcher.Md5Hex(b) + "\",\"known\":false}"); return 2; }
                            Console.WriteLine("{\"md5\":\"" + id.Md5 + "\",\"known\":true,\"pristine\":" + (id.IsPristine ? "true" : "false") +
                                              ",\"base\":" + Q(id.Base) + ",\"layout\":" + id.Layout.ToJson() + "}");
                            return 0;
                        }
                    case "check-base":
                        {
                            Need(pos, 2);
                            int c = cp ?? AnsiCodePage.System;
                            bool ok = DllSlotPatcher.TryValidateBase(pos[1], c, out var bytes, out var why);
                            Console.WriteLine("{\"base\":" + Q(pos[1]) + ",\"code_page\":" + c + ",\"ok\":" + (ok ? "true" : "false") +
                                              ",\"bytes\":" + (ok ? bytes.Length.ToString(CultureInfo.InvariantCulture) : "null") + ",\"reason\":" + Q(why) + "}");
                            return ok ? 0 : 2;
                        }
                    case "patch":
                        {
                            Need(pos, 4);
                            var r = DllSlotPatcher.PatchFile(pos[1], pos[2], pos[3], null, opt.ContainsKey("overwrite"), cp);
                            Console.WriteLine(r.ToJson());
                            return 0;
                        }
                    case "verify":
                        {
                            Need(pos, 3);
                            bool ok = DllSlotPatcher.VerifyFile(pos[1], pos[2], out var why, null, cp);
                            Console.WriteLine("{\"ok\":" + (ok ? "true" : "false") + ",\"reason\":" + Q(why) + "}");
                            return ok ? 0 : 1;
                        }
                    case "plan":
                        {
                            Need(pos, 2);
                            var p = SlotAliasPlanner.Plan(pos[1], Lad(opt), opt.TryGetValue("volume-root", out var vr) ? vr : null, cp);
                            Console.WriteLine("{\"work\":" + Q(p.WorkDir) + ",\"key6\":" + Q(p.Key6) + ",\"code_page\":" + p.CodePage +
                                              ",\"work_is_ansi\":" + (p.WorkDirIsAnsi ? "true" : "false") + ",\"kind\":" + Q(p.Kind.ToString()) +
                                              ",\"slot_base\":" + Q(p.SlotBase) + ",\"alias_dir\":" + Q(p.AliasDir) +
                                              ",\"candidates\":[" + string.Join(",", p.Candidates.Select(c => "{\"kind\":" + Q(c.Kind.ToString()) + ",\"slot_base\":" + Q(c.SlotBase) + "}")) +
                                              "],\"rejected\":[" + string.Join(",", p.Rejected.Select(Q)) + "]}");
                            return 0;
                        }
                    case "ensure-alias":
                        {
                            Need(pos, 2);
                            var r = SlotAlias.Ensure(pos[1], Lad(opt), opt.TryGetValue("volume-root", out var vr) ? vr : null, cp);
                            Console.WriteLine(r.ToJson());
                            return 0;
                        }
                    case "remove-junction":
                        {
                            Need(pos, 2);
                            if (!opt.TryGetValue("expect-target", out var t)) throw new ArgumentException("--expect-target <dir> is required");
                            Junction.Remove(pos[1], t);
                            Console.WriteLine("{\"removed\":" + Q(pos[1]) + "}");
                            return 0;
                        }
                    default:
                        Console.Error.WriteLine("usage: h2lm-slots layout|check-base|patch|verify|plan|ensure-alias|remove-junction ... (see Program.cs)");
                        return 2;
                }
            }
            catch (Exception e) when (e is SlotPatchException || e is SlotBaseException || e is SlotAliasRefusedException || e is JunctionException || e is ArgumentException)
            {
                Console.WriteLine("{\"refused\":" + Q(e.Message) + "}");
                return 2;
            }
            catch (Exception e)
            {
                Console.WriteLine("{\"error\":" + Q(e.GetType().Name + ": " + e.Message) + "}");
                return 3;
            }
        }

        private static string Lad(Dictionary<string, string> opt) =>
            opt.TryGetValue("localappdata", out var l) ? l : Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);

        private static void Need(List<string> pos, int n) { if (pos.Count < n) throw new ArgumentException("missing arguments for '" + pos[0] + "'"); }

        private static string Q(string s)
        {
            if (s == null) return "null";
            var sb = new System.Text.StringBuilder("\"");
            foreach (char c in s)
            {
                if (c == '"' || c == '\\') sb.Append('\\').Append(c);
                else if (c < 0x20 || c > 0x7E) sb.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
                else sb.Append(c);
            }
            return sb.Append('"').ToString();
        }
    }
}
