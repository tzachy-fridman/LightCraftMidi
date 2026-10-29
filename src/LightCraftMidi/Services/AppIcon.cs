using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace LightCraftMidi.Services;

/// <summary>Draws the app icon (three faders) at runtime so no binary assets are needed.</summary>
public static class AppIcon
{
    public static Icon Create(bool active = true, int size = 32)
    {
        using var bmp = new Bitmap(size, size);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(System.Drawing.Color.Transparent);
            float s = size / 32f;
            using (var bg = new SolidBrush(System.Drawing.Color.FromArgb(255, 32, 36, 44)))
            using (var path = RoundedRect(new RectangleF(1 * s, 1 * s, 30 * s, 30 * s), 7 * s))
                g.FillPath(bg, path);

            var accent = active ? System.Drawing.Color.FromArgb(255, 255, 167, 38) : System.Drawing.Color.FromArgb(255, 140, 140, 140);
            using var track = new System.Drawing.Pen(System.Drawing.Color.FromArgb(255, 110, 116, 128), 2 * s);
            using var knob = new SolidBrush(accent);
            float[] xs = [9, 16, 23];
            float[] ys = [19, 10, 15];
            for (int i = 0; i < 3; i++)
            {
                g.DrawLine(track, xs[i] * s, 6 * s, xs[i] * s, 26 * s);
                g.FillRectangle(knob, (xs[i] - 3.5f) * s, (ys[i] - 2.5f) * s, 7 * s, 5 * s);
            }
        }
        var hIcon = bmp.GetHicon();
        try
        {
            using var tmp = Icon.FromHandle(hIcon);
            return (Icon)tmp.Clone();
        }
        finally
        {
            DestroyIcon(hIcon);
        }
    }

    public static ImageSource CreateImageSource()
    {
        using var icon = Create(true, 64);
        var source = Imaging.CreateBitmapSourceFromHIcon(icon.Handle, System.Windows.Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
        source.Freeze();
        return source;
    }

    private static GraphicsPath RoundedRect(RectangleF r, float radius)
    {
        var path = new GraphicsPath();
        float d = radius * 2;
        path.AddArc(r.X, r.Y, d, d, 180, 90);
        path.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        path.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        path.CloseFigure();
        return path;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool DestroyIcon(IntPtr hIcon);
}
