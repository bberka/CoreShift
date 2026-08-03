using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Windows.Forms;
using CoreShift;

namespace CoreShift.App;

/// <summary>
/// Main dashboard: running GUI app list with native .exe icons, per-app tuning controls
/// (affinity, EcoQoS, priority, background demotion) and the global demotion policy.
/// Edits happen on an in-memory copy of the config; "Save Config" writes config.json and
/// swaps the live config into the CoreManager atomically.
/// </summary>
public sealed class MainForm : Form
{
    private const int WM_NCLBUTTONDOWN = 0x00A1;
    private const int HTCAPTION = 0x0002;

    private static readonly Font UiFont = new("Segoe UI", 9f);
    private static readonly Font UiFontBold = new("Segoe UI", 9f, FontStyle.Bold);

    private readonly CoreManager _manager;
    private readonly Func<bool> _isExiting;
    private AppConfig _edit;      // editable copy; swapped into the manager on Save/Apply
    private Target? _current;     // target bound to the tuning controls
    private bool _loading;        // suppresses change events during programmatic updates

    private Panel _header = null!;
    private Button _btnClose = null!;
    private ListView _list = null!;
    private ImageList _icons = null!;
    private Label _lblApp = null!;
    private RadioButton _rbAll = null!;
    private RadioButton _rbP = null!;
    private RadioButton _rbE = null!;
    private CheckBox _chkEco = null!;
    private CheckBox _chkHigh = null!;
    private CheckBox _chkDemote = null!;
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
        Font = UiFont;
        FormBorderStyle = FormBorderStyle.None;
        StartPosition = FormStartPosition.CenterScreen;
        Size = new Size(840, 600);
        MinimumSize = new Size(780, 540);
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
        _header = new Panel { Dock = DockStyle.Top, Height = 42, BackColor = Color.FromArgb(0x11, 0x11, 0x14) };
        _header.Paint += (_, e) =>
        {
            using var pen = new Pen(Palette.Border);
            e.Graphics.DrawLine(pen, 0, _header.Height - 1, _header.Width, _header.Height - 1);
        };
        _header.MouseDown += OnHeaderMouseDown;

        var title = new Label
        {
            Text = "CoreShift",
            ForeColor = Palette.Text,
            Font = UiFontBold,
            AutoSize = true,
            BackColor = Color.Transparent,
            Location = new Point(14, 11),
        };
        title.MouseDown += OnHeaderMouseDown;

        var subtitle = new Label
        {
            Text = "CPU core · priority · EcoQoS",
            ForeColor = Palette.TextFaint,
            Font = UiFont,
            AutoSize = true,
            BackColor = Color.Transparent,
            Location = new Point(96, 13),
        };
        subtitle.MouseDown += OnHeaderMouseDown;

        _btnClose = new Button
        {
            Text = "✕",
            Dock = DockStyle.Right,
            Width = 44,
            FlatStyle = FlatStyle.Flat,
            BackColor = Color.Transparent,
            ForeColor = Palette.TextDim,
            Font = UiFont,
            Cursor = Cursors.Hand,
        };
        _btnClose.FlatAppearance.BorderSize = 0;
        _btnClose.FlatAppearance.MouseOverBackColor = Color.FromArgb(0xC0, 0x28, 0x28);
        _btnClose.Click += (_, _) => Close(); // hides back to tray (OnFormClosing)

        _status = new Label
        {
            Dock = DockStyle.Bottom,
            Height = 26,
            BackColor = Color.FromArgb(0x10, 0x10, 0x13),
            ForeColor = Palette.TextDim,
            Font = UiFont,
            TextAlign = ContentAlignment.MiddleLeft,
            Padding = new Padding(10, 0, 0, 0),
        };

        // ---- left: app list ----
        var toolbar = new Panel { Dock = DockStyle.Top, Height = 40, BackColor = Palette.Bg };
        var btnRefresh = MakeButton("↻ Refresh");
        var btnAdd = MakeButton("Add Custom Exe…");
        var btnRemove = MakeButton("Remove Target");
        btnRefresh.Margin = new Padding(12, 8, 4, 8);
        btnAdd.Margin = new Padding(4, 8, 4, 8);
        btnRemove.Margin = new Padding(4, 8, 8, 8);
        btnRefresh.Dock = btnAdd.Dock = btnRemove.Dock = DockStyle.Left;
        btnRefresh.Click += (_, _) => RefreshList();
        btnAdd.Click += (_, _) => AddCustom();
        btnRemove.Click += (_, _) => RemoveTarget();
        toolbar.Controls.AddRange(new Control[] { btnRemove, btnAdd, btnRefresh });

        _icons = new ImageList { ImageSize = new Size(16, 16), ColorDepth = ColorDepth.Depth32Bit };
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
            Font = UiFont,
            Margin = new Padding(12, 4, 6, 12),
            SmallImageList = _icons,
            HeaderStyle = ColumnHeaderStyle.Nonclickable,
        };
        _list.Columns.Add("App", 220);
        _list.Columns.Add("Path", 280);
        _list.DrawColumnHeader += OnDrawColumnHeader;
        _list.DrawSubItem += OnDrawSubItem;
        _list.SelectedIndexChanged += OnSelectionChanged;

        var left = new Panel { Dock = DockStyle.Fill, BackColor = Palette.Bg, Padding = new Padding(12, 4, 6, 12) };
        left.Controls.Add(_list);
        left.Controls.Add(toolbar);

        // ---- right: tuning controls ----
        var right = new Panel { Dock = DockStyle.Fill, BackColor = Palette.Bg, Padding = new Padding(6, 4, 12, 12) };

        _lblApp = new Label
        {
            Text = "No app selected",
            ForeColor = Palette.Text,
            Font = UiFontBold,
            AutoSize = false,
            Height = 22,
            Location = new Point(14, 14),
            Size = new Size(270, 22),
        };

        var affinityCard = new CardPanel { Location = new Point(14, 44), Size = new Size(280, 118) };
        affinityCard.Controls.Add(MakeTitle("CPU AFFINITY", 12, 8));
        _rbAll = MakeRadio("All Cores", 16, 30);
        _rbP = MakeRadio("P-Cores only (performance)", 16, 54);
        _rbE = MakeRadio("E-Cores only (efficiency)", 16, 78);
        affinityCard.Controls.AddRange(new Control[] { _rbAll, _rbP, _rbE });

        var tuneCard = new CardPanel { Location = new Point(14, 172), Size = new Size(280, 122) };
        tuneCard.Controls.Add(MakeTitle("ECOQOS & PRIORITY", 12, 8));
        _chkEco = MakeCheck("EcoQoS — Efficiency Mode", 16, 32);
        _chkHigh = MakeCheck("High process priority", 16, 56);
        _chkDemote = MakeCheck("Demote when in background", 16, 80);
        tuneCard.Controls.AddRange(new Control[] { _chkEco, _chkHigh, _chkDemote });

        var bgCard = new CardPanel { Location = new Point(14, 304), Size = new Size(280, 122) };
        bgCard.Controls.Add(MakeTitle("BACKGROUND DEMOTION", 12, 8));
        _chkBgEco = MakeCheck("Push background apps to EcoQoS", 16, 30);
        _chkBgE = MakeCheck("Pin background apps to E-Cores", 16, 54);
        var hint = new Label
        {
            Text = "Applies while a tuned app owns the foreground.",
            ForeColor = Palette.TextFaint,
            Font = new Font("Segoe UI", 7.5f),
            AutoSize = true,
            BackColor = Color.Transparent,
            Location = new Point(16, 84),
        };
        bgCard.Controls.AddRange(new Control[] { _chkBgEco, _chkBgE, hint });

        var btnApply = MakeButton("Apply Now", primary: true);
        var btnSave = MakeButton("Save Config");
        btnApply.Location = new Point(14, 440);
        btnSave.Location = new Point(104, 440);

        right.Controls.Add(_lblApp);
        right.Controls.Add(affinityCard);
        right.Controls.Add(tuneCard);
        right.Controls.Add(bgCard);
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
        body.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 64f));
        body.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 36f));
        body.Controls.Add(left, 0, 0);
        body.Controls.Add(right, 1, 0);

        Controls.Add(body);
        Controls.Add(_status);
        Controls.Add(_header);
        Controls.Add(_btnClose);

        // events
        foreach (var rb in new[] { _rbAll, _rbP, _rbE }) rb.CheckedChanged += (_, _) => UpdateTargetFromUi();
        foreach (var cb in new[] { _chkEco, _chkHigh, _chkDemote }) cb.CheckedChanged += (_, _) => UpdateTargetFromUi();
        _chkBgEco.CheckedChanged += (_, _) => UpdateTargetFromUi();
        _chkBgE.CheckedChanged += (_, _) => UpdateTargetFromUi();
        btnApply.Click += (_, _) => ApplyNow();
        btnSave.Click += (_, _) => SaveConfig();
    }

    // ================================================================== list population

    private void RefreshList()
    {
        string? selectedName = _list.SelectedItems.Count > 0
            ? (_list.SelectedItems[0].Tag as AppEntry)?.Name
            : null;

        _list.BeginUpdate();
        _list.Items.Clear();
        _icons.Images.Clear();

        var entries = EnumerateGuiApps();
        var runningNames = new HashSet<string>(entries.Select(e => AppIdentity.NormalizeName(e.Name)), StringComparer.Ordinal);

        // Include configured targets that are not running so they stay editable.
        foreach (var t in Targets.Where(t => !string.IsNullOrWhiteSpace(t.Name)))
        {
            if (!runningNames.Contains(AppIdentity.NormalizeName(t.Name)))
                entries.Add(new AppEntry { Name = t.Name, Path = t.Path ?? string.Empty, Running = false });
        }

        foreach (var e in entries.OrderBy(e => e.Name, StringComparer.OrdinalIgnoreCase))
        {
            _icons.Images.Add(LoadAppIcon(e));
            int idx = _icons.Images.Count - 1;
            var lvi = new ListViewItem(e.Name) { Tag = e, ImageIndex = idx };
            lvi.SubItems.Add(e.Running ? e.Path : $"(not running) {e.Path}");
            lvi.ForeColor = e.Running ? Palette.Text : Palette.TextDim;
            _list.Items.Add(lvi);
        }
        _list.EndUpdate();

        if (selectedName is not null)
        {
            foreach (ListViewItem item in _list.Items)
            {
                if ((item.Tag as AppEntry)?.Name.Equals(selectedName, StringComparison.OrdinalIgnoreCase) == true)
                {
                    item.Selected = true;
                    _list.EnsureVisible(item.Index);
                    break;
                }
            }
        }
    }

    private static List<AppEntry> EnumerateGuiApps()
    {
        var result = new List<AppEntry>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        int self = Environment.ProcessId;

        NativeMethods.EnumWindows((hwnd, _) =>
        {
            if (!NativeMethods.IsWindowVisible(hwnd)) return true;
            if (NativeMethods.GetWindow(hwnd, NativeMethods.GW_OWNER) != IntPtr.Zero) return true; // owned windows skip
            NativeMethods.GetWindowThreadProcessId(hwnd, out uint pid);
            if (pid == 0 || pid == self) return true;

            string? path = null;
            using (var h = NativeMethods.OpenProcess(NativeMethods.PROCESS_QUERY_LIMITED_INFORMATION, false, pid))
            {
                if (!h.IsInvalid)
                {
                    var sb = new StringBuilder(1024);
                    uint len = (uint)sb.Capacity;
                    if (NativeMethods.QueryFullProcessImageName(h, 0, sb, ref len))
                        path = sb.ToString();
                }
            }
            if (path is null) return true;

            var name = Path.GetFileName(path);
            if (CoreManager.IsProtectedProcess((int)pid, name)) return true;
            if (!seen.Add(AppIdentity.NormalizeName(name))) return true; // one row per app

            result.Add(new AppEntry { Name = name, Path = path, Running = true });
            return true;
        }, IntPtr.Zero);

        return result;
    }

    private static Icon LoadAppIcon(AppEntry e)
    {
        try
        {
            if (!string.IsNullOrEmpty(e.Path) && File.Exists(e.Path))
            {
                var ico = Icon.ExtractAssociatedIcon(e.Path);
                if (ico is not null) return ico;
            }
        }
        catch { /* fall through to generic icon */ }
        return (Icon)SystemIcons.Application.Clone();
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

            var entry = item.Tag as AppEntry;
            if (entry is null) return;
            var t = Targets.FirstOrDefault(x => x.Matches(entry.Name));
            if (t is null)
            {
                t = new Target { Name = entry.Name, Path = File.Exists(entry.Path) ? entry.Path : null };
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

        // First meaningful change to an app that has no target yet: promote it to a real target.
        if (!Targets.Contains(_current))
            Targets.Add(_current);
    }

    private void RefreshPolicyUi()
    {
        _loading = true;
        try
        {
            _chkBgEco.Checked = _edit.BackgroundEcoQoS;
            _chkBgE.Checked = _edit.BackgroundECores;
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
        using var dlg = new OpenFileDialog
        {
            Title = "Select an executable to manage",
            Filter = "Programs (*.exe)|*.exe|All files (*.*)|*.*",
            CheckFileExists = true,
        };
        if (dlg.ShowDialog(this) != DialogResult.OK) return;

        var name = Path.GetFileName(dlg.FileName);
        var t = Targets.FirstOrDefault(x => x.Matches(name));
        if (t is null)
        {
            t = new Target { Name = name, Path = dlg.FileName };
            Targets.Add(t);
        }
        else
        {
            t.Path = dlg.FileName;
        }

        RefreshList();
        SelectByName(name);
        UpdateStatus($"Added {name} — choose settings, then Save Config.");
    }

    private void RemoveTarget()
    {
        if (_list.SelectedItems.Count == 0) { UpdateStatus("Select an app first."); return; }

        var entry = _list.SelectedItems[0].Tag as AppEntry;
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
            if ((item.Tag as AppEntry)?.Name.Equals(name, StringComparison.OrdinalIgnoreCase) == true)
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

    private void OnDrawColumnHeader(object? sender, DrawListViewColumnHeaderEventArgs e)
    {
        using var bg = new SolidBrush(Palette.Panel);
        e.Graphics.FillRectangle(bg, e.Bounds);
        var b = e.Bounds;
        b.X += 6; b.Width -= 6;
        TextRenderer.DrawText(e.Graphics, e.Header?.Text ?? string.Empty, UiFont, b, Palette.TextDim,
            TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
    }

    private void OnDrawSubItem(object? sender, DrawListViewSubItemEventArgs e)
    {
        bool selected = e.Item?.Selected ?? false;
        var bg = selected ? Palette.Selection : (e.ItemIndex % 2 == 0 ? Palette.Bg : Palette.RowAlt);
        using var brush = new SolidBrush(bg);
        e.Graphics.FillRectangle(brush, e.Bounds);

        var bounds = e.Bounds;
        if (e.ColumnIndex == 0)
        {
            if (e.ItemIndex >= 0 && e.ItemIndex < _icons.Images.Count)
            {
                var img = _icons.Images[e.ItemIndex];
                e.Graphics.DrawImage(img, bounds.X + 4, bounds.Y + (bounds.Height - img.Height) / 2);
            }
            bounds = new Rectangle(bounds.X + 24, bounds.Y, bounds.Width - 24, bounds.Height);
        }

        var color = selected ? Palette.Text : e.Item?.ForeColor ?? Palette.Text;
        TextRenderer.DrawText(e.Graphics, e.SubItem?.Text ?? string.Empty, UiFont, bounds, color,
            TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
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
        Font = UiFont,
        Height = 32,
        AutoSize = true,
        Padding = new Padding(12, 0, 12, 0),
        Cursor = Cursors.Hand,
    };

    private static Label MakeTitle(string text, int x, int y) => new()
    {
        Text = text,
        ForeColor = Palette.TextDim,
        Font = new Font("Segoe UI", 8f, FontStyle.Bold),
        AutoSize = true,
        BackColor = Color.Transparent,
        Location = new Point(x, y),
    };

    private static RadioButton MakeRadio(string text, int x, int y) => new()
    {
        Text = text,
        ForeColor = Palette.Text,
        Font = UiFont,
        AutoSize = true,
        BackColor = Color.Transparent,
        Cursor = Cursors.Hand,
        Location = new Point(x, y),
    };

    private static CheckBox MakeCheck(string text, int x, int y) => new()
    {
        Text = text,
        ForeColor = Palette.Text,
        Font = UiFont,
        AutoSize = true,
        BackColor = Color.Transparent,
        Cursor = Cursors.Hand,
        Location = new Point(x, y),
    };

    private sealed class AppEntry
    {
        public required string Name;
        public required string Path;
        public bool Running;
    }

    /// <summary>ListView with double-buffering enabled (AOT-safe — no reflection hacks).</summary>
    private sealed class DarkListView : ListView
    {
        public DarkListView()
        {
            SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint, true);
        }
    }

    /// <summary>Simple card panel with a dark border.</summary>
    private sealed class CardPanel : Panel
    {
        public CardPanel()
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint |
                     ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            BackColor = Palette.Panel;
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            using var pen = new Pen(Palette.Border);
            e.Graphics.DrawRectangle(pen, 0, 0, Width - 1, Height - 1);
        }
    }
}
