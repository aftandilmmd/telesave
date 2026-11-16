using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using TeleSave.Core;
using TeleSave.UI;
using TeleSave.UI.Views;

namespace TeleSave.Shell;

/// <summary>
/// Optional thin strip at the right screen edge that widens into a drop target when a file is dragged over
/// it (the Windows counterpart of the macOS drop shelf; the tray icon itself cannot accept drops).
/// </summary>
public sealed class EdgeShelfWindow : Window
{
    private const int Collapsed = 8, Expanded = 240, ShelfHeight = 120;
    private readonly AppModel _model;
    private readonly Border _body;
    private readonly StackPanel _content;

    public EdgeShelfWindow(AppModel model)
    {
        _model = model;
        SystemDecorations = SystemDecorations.None;
        ShowInTaskbar = false;
        Topmost = true;
        CanResize = false;
        Background = Brushes.Transparent;
        TransparencyLevelHint = [WindowTransparencyLevel.Transparent];
        Width = Collapsed;
        Height = ShelfHeight;

        _content = new StackPanel { Spacing = 2, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(16, 0), IsVisible = false };
        _body = new Border
        {
            CornerRadius = new CornerRadius(10, 0, 0, 10),
            Background = new SolidColorBrush(Color.Parse("#2AABEE"), 0.85),
            Child = _content
        };
        Content = _body;

        DragDrop.SetAllowDrop(this, true);
        AddHandler(DragDrop.DragEnterEvent, (_, e) =>
        {
            e.DragEffects = DroppedFiles.Effect(e);
            Expand(true);
        });
        AddHandler(DragDrop.DragOverEvent, (_, e) => e.DragEffects = DroppedFiles.Effect(e));
        AddHandler(DragDrop.DragLeaveEvent, (_, _) => Expand(false));
        AddHandler(DragDrop.DropEvent, (_, e) =>
        {
            Expand(false);
            _model.HandleDrop(DroppedFiles.Paths(e), fromShell: true);
        });
        Opened += (_, _) => Place();
    }

    private void Expand(bool expanded)
    {
        _content.Children.Clear();
        if (expanded)
        {
            _content.Children.Add(new TextBlock { Text = L10n.T("TeleSave'e bırakın"), Foreground = Brushes.White, FontWeight = FontWeight.SemiBold, FontSize = 14 });
            _content.Children.Add(new TextBlock { Text = _model.Destination.DisplayTitle, Foreground = Brushes.White, FontSize = 12, TextTrimming = TextTrimming.CharacterEllipsis });
        }
        _content.IsVisible = expanded;
        Width = expanded ? Expanded : Collapsed;
        Place();
    }

    private void Place()
    {
        if (Screens.Primary is not { } screen) return;
        var area = screen.WorkingArea;
        var scale = screen.Scaling;
        Position = new PixelPoint(area.Right - (int)(Width * scale), area.Y + (area.Height - (int)(ShelfHeight * scale)) / 2);
    }
}
