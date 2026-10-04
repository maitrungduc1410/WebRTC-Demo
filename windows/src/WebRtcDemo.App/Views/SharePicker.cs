using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using WebRtcDemo.Core.Call;
using WebRtcDemo.Core.Media;
using WebRtcDemo.Interop;
using static WebRtcDemo.App.Ui.UiFactory;

namespace WebRtcDemo.App.Views;

/// <summary>Screens or windows with live thumbnails; sharing starts while the list is still open.</summary>
internal static class SharePicker
{
    private static readonly TimeSpan RefreshInterval = TimeSpan.FromSeconds(2);

    public static async Task ShowAsync(CallViewModel vm, XamlRoot root, DesktopSourceType type)
    {
        var screens = type == DesktopSourceType.Screen;
        IShareSourceList list;
        try
        {
            list = vm.CreateShareSourceList(type);
        }
        catch (WebRtcException e)
        {
            App.Log("Share list: " + e.Message);
            return;
        }

        using (list)
        {
            var items = new Dictionary<string, (GridViewItem Item, Image Image)>();
            var grid = new GridView
            {
                SelectionMode = ListViewSelectionMode.Single,
                IsItemClickEnabled = false,
                MinHeight = 240,
                MaxHeight = 420,
            };
            var status = Text("Looking for " + (screens ? "screens" : "windows") + "…", 13, foreground: ThemeBrush("TextFillColorSecondaryBrush"));
            var dialog = new ContentDialog
            {
                XamlRoot = root,
                RequestedTheme = ElementTheme.Dark,
                Title = screens ? "Share your screen" : "Share a window",
                Content = Column(8, status, grid),
                PrimaryButtonText = "Share",
                CloseButtonText = "Cancel",
                DefaultButton = ContentDialogButton.Primary,
                IsPrimaryButtonEnabled = false,
            };
            grid.SelectionChanged += (_, _) => dialog.IsPrimaryButtonEnabled = grid.SelectedItem != null;
            grid.DoubleTapped += (_, _) =>
            {
                if (grid.SelectedItem == null) return;
                dialog.Tag = "share";
                dialog.Hide();
            };

            async Task SetThumbnailAsync(ShareSource source)
            {
                if (!items.TryGetValue(source.Id, out var entry)) return;
                var bitmap = await JpegAsync(source.Thumbnail);
                if (bitmap != null) entry.Image.Source = bitmap;
            }
            list.ThumbnailChanged += source => _ = SetThumbnailAsync(source);

            async Task RefreshAsync()
            {
                IReadOnlyList<ShareSource> sources;
                try
                {
                    sources = await list.RefreshAsync();
                }
                catch (Exception e) when (e is WebRtcException or ObjectDisposedException)
                {
                    return;
                }
                var selected = (grid.SelectedItem as GridViewItem)?.Tag as ShareSource;
                var seen = new HashSet<string>();
                foreach (var source in sources)
                {
                    seen.Add(source.Id);
                    if (!items.TryGetValue(source.Id, out var existing))
                    {
                        var image = new Image { Stretch = Stretch.Uniform };
                        var item = new GridViewItem
                        {
                            Tag = source,
                            Margin = new Thickness(4),
                            Content = Column(6,
                                new Border
                                {
                                    Width = 200,
                                    Height = 120,
                                    CornerRadius = new CornerRadius(6),
                                    Background = Brush(0x101014),
                                    Child = image,
                                },
                                Text(source.Name.Length > 0 ? source.Name : screens ? "Screen" : "Window", 12).With(t =>
                                {
                                    t.MaxWidth = 200;
                                    t.TextTrimming = TextTrimming.CharacterEllipsis;
                                    t.TextWrapping = TextWrapping.NoWrap;
                                })),
                        };
                        existing = (item, image);
                        items[source.Id] = existing;
                        grid.Items.Add(item);
                    }
                    // The list reuses unchanged sources; their new thumbnails arrive as ThumbnailChanged.
                    if (ReferenceEquals(existing.Item.Tag, source) && existing.Image.Source != null) continue;
                    existing.Item.Tag = source;
                    _ = SetThumbnailAsync(source);
                }
                foreach (var gone in items.Keys.Where(id => !seen.Contains(id)).ToList())
                {
                    grid.Items.Remove(items[gone].Item);
                    items.Remove(gone);
                }
                if (selected != null && items.TryGetValue(selected.Id, out var still)) grid.SelectedItem = still.Item;
                status.Text = sources.Count == 0
                    ? "Nothing to share was found"
                    : screens ? "Pick a screen" : "Pick a window";
                if (screens && grid.SelectedItem == null && grid.Items.Count == 1) grid.SelectedIndex = 0;
            }

            var timer = root.Content.DispatcherQueue.CreateTimer();
            timer.Interval = RefreshInterval;
            timer.Tick += (_, _) => _ = RefreshAsync();
            await RefreshAsync();
            timer.Start();
            ContentDialogResult result;
            try
            {
                result = await dialog.ShowAsync();
            }
            finally
            {
                timer.Stop();
            }

            if ((result == ContentDialogResult.Primary || dialog.Tag is "share") && (grid.SelectedItem as GridViewItem)?.Tag is ShareSource chosen)
            {
                // Before the list is disposed: the capturer is created from its source.
                await vm.ShareScreenAsync(chosen);
            }
        }
    }

    /// <summary>A video file to stream as if it were a camera.</summary>
    public static async Task ShareFileAsync(CallViewModel vm, MainWindow window)
    {
        var picker = new Microsoft.Windows.Storage.Pickers.FileOpenPicker(window.AppWindow.Id)
        {
            SuggestedStartLocation = Microsoft.Windows.Storage.Pickers.PickerLocationId.VideosLibrary,
            ViewMode = Microsoft.Windows.Storage.Pickers.PickerViewMode.Thumbnail,
        };
        foreach (var extension in new[] { ".mp4", ".mov", ".m4v", ".mkv", ".webm", ".avi", ".wmv" }) picker.FileTypeFilter.Add(extension);
        var file = await picker.PickSingleFileAsync();
        if (file?.Path is { Length: > 0 } path) await vm.ShareFileAsync(path);
    }
}
