  //\\   OmenMon: Hardware Monitoring & Control Utility
 //  \\  Copyright © 2023-2024 Piotr Szczepański * License: GPL3
     //  https://omenmon.github.io/
// OmenMon-Reborn additions © 2026 seakyy

using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;
using OmenMon.Hardware.Platform;

namespace OmenMon.AppGui {

    // GDI+ canvas that draws and edits a fan curve.
    //
    // The control owns no configuration state: it edits the List<FanCurvePoint> it is
    // handed, and raises PointsChanged once per completed gesture. Persisting that list
    // (and deciding whether a running program needs restarting) is the dialog's job.
    //
    // The curve is painted STEPWISE rather than as straight segments, because that is
    // how FanProgram.GetTemperatureLevel actually resolves a temperature: it floors to
    // the nearest defined threshold. A smooth line would misrepresent real behaviour.
    public class GuiFanCurveCanvas : Panel {

#region Constants
        private const int MarginLeft = 46;
        private const int MarginRight = 14;
        private const int MarginTop = 14;
        private const int MarginBottom = 28;

        private const int HandleRadius = 5;
        private const int HitRadius = 8;

        // Upper bound of the X axis. Slightly above the schema ceiling so that a node
        // sitting at the maximum temperature is not glued to the right edge.
        private const int AxisTemperatureMax = 100;

        private static readonly Color ColorCpu = Color.FromArgb(0xD9, 0x53, 0x2B);
        private static readonly Color ColorGpu = Color.FromArgb(0x2B, 0x7F, 0xD9);
        private static readonly Color ColorGrid = Color.FromArgb(0xE0, 0xE0, 0xE0);
        private static readonly Color ColorAxis = Color.FromArgb(0x90, 0x90, 0x90);
#endregion

#region Data
        // The points being edited. Sorted ascending by temperature on every commit.
        public List<FanCurvePoint> Points { get; private set; }

        // Highest level the user may set. Usually Config.FanLevelMax, lowered by one
        // step on boards in FanArray.HasMaxFanFreeze when the override is not confirmed.
        public int LevelMax { get; private set; }

        // Live temperatures painted as vertical markers, or -1 when unknown
        public int CpuTemperature { get; set; }
        public int GpuTemperature { get; set; }

        // Raised after a gesture that changed the points
        public event EventHandler PointsChanged;

        // Raised when the point under the cursor changes
        public event EventHandler SelectionChanged;

        // Index of the selected point, or -1
        public int SelectedIndex { get; private set; }

        // Which series the last interaction addressed
        public bool IsCpuSelected { get; private set; }

        private int DragIndex;
        private bool DragCpu;
        private bool Dragging;

        public GuiFanCurveCanvas() {

            this.Points = new List<FanCurvePoint>();
            this.LevelMax = FanCurve.LevelMaxDefault;
            this.CpuTemperature = -1;
            this.GpuTemperature = -1;
            this.SelectedIndex = -1;
            this.DragIndex = -1;

            // Selectable makes the panel focusable so OnKeyDown receives the arrow-key
            // nudges; a bare Panel is not focusable by default.
            SetStyle(ControlStyles.AllPaintingInWmPaint
                   | ControlStyles.OptimizedDoubleBuffer
                   | ControlStyles.UserPaint
                   | ControlStyles.ResizeRedraw
                   | ControlStyles.Selectable, true);

            this.TabStop = true;
            this.BackColor = Color.White;
            this.Cursor = Cursors.Cross;

        }
#endregion

#region Data Management
        // Replaces the edited points. Values are clamped into the current domain so a
        // curve loaded from a file can never put the control into an unrenderable state.
        public void SetPoints(IEnumerable<FanCurvePoint> points, int levelMax) {

            this.LevelMax = levelMax < FanCurve.LevelMin ? FanCurve.LevelMaxDefault : levelMax;
            this.Points = new List<FanCurvePoint>();

            if(points != null)
                foreach(FanCurvePoint point in points) {

                    FanCurvePoint sanitized = Sanitize(point);

                    // Clamping can collapse two out-of-range thresholds onto the same
                    // temperature; keep the first one rather than emitting a curve the
                    // SortedDictionary would reject
                    if(this.Points.Count > 0 && IndexOfTemperature(sanitized.Temperature) >= 0)
                        continue;

                    this.Points.Add(sanitized);

                }

            SortPoints();

            this.SelectedIndex = -1;
            this.DragIndex = -1;
            this.Dragging = false;

            Invalidate();
            RaiseSelectionChanged();

        }

        // Applies a new level ceiling in place, clamping any point that now sits above it
        public void SetLevelMax(int levelMax) {

            this.LevelMax = levelMax < FanCurve.LevelMin ? FanCurve.LevelMaxDefault : levelMax;

            for(int i = 0; i < this.Points.Count; i++)
                this.Points[i] = Sanitize(this.Points[i]);

            Invalidate();

        }

        public FanCurvePoint GetSelected() {

            if(this.SelectedIndex < 0 || this.SelectedIndex >= this.Points.Count)
                return new FanCurvePoint(-1, 0, 0);

            return this.Points[this.SelectedIndex];

        }

        // Adds a point at the given temperature, taking the initial levels from the
        // curve as it stands so the new node never introduces a visual jump
        public void AddPoint(int temperature) {

            temperature = FanCurve.ClampTemperature(temperature);

            for(int i = 0; i < this.Points.Count; i++)
                if(this.Points[i].Temperature == temperature)
                    return; // Already a node here

            SortedDictionary<byte, byte[]> level = ToLevels();
            byte[] resolved = FanCurve.LookupLevels(level, (byte) temperature);

            int cpu = resolved == null ? 0 : resolved[0];
            int gpu = resolved == null ? 0 : resolved[1];

            this.Points.Add(new FanCurvePoint(temperature, cpu, gpu));
            SortPoints();

            this.SelectedIndex = IndexOfTemperature(temperature);

            Invalidate();
            RaiseSelectionChanged();
            Commit();

        }

        // Removes the selected point, refusing to empty the curve
        public bool RemoveSelected() {

            if(this.SelectedIndex < 0 || this.Points.Count <= 1)
                return false;

            this.Points.RemoveAt(this.SelectedIndex);
            this.SelectedIndex = -1;

            Invalidate();
            RaiseSelectionChanged();
            Commit();

            return true;

        }

        // Applies an edited value to the selected point
        public void UpdateSelected(int temperature, int cpu, int gpu) {

            if(this.SelectedIndex < 0 || this.SelectedIndex >= this.Points.Count)
                return;

            FanCurvePoint updated = Sanitize(new FanCurvePoint(temperature, cpu, gpu));

            // Refuse a temperature already taken by a different node: silently merging
            // two points would delete one of them without the user noticing
            for(int i = 0; i < this.Points.Count; i++)
                if(i != this.SelectedIndex && this.Points[i].Temperature == updated.Temperature)
                    return;

            this.Points[this.SelectedIndex] = updated;
            SortPoints();

            this.SelectedIndex = IndexOfTemperature(updated.Temperature);

            Invalidate();
            RaiseSelectionChanged();
            Commit();

        }

        // Paints fresh live temperature markers without touching the curve
        public void SetLiveTemperatures(int cpu, int gpu) {

            this.CpuTemperature = cpu;
            this.GpuTemperature = gpu;

            Invalidate();

        }

        private FanCurvePoint Sanitize(FanCurvePoint point) {

            return new FanCurvePoint(
                FanCurve.ClampTemperature(point.Temperature),
                FanCurve.ClampLevel(point.Cpu, this.LevelMax),
                FanCurve.ClampLevel(point.Gpu, this.LevelMax));

        }

        private void SortPoints() {

            this.Points.Sort(delegate(FanCurvePoint a, FanCurvePoint b) {
                return a.Temperature.CompareTo(b.Temperature);
            });

        }

        private int IndexOfTemperature(int temperature) {

            for(int i = 0; i < this.Points.Count; i++)
                if(this.Points[i].Temperature == temperature)
                    return i;

            return -1;

        }

        private SortedDictionary<byte, byte[]> ToLevels() {

            SortedDictionary<byte, byte[]> level = new SortedDictionary<byte, byte[]>();

            for(int i = 0; i < this.Points.Count; i++) {
                FanCurvePoint point = this.Points[i];
                level[(byte) point.Temperature] = new byte[] { (byte) point.Cpu, (byte) point.Gpu };
            }

            return level;

        }

        private void Commit() {

            EventHandler handler = this.PointsChanged;
            if(handler != null) handler(this, EventArgs.Empty);

        }

        private void RaiseSelectionChanged() {

            EventHandler handler = this.SelectionChanged;
            if(handler != null) handler(this, EventArgs.Empty);

        }
#endregion

#region Coordinate Mapping
        private Rectangle PlotArea {
            get {
                int width = this.ClientSize.Width - MarginLeft - MarginRight;
                int height = this.ClientSize.Height - MarginTop - MarginBottom;

                if(width < 10) width = 10;
                if(height < 10) height = 10;

                return new Rectangle(MarginLeft, MarginTop, width, height);
            }
        }

        private int EffectiveLevelMax {
            get {
                return this.LevelMax < FanCurve.LevelMin ? FanCurve.LevelMaxDefault : this.LevelMax;
            }
        }

        private int TemperatureToX(int temperature) {

            Rectangle area = PlotArea;

            double ratio = (double) temperature / AxisTemperatureMax;

            return area.Left + (int) Math.Round(ratio * area.Width);

        }

        private int XToTemperature(int x) {

            Rectangle area = PlotArea;

            double ratio = (double) (x - area.Left) / area.Width;

            return (int) Math.Round(ratio * AxisTemperatureMax);

        }

        private int LevelToY(int level) {

            Rectangle area = PlotArea;

            double ratio = (double) level / EffectiveLevelMax;

            return area.Bottom - (int) Math.Round(ratio * area.Height);

        }

        private int YToLevel(int y) {

            Rectangle area = PlotArea;

            double ratio = (double) (area.Bottom - y) / area.Height;

            return (int) Math.Round(ratio * EffectiveLevelMax);

        }
#endregion

#region Mouse Interaction
        // Finds the node closest to the cursor within the hit radius
        private void HitTest(int x, int y, out int index, out bool isCpu) {

            index = -1;
            isCpu = true;

            double best = HitRadius;

            for(int i = 0; i < this.Points.Count; i++) {

                FanCurvePoint point = this.Points[i];
                int px = TemperatureToX(point.Temperature);

                double cpuDistance = Math.Sqrt(Math.Pow(px - x, 2) + Math.Pow(LevelToY(point.Cpu) - y, 2));
                double gpuDistance = Math.Sqrt(Math.Pow(px - x, 2) + Math.Pow(LevelToY(point.Gpu) - y, 2));

                if(cpuDistance <= best) {
                    best = cpuDistance;
                    index = i;
                    isCpu = true;
                }

                if(gpuDistance <= best) {
                    best = gpuDistance;
                    index = i;
                    isCpu = false;
                }

            }

        }

        protected override void OnMouseDown(MouseEventArgs e) {

            base.OnMouseDown(e);

            // The canvas needs focus for the arrow-key nudging below
            if(this.Focused == false)
                Focus();

            int index;
            bool isCpu;
            HitTest(e.X, e.Y, out index, out isCpu);

            if(e.Button == MouseButtons.Left) {

                if(index >= 0) {

                    // Grab an existing node for dragging
                    this.SelectedIndex = index;
                    this.IsCpuSelected = isCpu;
                    this.DragIndex = index;
                    this.DragCpu = isCpu;
                    this.Dragging = true;

                } else {

                    // Empty space: add a node at this temperature
                    AddPoint(XToTemperature(e.X));
                    return;

                }

            } else if(e.Button == MouseButtons.Right) {

                // Right click removes the node under the cursor
                if(index >= 0) {
                    this.SelectedIndex = index;
                    RemoveSelected();
                }

                return;

            } else {

                return;

            }

            Invalidate();
            RaiseSelectionChanged();

        }

        protected override void OnMouseMove(MouseEventArgs e) {

            base.OnMouseMove(e);

            if(!this.Dragging || this.DragIndex < 0 || this.DragIndex >= this.Points.Count) {

                // Not dragging: only update the hover cursor
                int hoverIndex;
                bool hoverCpu;
                HitTest(e.X, e.Y, out hoverIndex, out hoverCpu);

                this.Cursor = hoverIndex >= 0 ? Cursors.Hand : Cursors.Cross;
                return;

            }

            FanCurvePoint point = this.Points[this.DragIndex];

            int newTemperature = FanCurve.ClampTemperature(XToTemperature(e.X));
            int newLevel = FanCurve.ClampLevel(YToLevel(e.Y), this.LevelMax);

            // Refuse to slide onto a temperature already occupied by another node.
            // The alternative - swapping - would make the curve jump under the cursor;
            // blocking the move keeps the key set unique without an exception and
            // matches how other curve editors behave.
            for(int i = 0; i < this.Points.Count; i++)
                if(i != this.DragIndex && this.Points[i].Temperature == newTemperature)
                    return;

            this.Points[this.DragIndex] = this.DragCpu
                ? new FanCurvePoint(newTemperature, newLevel, point.Gpu)
                : new FanCurvePoint(newTemperature, point.Cpu, newLevel);

            SortPoints();

            // The re-sort may have shuffled indices - re-locate the node being dragged
            this.DragIndex = IndexOfTemperature(newTemperature);
            this.SelectedIndex = this.DragIndex;

            Invalidate();
            RaiseSelectionChanged();

        }

        protected override void OnMouseUp(MouseEventArgs e) {

            base.OnMouseUp(e);

            if(!this.Dragging)
                return;

            this.Dragging = false;
            this.DragIndex = -1;

            // One commit per gesture, not one per pixel of movement
            Commit();

        }

        protected override void OnKeyDown(KeyEventArgs e) {

            base.OnKeyDown(e);

            if(this.SelectedIndex < 0 || this.SelectedIndex >= this.Points.Count)
                return;

            FanCurvePoint point = this.Points[this.SelectedIndex];

            int temperature = point.Temperature;
            int cpu = point.Cpu;
            int gpu = point.Gpu;

            switch(e.KeyCode) {

                case Keys.Left:
                    temperature = FanCurve.ClampTemperature(temperature - 1);
                    break;

                case Keys.Right:
                    temperature = FanCurve.ClampTemperature(temperature + 1);
                    break;

                case Keys.Up:
                    if(this.IsCpuSelected) cpu = FanCurve.ClampLevel(cpu + 1, this.LevelMax);
                    else gpu = FanCurve.ClampLevel(gpu + 1, this.LevelMax);
                    break;

                case Keys.Down:
                    if(this.IsCpuSelected) cpu = FanCurve.ClampLevel(cpu - 1, this.LevelMax);
                    else gpu = FanCurve.ClampLevel(gpu - 1, this.LevelMax);
                    break;

                case Keys.Delete:
                    e.Handled = true;
                    RemoveSelected();
                    return;

                default:
                    return;

            }

            e.Handled = true;
            UpdateSelected(temperature, cpu, gpu);

        }
#endregion

#region Painting
        protected override void OnPaint(PaintEventArgs e) {

            base.OnPaint(e);

            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;

            Rectangle area = PlotArea;

            DrawGrid(g, area);
            DrawCurves(g, area);
            DrawLiveMarkers(g, area);
            DrawHandles(g);

        }

        private void DrawGrid(Graphics g, Rectangle area) {

            using(Pen gridPen = new Pen(ColorGrid))
            using(Pen axisPen = new Pen(ColorAxis))
            using(Brush textBrush = new SolidBrush(ColorAxis))
            using(Font font = new Font(FontFamily.GenericSansSerif, 7.5f)) {

                // Horizontal level lines at 0 / 25 / 50 / 75 / 100 % of the ceiling
                int max = EffectiveLevelMax;

                for(int step = 0; step <= 4; step++) {

                    int level = (int) Math.Round(max * step / 4.0);
                    int y = LevelToY(level);

                    g.DrawLine(step == 0 ? axisPen : gridPen, area.Left, y, area.Right, y);

                    string label = level.ToString();
                    SizeF size = g.MeasureString(label, font);

                    g.DrawString(label, font, textBrush, area.Left - size.Width - 4, y - size.Height / 2);

                }

                // Vertical temperature lines every 20 C
                for(int temperature = 0; temperature <= AxisTemperatureMax; temperature += 20) {

                    int x = TemperatureToX(temperature);

                    g.DrawLine(gridPen, x, area.Top, x, area.Bottom);

                    string label = temperature.ToString();
                    SizeF size = g.MeasureString(label, font);

                    g.DrawString(label, font, textBrush, x - size.Width / 2, area.Bottom + 4);

                }

                g.DrawRectangle(axisPen, area);

            }

        }

        private void DrawCurves(Graphics g, Rectangle area) {

            // GPU first so the CPU series stays legible where they overlap
            DrawStepSeries(g, area, ColorGpu, false);
            DrawStepSeries(g, area, ColorCpu, true);

        }

        // Draws one series as a step function: the level holds until the next threshold
        // and only changes at a node, which is exactly how the runtime resolves a
        // temperature. The tail is extended to the right edge so the last node's level
        // is visibly in force up to the maximum temperature.
        private void DrawStepSeries(Graphics g, Rectangle area, Color color, bool isCpu) {

            if(this.Points.Count == 0)
                return;

            List<Point> vertices = new List<Point>();

            for(int i = 0; i < this.Points.Count; i++) {

                FanCurvePoint point = this.Points[i];

                int x = TemperatureToX(point.Temperature);
                int y = LevelToY(isCpu ? point.Cpu : point.Gpu);

                vertices.Add(new Point(x, y));

                // Horizontal run from this node to the next threshold
                int nextX = i + 1 < this.Points.Count
                    ? TemperatureToX(this.Points[i + 1].Temperature)
                    : area.Right;

                if(nextX > x)
                    vertices.Add(new Point(nextX, y));

            }

            using(Pen pen = new Pen(color, 2f))
            using(Brush fill = new SolidBrush(Color.FromArgb(60, color))) {

                g.DrawLines(pen, vertices.ToArray());

                // Shade the area under the step curve
                List<Point> polygon = new List<Point>(vertices);
                polygon.Add(new Point(area.Right, area.Bottom));
                polygon.Add(new Point(vertices[0].X, area.Bottom));

                g.FillPolygon(fill, polygon.ToArray());

            }

        }

        private void DrawLiveMarkers(Graphics g, Rectangle area) {

            using(Font font = new Font(FontFamily.GenericSansSerif, 7.5f, FontStyle.Bold)) {

                DrawLiveMarker(g, area, this.CpuTemperature, ColorCpu, font);
                DrawLiveMarker(g, area, this.GpuTemperature, ColorGpu, font);

            }

        }

        private void DrawLiveMarker(Graphics g, Rectangle area, int temperature, Color color, Font font) {

            if(temperature < 0)
                return;

            if(temperature > AxisTemperatureMax)
                temperature = AxisTemperatureMax;

            int x = TemperatureToX(temperature);

            using(Pen pen = new Pen(color) { DashStyle = DashStyle.Dash })
            using(Brush brush = new SolidBrush(color)) {

                g.DrawLine(pen, x, area.Top, x, area.Bottom);

                string label = temperature.ToString() + " C";
                SizeF size = g.MeasureString(label, font);

                float top = area.Top + 1;

                g.FillRectangle(brush, x + 2, top, size.Width + 4, size.Height);
                g.DrawString(label, font, Brushes.White, x + 4, top);

            }

        }

        private void DrawHandles(Graphics g) {

            using(Brush cpuBrush = new SolidBrush(ColorCpu))
            using(Brush gpuBrush = new SolidBrush(ColorGpu))
            using(Pen selectionPen = new Pen(Color.Black, 2f)) {

                for(int i = 0; i < this.Points.Count; i++) {

                    FanCurvePoint point = this.Points[i];
                    int x = TemperatureToX(point.Temperature);

                    int cpuY = LevelToY(point.Cpu);
                    int gpuY = LevelToY(point.Gpu);

                    g.FillEllipse(cpuBrush, x - HandleRadius, cpuY - HandleRadius, HandleRadius * 2, HandleRadius * 2);
                    g.FillEllipse(gpuBrush, x - HandleRadius, gpuY - HandleRadius, HandleRadius * 2, HandleRadius * 2);

                    if(i == this.SelectedIndex) {

                        int y = this.IsCpuSelected ? cpuY : gpuY;

                        g.DrawEllipse(selectionPen,
                            x - HandleRadius - 2, y - HandleRadius - 2,
                            (HandleRadius + 2) * 2, (HandleRadius + 2) * 2);

                    }

                }

            }

        }
#endregion

    }

}
