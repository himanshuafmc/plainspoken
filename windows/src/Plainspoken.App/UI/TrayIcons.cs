using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using Plainspoken.App.Platform;
using Plainspoken.Core.Dictation;

namespace Plainspoken.App.UI;

/// <summary>Tray icons drawn at runtime: teal = ready, red = listening, amber = transcribing.</summary>
internal sealed class TrayIcons : IDisposable
{
    private readonly Dictionary<DictationState, Icon> _icons = [];

    public TrayIcons()
    {
        var size = Math.Max(16, SystemInformation.SmallIconSize.Width);
        _icons[DictationState.Idle] = Draw(Color.FromArgb(11, 122, 107), size);
        _icons[DictationState.Recording] = Draw(Color.FromArgb(220, 38, 38), size);
        _icons[DictationState.Transcribing] = Draw(Color.FromArgb(217, 119, 6), size);
    }

    public Icon For(DictationState state) => _icons[state];

    public void Dispose()
    {
        foreach (var icon in _icons.Values)
        {
            icon.Dispose();
        }
    }

    private static Icon Draw(Color background, int size)
    {
        using var bmp = new Bitmap(size, size, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(Color.Transparent);

            // Same geometry as assets/plainspoken.svg (a 256-unit grid): speech bubble + sound wave.
            var k = size / 256f;
            using (var fill = new SolidBrush(background))
            using (var body = new GraphicsPath())
            {
                AddRoundedRect(body, new RectangleF(24 * k, 24 * k, 208 * k, 172 * k), 44 * k);
                g.FillPath(fill, body);
                g.FillPolygon(fill, [new PointF(68 * k, 180 * k), new PointF(118 * k, 192 * k), new PointF(64 * k, 234 * k)]);
            }

            // Fewer, wider bars at tray size so the wave stays readable at 16-24 px.
            (float X, float H)[] bars = size < 40
                ? [(80, 60), (128, 110), (176, 76)]
                : [(64, 48), (96, 88), (128, 120), (160, 80), (192, 36)];
            var barW = (size < 40 ? 26 : 18) * k;
            using var white = new SolidBrush(Color.White);
            foreach (var (x, h) in bars)
            {
                using var bar = new GraphicsPath();
                AddRoundedRect(bar, new RectangleF((x * k) - (barW / 2), (110 - (h / 2)) * k, barW, h * k), barW / 2);
                g.FillPath(white, bar);
            }
        }

        var handle = bmp.GetHicon();
        try
        {
            using var temp = Icon.FromHandle(handle);
            return (Icon)temp.Clone(); // the clone owns its own handle
        }
        finally
        {
            NativeMethods.DestroyIcon(handle);
        }
    }

    private static void AddRoundedRect(GraphicsPath path, RectangleF r, float radius)
    {
        var d = Math.Min(radius * 2, Math.Min(r.Width, r.Height));
        path.AddArc(r.Left, r.Top, d, d, 180, 90);
        path.AddArc(r.Right - d, r.Top, d, d, 270, 90);
        path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        path.AddArc(r.Left, r.Bottom - d, d, d, 90, 90);
        path.CloseFigure();
    }
}
