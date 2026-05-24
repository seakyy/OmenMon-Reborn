  //\\   OmenMon: Hardware Monitoring & Control Utility
 //  \\  OmenMon-Reborn v1.5 — Variant B custom-drawn controls
     //  WinForms-only, GDI+ paint, zero NuGet

using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;
using OmenMon.Library;

namespace OmenMon.AppGui {

#region ModernGroupBox
    // Borderless dark "card" that pretends to be a GroupBox so existing
    // GuiFormMain.cs code (Controls.Find, Controls[index]) continues to work
    // unchanged. The original GroupBox chrome (border + caption) is suppressed.
    public class ModernGroupBox : GroupBox {

        public Color CardBackColor { get; set; } = GuiTheme.Bg2;
        public Color CardBorderColor { get; set; } = GuiTheme.Hairline;
        public int CornerRadius { get; set; } = 8;
        public bool DrawBorder { get; set; } = true;

        public ModernGroupBox() {
            this.SetStyle(
                ControlStyles.AllPaintingInWmPaint
                | ControlStyles.OptimizedDoubleBuffer
                | ControlStyles.UserPaint
                | ControlStyles.ResizeRedraw, true);
            this.BackColor = GuiTheme.Bg1;
            this.ForeColor = GuiTheme.Fg0;
            this.Font = GuiTheme.Text;
        }

        protected override void OnPaint(PaintEventArgs e) {
            // Skip base GroupBox chrome entirely.
            var g = e.Graphics;
            g.Clear(this.Parent != null ? this.Parent.BackColor : GuiTheme.Bg1);
            var r = new Rectangle(0, 0, this.Width - 1, this.Height - 1);
            GuiTheme.FillRoundedRect(g, r, CornerRadius, CardBackColor);
            if(DrawBorder)
                GuiTheme.StrokeRoundedRect(g, r, CornerRadius, CardBorderColor);
        }
    }
#endregion

#region ModernPanel
    // Generic rounded dark panel — same paint as ModernGroupBox without
    // the GroupBox inheritance.
    public class ModernPanel : Panel {

        public Color CardBackColor { get; set; } = GuiTheme.Bg2;
        public Color CardBorderColor { get; set; } = GuiTheme.Hairline;
        public int CornerRadius { get; set; } = 8;
        public bool DrawBorder { get; set; } = true;

        public ModernPanel() {
            this.SetStyle(
                ControlStyles.AllPaintingInWmPaint
                | ControlStyles.OptimizedDoubleBuffer
                | ControlStyles.UserPaint
                | ControlStyles.ResizeRedraw, true);
            this.BackColor = GuiTheme.Bg1;
            this.ForeColor = GuiTheme.Fg0;
            this.Font = GuiTheme.Text;
        }

        protected override void OnPaint(PaintEventArgs e) {
            var g = e.Graphics;
            g.Clear(this.Parent != null ? this.Parent.BackColor : GuiTheme.Bg1);
            var r = new Rectangle(0, 0, this.Width - 1, this.Height - 1);
            GuiTheme.FillRoundedRect(g, r, CornerRadius, CardBackColor);
            if(DrawBorder)
                GuiTheme.StrokeRoundedRect(g, r, CornerRadius, CardBorderColor);
        }
    }
#endregion

#region ModernTabButton
    // Single tab in the topbar — flat, with aurora underline when active.
    // Used as a Label so it doesn't grab focus and can be hooked via Click.
    public class ModernTabButton : Label {

        private bool _active;
        private bool _hover;

        public bool Active {
            get => _active;
            set { _active = value; Invalidate(); }
        }

        public ModernTabButton() {
            this.SetStyle(
                ControlStyles.AllPaintingInWmPaint
                | ControlStyles.OptimizedDoubleBuffer
                | ControlStyles.UserPaint
                | ControlStyles.ResizeRedraw, true);
            this.BackColor = GuiTheme.TopbarLo;
            this.ForeColor = GuiTheme.Fg2;
            this.Font = GuiTheme.Text;
            this.TextAlign = ContentAlignment.MiddleCenter;
            this.Cursor = Cursors.Hand;
        }

        protected override void OnMouseEnter(EventArgs e) { _hover = true; Invalidate(); base.OnMouseEnter(e); }
        protected override void OnMouseLeave(EventArgs e) { _hover = false; Invalidate(); base.OnMouseLeave(e); }

        protected override void OnPaint(PaintEventArgs e) {
            var g = e.Graphics;
            g.Clear(this.BackColor);

            // Hover / active background pill (radius 6).
            var pad = new Rectangle(2, 4, this.Width - 4, this.Height - 8);
            if(_active) {
                GuiTheme.FillRoundedRect(g, pad, 6, GuiTheme.TabActiveBg);
            } else if(_hover) {
                GuiTheme.FillRoundedRect(g, pad, 6, GuiTheme.TabHoverBg);
            }

            // Text
            var color = _active ? GuiTheme.Fg0 : (_hover ? GuiTheme.Fg0 : GuiTheme.Fg2);
            TextRenderer.DrawText(g, this.Text, this.Font, this.ClientRectangle, color,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);

            // Active underline — full-bleed 2px aurora line at the bottom.
            if(_active) {
                int y = this.Height - 2;
                using(var br = GuiTheme.AuroraBrush(new Rectangle(0, y, this.Width, 2), LinearGradientMode.Horizontal)) {
                    g.FillRectangle(br, new Rectangle(8, y, this.Width - 16, 2));
                }
            }
        }
    }
#endregion

#region ModernSliderH
    // Slim horizontal trackbar replacement. Used for fan-level sliders.
    public class ModernSliderH : Control {

        private int _min = 0, _max = 100, _value = 50;
        private bool _dragging;
        public Color FillColor { get; set; } = GuiTheme.Aurora1;
        public event EventHandler ValueChanged;

        public int Minimum { get => _min; set { _min = value; Invalidate(); } }
        public int Maximum { get => _max; set { _max = value; Invalidate(); } }
        public int Value {
            get => _value;
            set {
                int v = Math.Max(_min, Math.Min(_max, value));
                if(v == _value) return;
                _value = v;
                Invalidate();
                ValueChanged?.Invoke(this, EventArgs.Empty);
            }
        }

        public ModernSliderH() {
            this.SetStyle(
                ControlStyles.AllPaintingInWmPaint
                | ControlStyles.OptimizedDoubleBuffer
                | ControlStyles.UserPaint
                | ControlStyles.ResizeRedraw
                | ControlStyles.Selectable, true);
            this.BackColor = GuiTheme.Bg2;
            this.Height = 24;
            this.Cursor = Cursors.Hand;
            this.TabStop = true;
        }

        protected override void OnMouseDown(MouseEventArgs e) {
            base.OnMouseDown(e);
            if(!this.Enabled) return;
            _dragging = true;
            this.Capture = true;
            SetValueFromX(e.X);
        }

        protected override void OnMouseMove(MouseEventArgs e) {
            base.OnMouseMove(e);
            if(_dragging) SetValueFromX(e.X);
        }

        protected override void OnMouseUp(MouseEventArgs e) {
            base.OnMouseUp(e);
            _dragging = false;
            this.Capture = false;
        }

        private void SetValueFromX(int x) {
            int range = _max - _min;
            if(range <= 0 || this.Width <= 16) return;
            int usable = this.Width - 16;
            float pct = Math.Max(0, Math.Min(1, (x - 8f) / usable));
            this.Value = _min + (int)Math.Round(pct * range);
        }

        protected override void OnPaint(PaintEventArgs e) {
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(this.Parent != null ? this.Parent.BackColor : GuiTheme.Bg2);

            // Track
            int trackY = this.Height / 2 - 2;
            var track = new Rectangle(8, trackY, this.Width - 16, 4);
            GuiTheme.FillRoundedRect(g, track, 2, Color.FromArgb(this.Enabled ? 26 : 10, 0xFF, 0xFF, 0xFF));

            // Fill
            int range = _max - _min;
            if(range > 0) {
                float pct = (float)(_value - _min) / range;
                int w = (int)Math.Round((this.Width - 16) * pct);
                if(w > 0) {
                    var fill = new Rectangle(8, trackY, w, 4);
                    using(var br = new SolidBrush(this.Enabled ? FillColor : Color.FromArgb(160, FillColor))) {
                        var path = GuiTheme.RoundedRect(fill, 2);
                        g.FillPath(br, path);
                        path.Dispose();
                    }
                }

                // Thumb
                int thumbX = 8 + (int)Math.Round((this.Width - 16) * pct);
                int thumbR = 7;
                var thumb = new Rectangle(thumbX - thumbR, this.Height / 2 - thumbR, thumbR * 2, thumbR * 2);
                using(var br = new SolidBrush(this.Enabled ? GuiTheme.Fg0 : GuiTheme.Fg2))
                    g.FillEllipse(br, thumb);
                using(var pen = new Pen(this.Enabled ? FillColor : GuiTheme.Hairline, 2f))
                    g.DrawEllipse(pen, Rectangle.Inflate(thumb, -1, -1));
            }
        }
    }
#endregion

#region ModernSegmentButton
    // One segment in a horizontal segmented control. Visually a flat dark
    // pill that lights up when Checked. Designed to wrap an existing
    // RadioButton's behavior — set Checked from the radio's CheckedChanged.
    public class ModernSegmentButton : Label {

        private bool _checked;
        private bool _hover;
        public Color AccentColor { get; set; } = GuiTheme.Aurora1;

        public bool Checked {
            get => _checked;
            set { _checked = value; Invalidate(); }
        }

        public ModernSegmentButton() {
            this.SetStyle(
                ControlStyles.AllPaintingInWmPaint
                | ControlStyles.OptimizedDoubleBuffer
                | ControlStyles.UserPaint
                | ControlStyles.ResizeRedraw, true);
            this.BackColor = GuiTheme.Bg2;
            this.ForeColor = GuiTheme.Fg1;
            this.Font = GuiTheme.Text;
            this.TextAlign = ContentAlignment.MiddleCenter;
            this.Cursor = Cursors.Hand;
        }

        protected override void OnMouseEnter(EventArgs e) { _hover = true; Invalidate(); base.OnMouseEnter(e); }
        protected override void OnMouseLeave(EventArgs e) { _hover = false; Invalidate(); base.OnMouseLeave(e); }

        protected override void OnPaint(PaintEventArgs e) {
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(this.Parent != null ? this.Parent.BackColor : GuiTheme.Bg2);

            var rect = new Rectangle(0, 0, this.Width - 1, this.Height - 1);
            Color bg = _checked
                ? Color.FromArgb(20, AccentColor.R, AccentColor.G, AccentColor.B)
                : (_hover ? GuiTheme.TabHoverBg : GuiTheme.Bg3);
            Color border = _checked ? Color.FromArgb(90, AccentColor.R, AccentColor.G, AccentColor.B)
                                    : GuiTheme.Hairline;

            GuiTheme.FillRoundedRect(g, rect, 6, bg);
            GuiTheme.StrokeRoundedRect(g, rect, 6, border);

            Color textColor = _checked ? AccentColor : (_hover ? GuiTheme.Fg0 : GuiTheme.Fg1);
            TextRenderer.DrawText(g, this.Text, this.Font, this.ClientRectangle, textColor,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
        }
    }
#endregion

#region ModernCaptionButton
    // Custom min/max/close in the topbar. Standard hover (white@6%), close
    // hover is Microsoft's #C42B1C red.
    public class ModernCaptionButton : Control {

        public enum Kind { Min, Max, Close }
        private Kind _kind;
        private bool _hover;

        public ModernCaptionButton(Kind k) {
            _kind = k;
            this.SetStyle(
                ControlStyles.AllPaintingInWmPaint
                | ControlStyles.OptimizedDoubleBuffer
                | ControlStyles.UserPaint
                | ControlStyles.ResizeRedraw, true);
            this.BackColor = GuiTheme.TopbarLo;
            this.Width = 36;
            this.Cursor = Cursors.Hand;
        }

        protected override void OnMouseEnter(EventArgs e) { _hover = true; Invalidate(); base.OnMouseEnter(e); }
        protected override void OnMouseLeave(EventArgs e) { _hover = false; Invalidate(); base.OnMouseLeave(e); }

        protected override void OnPaint(PaintEventArgs e) {
            var g = e.Graphics;
            g.Clear(this.BackColor);
            if(_hover) {
                Color hb = _kind == Kind.Close
                    ? Color.FromArgb(0xFF, 0xC4, 0x2B, 0x1C)
                    : Color.FromArgb(15, 0xFF, 0xFF, 0xFF);
                using(var br = new SolidBrush(hb)) g.FillRectangle(br, this.ClientRectangle);
            }
            int cx = this.Width / 2, cy = this.Height / 2;
            Color stroke = _hover && _kind == Kind.Close ? Color.White : GuiTheme.Fg1;
            using(var pen = new Pen(stroke, 1f)) {
                g.SmoothingMode = SmoothingMode.None;
                switch(_kind) {
                    case Kind.Min:
                        g.DrawLine(pen, cx - 5, cy, cx + 5, cy);
                        break;
                    case Kind.Max:
                        g.DrawRectangle(pen, cx - 5, cy - 5, 10, 10);
                        break;
                    case Kind.Close:
                        g.SmoothingMode = SmoothingMode.AntiAlias;
                        g.DrawLine(pen, cx - 5, cy - 5, cx + 5, cy + 5);
                        g.DrawLine(pen, cx + 5, cy - 5, cx - 5, cy + 5);
                        break;
                }
            }
        }
    }
#endregion

#region ModernSensorTile
    // Big sensor card with eyebrow (register code), reg-name, large numeric
    // value, unit, and a thin gradient bar at the bottom. Stateless paint —
    // call SetReading(...) and it invalidates.
    public class ModernSensorTile : ModernPanel {

        public string RegCode { get; set; } = "----";
        public string DisplayName { get; set; } = "Sensor";
        public int Value { get; set; }            // 0 = no reading
        public string Unit { get; set; } = "°C";
        public bool UseAsTemperature { get; set; } = true;
        public bool IsActive { get; set; } = true;
        public Font ValueFont { get; set; }       // optional override (e.g. IoMon FigureFont)

        public ModernSensorTile() {
            this.CornerRadius = 10;
            this.Padding = new Padding(14);
            this.MinimumSize = new Size(120, 110);
        }

        public void SetReading(int value, bool active = true) {
            this.Value = value;
            this.IsActive = active;
            Invalidate();
        }

        protected override void OnPaint(PaintEventArgs e) {
            base.OnPaint(e);
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;

            int padX = 14, padY = 12;
            int w = this.Width, h = this.Height;

            // Header row: reg code (mono) + status dot
            int dotY = padY + 4;
            int dotR = 4;
            Color dotColor = IsActive
                ? (UseAsTemperature && Value > 0 ? GuiTheme.TempColor(Value) : GuiTheme.Aurora1)
                : GuiTheme.Fg3;
            using(var br = new SolidBrush(dotColor))
                g.FillEllipse(br, w - padX - dotR * 2 - 4, dotY, dotR * 2, dotR * 2);
            TextRenderer.DrawText(g, RegCode, GuiTheme.Mono,
                new Point(padX, padY), GuiTheme.Fg1,
                TextFormatFlags.NoPadding);

            // Sub-name (one line, ellipsised)
            var nameRect = new Rectangle(padX, padY + 14, w - padX * 2 - 12, 14);
            TextRenderer.DrawText(g, DisplayName, GuiTheme.TextSm, nameRect, GuiTheme.Fg2,
                TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding);

            // Big value + unit
            string valStr = (!IsActive || Value <= 0) ? "—" : Value.ToString();
            var f = ValueFont ?? GuiTheme.DisplayXl;
            var valSize = TextRenderer.MeasureText(g, valStr, f, new Size(0, 0), TextFormatFlags.NoPadding);
            int valX = padX;
            int valY = h - padY - 28 - valSize.Height + 18;
            TextRenderer.DrawText(g, valStr, f,
                new Point(valX, valY), IsActive ? GuiTheme.Fg0 : GuiTheme.Fg3,
                TextFormatFlags.NoPadding);
            if(IsActive && Value > 0) {
                TextRenderer.DrawText(g, Unit, GuiTheme.TextSm,
                    new Point(valX + valSize.Width + 4, valY + valSize.Height - 16),
                    GuiTheme.Fg2, TextFormatFlags.NoPadding);
            }

            // Thin temperature ramp bar
            int barY = h - padY - 6;
            var barBg = new Rectangle(padX, barY, w - padX * 2, 4);
            GuiTheme.FillRoundedRect(g, barBg, 2, Color.FromArgb(26, 0xFF, 0xFF, 0xFF));
            if(IsActive && UseAsTemperature && Value > 0) {
                int pct = Math.Min(100, Math.Max(0, Value));
                int fillW = barBg.Width * pct / 100;
                if(fillW > 2) {
                    var fillR = new Rectangle(padX, barY, fillW, 4);
                    using(var br = GuiTheme.TempRampBrush(new Rectangle(padX, barY, barBg.Width, 4))) {
                        var path = GuiTheme.RoundedRect(fillR, 2);
                        g.FillPath(br, path);
                        path.Dispose();
                    }
                }
            }
        }
    }
#endregion

}
