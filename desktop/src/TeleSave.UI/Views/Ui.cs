using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Shapes;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using TeleSave.Core;

namespace TeleSave.UI.Views;

/// <summary>Segoe Fluent Icons (Windows 11) / Segoe MDL2 Assets (Windows 10) glyphs.</summary>
public static class Glyph
{
    public const string Settings = "", Delete = "", Download = "", Close = "", Refresh = "",
        OpenExternal = "", ChevronDown = "", Back = "", Check = "", CheckCircle = "",
        Error = "", Clock = "", Warning = "", Info = "", Person = "", People = "",
        Bot = "", Send = "", Bookmark = "", QrCode = "", Search = "", Play = "",
        Upload = "", NetworkOff = "", Megaphone = "", Chat = "", Forum = "", Retry = "",
        Inbox = "", Turn = "", Number = "", Github = "";

    public static readonly FontFamily Font = new("Segoe Fluent Icons, Segoe MDL2 Assets");
}

internal static class Ui
{
    public static IBrush Accent => Application.Current?.TryGetResource("SystemAccentColor", Application.Current.ActualThemeVariant, out var c) == true && c is Color color
        ? new SolidColorBrush(color) : new SolidColorBrush(Color.Parse("#2AABEE"));
    public static readonly IBrush Secondary = new SolidColorBrush(Color.Parse("#8A8A8A"));
    public static readonly IBrush Orange = new SolidColorBrush(Color.Parse("#E8890C"));
    public static readonly IBrush Red = new SolidColorBrush(Color.Parse("#D13438"));
    public static readonly IBrush Green = new SolidColorBrush(Color.Parse("#2EA043"));
    public static readonly IBrush Yellow = new SolidColorBrush(Color.Parse("#D9A400"));

    public static TextBlock Text(string text, double size = 13, FontWeight weight = FontWeight.Normal, IBrush? brush = null, bool wrap = false)
    {
        var block = new TextBlock
        {
            Text = text, FontSize = size, FontWeight = weight,
            TextWrapping = wrap ? TextWrapping.Wrap : TextWrapping.NoWrap,
            TextTrimming = wrap ? TextTrimming.None : TextTrimming.CharacterEllipsis
        };
        // Leave Foreground unset when no color is given, so the theme's text color is inherited.
        if (brush is not null) block.Foreground = brush;
        return block;
    }

    public static TextBlock Caption(string text, IBrush? brush = null, bool wrap = false) => Text(text, 11.5, brush: brush ?? Secondary, wrap: wrap);

    public static TextBlock Icon(string glyph, double size = 14, IBrush? brush = null)
    {
        var block = new TextBlock
        {
            Text = glyph, FontFamily = Glyph.Font, FontSize = size,
            VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center
        };
        if (brush is not null) block.Foreground = brush;
        return block;
    }

    public static Button IconButton(string glyph, string tip, Action click, double size = 14)
    {
        var button = new Button
        {
            Content = Icon(glyph, size), Width = 28, Height = 28, Padding = new Thickness(0),
            Background = Brushes.Transparent, BorderThickness = new Thickness(0)
        };
        ToolTip.SetTip(button, tip);
        Avalonia.Automation.AutomationProperties.SetName(button, tip);
        button.Click += (_, _) => click();
        return button;
    }

    public static Button Link(string text, Action click, double size = 12)
    {
        var button = new Button
        {
            Content = Text(text, size, brush: Accent), Background = Brushes.Transparent, BorderThickness = new Thickness(0),
            Padding = new Thickness(0), HorizontalAlignment = HorizontalAlignment.Center
        };
        button.Click += (_, _) => click();
        return button;
    }

    public static Button Primary(string text, Action click)
    {
        var button = new Button { Content = text, HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Center };
        button.Classes.Add("accent");
        button.Click += (_, _) => click();
        return button;
    }

    public static Border Banner(string text, string glyph, IBrush color, Action? onClose = null, Action? onClick = null)
    {
        var row = new DockPanel { LastChildFill = true };
        var icon = Icon(glyph, 14, color);
        icon.VerticalAlignment = VerticalAlignment.Top;
        icon.Margin = new Thickness(0, 1, 8, 0);
        DockPanel.SetDock(icon, Dock.Left);
        row.Children.Add(icon);
        if (onClose is not null)
        {
            var close = IconButton(Glyph.Close, L10n.T("Vazgeç"), onClose, 10);
            close.Width = close.Height = 18;
            DockPanel.SetDock(close, Dock.Right);
            row.Children.Add(close);
        }
        row.Children.Add(Text(text, 11.5, wrap: true));
        var border = new Border
        {
            Child = row, Padding = new Thickness(8), CornerRadius = new CornerRadius(8),
            Background = new SolidColorBrush(((ISolidColorBrush)color).Color, 0.14)
        };
        if (onClick is not null)
        {
            border.Cursor = new Avalonia.Input.Cursor(Avalonia.Input.StandardCursorType.Hand);
            border.PointerPressed += (_, _) => onClick();
        }
        return border;
    }

    public static Border Divider() => new() { Height = 1, Background = new SolidColorBrush(Colors.Gray, 0.25) };

    public static T With<T>(this T control, Action<T> configure)
    {
        configure(control);
        return control;
    }

    public static Bitmap? Load(string? path)
    {
        if (path is null || !File.Exists(path)) return null;
        try { using var stream = File.OpenRead(path); return Bitmap.DecodeToWidth(stream, 96); } catch (Exception) { return null; }
    }

    public static Bitmap? Load(byte[]? data)
    {
        if (data is null) return null;
        try { using var stream = new MemoryStream(data); return new Bitmap(stream); } catch (Exception) { return null; }
    }
}

/// <summary>Round avatar: profile photo, or initials on an accent circle.</summary>
internal sealed class Avatar : Border
{
    public Avatar(string name, string? photoPath, double size = 32)
    {
        Width = Height = size;
        CornerRadius = new CornerRadius(size / 2);
        ClipToBounds = true;
        if (Ui.Load(photoPath) is { } photo)
        {
            Child = new Image { Source = photo, Stretch = Stretch.UniformToFill };
        }
        else
        {
            Background = Ui.Accent;
            var initials = string.Concat(name.Split(' ', StringSplitOptions.RemoveEmptyEntries).Take(2).Select(p => char.ToUpperInvariant(p[0])));
            Child = new TextBlock
            {
                Text = initials.Length == 0 ? "?" : initials, Foreground = Brushes.White, FontWeight = FontWeight.Bold, FontSize = size * 0.36,
                HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center
            };
        }
        Avalonia.Automation.AutomationProperties.SetName(this, name);
    }
}

/// <summary>Connection status as a dot before the account name; the text is in the tooltip.</summary>
internal sealed class ConnectionDot : Ellipse
{
    public ConnectionDot(ConnectionState state, bool networkAvailable)
    {
        var (text, color) = !networkAvailable ? (L10n.T("Ağ yok — yüklemeler duraklatıldı"), Ui.Orange) : state switch
        {
            ConnectionState.Ready => (L10n.T("Telegram'a bağlı"), Ui.Green),
            ConnectionState.Updating => (L10n.T("Güncelleniyor…"), Ui.Yellow),
            ConnectionState.WaitingForNetwork => (L10n.T("Ağ bekleniyor…"), Ui.Orange),
            _ => (L10n.T("Bağlanıyor…"), Ui.Orange)
        };
        Width = Height = 7;
        Fill = color;
        Margin = new Thickness(0, 0, 6, 0);
        VerticalAlignment = VerticalAlignment.Center;
        ToolTip.SetTip(this, text);
        Avalonia.Automation.AutomationProperties.SetName(this, text);
    }
}
