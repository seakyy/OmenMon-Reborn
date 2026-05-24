  //\\   OmenMon: Hardware Monitoring & Control Utility
 //  \\  OmenMon-Reborn v1.5 — Variant B "Tabbed Modern" theme
     //  Dark surfaces + aurora accent, restrained, native-feeling

using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using OmenMon.Library;

namespace OmenMon.AppGui {

    // Centralised color, font, and surface tokens for the Variant B theme.
    // Re-uses the brand constants already in Config.GuiColor* so the aurora
    // gradient stays in sync with the in-app constants and ConfigData.cs.
    public static class GuiTheme {

#region Palette
        // Aurora — the brand gradient (Cool-Lite → Blue → Cool-Dark)
        public static readonly Color Aurora1 = Color.FromArgb(Config.GuiColorCoolLite);  // #03EF9B
        public static readonly Color AuroraBlue = Color.FromArgb(Config.GuiColorTextBlue); // #4182C9
        public static readonly Color Aurora3 = Color.FromArgb(Config.GuiColorCoolDark);  // #8804FF

        // Warm gradient (fan rate)
        public static readonly Color WarmLite = Color.FromArgb(Config.GuiColorWarmLite); // #AC02FF
        public static readonly Color WarmDark = Color.FromArgb(Config.GuiColorWarmDark); // #FF0802

        // Backgrounds
        public static readonly Color Bg0 = Color.FromArgb(0xFF, 0x07, 0x09, 0x0F);  // page
        public static readonly Color Bg1 = Color.FromArgb(0xFF, 0x0F, 0x14, 0x26);  // surface
        public static readonly Color Bg2 = Color.FromArgb(0xFF, 0x16, 0x1B, 0x30);  // raised card
        public static readonly Color Bg3 = Color.FromArgb(0xFF, 0x1E, 0x24, 0x40);  // popover / hover

        // Topbar gets a subtle vertical gradient — store the two stops.
        public static readonly Color TopbarHi = Color.FromArgb(0xFF, 0x0E, 0x13, 0x26);
        public static readonly Color TopbarLo = Color.FromArgb(0xFF, 0x0B, 0x0F, 0x1E);

        // Hairlines
        public static readonly Color Hairline = Color.FromArgb(20, 0xFF, 0xFF, 0xFF);       // ~8% white
        public static readonly Color HairlineStrong = Color.FromArgb(41, 0xFF, 0xFF, 0xFF); // ~16% white

        // Foregrounds
        public static readonly Color Fg0 = Color.FromArgb(0xFF, 0xF5, 0xF7, 0xFB);  // primary text
        public static readonly Color Fg1 = Color.FromArgb(0xFF, 0xC6, 0xCB, 0xDC);  // secondary
        public static readonly Color Fg2 = Color.FromArgb(0xFF, 0x8A, 0x90, 0xA8);  // tertiary / muted
        public static readonly Color Fg3 = Color.FromArgb(0xFF, 0x5A, 0x60, 0x75);  // deep muted

        // Semantic accents (temperature ladder)
        public static readonly Color TempCool = Color.FromArgb(0xFF, 0x03, 0xEF, 0x9B); // 0-50
        public static readonly Color TempMid  = Color.FromArgb(0xFF, 0x41, 0x82, 0xC9); // 50-65
        public static readonly Color TempWarm = Color.FromArgb(0xFF, 0xFD, 0xE0, 0x05); // 65-75
        public static readonly Color TempHot  = Color.FromArgb(0xFF, 0xFE, 0x60, 0x06); // 75-85
        public static readonly Color TempCrit = Color.FromArgb(0xFF, 0xFF, 0x08, 0x02); // 85+

        // Tab active background (aurora at 8%)
        public static readonly Color TabActiveBg = Color.FromArgb(20, 0x03, 0xEF, 0x9B);
        public static readonly Color TabHoverBg  = Color.FromArgb(8,  0xFF, 0xFF, 0xFF);
#endregion

#region Fonts
        // Lazy-initialised so a missing font on the build machine
        // doesn't crash the app at static-init time.
        private static Font _displaySm, _displayMd, _displayLg, _displayXl, _text, _textSm, _mono, _monoSm;

        // Display = Segoe UI Semibold (Inter/Space Grotesk would need to ship
        // a TTF; we stay on system fonts to keep zero-NuGet promise)
        public static Font DisplaySm => _displaySm ?? (_displaySm = SafeFont("Segoe UI Semibold", 11f, FontStyle.Regular));
        public static Font DisplayMd => _displayMd ?? (_displayMd = SafeFont("Segoe UI Semibold", 13f, FontStyle.Regular));
        public static Font DisplayLg => _displayLg ?? (_displayLg = SafeFont("Segoe UI Semibold", 15f, FontStyle.Regular));
        public static Font DisplayXl => _displayXl ?? (_displayXl = SafeFont("Segoe UI Semibold", 22f, FontStyle.Bold));

        public static Font Text   => _text   ?? (_text   = SafeFont("Segoe UI", 9.25f, FontStyle.Regular));
        public static Font TextSm => _textSm ?? (_textSm = SafeFont("Segoe UI", 8.25f, FontStyle.Regular));

        public static Font Mono   => _mono   ?? (_mono   = SafeFont("Consolas", 8.5f, FontStyle.Bold));
        public static Font MonoSm => _monoSm ?? (_monoSm = SafeFont("Consolas", 7.5f, FontStyle.Bold));

        // Returns the named font if installed, else falls back to MS Shell Dlg.
        // (Segoe UI ships with every supported Windows; this is defensive.)
        private static Font SafeFont(string family, float size, FontStyle style) {
            try {
                using(var ff = new FontFamily(family)) {
                    return new Font(ff, size, style, GraphicsUnit.Point);
                }
            } catch {
                return new Font(Gui.DIALOG_FONT, size, style, GraphicsUnit.Point);
            }
        }
#endregion

#region Brushes & Pens
        // Caller is responsible for disposing each returned object.
        public static LinearGradientBrush AuroraBrush(Rectangle r,
            LinearGradientMode mode = LinearGradientMode.ForwardDiagonal) {
            var b = new LinearGradientBrush(r, Aurora1, Aurora3, mode);
            var blend = new ColorBlend(3);
            blend.Colors    = new[] { Aurora1, AuroraBlue, Aurora3 };
            blend.Positions = new[] { 0f,      0.5f,        1f };
            b.InterpolationColors = blend;
            return b;
        }

        public static LinearGradientBrush TempRampBrush(Rectangle r) {
            var b = new LinearGradientBrush(r, TempCool, TempCrit, LinearGradientMode.Horizontal);
            var blend = new ColorBlend(5);
            blend.Colors    = new[] { TempCool, TempMid, TempWarm, TempHot, TempCrit };
            blend.Positions = new[] { 0f, 0.35f, 0.65f, 0.82f, 1f };
            b.InterpolationColors = blend;
            return b;
        }

        // Pick a single colour out of the temperature ramp for a given °C value.
        public static Color TempColor(int celsius) {
            if(celsius >= 85) return TempCrit;
            if(celsius >= 75) return TempHot;
            if(celsius >= 65) return TempWarm;
            if(celsius >= 50) return TempMid;
            return TempCool;
        }
#endregion

#region Drawing helpers
        // Rounded rectangle path. Caller must dispose.
        public static GraphicsPath RoundedRect(Rectangle r, int radius) {
            var p = new GraphicsPath();
            if(radius <= 0) {
                p.AddRectangle(r);
                p.CloseFigure();
                return p;
            }
            int d = radius * 2;
            p.AddArc(r.X,                 r.Y,                 d, d, 180, 90);
            p.AddArc(r.Right  - d,        r.Y,                 d, d, 270, 90);
            p.AddArc(r.Right  - d,        r.Bottom - d,        d, d,   0, 90);
            p.AddArc(r.X,                 r.Bottom - d,        d, d,  90, 90);
            p.CloseFigure();
            return p;
        }

        // Fills a rounded rectangle with the given solid colour.
        public static void FillRoundedRect(Graphics g, Rectangle r, int radius, Color fill) {
            using(var path = RoundedRect(r, radius))
            using(var b = new SolidBrush(fill)) {
                var old = g.SmoothingMode;
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.FillPath(b, path);
                g.SmoothingMode = old;
            }
        }

        // Strokes a 1-px rounded border in the given colour.
        public static void StrokeRoundedRect(Graphics g, Rectangle r, int radius, Color stroke) {
            using(var path = RoundedRect(r, radius))
            using(var pen = new Pen(stroke, 1f)) {
                var old = g.SmoothingMode;
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.DrawPath(pen, path);
                g.SmoothingMode = old;
            }
        }
#endregion

#region Window chrome (DWM dark titlebar)
        // DWM attribute used to switch the titlebar/system chrome to dark mode.
        // Build 20H1 (19041) uses 19; 22H2+ uses 20. Try 20 first, fall back to 19.
        // Silently no-ops on Win10 < 19H1 / Win7 where the attribute doesn't exist.
        public static void EnableDarkTitlebar(IntPtr hwnd) {
            if(hwnd == IntPtr.Zero) return;
            int dark = 1;
            try { DwmSetWindowAttribute(hwnd, 20, ref dark, sizeof(int)); }
            catch { /* unsupported — silently degrade */ }
            try { DwmSetWindowAttribute(hwnd, 19, ref dark, sizeof(int)); }
            catch { /* unsupported — silently degrade */ }
        }

        [DllImport("dwmapi.dll", PreserveSig = true)]
        private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int attrValue, int attrSize);
#endregion

    }

}
