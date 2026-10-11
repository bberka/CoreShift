using System.Drawing;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows.Forms;
using CoreShift;

namespace CoreShift.App;

/// <summary>
/// Main dashboard: the configured target list (with native .exe icons), per-app tuning
/// controls (affinity, EcoQoS, priority, background demotion) and global policy.
/// Edits happen on an in-memory copy of the config; "Save Config" writes config.json and
/// swaps the live config into the CoreManager atomically.
/// </summary>
public sealed class MainForm : Form
{
    private const int WM_NCLBUTTONDOWN = 0x00A1;
    private const int HTCAPTION = 0x0002;

    private readonly CoreManager _manager;
    private readonly Func<bool> _isExiting;
    private AppConfig _edit;      // editable copy; swapped into the manager on Save/Apply
    private Target? _current;     // target bound to the tuning controls
    private bool _loading;        // suppresses change events during programmatic updates

    private Panel _header = null!;
    private ListView _list = null!;
    private ImageList _icons = null!;
    private Label _emptyHint = null!;
    private Label _lblApp = null!;
    private RadioButton _rbAll = null!;
    private RadioButton _rbP = null!;
    private RadioButton _rbE = null!;
    private CheckBox _chkEco = null!;
    private CheckBox _chkHigh = null!;
    private CheckBox _chkDemote = null!;
    private CheckBox _chkAuto = null!;
    private CheckBox _chkAutoStart = null!;
    private CheckBox _chkBgEco = null!;
    private CheckBox _chkBgE = null!;
    private Label _status = null!;

    /// <summary>Null-safe access to the editable target list (defensive against bad config files).</summary>
    private List<Target> Targets => _edit.Targets ??= new List<Target>();

    public MainForm(CoreManager manager, AppConfig config, Func<bool> isExiting)
    {
        _manager = manager;
        _isExiting = isExiting;

        // Clone the config so dashboard edits stay local until "Save Config" commits them.
        try
        {
            var json = JsonSerializer.Serialize(config, AppConfigJsonContext.Default.AppConfig);
            _edit = JsonSerializer.Deserialize(json, AppConfigJsonContext.Default.AppConfig) ?? new AppConfig();
        }
        catch
        {
            _edit = new AppConfig();
        }
        _edit.Targets ??= new List<Target>();

        BackColor = Palette.Bg;
        ForeColor = Palette.Text;
        Font = UiKit.Font;
        FormBorderStyle = FormBorderStyle.None;
        StartPosition = FormStartPosition.CenterScreen;
        Size = new Size(1080, 640);
        MinimumSize = new Size(1000, 600);
        Text = "CoreShift";
        Icon = IconFactory.AppIcon;

        BuildUi();
        RefreshList();
        RefreshPolicyUi();
        UpdateStatus("Ready");
    }

    // ================================================================== UI construction

    private void BuildUi()
    {
        // ---- header (icon + title + close live inside this panel) ----
        _header = new Panel { Dock = DockStyle.Top, Height = 42, BackColor = Color.FromArgb(0x11, 0x11, 0x14) };
        _header.Paint += (_, e) =>
        {
            using var pen = new Pen(Palette.Border);
            e.Graphics.DrawLine(pen, 0, _header.Height - 1, _header.Width, _header.Height - 1);
        };
        _header.MouseDown += OnHeaderMouseDown;

        var iconPb = new PictureBox
        {
            Image = IconFactory.AppIcon.ToBitmap(),
            Size = new Size(20, 20),
            SizeMode = PictureBoxSizeMode.Zoom,
            BackColor = Color.Transparent,
            Location = new Point(14, 11),
        };
        iconPb.MouseDown += OnHeaderMouseDown;

        var title = new Label
        {
            Text = "CoreShift",
            ForeColor = Palette.Text,
            Font = UiKit.FontBold,
            AutoSize = true,
            BackColor = Color.Transparent,
            Location = new Point(42, 11),
        };
        title.MouseDown += OnHeaderMouseDown;

        var subtitle = new Label
        {
            Text = "CPU core · priority · EcoQoS",
            ForeColor = Palette.TextFaint,
            Font = UiKit.Font,
            AutoSize = true,
            BackColor = Color.Transparent,
            Location = new Point(122, 13),
        };
        subtitle.MouseDown += OnHeaderMouseDown;

        var btnClose = new Button
        {
            Text = "✕",
            Dock = DockStyle.Right,
            Width = 44,
            FlatStyle = FlatStyle.Flat,
            BackColor = Color.Transparent,
            ForeColor = Palette.TextDim,
            Font = UiKit.Font,
            Cursor = Cursors.Hand,
        };
        btnClose.FlatAppearance.BorderSize = 0;
        btnClose.FlatAppearance.MouseOverBackColor = Color.FromArgb(0xC0, 0x28, 0x28);
        btnClose.Click += (_, _) => Close(); // hides back to tray (OnFormClosing)

        _header.Controls.Add(iconPb);
        _header.Controls.Add(title);
        _header.Controls.Add(subtitle);
        _header.Controls.Add(btnClose);

        // ---- status bar ----
        _status = new Label
        {
            Dock = DockStyle.Bottom,
            Height = 26,
            BackColor = Color.FromArgb(0x10, 0x10, 0x13),
            ForeColor = Palette.TextDim,
            Font = UiKit.Font,
            TextAlign = ContentAlignment.MiddleLeft,
            Padding = new Padding(10, 0, 0, 0),
        };

        // ---- left: configured target list ----
        var toolbar = new Panel { Dock = DockStyle.Top, Height = 40, BackColor = Palette.Bg };
        var btnAdd = MakeButton("+ Add App…", primary: true);
        var btnRemove = MakeButton("− Remove");
        btnAdd.Margin = new Padding(14, 8, 4, 8);
        btnRemove.Margin = new Padding(4, 8, 8, 8);
        btnAdd.Dock = btnRemove.Dock = DockStyle.Left;
        btnAdd.Click += (_, _) => AddCustom();
        btnRemove.Click += (_, _) => RemoveTarget();
        toolbar.Controls.AddRange(new Control[] { btnRemove, btnAdd });

        _icons = new ImageList { ImageSize = new Size(20, 20), ColorDepth = ColorDepth.Depth32Bit };
        _list = new DarkListView
        {
            Dock = DockStyle.Fill,
            View = View.Details,
            FullRowSelect = true,
            HideSelection = false,
            OwnerDraw = true,
            BorderStyle = BorderStyle.None,
            BackColor = Palette.Bg,
            ForeColor = Palette.Text,
            Font = UiKit.Font,
            SmallImageList = _icons,
            HeaderStyle = ColumnHeaderStyle.Nonclickable,
        };
        _list.Columns.Add("App", 260);
        _list.Columns.Add("Path", -2); // fill remaining width
        _list.DrawColumnHeader += ListPainter.DrawColumnHeader;
        _list.DrawSubItem += (s, e) => ListPainter.DrawSubItem(s, e, _icons);
        _list.SelectedIndexChanged += OnSelectionChanged;

        _emptyHint = new Label
        {
            Dock = DockStyle.Fill,
            Text = "No apps configured — use “+ Add App…” to pick a running app or an exe.",
            ForeColor = Palette.TextFaint,
            Font = UiKit.Font,
            TextAlign = ContentAlignment.MiddleCenter,
            BackColor = Palette.Bg,
        };

        var left = new Panel { Dock = DockStyle.Fill, BackColor = Palette.Bg, Padding = new Padding(14, 4, 8, 12) };
        left.Controls.Add(_list);
        left.Controls.Add(_emptyHint); // covers the list only while it is empty
        left.Controls.Add(toolbar);

        // ---- right: tuning controls ----
        var right = new Panel { Dock = DockStyle.Fill, BackColor = Palette.Bg, Padding = new Padding(6, 4, 14, 12) };

        _lblApp = new Label
        {
            Text = "No app selected",
            ForeColor = Palette.Text,
            Font = UiKit.FontBold,
            AutoSize = false,
            Height = 22,
            Location = new Point(14, 14),
            Size = new Size(320, 22),
        };

        var affinityCard = new CardPanel { Location = new Point(14, 44), Size = new Size(336, 118) };
        affinityCard.Controls.Add(MakeTitle("CPU AFFINITY", 12, 8));
        _rbAll = MakeRadio("All Cores", 16, 30);
        _rbP = MakeRadio("P-Cores only (performance)", 16, 54);
        _rbE = MakeRadio("E-Cores only (efficiency)", 16, 78);
        affinityCard.Controls.AddRange(new Control[] { _rbAll, _rbP, _rbE });

        var tuneCard = new CardPanel { Location = new Point(14, 172), Size = new Size(336, 122) };
        tuneCard.Controls.Add(MakeTitle("ECOQOS & PRIORITY", 12, 8));
        _chkEco = MakeCheck("EcoQoS — Efficiency Mode", 16, 32);
        _chkHigh = MakeCheck("High process priority", 16, 56);
        _chkDemote = MakeCheck("Demote when in background", 16, 80);
        tuneCard.Controls.AddRange(new Control[] { _chkEco, _chkHigh, _chkDemote });

        var policyCard = new CardPanel { Location = new Point(14, 304), Size = new Size(336, 158) };
        policyCard.Controls.Add(MakeTitle("GLOBAL POLICY", 12, 8));
        _chkAuto = MakeCheck("Auto-detect fullscreen games → P-Cores", 16, 30);
        _chkBgEco = MakeCheck("Push background apps to EcoQoS", 16, 54);
        _chkBgE = MakeCheck("Pin background apps to E-Cores", 16, 78);
        _chkAutoStart = MakeCheck("Start with Windows (silent tray)", 16, 102);
        var hint = new Label
        {
            Text = "Auto-detect applies to untargeted fullscreen windows;",
            ForeColor = Palette.TextFaint,
            Font = UiKit.FontSmall,
            AutoSize = true,
            BackColor = Color.Transparent,
            Location = new Point(16, 128),
        };
        var hint2 = new Label
        {
            Text = "background demotion applies while a tuned app is focused.",
            ForeColor = Palette.TextFaint,
            Font = UiKit.FontSmall,
            AutoSize = true,
            BackColor = Color.Transparent,
            Location = new Point(16, 143),
        };
        policyCard.Controls.AddRange(new Control[] { _chkAuto, _chkBgEco, _chkBgE, _chkAutoStart, hint, hint2 });

        var btnApply = MakeButton("Apply Now", primary: true);
        var btnSave = MakeButton("Save Config");
        btnApply.Location = new Point(14, 476);
        btnSave.Location = new Point(130, 476);

        right.Controls.Add(_lblApp);
        right.Controls.Add(affinityCard);
        right.Controls.Add(tuneCard);
        right.Controls.Add(policyCard);
        right.Controls.Add(btnApply);
        right.Controls.Add(btnSave);

        // ---- body ----
        var body = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 2,
            RowCount = 1,
            BackColor = Palette.Bg,
        };
        body.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 62f));
        body.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 38f));
        body.Controls.Add(left, 0, 0);
        body.Controls.Add(right, 1, 0);

        Controls.Add(body);
        Controls.Add(_status);
        Controls.Add(_header);

        // events
        foreach (var rb in new[] { _rbAll, _rbP, _rbE }) rb.CheckedChanged += (_, _) => UpdateTargetFromUi();
        foreach (var cb in new[] { _chkEco, _chkHigh, _chkDemote }) cb.CheckedChanged += (_, _) => UpdateTargetFromUi();
        _chkAuto.CheckedChanged += (_, _) => UpdateTargetFromUi();
        _chkAutoStart.CheckedChanged += (_, _) =>
        {
            if (_loading) return;
            AutoStart.SetEnabled(_chkAutoStart.Checked);
            UpdateStatus(_chkAutoStart.Checked ? "Auto-start enabled." : "Auto-start disabled.");
        };
        _chkBgEco.CheckedChanged += (_, _) => UpdateTargetFromUi();
        _chkBgE.CheckedChanged += (_, _) => UpdateTargetFromUi();
        btnApply.Click += (_, _) => ApplyNow();
        btnSave.Click += (_, _) => SaveConfig();
    }

    // ================================================================== list population (configured targets only)

    private void RefreshList()
    {
        string? selectedName = _list.SelectedItems.Count > 0
            ? (_list.SelectedItems[0].Tag as AppInfo)?.Name
            : null;

        _list.BeginUpdate();
        _list.Items.Clear();
        _icons.Images.Clear();

        var entries = Targets
            .Where(t => !string.IsNullOrWhiteSpace(t.Name))
            .Select(t => new AppInfo(t.Name, t.Path ?? string.Empty, AppScanner.IsProcessRunning(t.Name)))
            .OrderBy(e => e.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();

        foreach (var e in entries)
        {
            _icons.Images.Add(AppScanner.LoadAppIcon(e));
            int idx = _icons.Images.Count - 1;
            var lvi = new ListViewItem(e.Name) { Tag = e, ImageIndex = idx };
            string pathText = e.Path.Length > 0 ? e.Path : "(no path)";
            lvi.SubItems.Add(e.Running ? pathText : $"(not running) {pathText}");
            lvi.ForeColor = e.Running ? Palette.Text : Palette.TextDim;
            _list.Items.Add(lvi);
        }
        _list.EndUpdate();
        _emptyHint.Visible = entries.Count == 0;

        if (selectedName is not null)
        {
            foreach (ListViewItem item in _list.Items)
            {
                if ((item.Tag as AppInfo)?.Name.Equals(selectedName, StringComparison.OrdinalIgnoreCase) == true)
                {
                    item.Selected = true;
                    _list.EnsureVisible(item.Index);
                    break;
                }
            }
        }
    }

    // ================================================================== selection & tuning

    private void OnSelectionChanged(object? sender, EventArgs e)
    {
        var item = _list.SelectedItems.Count > 0 ? _list.SelectedItems[0] : null;
        _loading = true;
        try
        {
            if (item is null)
            {
                _current = null;
                _lblApp.Text = "No app selected";
                SetTuningEnabled(false);
                return;
            }

            var entry = item.Tag as AppInfo;
            if (entry is null) return;
            var t = Targets.FirstOrDefault(x => x.Matches(entry.Name));
            if (t is null)
            {
                t = new Target { Name = entry.Name, Path = string.IsNullOrEmpty(entry.Path) ? null : entry.Path };
            }
            _current = t;
            _lblApp.Text = t.Name;

            SetTuningEnabled(true);
            _rbAll.Checked = t.Affinity == AffinityMode.AllCores;
            _rbP.Checked = t.Affinity == AffinityMode.PCoresOnly;
            _rbE.Checked = t.Affinity == AffinityMode.ECoresOnly;
            _chkEco.Checked = t.EcoQoS;
            _chkHigh.Checked = t.HighPriority;
            _chkDemote.Checked = t.DemoteInBackground;
        }
        finally { _loading = false; }
    }

    private void UpdateTargetFromUi()
    {
        if (_loading || _current is null) return;

        var mode = _rbAll.Checked ? AffinityMode.AllCores
            : _rbP.Checked ? AffinityMode.PCoresOnly
            : _rbE.Checked ? AffinityMode.ECoresOnly
            : AffinityMode.None;

        _current.Affinity = mode;
        _current.EcoQoS = _chkEco.Checked;
        _current.HighPriority = _chkHigh.Checked;
        _current.DemoteInBackground = _chkDemote.Checked;

        _edit.BackgroundEcoQoS = _chkBgEco.Checked;
        _edit.BackgroundECores = _chkBgE.Checked;
        _edit.AutoDetectGames = _chkAuto.Checked;

        // First meaningful change to an app that has no target yet: promote it to a real target.
        if (!Targets.Contains(_current))
            Targets.Add(_current);
    }

    private void RefreshPolicyUi()
    {
        _loading = true;
        try
        {
            _chkAuto.Checked = _edit.AutoDetectGames;
            _chkBgEco.Checked = _edit.BackgroundEcoQoS;
            _chkBgE.Checked = _edit.BackgroundECores;
            _chkAutoStart.Checked = AutoStart.IsEnabled();
        }
        finally { _loading = false; }
    }

    private void SetTuningEnabled(bool enabled)
    {
        _rbAll.Enabled = _rbP.Enabled = _rbE.Enabled = enabled;
        _chkEco.Enabled = _chkHigh.Enabled = _chkDemote.Enabled = enabled;
    }

    // ================================================================== actions

    private void ApplyNow()
    {
        UpdateTargetFromUi();
        _manager.SetConfig(_edit);
        _manager.ApplyNow();
        UpdateStatus("Applied current policy to running processes.");
    }

    private void SaveConfig()
    {
        UpdateTargetFromUi();
        ConfigStore.Save(_edit);
        _manager.SetConfig(_edit);
        UpdateStatus($"Saved {Targets.Count} target(s) → {ConfigStore.ConfigPath}");
    }

    private void AddCustom()
    {
        using var dlg = new AddAppDialog();
        if (dlg.ShowDialog(this) != DialogResult.OK || string.IsNullOrEmpty(dlg.SelectedPath)) return;

        var name = Path.GetFileName(dlg.SelectedPath);
        var t = Targets.FirstOrDefault(x => x.Matches(name));
        if (t is null)
        {
            t = new Target { Name = name, Path = dlg.SelectedPath };
            Targets.Add(t);
        }
        else
        {
            t.Path = dlg.SelectedPath;
        }

        RefreshList();
        SelectByName(name);
        UpdateStatus($"Added {name} — choose settings, then Save Config.");
    }

    private void RemoveTarget()
    {
        if (_list.SelectedItems.Count == 0) { UpdateStatus("Select an app first."); return; }

        var entry = _list.SelectedItems[0].Tag as AppInfo;
        if (entry is null) { UpdateStatus("Select an app first."); return; }
        var t = Targets.FirstOrDefault(x => x.Matches(entry.Name));
        if (t is not null) Targets.Remove(t);
        if (ReferenceEquals(_current, t)) _current = null;

        RefreshList();
        UpdateStatus($"Removed target {entry.Name}.");
    }

    private void SelectByName(string name)
    {
        foreach (ListViewItem item in _list.Items)
        {
            if ((item.Tag as AppInfo)?.Name.Equals(name, StringComparison.OrdinalIgnoreCase) == true)
            {
                item.Selected = true;
                item.Focused = true;
                _list.EnsureVisible(item.Index);
                return;
            }
        }
    }

    private void UpdateStatus(string text) => _status.Text = $"{CpuTopology.Description}   •   {text}";

    // ================================================================== theming & window chrome

    private void OnHeaderMouseDown(object? sender, MouseEventArgs e)
    {
        if (e.Button != MouseButtons.Left) return;
        NativeMethods.ReleaseCapture();
        NativeMethods.SendMessage(Handle, WM_NCLBUTTONDOWN, (IntPtr)HTCAPTION, IntPtr.Zero);
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        // Dashboard close = back to tray. Real exit only happens from the tray menu.
        if (!_isExiting())
        {
            e.Cancel = true;
            Hide();
            return;
        }
        base.OnFormClosing(e);
    }

    // ================================================================== small helpers

    private static Button MakeButton(string text, bool primary = false) => new()
    {
        Text = text,
        FlatStyle = FlatStyle.Flat,
        BackColor = primary ? Palette.Accent : Palette.Panel,
        ForeColor = primary ? Color.FromArgb(0x0D, 0x0D, 0x0E) : Palette.Text,
        Font = UiKit.Font,
        Height = 32,
        AutoSize = true,
        Padding = new Padding(12, 0, 12, 0),
        Cursor = Cursors.Hand,
    };

    private static Label MakeTitle(string text, int x, int y) => new()
    {
        Text = text,
        ForeColor = Palette.TextDim,
        Font = UiKit.FontCaption,
        AutoSize = true,
        BackColor = Color.Transparent,
        Location = new Point(x, y),
    };

    private static RadioButton MakeRadio(string text, int x, int y) => new()
    {
        Text = text,
        ForeColor = Palette.Text,
        Font = UiKit.Font,
        AutoSize = true,
        BackColor = Color.Transparent,
        Cursor = Cursors.Hand,
        Location = new Point(x, y),
    };

    private static CheckBox MakeCheck(string text, int x, int y) => new()
    {
        Text = text,
        ForeColor = Palette.Text,
        Font = UiKit.Font,
        AutoSize = true,
        BackColor = Color.Transparent,
        Cursor = Cursors.Hand,
        Location = new Point(x, y),
    };
}
