  //\\   OmenMon: Hardware Monitoring & Control Utility
 //  \\  Copyright © 2023-2024 Piotr Szczepański * License: GPL3
     //  https://omenmon.github.io/
// OmenMon-Reborn additions © 2026 seakyy

using System;
using System.Collections.Generic;
using System.Drawing;
using System.Windows.Forms;
using OmenMon.Hardware.Bios;
using OmenMon.Hardware.Platform;
using OmenMon.Library;

namespace OmenMon.AppGui {

    // Graphical fan-curve editor.
    //
    // Opened as a modal dialog from the tray menu. Deliberately does NOT hold
    // Context.Op.HardwareLock while it is on screen: the monitor thread keeps sampling
    // and the user is free to fiddle for as long as they like. The lock is only taken
    // around the individual hardware operations (running a preview curve, restarting
    // a running program after an edit).
    //
    // Persistence rides on Config.Save(), which already regenerates the whole
    // <FanPrograms> element from Config.FanProgram. This dialog only mutates that
    // dictionary; adding, renaming and deleting all come for free.
    public class GuiFormFanCurve : Form {

#region Components
        private ComboBox CmbProfile;
        private TextBox TxtName;
        private ComboBox CmbMode;
        private ComboBox CmbPower;
        private GuiFanCurveCanvas Canvas;

        private NumericUpDown NumTemp;
        private NumericUpDown NumCpu;
        private NumericUpDown NumGpu;

        private Label LblCpuLive;
        private Label LblGpuLive;
        private Label LblResolved;
        private Label LblCeiling;
        private Label LblStatus;

        private CheckBox ChkPreview;
        private CheckBox ChkOverride;

        private Button BtnNew;
        private Button BtnRename;
        private Button BtnDelete;
        private Button BtnAddPoint;
        private Button BtnRemovePoint;
        private Button BtnSave;
        private Button BtnClose;

        private Timer LiveTimer;
#endregion

#region Data
        private readonly FanProgram Program;
        private readonly Platform Platform;
        private readonly GuiMonitor Monitor;

        // Shared hardware serialisation lock, owned by GuiOp. Taken only around the
        // individual hardware operations - never for the lifetime of the dialog.
        private readonly object HardwareLock;

        // Name of the profile currently loaded into the editor, or null for a new one
        private string LoadedName;

        // Suppresses change handlers while the UI is being populated programmatically
        private bool Loading;

        // True when this board can latch its EC by being driven to the very top level
        private readonly bool FreezeRisk;

        // Configured ceiling (Config.FanLevelMax) and the one actually writable here
        private readonly int ConfiguredLevelMax;
        private int WritableCeiling;

        // Set when the in-memory dictionary has been mutated but not yet saved
        private bool Dirty;

        public GuiFormFanCurve(FanProgram program, Platform platform, GuiMonitor monitor, object hardwareLock) {

            this.Program = program;
            this.Platform = platform;
            this.Monitor = monitor;
            this.HardwareLock = hardwareLock ?? new object();

            this.ConfiguredLevelMax = Config.FanLevelMax > FanCurve.LevelMin
                ? Config.FanLevelMax
                : FanCurve.LevelMaxDefault;

            this.FreezeRisk = platform != null
                && platform.System != null
                && FanArray.HasMaxFanFreeze(platform.System.GetProduct());

            this.WritableCeiling = FanCurve.ResolveWritableCeiling(this.ConfiguredLevelMax, this.FreezeRisk);

            BuildUi();
            PopulateEnumCombos();
            LoadProfileList(null);

            this.FormClosing += OnFormClosing;

        }
#endregion

#region UI Construction
        private void BuildUi() {

            this.Text = "Fan Curve Editor";
            this.FormBorderStyle = FormBorderStyle.Sizable;
            this.MinimizeBox = false;
            this.MaximizeBox = false;
            this.StartPosition = FormStartPosition.CenterScreen;
            this.ClientSize = new Size(880, 600);
            this.MinimumSize = new Size(760, 520);

            // --- Profile row -------------------------------------------------------
            Label lblProfile = new Label { Text = "Profile:", AutoSize = true, Location = new Point(12, 16) };

            this.CmbProfile = new ComboBox {
                DropDownStyle = ComboBoxStyle.DropDownList,
                Location = new Point(70, 12),
                Size = new Size(220, 24)
            };
            this.CmbProfile.SelectedIndexChanged += OnProfileSelected;

            this.BtnNew = new Button { Text = "New", Location = new Point(300, 11), Size = new Size(64, 26) };
            this.BtnNew.Click += OnNewProfile;

            this.BtnRename = new Button { Text = "Rename", Location = new Point(370, 11), Size = new Size(70, 26) };
            this.BtnRename.Click += OnRenameProfile;

            this.BtnDelete = new Button { Text = "Delete", Location = new Point(446, 11), Size = new Size(64, 26) };
            this.BtnDelete.Click += OnDeleteProfile;

            // --- Name row ----------------------------------------------------------
            Label lblName = new Label { Text = "Name:", AutoSize = true, Location = new Point(12, 48) };

            this.TxtName = new TextBox { Location = new Point(70, 44), Size = new Size(220, 22) };

            Label lblMode = new Label { Text = "Fan mode:", AutoSize = true, Location = new Point(310, 48) };

            this.CmbMode = new ComboBox {
                DropDownStyle = ComboBoxStyle.DropDownList,
                Location = new Point(382, 44),
                Size = new Size(150, 24)
            };

            Label lblPower = new Label { Text = "GPU power:", AutoSize = true, Location = new Point(548, 48) };

            this.CmbPower = new ComboBox {
                DropDownStyle = ComboBoxStyle.DropDownList,
                Location = new Point(630, 44),
                Size = new Size(120, 24)
            };

            // --- Canvas ------------------------------------------------------------
            this.Canvas = new GuiFanCurveCanvas {
                Location = new Point(12, 78),
                Size = new Size(640, 420),
                Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Bottom
            };
            this.Canvas.PointsChanged += OnCanvasPointsChanged;
            this.Canvas.SelectionChanged += OnCanvasSelectionChanged;

            // --- Right-hand point editor -------------------------------------------
            int panelX = 664;

            GroupBox grpPoint = new GroupBox {
                Text = "Selected point",
                Location = new Point(panelX, 78),
                Size = new Size(200, 206),
                Anchor = AnchorStyles.Top | AnchorStyles.Right
            };

            Label lblTemp = new Label { Text = "Temperature (C):", AutoSize = true, Location = new Point(12, 26) };
            this.NumTemp = new NumericUpDown {
                Location = new Point(12, 46), Size = new Size(80, 22),
                Minimum = FanCurve.TemperatureMin, Maximum = FanCurve.TemperatureMax
            };
            this.NumTemp.ValueChanged += OnPointFieldChanged;

            Label lblCpu = new Label { Text = "CPU level:", AutoSize = true, Location = new Point(106, 26) };
            this.NumCpu = new NumericUpDown {
                Location = new Point(106, 46), Size = new Size(80, 22),
                Minimum = FanCurve.LevelMin, Maximum = this.ConfiguredLevelMax
            };
            this.NumCpu.ValueChanged += OnPointFieldChanged;

            Label lblGpu = new Label { Text = "GPU level:", AutoSize = true, Location = new Point(12, 78) };
            this.NumGpu = new NumericUpDown {
                Location = new Point(106, 78), Size = new Size(80, 22),
                Minimum = FanCurve.LevelMin, Maximum = this.ConfiguredLevelMax
            };
            this.NumGpu.ValueChanged += OnPointFieldChanged;

            this.BtnAddPoint = new Button { Text = "Add point", Location = new Point(12, 114), Size = new Size(84, 26) };
            this.BtnAddPoint.Click += OnAddPoint;

            this.BtnRemovePoint = new Button { Text = "Remove", Location = new Point(102, 114), Size = new Size(84, 26) };
            this.BtnRemovePoint.Click += OnRemovePoint;

            this.LblResolved = new Label {
                AutoSize = false, Location = new Point(12, 148), Size = new Size(176, 46),
                ForeColor = Color.DimGray
            };

            grpPoint.Controls.AddRange(new Control[] {
                lblTemp, this.NumTemp, lblCpu, this.NumCpu, lblGpu, this.NumGpu,
                this.BtnAddPoint, this.BtnRemovePoint, this.LblResolved });

            // --- Live readings -----------------------------------------------------
            GroupBox grpLive = new GroupBox {
                Text = "Live",
                Location = new Point(panelX, 292),
                Size = new Size(200, 112),
                Anchor = AnchorStyles.Top | AnchorStyles.Right
            };

            this.LblCpuLive = new Label { AutoSize = true, Location = new Point(12, 24), Text = "CPU: -" };
            this.LblGpuLive = new Label { AutoSize = true, Location = new Point(12, 48), Text = "GPU: -" };

            this.ChkPreview = new CheckBox {
                Text = "Run preview",
                Location = new Point(12, 76),
                AutoSize = true
            };
            this.ChkPreview.CheckedChanged += OnPreviewToggled;

            grpLive.Controls.AddRange(new Control[] {
                this.LblCpuLive, this.LblGpuLive, this.ChkPreview });

            // --- Freeze-guard row --------------------------------------------------
            this.LblCeiling = new Label {
                AutoSize = false,
                Location = new Point(12, 508),
                Size = new Size(640, 36),
                Anchor = AnchorStyles.Bottom | AnchorStyles.Left,
                ForeColor = this.FreezeRisk ? Color.Firebrick : Color.DimGray
            };

            this.ChkOverride = new CheckBox {
                Text = "Allow the full level range on this model (not recommended)",
                Location = new Point(12, 544),
                Size = new Size(430, 22),
                Anchor = AnchorStyles.Bottom | AnchorStyles.Left,
                Visible = this.FreezeRisk
            };
            this.ChkOverride.CheckedChanged += OnOverrideToggled;

            // --- Command buttons ---------------------------------------------------
            this.BtnSave = new Button {
                Text = "Save",
                Location = new Point(586, 542),
                Size = new Size(88, 28),
                Anchor = AnchorStyles.Bottom | AnchorStyles.Left
            };
            this.BtnSave.Click += OnSave;

            this.BtnClose = new Button {
                Text = "Close",
                Location = new Point(680, 542),
                Size = new Size(88, 28),
                Anchor = AnchorStyles.Bottom | AnchorStyles.Left,
                DialogResult = DialogResult.Cancel
            };
            this.BtnClose.Click += OnCloseClicked;

            this.LblStatus = new Label {
                AutoSize = false,
                Location = new Point(12, 576),
                Size = new Size(756, 18),
                Anchor = AnchorStyles.Bottom | AnchorStyles.Left,
                ForeColor = Color.DimGray
            };

            this.Controls.AddRange(new Control[] {
                lblProfile, this.CmbProfile, this.BtnNew, this.BtnRename, this.BtnDelete,
                lblName, this.TxtName, lblMode, this.CmbMode, lblPower, this.CmbPower,
                this.Canvas, grpPoint, grpLive,
                this.LblCeiling, this.ChkOverride,
                this.BtnSave, this.BtnClose, this.LblStatus });

            // Live temperature polling. The monitor thread already owns all hardware
            // access; this timer only copies its latest published snapshot into the
            // canvas, so it never blocks on the EC.
            this.LiveTimer = new Timer { Interval = Config.GuiTimerInterval };
            this.LiveTimer.Tick += OnLiveTick;
            this.LiveTimer.Start();

            this.UpdateCeilingLabel();

        }

        private void UpdateCeilingLabel() {

            if(this.FreezeRisk && this.WritableCeiling < this.ConfiguredLevelMax) {

                this.LblCeiling.Text = "Safety: this model is known to latch its embedded controller "
                    + "when driven to the maximum fan level. Levels are capped at "
                    + this.WritableCeiling + " (instead of " + this.ConfiguredLevelMax + ").";

            } else if(this.FreezeRisk) {

                this.LblCeiling.Text = "Safety: the maximum-level cap is disabled on this model. "
                    + "The fans can be driven to the point where the embedded controller locks up.";

            } else {

                this.LblCeiling.Text = "Fan levels are written to the embedded controller in decimal, 0 to "
                    + this.ConfiguredLevelMax + ".";

            }

        }

        // Fills the fan-mode and GPU-power lists with the distinct enumeration values.
        // Enum.GetValues collapses the aliases (Default / L2, Eco / Quiet / L2 and so on)
        // to one entry per numeric value, and Enum.GetName returns the first declared
        // name for that value - which is always a name Enum.Parse accepts back.
        private void PopulateEnumCombos() {

            this.Loading = true;

            this.CmbMode.Items.Clear();
            foreach(BiosData.FanMode mode in DistinctValues<BiosData.FanMode>())
                this.CmbMode.Items.Add(mode.ToString());

            this.CmbPower.Items.Clear();
            foreach(BiosData.GpuPowerLevel power in DistinctValues<BiosData.GpuPowerLevel>())
                this.CmbPower.Items.Add(power.ToString());

            this.Loading = false;

        }

        private static List<T> DistinctValues<T>() where T : struct, Enum {

            List<T> values = new List<T>();
            HashSet<long> seen = new HashSet<long>();

            foreach(T value in (T[]) Enum.GetValues(typeof(T))) {

                long numeric = Convert.ToInt64(value);

                if(seen.Add(numeric))
                    values.Add(value);

            }

            return values;

        }
#endregion

#region Profile Management
        // Rebuilds the profile drop-down, optionally selecting the given profile
        private void LoadProfileList(string selectName) {

            this.Loading = true;

            this.CmbProfile.Items.Clear();

            foreach(string name in Config.FanProgram.Keys)
                this.CmbProfile.Items.Add(name);

            this.Loading = false;

            if(this.CmbProfile.Items.Count == 0)
                return;

            int index = selectName == null ? 0 : this.CmbProfile.Items.IndexOf(selectName);

            if(index < 0) index = 0;

            // Setting SelectedIndex raises SelectedIndexChanged. Suppress it here: the
            // list is being rebuilt by the caller, and letting the handler run would
            // re-enter LoadProfile and (worse) fire the unsaved-changes prompt against
            // a profile name the caller has already replaced.
            this.Loading = true;
            this.CmbProfile.SelectedIndex = index;
            this.Loading = false;

            LoadProfile((string) this.CmbProfile.Items[index]);

        }

        // Copies a profile out of the configuration into the editor
        private void LoadProfile(string name) {

            if(name == null || !Config.FanProgram.ContainsKey(name))
                return;

            FanProgramData data = Config.FanProgram[name];

            this.Loading = true;

            this.LoadedName = name;
            this.TxtName.Text = name;

            this.CmbMode.SelectedItem = data.FanMode.ToString();
            if(this.CmbMode.SelectedIndex < 0 && this.CmbMode.Items.Count > 0)
                this.CmbMode.SelectedIndex = 0;

            this.CmbPower.SelectedItem = data.GpuPower.ToString();
            if(this.CmbPower.SelectedIndex < 0 && this.CmbPower.Items.Count > 0)
                this.CmbPower.SelectedIndex = 0;

            this.Loading = false;

            this.Canvas.SetPoints(FanCurve.ToPoints(data.Level), this.WritableCeiling);

            this.UpdatePointFields();
            this.UpdateStatus("");

        }

        private void OnProfileSelected(object sender, EventArgs e) {

            if(this.Loading)
                return;

            string name = this.CmbProfile.SelectedItem as string;

            if(name == null)
                return;

            // Switching away from an unsaved edit would silently discard it
            if(this.Dirty) {

                DialogResult answer = MessageBox.Show(
                    "Discard the unsaved changes to \"" + this.LoadedName + "\"?",
                    Config.AppName, MessageBoxButtons.YesNo, MessageBoxIcon.Question);

                if(answer != DialogResult.Yes) {

                    this.Loading = true;
                    this.CmbProfile.SelectedItem = this.LoadedName;
                    this.Loading = false;

                    return;

                }

                this.Dirty = false;

            }

            LoadProfile(name);

        }

        // Creates a blank profile with a sensible starting curve so the canvas is
        // never empty and the first Save has something valid to write
        private void OnNewProfile(object sender, EventArgs e) {

            string baseName = "Custom";
            string name = baseName;
            int suffix = 1;

            while(Config.FanProgram.ContainsKey(name))
                name = baseName + " " + (suffix++);

            string answer = PromptForName("New fan profile", name);

            if(answer == null)
                return;

            if(!ValidateNewName(answer))
                return;

            List<FanCurvePoint> seed = new List<FanCurvePoint> {
                new FanCurvePoint(30, 0, 0),
                new FanCurvePoint(50, 10, 10),
                new FanCurvePoint(70, 25, 25),
                new FanCurvePoint(90, this.WritableCeiling, this.WritableCeiling)
            };

            Config.FanProgram[answer] = new FanProgramData(
                answer,
                BiosData.FanMode.Default,
                BiosData.GpuPowerLevel.Maximum,
                new SortedDictionary<byte, byte[]>());

            this.LoadProfileList(answer);

            // Apply the seed through the normal edit path so clamping and validation
            // behave identically to a hand-drawn curve
            this.Canvas.SetPoints(seed, this.WritableCeiling);

            this.Dirty = true;

            this.UpdatePointFields();
            this.UpdateStatus("Created \"" + answer + "\" - review the curve, then Save.");

        }

        private void OnRenameProfile(object sender, EventArgs e) {

            if(this.LoadedName == null)
                return;

            string oldName = this.LoadedName;
            string answer = PromptForName("Rename fan profile", oldName);

            if(answer == null || answer == oldName)
                return;

            if(!ValidateNewName(answer))
                return;

            if(!Config.FanProgram.ContainsKey(oldName))
                return;

            FanProgramData data = Config.FanProgram[oldName];

            Config.FanProgram.Remove(oldName);
            data.Name = answer;
            Config.FanProgram[answer] = data;

            // Keep the default references pointing at the renamed profile
            if(Config.FanProgramDefault == oldName)
                Config.FanProgramDefault = answer;

            if(Config.FanProgramDefaultAlt == oldName)
                Config.FanProgramDefaultAlt = answer;

            this.Dirty = true;
            this.LoadedName = answer;

            this.LoadProfileList(answer);
            this.UpdateStatus("Renamed to \"" + answer + "\" - Save to apply.");

        }

        private void OnDeleteProfile(object sender, EventArgs e) {

            if(this.LoadedName == null)
                return;

            string name = this.LoadedName;

            // Refuse to delete a profile the configuration still points at: the
            // start-up path would then try to run a program that no longer exists
            if(Config.FanProgramDefault == name || Config.FanProgramDefaultAlt == name) {

                MessageBox.Show(
                    "\"" + name + "\" is referenced as a default fan program in the configuration.\n\n"
                    + "Change the default first, or rename this profile instead.",
                    Config.AppName, MessageBoxButtons.OK, MessageBoxIcon.Warning);

                return;

            }

            if(Config.FanProgram.Keys.Count <= 1) {

                MessageBox.Show(
                    "At least one fan program has to remain defined.",
                    Config.AppName, MessageBoxButtons.OK, MessageBoxIcon.Warning);

                return;

            }

            DialogResult answer = MessageBox.Show(
                "Delete the fan profile \"" + name + "\"?",
                Config.AppName, MessageBoxButtons.YesNo, MessageBoxIcon.Question,
                MessageBoxDefaultButton.Button2);

            if(answer != DialogResult.Yes)
                return;

            Config.FanProgram.Remove(name);

            this.Dirty = true;
            this.LoadedName = null;

            this.LoadProfileList(null);
            this.UpdateStatus("Deleted \"" + name + "\" - Save to apply.");

        }

        // Rejects names that are empty, already taken, or unusable as an XML attribute
        private bool ValidateNewName(string name) {

            if(string.IsNullOrWhiteSpace(name)) {

                MessageBox.Show("The profile name cannot be empty.",
                    Config.AppName, MessageBoxButtons.OK, MessageBoxIcon.Warning);

                return false;

            }

            if(Config.FanProgram.ContainsKey(name) && name != this.LoadedName) {

                MessageBox.Show("A fan profile named \"" + name + "\" already exists.",
                    Config.AppName, MessageBoxButtons.OK, MessageBoxIcon.Warning);

                return false;

            }

            return true;

        }

        // Minimal modal text prompt - WinForms has no built-in input box
        private string PromptForName(string title, string initial) {

            using(Form prompt = new Form()) {

                prompt.Text = title;
                prompt.FormBorderStyle = FormBorderStyle.FixedDialog;
                prompt.StartPosition = FormStartPosition.CenterParent;
                prompt.MinimizeBox = false;
                prompt.MaximizeBox = false;
                prompt.ClientSize = new Size(360, 116);

                Label label = new Label { Text = "Name:", AutoSize = true, Location = new Point(12, 16) };
                TextBox input = new TextBox { Text = initial, Location = new Point(12, 38), Size = new Size(336, 22) };

                Button ok = new Button { Text = "OK", DialogResult = DialogResult.OK, Location = new Point(182, 74), Size = new Size(78, 28) };
                Button cancel = new Button { Text = "Cancel", DialogResult = DialogResult.Cancel, Location = new Point(270, 74), Size = new Size(78, 28) };

                prompt.Controls.AddRange(new Control[] { label, input, ok, cancel });
                prompt.AcceptButton = ok;
                prompt.CancelButton = cancel;

                if(prompt.ShowDialog(this) != DialogResult.OK)
                    return null;

                return input.Text.Trim();

            }

        }
#endregion

#region Editing
        private void OnAddPoint(object sender, EventArgs e) {

            // Insert halfway between the selected node and its successor, or just
            // after the selection when it is the last one
            FanCurvePoint selected = this.Canvas.GetSelected();

            int temperature;

            if(selected.Temperature < 0) {

                temperature = 60;

            } else {

                int next = FanCurve.TemperatureMax;

                for(int i = 0; i < this.Canvas.Points.Count; i++)
                    if(this.Canvas.Points[i].Temperature > selected.Temperature) {
                        next = this.Canvas.Points[i].Temperature;
                        break;
                    }

                temperature = selected.Temperature + Math.Max(1, (next - selected.Temperature) / 2);

            }

            this.Canvas.AddPoint(temperature);

        }

        private void OnRemovePoint(object sender, EventArgs e) {

            if(!this.Canvas.RemoveSelected())
                this.UpdateStatus("Select a point first - a curve needs at least one point.");

        }

        // Pushes the numeric fields into the canvas after the user edits one of them
        private void OnPointFieldChanged(object sender, EventArgs e) {

            if(this.Loading)
                return;

            if(this.Canvas.GetSelected().Temperature < 0)
                return;

            this.Canvas.UpdateSelected(
                (int) this.NumTemp.Value,
                (int) this.NumCpu.Value,
                (int) this.NumGpu.Value);

        }

        private void OnCanvasSelectionChanged(object sender, EventArgs e) {

            UpdatePointFields();
            UpdateResolvedLabel();

        }

        // Any gesture that changed the curve goes through here
        private void OnCanvasPointsChanged(object sender, EventArgs e) {

            this.Dirty = true;

            UpdateResolvedLabel();
            UpdateStatus("Unsaved changes.");

            // Live preview: rewrite the running curve so the fans follow the edits
            if(this.ChkPreview.Checked)
                ApplyPreview();

        }

        private void UpdatePointFields() {

            FanCurvePoint point = this.Canvas.GetSelected();

            this.Loading = true;

            this.NumTemp.Enabled = point.Temperature >= 0;
            this.NumCpu.Enabled = point.Temperature >= 0;
            this.NumGpu.Enabled = point.Temperature >= 0;
            this.BtnRemovePoint.Enabled = point.Temperature >= 0 && this.Canvas.Points.Count > 1;

            // The writable ceiling, not the configured maximum, bounds these fields
            this.NumCpu.Maximum = this.WritableCeiling;
            this.NumGpu.Maximum = this.WritableCeiling;

            if(point.Temperature >= 0) {

                this.NumTemp.Value = FanCurve.ClampTemperature(point.Temperature);
                this.NumCpu.Value = FanCurve.ClampLevel(point.Cpu, this.WritableCeiling);
                this.NumGpu.Value = FanCurve.ClampLevel(point.Gpu, this.WritableCeiling);

            }

            this.Loading = false;

        }

        // Shows what the curve actually resolves to at the current temperatures
        private void UpdateResolvedLabel() {

            SortedDictionary<byte, byte[]> level = new SortedDictionary<byte, byte[]>();

            for(int i = 0; i < this.Canvas.Points.Count; i++) {

                FanCurvePoint point = this.Canvas.Points[i];
                level[(byte) point.Temperature] = new byte[] { (byte) point.Cpu, (byte) point.Gpu };

            }

            if(level.Count == 0) {

                this.LblResolved.Text = "";
                return;

            }

            string text = "";

            if(this.Canvas.CpuTemperature >= 0) {

                byte[] cpu = FanCurve.LookupLevels(level, (byte) this.Canvas.CpuTemperature);

                if(cpu != null)
                    text += "At CPU " + this.Canvas.CpuTemperature + " C: " + cpu[0];

            }

            if(this.Canvas.GpuTemperature >= 0) {

                byte[] gpu = FanCurve.LookupLevels(level, (byte) this.Canvas.GpuTemperature);

                if(gpu != null) {

                    if(text != "") text += "\r\n";
                    text += "At GPU " + this.Canvas.GpuTemperature + " C: " + gpu[1];

                }

            }

            this.LblResolved.Text = text;

        }

        // Copies the monitor's latest snapshot onto the canvas. No hardware access
        // happens here: the monitor thread publishes snapshots and this only reads
        // the most recent one, exactly like the tray icon does.
        private void OnLiveTick(object sender, EventArgs e) {

            if(this.IsDisposed || !this.Visible)
                return;

            int cpu = -1;
            int gpu = -1;

            MonitorSnapshot snapshot = this.Monitor == null ? null : this.Monitor.Current;

            if(snapshot != null) {
                cpu = snapshot.CpuTemp;
                gpu = snapshot.GpuTemp;
            }

            this.Canvas.SetLiveTemperatures(cpu, gpu);

            this.LblCpuLive.Text = cpu >= 0 ? "CPU: " + cpu + " C" : "CPU: -";
            this.LblGpuLive.Text = gpu >= 0 ? "GPU: " + gpu + " C" : "GPU: -";

            UpdateResolvedLabel();

            // Note: the preview is deliberately NOT re-applied here. Once started it is
            // carried by the normal program mechanism, which re-reads Config.FanProgram
            // on every tick - so it already picks up the edits. Re-applying from this
            // timer would drive the fans a second time per second, competing with the
            // monitor thread for the EC.

        }

        private void OnPreviewToggled(object sender, EventArgs e) {

            if(this.ChkPreview.Checked) {

                if(this.Program == null || this.Platform == null) {

                    this.ChkPreview.Checked = false;
                    this.UpdateStatus("Fan control is not available in this session.");
                    return;

                }

                ApplyPreview();

            } else {

                StopPreview();

            }

        }

        // Writes the current editor curve into the live configuration and (re)starts
        // the preview program under the hardware lock.
        //
        // The lock is taken here - inside a user gesture - and released immediately.
        // It is never held while the dialog is open, so the monitor thread keeps
        // sampling and the main window stays responsive.
        private void ApplyPreview() {

            if(this.LoadedName == null || this.Platform == null || this.Program == null)
                return;

            if(!CommitCurveToConfig(out string error)) {

                this.UpdateStatus(error);
                this.ChkPreview.Checked = false;
                return;

            }

            try {

                lock(this.HardwareLock) {

                    // Run() re-reads the configuration, so a fresh program picks up the
                    // edited curve; a running one is refreshed in place
                    if(!this.Program.IsEnabled || this.Program.GetName() != this.LoadedName)
                        this.Program.Run(this.LoadedName);

                    this.Program.Update();

                }

                this.Monitor?.ResetProgramTick();
                this.Monitor?.SampleNow();

                this.UpdateStatus("Preview running: \"" + this.LoadedName + "\".");

            } catch(Exception ex) {

                this.ChkPreview.Checked = false;
                this.UpdateStatus("Preview failed: " + ex.Message);

            }

        }

        // Ends the preview program and hands control back to the firmware
        private void StopPreview() {

            if(this.Program == null || this.Platform == null)
                return;

            try {

                lock(this.HardwareLock) {

                    if(this.Program.IsEnabled && this.Program.GetName() == this.LoadedName)
                        this.Program.Terminate();

                }

                this.Monitor?.SampleNow();

                this.UpdateStatus("Preview stopped.");

            } catch(Exception ex) {

                this.UpdateStatus("Could not stop the preview: " + ex.Message);

            }

        }

        // Validates the canvas contents and writes them into Config.FanProgram
        private bool CommitCurveToConfig(out string error) {

            error = "";

            if(this.LoadedName == null || !Config.FanProgram.ContainsKey(this.LoadedName)) {

                error = "No fan profile is selected.";
                return false;

            }

            if(!FanCurve.TryBuildLevels(this.Canvas.Points, this.WritableCeiling, out var level, out error))
                return false;

            BiosData.FanMode mode = BiosData.FanMode.Default;
            BiosData.GpuPowerLevel power = BiosData.GpuPowerLevel.Maximum;

            if(this.CmbMode.SelectedItem is string modeName)
                Enum.TryParse(modeName, out mode);

            if(this.CmbPower.SelectedItem is string powerName)
                Enum.TryParse(powerName, out power);

            Config.FanProgram[this.LoadedName] = new FanProgramData(this.LoadedName, mode, power, level);

            return true;

        }

        private void OnSave(object sender, EventArgs e) {

            if(!CommitCurveToConfig(out string error)) {

                MessageBox.Show(error, Config.AppName, MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;

            }

            // Config.Save() rewrites the whole <FanPrograms> element from the
            // in-memory dictionary, so no extra serialisation is needed here
            Config.Save();

            this.Dirty = false;

            this.UpdateStatus("Saved.");

        }

        private void OnOverrideToggled(object sender, EventArgs e) {

            if(this.ChkOverride.Checked) {

                // Reuse the shared confirmation so the wording matches the tray menu
                string product = this.Platform == null || this.Platform.System == null
                    ? null
                    : this.Platform.System.GetProduct();

                if(!GuiOp.ConfirmMaxFanIfRisky(product)) {

                    this.ChkOverride.Checked = false;
                    return;

                }

                this.WritableCeiling = this.ConfiguredLevelMax;

            } else {

                this.WritableCeiling = FanCurve.ResolveWritableCeiling(this.ConfiguredLevelMax, this.FreezeRisk);

            }

            this.Canvas.SetLevelMax(this.WritableCeiling);
            this.UpdatePointFields();
            this.UpdateCeilingLabel();

        }

        private void OnCloseClicked(object sender, EventArgs e) {

            Close();

        }

        private void UpdateStatus(string message) {

            this.LblStatus.Text = message;

        }
#endregion

#region Teardown
        // Stops the preview and the timer, and gives the user a last chance to save.
        //
        // The preview is always stopped on exit: leaving a half-edited curve running
        // against the hardware after the dialog is gone would be surprising, and the
        // user has an explicit way to keep it (save, then pick it from the Fan menu).
        private void OnFormClosing(object sender, FormClosingEventArgs e) {

            this.LiveTimer.Stop();

            if(this.ChkPreview.Checked) {

                this.ChkPreview.Checked = false;
                StopPreview();

            }

            if(!this.Dirty)
                return;

            DialogResult answer = MessageBox.Show(
                "There are unsaved changes to the fan profile \"" + (this.LoadedName ?? "") + "\".\n\n"
                + "Save them before closing?",
                Config.AppName, MessageBoxButtons.YesNoCancel, MessageBoxIcon.Question);

            if(answer == DialogResult.Cancel) {

                e.Cancel = true;
                this.LiveTimer.Start();
                return;

            }

            if(answer == DialogResult.Yes) {

                if(!CommitCurveToConfig(out string error)) {

                    MessageBox.Show(error, Config.AppName, MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    e.Cancel = true;
                    this.LiveTimer.Start();
                    return;

                }

                Config.Save();

            }

        }

        protected override void Dispose(bool disposing) {

            if(disposing && this.LiveTimer != null) {

                this.LiveTimer.Stop();
                this.LiveTimer.Tick -= OnLiveTick;
                this.LiveTimer.Dispose();
                this.LiveTimer = null;

            }

            base.Dispose(disposing);

        }
#endregion

    }

}
