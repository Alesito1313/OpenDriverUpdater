using System;
using System.Linq;
using System.Text.RegularExpressions;

namespace OpenDriverUpdater.Core
{
    static class Versions
    {
        static readonly Regex Num = new Regex(@"\d+(?:\.\d+){1,3}", RegexOptions.Compiled);

        // "Intel - Display - 31.0.101.5590" -> "31.0.101.5590" (se queda con la ultima)
        public static string Extract(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return "";
            var m = Num.Matches(text);
            return m.Count > 0 ? m[m.Count - 1].Value : "";
        }

        public static Version Parse(string text)
        {
            var s = Extract(text);
            if (s.Length == 0 || !Version.TryParse(s, out var v)) return null;
            // 1.2 y 1.2.0.0 tienen que ser iguales
            return new Version(v.Major, v.Minor, Math.Max(0, v.Build), Math.Max(0, v.Revision));
        }

        /// <returns>&gt;0 si a es mas nueva, &lt;0 si es mas vieja, null si no se pueden comparar</returns>
        public static int? Compare(string a, string b)
        {
            var va = Parse(a);
            var vb = Parse(b);
            if (va == null || vb == null) return null;
            return va.CompareTo(vb);
        }

        // NVIDIA: la version de Windows 32.0.15.6094 es la 560.94 de su web
        public static string NvidiaFromWindows(string windowsVersion)
        {
            if (string.IsNullOrWhiteSpace(windowsVersion)) return null;
            var p = windowsVersion.Split('.');
            if (p.Length < 4 || !p[2].All(char.IsDigit) || !p[3].All(char.IsDigit)) return null;
            var digits = p[2] + p[3].PadLeft(4, '0');
            if (digits.Length < 5) return null;
            var last5 = digits.Substring(digits.Length - 5);
            return int.Parse(last5.Substring(0, 3)) + "." + last5.Substring(3);
        }
    }
}
