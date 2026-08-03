using System.Drawing;
using System.Windows.Forms;

namespace CoreShift.App;

/// <summary>Shared palette for the tray menu and the dashboard ("Dark Light Theme").</summary>
public static class Palette
{
    public static readonly Color Bg = Color.FromArgb(0x0D, 0x0D, 0x0E);      // dashboard background
    public static readonly Color MenuBg = Color.FromArgb(0x0F, 0x0F, 0x11);  // context menu background
    public static readonly Color Panel = Color.FromArgb(0x12, 0x12, 0x15);
    public static readonly Color Border = Color.FromArgb(0x2A, 0x2A, 0x30);
    public static readonly Color Hover = Color.FromArgb(0x22, 0x22, 0x2A);
    public static readonly Color RowAlt = Color.FromArgb(0x10, 0x10, 0x13);
    public static readonly Color Selection = Color.FromArgb(0x1E, 0x2A, 0x38);
    public static readonly Color Accent = Color.FromArgb(0x3A, 0xB8, 0xFF);
    public static readonly Color Text = Color.FromArgb(0xFF, 0xFF, 0xFF);
    public static readonly Color TextDim = Color.FromArgb(0xBF, 0xBF, 0xC5);
    public static readonly Color TextFaint = Color.FromArgb(0x8A, 0x8A, 0x92);
}

/// <summary>
/// ProfessionalColorTable that forces a pitch-dark context menu:
/// #0F0F11 background, #2A2A30 borders, #22222A hover, white text.
/// </summary>
public sealed class DarkColorTable : ProfessionalColorTable
{
    public override Color ToolStripDropDownBackground => Palette.MenuBg;
    public override Color ImageMarginGradientBegin => Palette.MenuBg;
    public override Color ImageMarginGradientMiddle => Palette.MenuBg;
    public override Color ImageMarginGradientEnd => Palette.MenuBg;
    public override Color MenuBorder => Palette.Border;
    public override Color MenuItemBorder => Palette.Border;
    public override Color MenuItemSelected => Palette.Hover;
    public override Color MenuItemSelectedGradientBegin => Palette.Hover;
    public override Color MenuItemSelectedGradientEnd => Palette.Hover;
    public override Color MenuItemPressedGradientBegin => Palette.Hover;
    public override Color MenuItemPressedGradientEnd => Palette.Hover;
    public override Color MenuItemPressedGradientMiddle => Palette.Hover;
    public override Color SeparatorDark => Palette.Border;
    public override Color SeparatorLight => Palette.MenuBg;
    public override Color CheckBackground => Palette.Hover;
    public override Color CheckSelectedBackground => Palette.Hover;
    public override Color CheckPressedBackground => Palette.Hover;
    public override Color ButtonSelectedHighlight => Palette.Hover;
    public override Color ButtonPressedHighlight => Palette.Hover;
    public override Color ButtonCheckedHighlight => Palette.Hover;
    public override Color ToolStripBorder => Palette.Border;
}

/// <summary>
/// Renderer that paints menu text white on the dark color table and draws a clearly
/// visible accent check box for checked items (the default glyph is invisible on dark).
/// </summary>
public sealed class DarkMenuRenderer : ToolStripProfessionalRenderer
{
    public DarkMenuRenderer() : base(new DarkColorTable()) { }

    protected override void OnRenderItemText(ToolStripItemTextRenderEventArgs e)
    {
        e.TextColor = e.Item.Enabled ? Palette.Text : Palette.TextFaint;
        base.OnRenderItemText(e);
    }

    protected override void OnRenderItemCheck(ToolStripItemImageRenderEventArgs e)
    {
        bool isChecked = (e.Item as ToolStripMenuItem)?.Checked ?? false;
        var rect = new Rectangle(e.ImageRectangle.X, e.ImageRectangle.Y, 14, 14);

        using var bg = new SolidBrush(e.Item.Selected ? Palette.Hover : Palette.Panel);
        e.Graphics.FillRectangle(bg, rect);

        using var border = new Pen(Palette.Border);
        e.Graphics.DrawRectangle(border, rect.X, rect.Y, rect.Width - 1, rect.Height - 1);

        if (isChecked)
        {
            using var check = new Pen(Palette.Accent, 2f);
            e.Graphics.DrawLines(check, new[]
            {
                new Point(rect.X + 2, rect.Y + 7),
                new Point(rect.X + 5, rect.Y + 10),
                new Point(rect.X + 12, rect.Y + 3),
            });
        }
    }
}
