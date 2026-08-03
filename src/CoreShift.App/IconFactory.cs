using System.Drawing;
using System.Drawing.Drawing2D;
using CoreShift;

namespace CoreShift.App;

/// <summary>
/// App icon source. Loads the embedded multi-size icon (Assets\CoreShift.ico — generated
/// from Bootstrap Icons "cpu-fill", MIT license) and falls back to a runtime-drawn icon
/// if the resource is ever missing.
/// </summary>
internal static class IconFactory
{
    private const string EmbeddedIconName = "CoreShift.App.Assets.CoreShift.ico";

    private static readonly Lazy<Icon> LazyAppIcon = new(BuildIcon);

    /// <summary>Shared app icon (tray + dashboard).</summary>
    public static Icon AppIcon => LazyAppIcon.Value;

    private static Icon BuildIcon()
    {
        try
        {
            var asm = typeof(IconFactory).Assembly;
            using var stream = asm.GetManifestResourceStream(EmbeddedIconName);
            if (stream is not null) return new Icon(stream);
        }
        catch
        {
            // resource missing → fall through to the drawn icon
        }
        return DrawFallbackIcon();
    }

    private static Icon DrawFallbackIcon()
    {
        using var bmp = new Bitmap(32, 32);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(Color.Transparent);

            var rect = new Rectangle(1, 1, 30, 30);
            using var path = RoundedRect(rect, 7);
            using var bg = new SolidBrush(Palette.MenuBg);
            g.FillPath(bg, path);
            using var pen = new Pen(Palette.Accent, 1.6f);
            g.DrawPath(pen, path);

            // 2×2 "core" grid
            using var core = new SolidBrush(Palette.Accent);
            for (int row = 0; row < 2; row++)
                for (int col = 0; col < 2; col++)
                    g.FillRectangle(core, 9 + col * 7, 9 + row * 7, 5, 5);
        }

        IntPtr hIcon = bmp.GetHicon();
        try
        {
            using var tmp = Icon.FromHandle(hIcon);
            return (Icon)tmp.Clone(); // clone so the icon outlives the handle
        }
        finally
        {
            NativeMethods.DestroyIcon(hIcon);
        }
    }

    private static GraphicsPath RoundedRect(Rectangle r, int radius)
    {
        var p = new GraphicsPath();
        int d = radius * 2;
        p.AddArc(r.X, r.Y, d, d, 180, 90);
        p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        p.CloseFigure();
        return p;
    }
}
