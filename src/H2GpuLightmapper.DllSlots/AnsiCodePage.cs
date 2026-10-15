using System;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32;

namespace H2GpuLightmapper.DllSlots
{
    /// <summary>
    /// The ANSI code page that tool_fast.exe / inject.exe / gpulm_estimator.dll (all "A" API users) will run under.
    /// That is the SYSTEM ACP (HKLM\...\Nls\CodePage\ACP), which can differ from this process's GetACP() if the shell's
    /// manifest ever opts into activeCodePage=UTF-8. Every ANSI check in this library takes the code page explicitly so
    /// tests can exercise cp1252 / cp932 / cp65001 on any machine.
    /// </summary>
    public static class AnsiCodePage
    {
        [DllImport("kernel32.dll")]
        private static extern uint GetACP();

        /// <summary>System ANSI code page (registry), falling back to GetACP().</summary>
        public static int System
        {
            get
            {
                try
                {
                    using (var k = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\Nls\CodePage"))
                    {
                        var v = k?.GetValue("ACP") as string;
                        if (v != null && int.TryParse(v, out int cp) && cp > 0) return cp;
                    }
                }
                catch (Exception) { /* fall through */ }
                return (int)GetACP();
            }
        }

        /// <summary>An encoding for <paramref name="codePage"/> that THROWS on any unmappable character in either
        /// direction (no best-fit mapping, no '?' replacement).</summary>
        public static Encoding Strict(int codePage) =>
            Encoding.GetEncoding(codePage, EncoderFallback.ExceptionFallback, DecoderFallback.ExceptionFallback);

        /// <summary>True when <paramref name="s"/> encodes to <paramref name="codePage"/> without loss and decodes back
        /// to exactly the same string (ordinal).</summary>
        public static bool TryEncode(string s, int codePage, out byte[] bytes)
        {
            bytes = null;
            if (s == null) return false;
            try
            {
                var enc = Strict(codePage);
                var b = enc.GetBytes(s);
                if (!string.Equals(enc.GetString(b), s, StringComparison.Ordinal)) return false;
                bytes = b;
                return true;
            }
            catch (EncoderFallbackException) { return false; }
            catch (DecoderFallbackException) { return false; }
            catch (ArgumentException) { return false; }
            catch (NotSupportedException) { return false; }
        }

        public static bool RoundTrips(string s, int codePage) => TryEncode(s, codePage, out _);
    }
}
