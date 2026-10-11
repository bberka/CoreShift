using System.Drawing;
using System.Windows.Forms;

namespace CoreShift.App;

/// <summary>ListView with double-buffering enabled (AOT-safe — no reflection hacks).</summary>
internal sealed class DarkListView : ListView
{
    public DarkListView()
    {
        SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint, true);
    }
}

/// <summary>Simple card panel with a dark border.</summary>
internal sealed class CardPanel : Panel
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

/// <summary>Owner-draw handlers for the dark ListViews shared by the dashboard and dialogs.</summary>
internal static class ListPainter
{
    public static void DrawColumnHeader(object? sender, DrawListViewColumnHeaderEventArgs e)
    {
        using var bg = new SolidBrush(Palette.Panel);
        e.Graphics.FillRectangle(bg, e.Bounds);
        var b = e.Bounds;
        b.X += 6; b.Width -= 6;
        TextRenderer.DrawText(e.Graphics, e.Header?.Text ?? string.Empty, UiKit.Font, b, Palette.TextDim,
            TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
    }

    public static void DrawSubItem(object? sender, DrawListViewSubItemEventArgs e, ImageList icons)
    {
        bool selected = e.Item?.Selected ?? false;
        var bg = selected ? Palette.Selection : (e.ItemIndex % 2 == 0 ? Palette.Bg : Palette.RowAlt);
        using var brush = new SolidBrush(bg);
        e.Graphics.FillRectangle(brush, e.Bounds);

        var bounds = e.Bounds;
        if (e.ColumnIndex == 0)
        {
            if (icons is not null && e.ItemIndex >= 0 && e.ItemIndex < icons.Images.Count)
            {
                var img = icons.Images[e.ItemIndex];
                e.Graphics.DrawImage(img, bounds.X + 4, bounds.Y + (bounds.Height - img.Height) / 2);
            }
            bounds = new Rectangle(bounds.X + 4 + (icons?.ImageSize.Width ?? 16) + 6, bounds.Y,
                bounds.Width - (icons?.ImageSize.Width ?? 16) - 10, bounds.Height);
        }

        var color = selected ? Palette.Text : e.Item?.ForeColor ?? Palette.Text;
        TextRenderer.DrawText(e.Graphics, e.SubItem?.Text ?? string.Empty, UiKit.Font, bounds, color,
            TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
    }
}

/// <summary>Shared UI fonts.</summary>
internal static class UiKit
{
    public static readonly Font Font = new("Segoe UI", 9f);
    public static readonly Font FontBold = new("Segoe UI", 9f, FontStyle.Bold);
    public static readonly Font FontSmall = new("Segoe UI", 8f);
    public static readonly Font FontCaption = new("Segoe UI", 8f, FontStyle.Bold);
}
