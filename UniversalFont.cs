namespace Atlas
{
    using System;
    using System.IO;
    using System.Runtime.InteropServices;
    using ClickableTransparentOverlay;
    using GameHelper;
    using ImGuiNET;

    /// <summary>
    ///     Loads the plugin's bundled fonts into the GameHelper overlay so map names in ANY language
    ///     render, without the user configuring a font in GH. Self-contained: fonts live in the
    ///     plugin folder and we drive <see cref="GameOverlay"/> directly, so it works on a vanilla GH.
    ///
    ///     Atlas builds one merged font (first source that has a glyph wins; merged sources only fill
    ///     gaps):
    ///       1. DejaVuSans      — pretty Latin + Cyrillic + Greek (priority for those).
    ///       2. the user's GH font (if present) — keeps their language pretty (e.g. CJK).
    ///       3. GNU Unifont     — fallback over the whole BMP (CJK/Arabic/Hebrew/Thai/Armenian/…).
    /// </summary>
    internal static unsafe class UniversalFont
    {
        // Whole Basic Multilingual Plane for the Unifont fallback. ImGui keeps this pointer until the
        // atlas is built (on the render thread), so the array must stay pinned for the app lifetime.
        private static readonly ushort[] FullBmpRange = { 0x0020, 0xFFFF, 0x0000 };
        private static GCHandle rangeHandle;
        private static bool applied;

        public static void Apply(string dllDirectory)
        {
            if (Core.Overlay == null)
                return;

            var fontsDir = Path.Join(dllDirectory, "fonts");
            var dejaVu = Path.Join(fontsDir, "DejaVuSans.ttf");
            var unifont = Path.Join(fontsDir, "unifont.ttf");
            if (!File.Exists(unifont) && !File.Exists(dejaVu))
                return; // no bundled fonts — leave the overlay untouched

            if (!rangeHandle.IsAllocated)
                rangeHandle = GCHandle.Alloc(FullBmpRange, GCHandleType.Pinned);
            var fullBmpPtr = rangeHandle.AddrOfPinnedObject();

            float size = Core.GHSettings.FontSize;
            var userFont = Core.GHSettings.FontPathName;
            var userLang = Core.GHSettings.FontLanguage;

            Core.Overlay.ReplaceFont(cfgRaw =>
            {
                var io = ImGui.GetIO();
                var fonts = io.Fonts;
                fonts.Clear(); // start from a known-empty atlas regardless of prior state
                var cfg = new ImFontConfigPtr(cfgRaw);
                cfg.SizePixels = size;

                bool haveBase = false;

                // 1) DejaVuSans — Latin + Cyrillic + Greek.
                if (File.Exists(dejaVu))
                {
                    cfg.MergeMode = false;
                    fonts.AddFontFromFileTTF(dejaVu, size, cfg, fonts.GetGlyphRangesCyrillic());
                    haveBase = true;
                }

                // 2) The user's configured GH font (keeps their language pretty), merged so it only
                //    fills glyphs DejaVu lacks. Skip if it's missing or the same file as DejaVu.
                if (!string.IsNullOrEmpty(userFont) && File.Exists(userFont) &&
                    !string.Equals(userFont, dejaVu, StringComparison.OrdinalIgnoreCase))
                {
                    cfg.MergeMode = haveBase;
                    fonts.AddFontFromFileTTF(userFont, size, cfg, RangeFor(fonts, userLang));
                    haveBase = true;
                }

                // 3) Unifont fallback over the whole BMP — fills everything still missing.
                if (File.Exists(unifont))
                {
                    cfg.MergeMode = haveBase;
                    fonts.AddFontFromFileTTF(unifont, size, cfg, fullBmpPtr);
                    haveBase = true;
                }

                if (!haveBase)
                    fonts.AddFontDefault();
                cfg.MergeMode = false;
            });

            applied = true;
        }

        /// <summary>Restore the overlay to the user's configured GH font (called on plugin disable).</summary>
        public static void Restore()
        {
            if (!applied || Core.Overlay == null)
                return;
            applied = false;
            Core.Overlay.ReplaceFont(Core.GHSettings.FontPathName, Core.GHSettings.FontSize, Core.GHSettings.FontLanguage);
        }

        private static IntPtr RangeFor(ImFontAtlasPtr fonts, FontGlyphRangeType lang) => lang switch
        {
            FontGlyphRangeType.ChineseSimplifiedCommon => fonts.GetGlyphRangesChineseSimplifiedCommon(),
            FontGlyphRangeType.ChineseFull => fonts.GetGlyphRangesChineseFull(),
            FontGlyphRangeType.Japanese => fonts.GetGlyphRangesJapanese(),
            FontGlyphRangeType.Korean => fonts.GetGlyphRangesKorean(),
            FontGlyphRangeType.Thai => fonts.GetGlyphRangesThai(),
            FontGlyphRangeType.Vietnamese => fonts.GetGlyphRangesVietnamese(),
            FontGlyphRangeType.Cyrillic => fonts.GetGlyphRangesCyrillic(),
            _ => fonts.GetGlyphRangesDefault(),
        };
    }
}
