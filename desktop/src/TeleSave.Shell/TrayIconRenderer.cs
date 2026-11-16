using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using TeleSave.UI;

namespace TeleSave.Shell;

/// <summary>Draws the tray icon: paper plane on a blue tile, with a badge for progress or errors.</summary>
internal static class TrayIconRenderer
{
    // Material Design "send" icon (Apache 2.0).
    private static readonly Geometry Plane = Geometry.Parse("M2.01 21L23 12 2.01 3 2 10l15 2-15 2z");
    private static readonly Color Blue = Color.Parse("#2AABEE");

    public static WindowIcon Render(AppModel.StatusIconState state)
    {
        const int size = 64;
        var bitmap = new RenderTargetBitmap(new PixelSize(size, size));
        using (var ctx = bitmap.CreateDrawingContext())
        {
            var loggedOut = state is AppModel.StatusIconState.LoggedOut;
            var tile = new SolidColorBrush(loggedOut ? Color.Parse("#8A8A8A") : Blue);
            ctx.DrawRectangle(tile, null, new RoundedRect(new Rect(2, 2, 60, 60), 14));
            using (ctx.PushTransform(Matrix.CreateScale(1.7, 1.7) * Matrix.CreateTranslation(13, 12)))
                ctx.DrawGeometry(Brushes.White, null, Plane);

            switch (state)
            {
                case AppModel.StatusIconState.Uploading uploading:
                    // Progress ring in the corner.
                    var center = new Point(48, 48);
                    ctx.DrawEllipse(Brushes.White, null, center, 15, 15);
                    ctx.DrawEllipse(null, new Pen(new SolidColorBrush(Color.Parse("#DDDDDD")), 5), center, 10, 10);
                    var sweep = Math.Clamp(uploading.Progress, 0.02, 0.999) * 2 * Math.PI;
                    var arc = new StreamGeometry();
                    using (var g = arc.Open())
                    {
                        g.BeginFigure(new Point(center.X, center.Y - 10), false);
                        g.ArcTo(new Point(center.X + 10 * Math.Sin(sweep), center.Y - 10 * Math.Cos(sweep)), new Size(10, 10), 0, sweep > Math.PI, SweepDirection.Clockwise);
                    }
                    ctx.DrawGeometry(null, new Pen(new SolidColorBrush(Color.Parse("#2EA043")), 5), arc);
                    break;
                case AppModel.StatusIconState.Error:
                    ctx.DrawEllipse(new SolidColorBrush(Color.Parse("#D13438")), new Pen(Brushes.White, 3), new Point(50, 50), 11, 11);
                    break;
            }
        }
        using var stream = new MemoryStream();
        bitmap.Save(stream);
        stream.Position = 0;
        return new WindowIcon(stream);
    }
}
