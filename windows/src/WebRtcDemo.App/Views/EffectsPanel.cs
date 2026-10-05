using System.Numerics;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.System;
using WebRtcDemo.App.Ui;
using WebRtcDemo.App.Video;
using WebRtcDemo.Core.Call;
using WebRtcDemo.Effects;
using static WebRtcDemo.App.Ui.UiFactory;

namespace WebRtcDemo.App.Views;

/// <summary>
/// Backgrounds and face stickers for the camera, in the chat panel's place: a live preview of
/// the effect (mirrored like the self view; the peer gets it unmirrored), then the catalog as thumbnail grids.
/// </summary>
internal sealed class EffectsPanel : Grid, IDisposable
{
    private const double TileGap = 8;
    private const int ThumbnailWidth = 320;
    private const float SelectedScale = 0.94f;

    private readonly CallViewModel _vm;
    private readonly Bindings _bindings = new();
    private readonly VideoView _preview = new() { Mirrored = true, Rounding = 24 };
    private readonly Border _previewMessage;
    private readonly TextBlock _previewText = Text(string.Empty, 13, foreground: White(0xCC));
    private readonly ProgressRing _loading = new() { Width = 32, Height = 32, Foreground = White(), IsActive = false };
    private readonly InfoBar _error = new() { Severity = InfoBarSeverity.Error, IsClosable = true, Margin = new Thickness(16, 0, 16, 8) };
    private readonly SelectorBar _tabs = new();
    private readonly SelectorBarItem _backgroundsTab = new() { Text = "Backgrounds", IsSelected = true };
    private readonly SelectorBarItem _filtersTab = new() { Text = "Filters" };
    private readonly ScrollViewer _backgroundsPage;
    private readonly ScrollViewer _filtersPage;
    private readonly List<(Tile Tile, string Id)> _backgroundTiles = [];
    private readonly List<(Tile Tile, string? Id)> _stickerTiles = [];

    public EffectsPanel(CallViewModel vm)
    {
        _vm = vm;
        Width = ChatPanel.PanelWidth;
        Background = Brush(0x1C1C21);
        BorderBrush = White(0x1A);
        BorderThickness = new Thickness(1, 0, 0, 0);
        RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        RowDefinitions.Add(new RowDefinition());

        var close = new Button
        {
            Content = Icon(Glyphs.Close, 12),
            Background = Transparent,
            BorderThickness = new Thickness(0),
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Top,
        }.Tip("Close (B)");
        AutomationProperties.SetName(close, "Close backgrounds and effects");
        close.Click += (_, _) => _vm.SetEffectsOpen(false);
        var header = new Grid { Padding = new Thickness(16, 12, 8, 12), ColumnSpacing = 8 };
        header.ColumnDefinitions.Add(new ColumnDefinition());
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        header.Children.Add(Column(2,
            Text("Backgrounds and effects", 18, FontWeights.SemiBold, White()),
            Text("Only your camera changes. Your preview is mirrored; the other person sees it the right way round.", 12, foreground: White(0x99))));
        header.Children.Add(close.Grid(column: 1));
        Children.Add(header);

        _previewMessage = new Border
        {
            Background = Black(0x99),
            CornerRadius = new CornerRadius(12),
            Padding = new Thickness(12, 8, 12, 8),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Child = _previewText.With(t => t.TextAlignment = TextAlignment.Center),
        };
        var preview = new Grid
        {
            Height = 220,
            Margin = new Thickness(16, 0, 16, 12),
            CornerRadius = new CornerRadius(24),
            Background = Brush(0x0B0B0F),
            Children = { _preview, _loading, _previewMessage },
        };
        _loading.HorizontalAlignment = HorizontalAlignment.Center;
        _loading.VerticalAlignment = VerticalAlignment.Center;
        AutomationProperties.SetName(preview, "Mirrored preview of your camera with effects");
        Children.Add(preview.Grid(row: 1));

        _error.Closed += (_, _) => _vm.DismissEffectsError();
        Children.Add(_error.Grid(row: 2));

        _tabs.Items.Add(_backgroundsTab);
        _tabs.Items.Add(_filtersTab);
        _tabs.Margin = new Thickness(12, 0, 12, 4);
        _tabs.SelectionChanged += (_, _) => ShowTab();
        Children.Add(_tabs.Grid(row: 3));

        _backgroundsPage = Page(BackgroundsContent());
        _filtersPage = Page(FiltersContent());
        Children.Add(_backgroundsPage.Grid(row: 4));
        Children.Add(_filtersPage.Grid(row: 4));
        ShowTab();

        var escape = new KeyboardAccelerator { Key = VirtualKey.Escape };
        escape.Invoked += (_, e) =>
        {
            e.Handled = true;
            _vm.SetEffectsOpen(false);
        };
        KeyboardAccelerators.Add(escape);

        Bind();
    }

    private void Bind()
    {
        _bindings.Observe(_vm, UpdatePreview,
            nameof(CallViewModel.LocalVideo), nameof(CallViewModel.CameraOn), nameof(CallViewModel.HasCamera),
            nameof(CallViewModel.IsPresenting), nameof(CallViewModel.EffectsStatus), nameof(CallViewModel.EffectsOpen));
        _bindings.Observe(_vm, UpdateSelection,
            nameof(CallViewModel.Effects), nameof(CallViewModel.HasCamera), nameof(CallViewModel.IsPresenting));
        _bindings.Observe(_vm, () =>
        {
            _error.Message = _vm.EffectsError ?? string.Empty;
            _error.IsOpen = _vm.EffectsError != null;
        }, nameof(CallViewModel.EffectsError));
    }

    private void UpdatePreview()
    {
        string? message = !_vm.HasCamera ? "Effects need a camera"
            : _vm.IsPresenting ? "Effects are paused while you present"
            : !_vm.CameraOn ? "Your camera is off"
            : null;
        // Only render while the panel shows, so a closed panel costs nothing.
        _preview.Feed = message == null && _vm.EffectsOpen ? _vm.LocalVideo : null;
        _previewText.Text = message ?? string.Empty;
        _previewMessage.Visibility = Visible(message != null);
        var loading = message == null && _vm.EffectsStatus == EffectsStatus.Loading;
        _loading.IsActive = loading;
        _loading.Visibility = Visible(loading);
    }

    private void UpdateSelection()
    {
        var enabled = _vm.CanPickEffects;
        foreach (var (tile, id) in _backgroundTiles) tile.Update(id == _vm.Effects.Background, enabled);
        foreach (var (tile, id) in _stickerTiles) tile.Update(id == _vm.Effects.Sticker, enabled);
    }

    private void ShowTab()
    {
        var filters = _tabs.SelectedItem == _filtersTab;
        _backgroundsPage.Visibility = Visible(!filters);
        _filtersPage.Visibility = Visible(filters);
    }

    // ---- Catalog ----------------------------------------------------------------------------

    private StackPanel BackgroundsContent()
    {
        var catalog = _vm.EffectsCatalog;
        var content = new StackPanel { Spacing = 8 };
        var blur = catalog.Backgrounds.Where(b => b.Kind is BackgroundKind.None or BackgroundKind.Blur).ToList();
        AddSection(content, "Blur", blur);
        AddSection(content, "Pictures", catalog.Backgrounds.Where(b => b.Kind == BackgroundKind.Image).ToList());
        AddSection(content, "Videos", catalog.Backgrounds.Where(b => b.Kind == BackgroundKind.Video).ToList());
        return content;
    }

    private void AddSection(StackPanel content, string title, List<BackgroundOption> options)
    {
        if (options.Count == 0) return;
        var grid = TileGrid(columns: 3);
        foreach (var option in options)
        {
            var tile = new Tile(9.0 / 16, option.Name);
            switch (option.Kind)
            {
                case BackgroundKind.None:
                    tile.SetGlyph(Glyphs.Close, option.Name, Brush(0x2E2E35));
                    break;
                case BackgroundKind.Blur:
                    tile.SetGlyph(Glyphs.Blur, option.Name, Gradient(0x5B6B8C, 0x2E3445));
                    break;
                default:
                    tile.SetPicture(option.Thumbnail ?? (option.Kind == BackgroundKind.Image ? option.File : null), option.Kind == BackgroundKind.Video);
                    break;
            }
            var id = option.Id;
            tile.Click += (_, _) => _vm.SetBackground(id);
            _backgroundTiles.Add((tile, id));
            AddTile(grid, tile);
        }
        content.Children.Add(SectionTitle(title));
        content.Children.Add(grid);
    }

    private StackPanel FiltersContent()
    {
        var grid = TileGrid(columns: 4);
        var none = new Tile(1, "No filter");
        none.SetGlyph(Glyphs.Close, "No filter", Brush(0x2E2E35));
        none.Click += (_, _) => _vm.SetSticker(null);
        _stickerTiles.Add((none, null));
        AddTile(grid, none);
        foreach (var sticker in _vm.EffectsCatalog.Stickers)
        {
            var tile = new Tile(1, sticker.Name);
            tile.SetSticker(sticker.File);
            var id = sticker.Id;
            tile.Click += (_, _) => _vm.SetSticker(id);
            _stickerTiles.Add((tile, id));
            AddTile(grid, tile);
        }
        var content = new StackPanel { Spacing = 8 };
        content.Children.Add(SectionTitle("Face filters"));
        content.Children.Add(grid);
        if (_vm.EffectsCatalog.Stickers.Count == 0)
            content.Children.Add(Text("No filters are bundled with this build.", 13, foreground: White(0x80)));
        return content;
    }

    private static ScrollViewer Page(UIElement content) => new()
    {
        Content = content,
        Padding = new Thickness(16, 4, 16, 16),
        VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
    };

    /// <summary>Equal columns sharing whatever width the page has (web: grid-cols-N gap-2.5).</summary>
    private static Grid TileGrid(int columns)
    {
        var grid = new Grid { ColumnSpacing = TileGap, RowSpacing = TileGap };
        for (var i = 0; i < columns; i++) grid.ColumnDefinitions.Add(new ColumnDefinition());
        return grid;
    }

    private static void AddTile(Grid grid, Tile tile)
    {
        var index = grid.Children.Count;
        var columns = grid.ColumnDefinitions.Count;
        if (index % columns == 0) grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.Children.Add(tile.Grid(row: index / columns, column: index % columns));
    }

    private static TextBlock SectionTitle(string text) =>
        Text(text, 13, FontWeights.SemiBold, White(0xB0)).With(t => t.Margin = new Thickness(0, 8, 0, 0));

    public void Dispose()
    {
        _bindings.Dispose();
        _preview.Feed = null;
    }

    /// <summary>A thumbnail button; the picked one gets an accent ring and shrinks a little.</summary>
    private sealed class Tile : Button
    {
        private readonly Border _frame;
        private readonly Grid _body = new();
        private readonly string _name;

        /// <param name="aspect">Height over width; the width is the grid column's.</param>
        public Tile(double aspect, string name)
        {
            _name = name;
            Padding = new Thickness(0);
            Background = Transparent;
            BorderThickness = new Thickness(0);
            CornerRadius = new CornerRadius(12);
            HorizontalAlignment = HorizontalAlignment.Stretch;
            VerticalAlignment = VerticalAlignment.Top;
            HorizontalContentAlignment = HorizontalAlignment.Stretch;
            VerticalContentAlignment = VerticalAlignment.Stretch;
            _frame = new Border
            {
                CornerRadius = new CornerRadius(12),
                BorderThickness = new Thickness(3),
                BorderBrush = Transparent,
                Background = Brush(0x2E2E35),
                Child = _body,
            };
            Content = _frame;
            SizeChanged += (_, e) =>
            {
                Height = Math.Round(e.NewSize.Width * aspect);
                CenterPoint = new Vector3((float)e.NewSize.Width / 2, (float)Height / 2, 0);
            };
            ScaleTransition = new Vector3Transition { Duration = TimeSpan.FromMilliseconds(150) };
            this.Tip(name);
            AutomationProperties.SetName(this, name);
        }

        public void SetGlyph(string glyph, string label, Brush background)
        {
            _frame.Background = background;
            _body.Children.Add(Column(2,
                Icon(glyph, 16).With(i => i.Foreground = White()),
                Text(label, 12, foreground: White()).With(t =>
                {
                    t.TextAlignment = TextAlignment.Center;
                    t.TextWrapping = TextWrapping.NoWrap;
                    t.TextTrimming = TextTrimming.CharacterEllipsis;
                })).With(c =>
                {
                    c.HorizontalAlignment = HorizontalAlignment.Center;
                    c.VerticalAlignment = VerticalAlignment.Center;
                }));
        }

        public void SetPicture(string? path, bool video)
        {
            if (path != null) _body.Children.Add(new Image { Source = Thumbnail(path), Stretch = Stretch.UniformToFill });
            if (!video) return;
            _body.Children.Add(new Border
            {
                Width = 22,
                Height = 22,
                CornerRadius = new CornerRadius(11),
                Background = Black(0x99),
                Margin = new Thickness(6),
                HorizontalAlignment = HorizontalAlignment.Left,
                VerticalAlignment = VerticalAlignment.Bottom,
                Child = Icon(Glyphs.Play, 10).With(i => i.Foreground = White()),
            });
        }

        public void SetSticker(string path)
        {
            _body.Padding = new Thickness(10);
            _body.Children.Add(new Image { Source = Thumbnail(path), Stretch = Stretch.Uniform });
        }

        public void Update(bool selected, bool enabled)
        {
            IsEnabled = enabled;
            Opacity = enabled ? 1 : 0.5;
            _frame.BorderBrush = selected ? ThemeBrush("AccentFillColorDefaultBrush") : Transparent;
            Scale = selected ? new Vector3(SelectedScale, SelectedScale, 1) : Vector3.One;
            AutomationProperties.SetName(this, selected ? $"{_name}, selected" : _name);
        }

        // BitmapImage decodes off the UI thread, at thumbnail size rather than the full picture.
        private static BitmapImage Thumbnail(string path) => new()
        {
            DecodePixelWidth = ThumbnailWidth,
            DecodePixelType = DecodePixelType.Logical,
            UriSource = new Uri(path),
        };
    }
}
