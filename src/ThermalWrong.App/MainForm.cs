// ThermalWrong by soybean_alien
// https://github.com/soybean-alien/ThermalWrong · MIT License
using System.Diagnostics;
using ThermalWrong.Core;

namespace ThermalWrong.App;

/// <summary>The control window plus tray icon. Closing the window keeps playing from the tray.</summary>
sealed class MainForm : Form
{
    readonly Settings _settings = Settings.Load();
    readonly Player _player = new();
    readonly SensorSource _sensors = new();
    readonly TempOverlay _overlay;
    readonly NotifyIcon _tray = new();
    readonly ListBox _media = new() { Dock = DockStyle.Fill, IntegralHeight = false, BorderStyle = BorderStyle.None };
    readonly PictureBox _preview = new() { Dock = DockStyle.Fill, SizeMode = PictureBoxSizeMode.Zoom, BackColor = Color.Black };
    readonly Label _status = new() { Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft, AutoEllipsis = true };
    readonly ComboBox _rotation = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 110 };
    readonly Button _show = new() { Text = "Show on screen", AutoSize = true };
    readonly Button _add = new() { Text = "Add media…", AutoSize = true };
    readonly Button _folder = new() { Text = "Open folder", AutoSize = true };
    readonly Button _handOff = new() { AutoSize = true };
    readonly CheckBox _temps = new() { Text = "Show temperatures", AutoSize = true, Padding = new Padding(8, 6, 0, 0) };
    readonly CheckBox _autostart = new() { Text = "Start with Windows", AutoSize = true, Padding = new Padding(8, 6, 0, 0) };
    readonly System.Windows.Forms.Timer _tick = new() { Interval = 250 };
    readonly bool _startHidden;
    bool _exiting, _busy;
    int _previewSerial = -1;

    static readonly (string Label, int? Value)[] Rotations = [("Auto", null), ("0°", 0), ("90°", 90), ("180°", 180), ("270°", 270)];

    public MainForm(bool startHidden)
    {
        _startHidden = startHidden;
        Text = "ThermalWrong";
        Icon = Icon.ExtractAssociatedIcon(Environment.ProcessPath!);
        ClientSize = new Size(900, 470);
        MinimumSize = new Size(640, 360);
        StartPosition = FormStartPosition.CenterScreen;
        Font = new Font("Segoe UI", 9.5f);

        PrepareMedia();

        BuildLayout();
        BuildTray();

        foreach (var r in Rotations) _rotation.Items.Add(r.Label);
        _rotation.SelectedIndex = Math.Max(0, Array.FindIndex(Rotations, r => r.Value == _settings.Rotation));
        _autostart.Checked = Autostart.IsEnabled;
        _overlay = new TempOverlay(_sensors);
        _temps.Checked = _settings.ShowTemperatures;
        _player.Overlay = _settings.ShowTemperatures ? _overlay : null;
        _temps.CheckedChanged += (_, _) => SetTemperatures(_temps.Checked);
        RefreshMediaList();

        _media.DoubleClick += (_, _) => ShowSelected();
        _show.Click += (_, _) => ShowSelected();
        _add.Click += (_, _) => AddMedia();
        _folder.Click += (_, _) => Process.Start(new ProcessStartInfo(_settings.MediaFolder!) { UseShellExecute = true })?.Dispose();
        _rotation.SelectionChangeCommitted += (_, _) => ApplyRotation();
        _handOff.Click += async (_, _) => await ToggleScreenOwner();
        _autostart.CheckedChanged += async (_, _) => await SetAutostart(_autostart.Checked);
        _tick.Tick += (_, _) => UpdateStatus();
        _tick.Start();

        _player.SetReleased(_settings.ScreenHandedToTrcc);
        _player.Show(_settings.MediaPath, _settings.Rotation);
        if (_settings.ShowTemperatures) _ = EnsureSensorHelper();
    }

    void SetTemperatures(bool on)
    {
        _settings.ShowTemperatures = on;
        _settings.Save();
        _player.Overlay = on ? _overlay : null;
        if (on) _ = EnsureSensorHelper();
    }

    /// <summary>The temperatures come from TRCC's sensor helper; start it if neither it nor TRCC is running.</summary>
    Task EnsureSensorHelper() => Task.Run(() =>
    {
        if (!Trcc.SensorHelperRunning && !Trcc.IsRunning) Trcc.StartSensorHelper();
    });

    /// <summary>
    /// Media lives in Videos\ThermalWrong. On first run the bundled samples are copied there and the
    /// first one is shown, so a fresh install has something on the screen straight away.
    /// </summary>
    void PrepareMedia()
    {
        if (string.IsNullOrEmpty(_settings.MediaFolder) || !Directory.Exists(_settings.MediaFolder))
            _settings.MediaFolder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyVideos), "ThermalWrong");
        Directory.CreateDirectory(_settings.MediaFolder);

        string samples = Path.Combine(AppContext.BaseDirectory, "samples");
        if (!MediaFiles().Any() && Directory.Exists(samples))
            foreach (var sample in Directory.EnumerateFiles(samples))
                File.Copy(sample, Path.Combine(_settings.MediaFolder, Path.GetFileName(sample)), overwrite: false);

        if (_settings.MediaPath == null || !File.Exists(_settings.MediaPath))
            _settings.MediaPath = MediaFiles().FirstOrDefault();
        _settings.Save();
    }

    void BuildLayout()
    {
        var root = new TableLayoutPanel { Dock = DockStyle.Fill, RowCount = 3, ColumnCount = 1, Padding = new Padding(10) };
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 30));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));

        var split = new SplitContainer { Dock = DockStyle.Fill, FixedPanel = FixedPanel.Panel1 };
        Load += (_, _) => split.SplitterDistance = LogicalToDeviceUnits(260); // Only sticks once the form has its size.
        var listBox = new GroupBox { Text = "Media", Dock = DockStyle.Fill, Padding = new Padding(6) };
        listBox.Controls.Add(_media);
        var previewBox = new GroupBox { Text = "On the screen now", Dock = DockStyle.Fill, Padding = new Padding(6) };
        previewBox.Controls.Add(_preview);
        split.Panel1.Controls.Add(listBox);
        split.Panel2.Controls.Add(previewBox);

        var bar = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, WrapContents = true, Padding = new Padding(0, 6, 0, 0) };
        var rotLabel = new Label { Text = "Rotation:", AutoSize = true, Padding = new Padding(12, 7, 0, 0) };
        bar.Controls.AddRange([_show, _add, _folder, rotLabel, _rotation, _handOff, _temps, _autostart]);

        root.Controls.Add(_status, 0, 0);
        root.Controls.Add(split, 0, 1);
        root.Controls.Add(bar, 0, 2);
        Controls.Add(root);
    }

    void BuildTray()
    {
        _tray.Icon = Icon;
        _tray.Text = "ThermalWrong";
        _tray.Visible = true;
        _tray.DoubleClick += (_, _) => ShowWindow();
        var menu = new ContextMenuStrip();
        menu.Opening += (_, _) =>
        {
            menu.Items.Clear();
            menu.Items.Add("Open ThermalWrong", null, (_, _) => ShowWindow());
            var pick = new ToolStripMenuItem("Show on screen");
            foreach (var file in MediaFiles())
            {
                var item = new ToolStripMenuItem(Path.GetFileName(file), null, (_, _) => Play(file))
                {
                    Checked = string.Equals(file, _settings.MediaPath, StringComparison.OrdinalIgnoreCase),
                };
                pick.DropDownItems.Add(item);
            }
            pick.Enabled = pick.DropDownItems.Count > 0;
            menu.Items.Add(pick);
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add(_handOff.Text, null, async (_, _) => await ToggleScreenOwner()).Enabled = !_busy;
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add("About ThermalWrong", null, (_, _) => ShowAbout());
            menu.Items.Add("Exit", null, (_, _) => ExitApp());
        };
        menu.Items.Add("-"); // Opening only fires for a non-empty menu.
        _tray.ContextMenuStrip = menu;
    }

    protected override void SetVisibleCore(bool value)
    {
        // Launched with --tray: create the window handle but stay hidden.
        if (_startHidden && !IsHandleCreated)
        {
            CreateHandle();
            value = false;
        }
        base.SetVisibleCore(value);
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        SingleInstance.AllowShowMessage(Handle);
    }

    protected override void WndProc(ref Message m)
    {
        if (m.Msg == (int)SingleInstance.ShowMessage) ShowWindow();
        base.WndProc(ref m);
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        if (!_exiting && e.CloseReason == CloseReason.UserClosing)
        {
            e.Cancel = true;
            Hide();
            // Drop the preview while in the tray; only the frame cache needs to stay resident.
            var old = _preview.Image;
            _preview.Image = null;
            old?.Dispose();
            _previewSerial = -1;
            return;
        }
        base.OnFormClosing(e);
    }

    void ShowWindow()
    {
        Show();
        WindowState = FormWindowState.Normal;
        Activate();
    }

    void ExitApp()
    {
        _exiting = true;
        _tick.Stop();
        _tray.Visible = false;
        _player.Dispose(); // Releases the screen; it keeps showing the last frame.
        _overlay.Dispose();
        _sensors.Dispose();
        Close();
    }

    IEnumerable<string> MediaFiles() => Directory.Exists(_settings.MediaFolder)
        ? Directory.EnumerateFiles(_settings.MediaFolder!)
            .Where(f => MediaDecoder.Extensions.Contains(Path.GetExtension(f).ToLowerInvariant()))
            .OrderBy(f => Path.GetFileName(f), StringComparer.OrdinalIgnoreCase)
        : [];

    void RefreshMediaList()
    {
        _media.BeginUpdate();
        _media.Items.Clear();
        foreach (var f in MediaFiles()) _media.Items.Add(new MediaItem(f));
        _media.EndUpdate();
        var current = _media.Items.Cast<MediaItem>().FirstOrDefault(m => string.Equals(m.Path, _settings.MediaPath, StringComparison.OrdinalIgnoreCase));
        if (current != null) _media.SelectedItem = current;
    }

    void ShowSelected()
    {
        if (_media.SelectedItem is MediaItem item) Play(item.Path);
    }

    void Play(string path)
    {
        _settings.MediaPath = path;
        _settings.Save();
        _player.Show(path, _settings.Rotation);
        RefreshMediaList();
    }

    void AddMedia()
    {
        using var dlg = new OpenFileDialog
        {
            Title = "Add media",
            Filter = "Videos, GIFs and images|" + string.Join(";", MediaDecoder.Extensions.Select(e => "*" + e)),
            Multiselect = true,
        };
        if (dlg.ShowDialog(this) != DialogResult.OK) return;
        string? last = null;
        foreach (var src in dlg.FileNames)
        {
            last = Path.Combine(_settings.MediaFolder!, Path.GetFileName(src));
            if (!string.Equals(Path.GetFullPath(src), Path.GetFullPath(last), StringComparison.OrdinalIgnoreCase))
                File.Copy(src, last, overwrite: true);
        }
        RefreshMediaList();
        if (last != null) Play(last);
    }

    void ApplyRotation()
    {
        _settings.Rotation = Rotations[_rotation.SelectedIndex].Value;
        _settings.Save();
        _player.Show(_settings.MediaPath, _settings.Rotation);
    }

    async Task ToggleScreenOwner()
    {
        if (_busy) return;
        _busy = true;
        _handOff.Enabled = false;
        try
        {
            if (_player.State == PlayerState.Released || Trcc.IsRunning)
            {
                // Take the screen: TRCC runs as admin, so stopping it shows a UAC prompt.
                bool stopped = await Task.Run(Trcc.Stop);
                if (!stopped)
                {
                    MessageBox.Show(this, "TRCC is still running, so the screen stays with TRCC.", "ThermalWrong", MessageBoxButtons.OK, MessageBoxIcon.Information);
                    return;
                }
                _settings.ScreenHandedToTrcc = false;
                _player.SetReleased(false);
            }
            else
            {
                _settings.ScreenHandedToTrcc = true;
                _player.SetReleased(true);
                await Task.Run(() =>
                {
                    _player.WaitUntilReleased(3000);
                    Trcc.Start();
                });
            }
            _settings.Save();
        }
        finally
        {
            _busy = false;
            _handOff.Enabled = true;
        }
    }

    void UpdateStatus()
    {
        bool trccHasIt = _player.State == PlayerState.Released || (_player.State == PlayerState.WaitingForScreen && Trcc.IsRunning);
        _handOff.Text = trccHasIt ? "Take screen from TRCC" : "Hand screen to TRCC";

        var panel = _player.Panel;
        string text = _player.Status;
        if (panel != null) text = $"{panel.Width}×{panel.Height}   ·   {text}";
        if (_player.State == PlayerState.Playing)
        {
            using var self = Process.GetCurrentProcess();
            var set = _player.Current;
            if (_player.Overlay != null)
                text += $"   ·   CPU {(_sensors.CpuTemp is int c ? c + "°" : "–")}   GPU {(_sensors.GpuTemp is int g ? g + "°" : "–")}";
            text += $"   ·   {_player.MeasuredFps:F1} fps   ·   {set?.Packets.Count} frames   ·   {self.PrivateMemorySize64 / 1048576} MB";
        }
        _status.Text = text;
        _tray.Text = Truncate("ThermalWrong: " + _player.Status, 63);

        // Mirror the screen at a few fps while the window is open.
        if (Visible && WindowState != FormWindowState.Minimized)
        {
            int serial = _player.FrameSerial;
            if (serial != _previewSerial && _player.CopyLastJpeg() is byte[] jpeg)
            {
                _previewSerial = serial;
                SetPreview(jpeg, _settings.Rotation ?? panel?.DefaultRotation ?? 0);
            }
        }
    }

    void SetPreview(byte[] jpeg, int rotation)
    {
        using var ms = new MemoryStream(jpeg, writable: false);
        using var decoded = Image.FromStream(ms);
        // Preview at half size: plenty for the window, a quarter of the memory.
        var img = new Bitmap(decoded, decoded.Width / 2, decoded.Height / 2);
        // Undo the mounting rotation so the preview looks the way the screen does.
        img.RotateFlip(rotation switch { 90 => RotateFlipType.Rotate270FlipNone, 180 => RotateFlipType.Rotate180FlipNone, 270 => RotateFlipType.Rotate90FlipNone, _ => RotateFlipType.RotateNoneFlipNone });
        var old = _preview.Image;
        _preview.Image = img;
        old?.Dispose();
    }

    static string Truncate(string s, int max) => s.Length <= max ? s : s[..(max - 1)] + "…";

    bool _syncingAutostart;

    /// <summary>Registers or removes the elevated logon task (one UAC prompt), then shows the real state.</summary>
    async Task SetAutostart(bool on)
    {
        if (_syncingAutostart) return;
        _autostart.Enabled = false;
        bool actual = await Task.Run(() =>
        {
            if (on) Autostart.Enable(Environment.ProcessPath!);
            else Autostart.Disable();
            return Autostart.IsEnabled;
        });
        _syncingAutostart = true;
        _autostart.Checked = actual;
        _syncingAutostart = false;
        _autostart.Enabled = true;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _tray.Dispose();
            _tick.Dispose();
            _preview.Image?.Dispose();
        }
        base.Dispose(disposing);
    }

    void ShowAbout()
    {
        string version = typeof(MainForm).Assembly.GetName().Version?.ToString(3) ?? "";
        MessageBox.Show(this,
            $"ThermalWrong {version} by soybean_alien. MIT License.\n\n" +
            "Plays your own animations and live CPU/GPU temperatures on Thermalright USB LCDs.\n\n" +
            "Built for the Thermalright Dynamic Vision 360 only. Other screens may work, but results may vary.\n\n" +
            "Unofficial project. Not affiliated with, endorsed by or supported by Thermalright.\n\n" +
            "https://github.com/soybean-alien/ThermalWrong",
            "About ThermalWrong", MessageBoxButtons.OK, MessageBoxIcon.Information);
    }

    sealed record MediaItem(string Path)
    {
        public override string ToString() => System.IO.Path.GetFileName(Path);
    }
}

