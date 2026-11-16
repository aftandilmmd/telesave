using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using TeleSave.Core;

namespace TeleSave.UI.Views;

/// <summary>The 360×480 panel: setup, login, main list, settings, destination picker, key change.</summary>
public sealed class PanelView : UserControl
{
    public enum Page { Main, Settings, Destination, ChangeKey }

    private readonly AppModel _model;
    private Page _page = Page.Main;
    private (AppModel.AppPhase, bool, Page, string)? _shown;
    private bool _refreshQueued;

    public PanelView(AppModel model)
    {
        _model = model;
        Width = 360;
        Height = 480;
        model.PropertyChanged += OnModelChanged;
        Render();
    }

    public Page CurrentPage => _page;

    public void Navigate(Page page)
    {
        _page = page;
        Render();
    }

    private void OnModelChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(AppModel.Phase) or nameof(AppModel.CanUpload) or nameof(AppModel.Settings) && !_refreshQueued)
        {
            _refreshQueued = true;
            Dispatcher.UIThread.Post(() => { _refreshQueued = false; Render(); });
        }
    }

    private void Render()
    {
        // Only swap the page when it changes, so text being typed is not lost.
        var key = (_model.Phase, _model.CanUpload, _page, _model.Settings.Language + _model.Settings.ShowChatFiles);
        if (_shown == key) return;
        _shown = key;
        Content = (_model.Phase, _page) switch
        {
            (AppModel.AppPhase.NeedsCredentials, _) => new CredentialsPage(_model, null),
            (_, Page.ChangeKey) => new CredentialsPage(_model, () => Navigate(Page.Settings)),
            (_, Page.Settings) => new SettingsPage(_model, () => Navigate(Page.Main), () => Navigate(Page.ChangeKey)),
            _ when !_model.CanUpload => new LoginPage(_model, () => Navigate(Page.Settings)),
            (_, Page.Destination) => new DestinationPage(_model, () => Navigate(Page.Main)),
            _ => new MainPage(_model, () => Navigate(Page.Settings), () => Navigate(Page.Destination))
        };
    }
}

/// <summary>Base for pages that redraw parts of themselves when the model changes.</summary>
public abstract class ModelPage : UserControl
{
    protected readonly AppModel Model;
    private bool _queued;

    protected ModelPage(AppModel model)
    {
        Model = model;
        AttachedToVisualTree += (_, _) => Model.PropertyChanged += OnChanged;
        DetachedFromVisualTree += (_, _) => Model.PropertyChanged -= OnChanged;
    }

    private void OnChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (_queued) return;
        _queued = true;
        Dispatcher.UIThread.Post(() => { _queued = false; Refresh(); }, DispatcherPriority.Background);
    }

    protected abstract void Refresh();
}

// MARK: - Main

public sealed class MainPage : ModelPage
{
    private readonly Action _onSettings;
    private readonly Action _onPickDestination;
    private readonly ContentControl _header = new();
    private readonly StackPanel _banners = new() { Spacing = 6, Margin = new Thickness(12, 12, 12, 0) };
    private readonly ContentControl _notice = new() { Margin = new Thickness(12, 0, 12, 8) };
    private readonly ContentControl _list = new();
    private readonly ContentControl _footer = new();
    private ChatFilesView? _chatFiles;

    public MainPage(AppModel model, Action onSettings, Action onPickDestination) : base(model)
    {
        _onSettings = onSettings;
        _onPickDestination = onPickDestination;
        var layout = new DockPanel();
        var top = new StackPanel();
        top.Children.Add(_header);
        top.Children.Add(Ui.Divider());
        top.Children.Add(_banners);
        top.Children.Add(new DropZone(model) { Margin = new Thickness(12) });
        top.Children.Add(_notice);
        top.Children.Add(Ui.Divider());
        DockPanel.SetDock(top, Dock.Top);
        DockPanel.SetDock(_footer, Dock.Bottom);
        layout.Children.Add(top);
        layout.Children.Add(_footer);
        layout.Children.Add(_list);
        Content = layout;
        if (model.Settings.ShowChatFiles) _list.Content = _chatFiles = new ChatFilesView(model);
        Refresh();
    }

    protected override void Refresh()
    {
        _header.Content = Header();
        _banners.Children.Clear();
        foreach (var banner in StatusBanners.For(Model)) _banners.Children.Add(banner);
        _banners.IsVisible = _banners.Children.Count > 0;
        _notice.Content = Model.Notice is { } notice ? Ui.Banner(notice, Glyph.Info, Ui.Orange, () => Model.Notice = null) : null;
        if (_chatFiles is null) _list.Content = QueueList();
        _footer.Content = Footer();
    }

    private Control Header()
    {
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto,Auto"), Margin = new Thickness(12) };
        var avatar = new Avatar(Model.Me?.DisplayName ?? "?", Model.AvatarPath) { Margin = new Thickness(0, 0, 10, 0) };
        grid.Children.Add(avatar);

        var nameRow = new StackPanel { Orientation = Orientation.Horizontal };
        nameRow.Children.Add(new ConnectionDot(Model.Connection, Model.NetworkAvailable));
        nameRow.Children.Add(Ui.Text(Model.Me?.DisplayName ?? "", 14, FontWeight.SemiBold));
        var info = new StackPanel { Spacing = 2, VerticalAlignment = VerticalAlignment.Center };
        info.Children.Add(nameRow);
        info.Children.Add(DestinationLine());
        Grid.SetColumn(info, 1);
        grid.Children.Add(info);

        var open = Ui.IconButton(Glyph.Chat, L10n.T("Kayıtlı Mesajları Telegram'da aç"), () => Model.OpenInTelegram());
        Grid.SetColumn(open, 2);
        grid.Children.Add(open);
        var settings = Ui.IconButton(Glyph.Settings, L10n.T("Ayarlar"), _onSettings);
        if (Model.AvailableUpdate is not null)
        {
            // Small dot on the gear when an update is available.
            var badge = new Grid();
            badge.Children.Add(settings);
            badge.Children.Add(new Ellipse { Width = 7, Height = 7, Fill = Ui.Accent, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 3, 3, 0), IsHitTestVisible = false });
            Grid.SetColumn(badge, 3);
            grid.Children.Add(badge);
        }
        else
        {
            Grid.SetColumn(settings, 3);
            grid.Children.Add(settings);
        }
        return grid;
    }

    /// <summary>Upload destination under the account name; anything but Saved Messages is shown in orange.</summary>
    private Control DestinationLine()
    {
        var destination = Model.Destination;
        var isSaved = destination.IsSavedMessages;
        var brush = isSaved ? Ui.Secondary : Ui.Orange;
        var content = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4 };
        content.Children.Add(Ui.Icon(isSaved ? Glyph.Bookmark : Glyph.Turn, 10, brush));
        content.Children.Add(Ui.Text(destination.DisplayTitle, 11.5, isSaved ? FontWeight.Normal : FontWeight.SemiBold, brush).With(t => t.MaxWidth = 200));
        content.Children.Add(Ui.Icon(Glyph.ChevronDown, 8, brush));
        var button = new Button { Content = content, Padding = new Thickness(0), Background = Brushes.Transparent, BorderThickness = new Thickness(0) };
        ToolTip.SetTip(button, L10n.T("Yükleme hedefi: %@. Değiştirmek için tıklayın.", destination.DisplayTitle));
        Avalonia.Automation.AutomationProperties.SetName(button, L10n.T("Yükleme hedefi: %@. Değiştir", destination.DisplayTitle));
        button.Click += (_, _) => _onPickDestination();
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4 };
        row.Children.Add(button);
        if (!isSaved)
        {
            var reset = Ui.IconButton(Glyph.Close, L10n.T("Kayıtlı Mesajlara dön"), () => Model.Select(UploadDestination.SavedMessages), 9);
            reset.Width = reset.Height = 16;
            row.Children.Add(reset);
        }
        return row;
    }

    private Control QueueList()
    {
        if (Model.Items.Count == 0)
            return Ui.Text(L10n.T("Henüz yükleme yok"), brush: Ui.Secondary).With(t => { t.HorizontalAlignment = HorizontalAlignment.Center; t.VerticalAlignment = VerticalAlignment.Center; });
        var stack = new StackPanel();
        foreach (var item in Model.Items.Reverse())
        {
            stack.Children.Add(new QueueRow(Model, item));
            stack.Children.Add(Ui.Divider().With(d => d.Margin = new Thickness(60, 0, 0, 0)));
        }
        return new ScrollViewer { Content = stack };
    }

    private Control? Footer()
    {
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), Margin = new Thickness(12, 8) };
        if (Model.BytesPerSecond > 0)
        {
            var text = L10n.T("%@/sn", Format.Bytes((long)Model.BytesPerSecond)) +
                       (Model.RemainingTime is { } eta ? " · " + L10n.T("%@ kaldı", Format.Duration(eta)) : "");
            grid.Children.Add(Ui.Caption(text).With(t => t.VerticalAlignment = VerticalAlignment.Center));
        }
        if (!Model.Settings.ShowChatFiles)
        {
            var clear = Ui.Link(L10n.T("Tamamlananları temizle"), Model.ClearFinished, 11.5);
            clear.IsEnabled = Model.Items.Any(i => i.Status is UploadStatus.Done or UploadStatus.Cancelled);
            Grid.SetColumn(clear, 1);
            grid.Children.Add(clear);
        }
        else if (Model.BytesPerSecond <= 0)
        {
            return null;
        }
        var panel = new StackPanel();
        panel.Children.Add(Ui.Divider());
        panel.Children.Add(grid);
        return panel;
    }
}

internal static class StatusBanners
{
    public static IEnumerable<Control> For(AppModel model)
    {
        if (!model.NetworkAvailable)
            yield return Ui.Banner(L10n.T("İnternet bağlantısı yok. Yüklemeler duraklatıldı; bağlantı gelince kendiliğinden devam edecek."), Glyph.NetworkOff, Ui.Orange);
        if (model.AvailableUpdate is { } update)
            yield return Ui.Banner(L10n.T("TeleSave %@ yayımlandı. İndirmek için tıklayın.", update.Version), Glyph.Download, Ui.Accent,
                onClick: () => model.Platform.OpenUrl(update.PageUrl));
        if (model.UpdateRequired)
            yield return Ui.Banner(L10n.T("Bu TeleSave sürümü artık desteklenmiyor. Lütfen en yeni sürümü yükleyin."), Glyph.Download, Ui.Red);
        if (model.RemoteMessage is { } message)
            yield return Ui.Banner(message, Glyph.Megaphone, Ui.Accent);
    }
}

/// <summary>Compact drop area; click to choose files, ⌘V/Ctrl+V to paste.</summary>
public sealed class DropZone : Border
{
    private readonly AppModel _model;
    private readonly TextBlock _icon;

    public DropZone(AppModel model)
    {
        _model = model;
        Height = 56;
        CornerRadius = new CornerRadius(10);
        BorderThickness = new Thickness(1.5);
        Cursor = new Cursor(StandardCursorType.Hand);
        _icon = Ui.Icon(Glyph.Inbox, 20, Ui.Secondary);
        var texts = new StackPanel { Spacing = 1, VerticalAlignment = VerticalAlignment.Center };
        texts.Children.Add(Ui.Text(L10n.T("Dosyaları buraya bırakın"), 13, FontWeight.Medium));
        texts.Children.Add(Ui.Caption(L10n.T("tıklayın ya da ⌘V").Replace("⌘V", "Ctrl+V")));
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        row.Children.Add(_icon);
        row.Children.Add(texts);
        Child = row;
        Avalonia.Automation.AutomationProperties.SetName(this, L10n.T("Dosya bırakma alanı"));
        SetTargeted(false);

        DragDrop.SetAllowDrop(this, true);
        AddHandler(DragDrop.DragEnterEvent, (_, e) => { e.DragEffects = DroppedFiles.Effect(e); SetTargeted(true); });
        AddHandler(DragDrop.DragOverEvent, (_, e) => e.DragEffects = DroppedFiles.Effect(e));
        AddHandler(DragDrop.DragLeaveEvent, (_, _) => SetTargeted(false));
        AddHandler(DragDrop.DropEvent, (_, e) =>
        {
            SetTargeted(false);
            model.HandleDrop(DroppedFiles.Paths(e));
            e.Handled = true;
        });
        PointerPressed += async (_, _) => await PickFilesAsync();
    }

    private void SetTargeted(bool targeted)
    {
        var accent = (ISolidColorBrush)Ui.Accent;
        Background = targeted ? new SolidColorBrush(accent.Color, 0.12) : new SolidColorBrush(Colors.Gray, 0.07);
        BorderBrush = targeted ? accent : new SolidColorBrush(Colors.Gray, 0.4);
        _icon.Foreground = targeted ? accent : Ui.Secondary;
    }

    private async Task PickFilesAsync()
    {
        if (TopLevel.GetTopLevel(this)?.StorageProvider is not { } storage) return;
        var files = await storage.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            AllowMultiple = true, Title = L10n.T("Kayıtlı Mesajlara yüklenecek dosyaları seçin")
        });
        _model.HandleDrop(files.Select(f => f.TryGetLocalPath()).OfType<string>().ToList());
    }
}

/// <summary>Local paths of files being dragged (Avalonia's DataTransfer API).</summary>
public static class DroppedFiles
{
    public static DragDropEffects Effect(DragEventArgs e) =>
        e.DataTransfer.Contains(DataFormat.File) ? DragDropEffects.Copy : DragDropEffects.None;

    public static List<string> Paths(DragEventArgs e) =>
        e.DataTransfer.TryGetFiles()?.Select(f => f.TryGetLocalPath()).OfType<string>().ToList() ?? [];
}

/// <summary>One upload: thumbnail with a status badge, name, size and state, actions.</summary>
public sealed class QueueRow : Grid
{
    public QueueRow(AppModel model, UploadItem item)
    {
        ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto");
        Margin = new Thickness(12, 8);

        var thumb = new Grid { Width = 40, Height = 40, Margin = new Thickness(0, 0, 10, 0) };
        thumb.Children.Add(new FileThumb(model.Platform.FileIcon(item.Path, 72), item.DisplayName));
        var badge = StatusGlyph(item);
        badge.HorizontalAlignment = HorizontalAlignment.Right;
        badge.VerticalAlignment = VerticalAlignment.Bottom;
        badge.Margin = new Thickness(0, 0, -2, -2);
        thumb.Children.Add(badge);
        Children.Add(thumb);

        var info = new StackPanel { Spacing = 3, VerticalAlignment = VerticalAlignment.Center };
        info.Children.Add(Ui.Text(item.DisplayName).With(t => t.TextTrimming = TextTrimming.PrefixCharacterEllipsis));
        if (item.Status == UploadStatus.Uploading)
            info.Children.Add(new ProgressBar { Value = item.Progress, Maximum = 1, Height = 4, MinHeight = 4 });
        info.Children.Add(Ui.Caption(Subtitle(item), item.Status == UploadStatus.Failed ? Ui.Red : null));
        Grid.SetColumn(info, 1);
        Children.Add(info);

        var actions = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        if (model.BusyItems.Contains(item.Id))
            actions.Children.Add(new ProgressBar { IsIndeterminate = true, Width = 20 });
        else
        {
            switch (item.Status)
            {
                case UploadStatus.Queued or UploadStatus.Uploading:
                    actions.Children.Add(Ui.IconButton(Glyph.Close, L10n.T("İptal"), () => model.Cancel(item)));
                    break;
                case UploadStatus.Failed:
                    actions.Children.Add(Ui.IconButton(Glyph.Retry, L10n.T("Tekrar dene"), () => model.Retry(item)));
                    actions.Children.Add(Ui.IconButton(Glyph.Delete, L10n.T("Listeden kaldır"), () => model.Remove(item)));
                    break;
                case UploadStatus.Done:
                    actions.Children.Add(Ui.IconButton(Glyph.Download, L10n.T("İndir (İndirilenler klasörüne)"), () => model.Download(item)));
                    actions.Children.Add(ConfirmDelete.Button(L10n.T("Telegram'dan sil"), () => model.DeleteFromTelegram(item)));
                    actions.Children.Add(Ui.IconButton(Glyph.OpenExternal, L10n.T("Telegram'da aç"), () => model.OpenInTelegram(item.Destination)));
                    break;
                case UploadStatus.Cancelled:
                    actions.Children.Add(Ui.IconButton(Glyph.Delete, L10n.T("Listeden kaldır"), () => model.Remove(item)));
                    break;
            }
        }
        Grid.SetColumn(actions, 2);
        Children.Add(actions);
    }

    private static TextBlock StatusGlyph(UploadItem item) => item.Status switch
    {
        UploadStatus.Queued => Ui.Icon(Glyph.Clock, 12, Ui.Secondary),
        UploadStatus.Uploading => Ui.Icon(Glyph.Upload, 12, Ui.Accent),
        UploadStatus.Done => Ui.Icon(Glyph.CheckCircle, 12, Ui.Green),
        UploadStatus.Failed => Ui.Icon(Glyph.Error, 12, Ui.Red),
        _ => Ui.Icon(Glyph.Close, 12, Ui.Secondary)
    };

    private static string Subtitle(UploadItem item)
    {
        var size = Format.Bytes(item.Size);
        var suffix = item.Destination is { } d ? " → " + d.Title : "";
        return item.Status switch
        {
            UploadStatus.Queued when item.NotBefore > DateTimeOffset.Now => L10n.T("%@ · yeniden denenecek", size),
            UploadStatus.Queued => L10n.T("%@ · sırada", size) + suffix,
            UploadStatus.Uploading => $"{size} · {Format.Percent(item.Progress)}",
            UploadStatus.Done => $"{size} · {Format.UploadDate(item.FinishedAt ?? item.CreatedAt)}{suffix}",
            UploadStatus.Failed => $"{size} · {item.FailureReason}",
            _ => L10n.T("%@ · iptal edildi", size)
        };
    }
}

/// <summary>Trash button that turns into "Delete / ✕" — deleting is for everyone and cannot be undone.</summary>
internal static class ConfirmDelete
{
    public static Control Button(string tip, Action delete)
    {
        var host = new ContentControl();
        void ShowTrash() => host.Content = Ui.IconButton(Glyph.Delete, tip, ShowConfirm);
        void ShowConfirm()
        {
            var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 2 };
            var confirm = new Button { Content = L10n.T("Sil"), Foreground = Brushes.White, Background = Ui.Red, Padding = new Thickness(8, 2), FontSize = 12 };
            ToolTip.SetTip(confirm, L10n.T("Mesajı Telegram'dan herkes için sil"));
            confirm.Click += (_, _) => delete();
            row.Children.Add(confirm);
            row.Children.Add(Ui.IconButton(Glyph.Close, L10n.T("Vazgeç"), ShowTrash, 11));
            host.Content = row;
        }
        ShowTrash();
        return host;
    }
}

/// <summary>36 px rounded preview: image, or a type label when there is no preview.</summary>
internal sealed class FileThumb : Border
{
    public FileThumb(Avalonia.Media.Imaging.Bitmap? image, string fileName, bool blurred = false)
    {
        Width = Height = 36;
        CornerRadius = new CornerRadius(6);
        ClipToBounds = true;
        VerticalAlignment = VerticalAlignment.Center;
        if (image is not null)
        {
            Child = new Image { Source = image, Stretch = Stretch.UniformToFill, Effect = blurred ? new BlurEffect { Radius = 2 } : null };
        }
        else
        {
            Background = new SolidColorBrush(Colors.Gray, 0.18);
            var ext = FileTypes.Extension(fileName).ToUpperInvariant();
            Child = Ui.Text(ext.Length is > 0 and <= 4 ? ext : "•", 9, FontWeight.Bold, Ui.Secondary)
                .With(t => { t.HorizontalAlignment = HorizontalAlignment.Center; t.VerticalAlignment = VerticalAlignment.Center; });
        }
    }
}
