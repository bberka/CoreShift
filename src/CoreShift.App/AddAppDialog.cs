using System.Drawing;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace CoreShift.App;

/// <summary>
/// "Add Application" dialog: pick from the currently running GUI apps (shown with full
/// paths and native icons) or browse for an arbitrary executable.
/// </summary>
public sealed class AddAppDialog : Form
{
    private const int WM_NCLBUTTONDOWN = 0x00A1;
    private const int HTCAPTION = 0x0002;

    private readonly ListView _list;
    private readonly ImageList _icons;
    private readonly Label _emptyHint;

    /// <summary>Full path of the chosen executable (set when DialogResult == OK).</summary>
    public string? SelectedPath { get; private set; }

    /// <summary>File name of the chosen executable.</summary>
    public string? SelectedName { get; private set; }

    public AddAppDialog()
    {
        BackColor = Palette.Bg;
        ForeColor = Palette.Text;
        Font = UiKit.Font;
        FormBorderStyle = FormBorderStyle.None;
        StartPosition = FormStartPosition.CenterParent;
        Size = new Size(700, 540);
        MinimumSize = new Size(640, 460);
        Text = "Add Application";
        Icon = IconFactory.AppIcon;

        // ---- header ----
        var header = new Panel { Dock = DockStyle.Top, Height = 42, BackColor = Color.FromArgb(0x11, 0x11, 0x14) };
        header.Paint += (_, e) =>
        {
            using var pen = new Pen(Palette.Border);
            e.Graphics.DrawLine(pen, 0, header.Height - 1, header.Width, header.Height - 1);
        };
        header.MouseDown += OnHeaderMouseDown;

        var title = new Label
        {
            Text = "Add Application",
            ForeColor = Palette.Text,
            Font = UiKit.FontBold,
            AutoSize = true,
            BackColor = Color.Transparent,
            Location = new Point(14, 11),
        };
        title.MouseDown += OnHeaderMouseDown;

        var subtitle = new Label
        {
            Text = "running apps with full paths, or browse for an exe",
            ForeColor = Palette.TextFaint,
            Font = UiKit.Font,
            AutoSize = true,
            BackColor = Color.Transparent,
            Location = new Point(132, 13),
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
        btnClose.Click += (_, _) => { DialogResult = DialogResult.Cancel; Close(); };
        header.Controls.Add(title);
        header.Controls.Add(subtitle);
        header.Controls.Add(btnClose);

        // ---- list ----
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
            Margin = new Padding(14, 12, 14, 8),
        };
        _list.Columns.Add("App", 220);
        _list.Columns.Add("Full Path", -2);
        _list.DrawColumnHeader += ListPainter.DrawColumnHeader;
        _list.DrawSubItem += (s, e) => ListPainter.DrawSubItem(s, e, _icons);
        _list.DoubleClick += (_, _) => ChooseSelected();

        _emptyHint = new Label
        {
            Dock = DockStyle.Fill,
            Text = "No running GUI apps found.",
            ForeColor = Palette.TextFaint,
            Font = UiKit.Font,
            TextAlign = ContentAlignment.MiddleCenter,
            BackColor = Palette.Bg,
        };

        // ---- footer ----
        var footer = new Panel { Dock = DockStyle.Bottom, Height = 54, BackColor = Palette.Bg };
        var btnCancel = MakeButton("Cancel");
        var btnRefresh = MakeButton("↻ Refresh");
        var btnBrowse = MakeButton("Browse Executable…");
        var btnAdd = MakeButton("Add Selected", primary: true);
        btnAdd.Margin = new Padding(0, 10, 14, 10);
        btnBrowse.Margin = new Padding(0, 10, 8, 10);
        btnRefresh.Margin = new Padding(0, 10, 8, 10);
        btnCancel.Margin = new Padding(0, 10, 0, 10);
        btnAdd.Dock = btnBrowse.Dock = btnRefresh.Dock = btnCancel.Dock = DockStyle.Right;
        btnCancel.Click += (_, _) => { DialogResult = DialogResult.Cancel; Close(); };
        btnRefresh.Click += (_, _) => RefreshList();
        btnBrowse.Click += (_, _) => Browse();
        btnAdd.Click += (_, _) => ChooseSelected();
        footer.Controls.Add(btnAdd);
        footer.Controls.Add(btnBrowse);
        footer.Controls.Add(btnRefresh);
        footer.Controls.Add(btnCancel);

        var body = new Panel { Dock = DockStyle.Fill, BackColor = Palette.Bg, Padding = new Padding(14, 12, 14, 8) };
        body.Controls.Add(_list);
        body.Controls.Add(_emptyHint);

        Controls.Add(body);
        Controls.Add(footer);
        Controls.Add(header);

        RefreshList();
    }

    private void RefreshList()
    {
        _list.BeginUpdate();
        _list.Items.Clear();
        _icons.Images.Clear();

        var apps = AppScanner.EnumerateGuiApps()
            .OrderBy(a => a.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();

        foreach (var a in apps)
        {
            _icons.Images.Add(AppScanner.LoadAppIcon(a));
            int idx = _icons.Images.Count - 1;
            var lvi = new ListViewItem(a.Name) { Tag = a, ImageIndex = idx };
            lvi.SubItems.Add(a.Path);
            _list.Items.Add(lvi);
        }
        _list.EndUpdate();
        _emptyHint.Visible = apps.Count == 0;
    }

    private void ChooseSelected()
    {
        if (_list.SelectedItems.Count == 0)
        {
            SetStatus("Select an app from the list, or Browse for an executable.");
            return;
        }
        var app = _list.SelectedItems[0].Tag as AppInfo;
        if (app is null || string.IsNullOrEmpty(app.Path)) return;

        SelectedPath = app.Path;
        SelectedName = app.Name;
        DialogResult = DialogResult.OK;
        Close();
    }

    private void Browse()
    {
        using var dlg = new OpenFileDialog
        {
            Title = "Select an executable to manage",
            Filter = "Programs (*.exe)|*.exe|All files (*.*)|*.*",
            CheckFileExists = true,
        };
        if (dlg.ShowDialog(this) != DialogResult.OK) return;

        SelectedPath = dlg.FileName;
        SelectedName = Path.GetFileName(dlg.FileName);
        DialogResult = DialogResult.OK;
        Close();
    }

    private void SetStatus(string text) => _emptyHint.Text = text;

    private void OnHeaderMouseDown(object? sender, MouseEventArgs e)
    {
        if (e.Button != MouseButtons.Left) return;
        NativeMethods.ReleaseCapture();
        NativeMethods.SendMessage(Handle, WM_NCLBUTTONDOWN, (IntPtr)HTCAPTION, IntPtr.Zero);
    }

    private static Button MakeButton(string text, bool primary = false) => new()
    {
        Text = text,
        FlatStyle = FlatStyle.Flat,
        BackColor = primary ? Palette.Accent : Palette.Panel,
        ForeColor = primary ? Color.FromArgb(0x0D, 0x0D, 0x0E) : Palette.Text,
        Font = UiKit.Font,
        Height = 34,
        AutoSize = true,
        Padding = new Padding(12, 0, 12, 0),
        Cursor = Cursors.Hand,
    };
}
