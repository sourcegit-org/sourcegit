using System;

namespace SourceGit.Models
{
    /// <summary>
    ///     Per-platform font defaults.
    ///
    ///     The embedded Inter font is used so every build renders western text identically, but it
    ///     carries no CJK coverage: Chinese characters are then filled in by whatever family the
    ///     platform happens to fall back to, which leaves mixed-script lines with a mismatched
    ///     baseline and weight. Naming an explicit CJK family per platform fixes that without
    ///     embedding a CJK font (which would add 8-15 MB to every build).
    ///
    ///     Only used when the corresponding preference is empty, so whatever the user typed in
    ///     Preferences > Appearance always wins.
    /// </summary>
    public static class PlatformFonts
    {
        /// <summary>UI font for western text, followed by the CJK family to fall back to.</summary>
        public static string DefaultFamily
        {
            get
            {
                if (OperatingSystem.IsWindows())
                    return "Segoe UI,Microsoft YaHei UI";

                if (OperatingSystem.IsMacOS())
                    return "Helvetica Neue,PingFang SC";

                return "Noto Sans,Noto Sans CJK SC,Source Han Sans SC,DejaVu Sans";
            }
        }

        /// <summary>
        ///     Tail of the monospace list: JetBrains Mono NL is embedded and comes first, these only
        ///     cover the characters it does not have (CJK, and a last-resort system fallback).
        /// </summary>
        public static string MonospaceFallback
        {
            get
            {
                if (OperatingSystem.IsWindows())
                    return "Cascadia Mono,Consolas,Microsoft YaHei UI";

                if (OperatingSystem.IsMacOS())
                    return "Menlo,Monaco,PingFang SC";

                return "Noto Sans Mono CJK SC,DejaVu Sans Mono";
            }
        }

        /// <summary>
        ///     Family used for controls that do not set a font themselves (Avalonia's
        ///     FontManagerOptions only takes a single name, so this is the first family above).
        /// </summary>
        public static string PrimaryFamily
        {
            get
            {
                var comma = DefaultFamily.IndexOf(',');
                return comma < 0 ? DefaultFamily : DefaultFamily.Substring(0, comma).Trim();
            }
        }

        /// <summary>
        ///     Same DIP size reads larger on Linux: fontconfig resolves a slightly bigger x-height
        ///     than DirectWrite, so 13 there looks like 14+ does on Windows. 12 keeps both builds
        ///     visually similar at the default 100% scaling.
        /// </summary>
        public static double DefaultFontSize => OperatingSystem.IsLinux() ? 12.0 : 13.0;
    }
}
