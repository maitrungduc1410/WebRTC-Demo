using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using WebRtcDemo.App.Ui;
using WebRtcDemo.Core.Call;
using static WebRtcDemo.App.Ui.UiFactory;

namespace WebRtcDemo.App.Views;

/// <summary>Short notices at the top center of the window, a few at a time. A click dismisses one early.</summary>
internal sealed class ToastPresenter : StackPanel
{
    private const int MaxVisible = 3;
    private static readonly TimeSpan Duration = TimeSpan.FromSeconds(3.5);

    // A DispatcherQueueTimer that nothing references can be garbage collected before it fires
    // (a call starting allocates a lot), and its toast would then never go away.
    private readonly Dictionary<UIElement, DispatcherQueueTimer> _timers = [];

    public ToastPresenter()
    {
        HorizontalAlignment = HorizontalAlignment.Center;
        VerticalAlignment = VerticalAlignment.Top;
        Margin = new Thickness(16, 60, 16, 0);
        Spacing = 8;
        ChildrenTransitions = [new AddDeleteThemeTransition()];
    }

    public void Show(Toast toast)
    {
        // The same notice twice in a row (e.g. two quick reconnects) is shown once.
        if (Children.Count > 0 && Children[^1] is FrameworkElement { Tag: string last } && last == toast.Text) return;
        while (Children.Count >= MaxVisible) Dismiss(Children[0]);

        var accent = toast.Kind switch
        {
            ToastKind.Success => Brush(0x6CCB5F),
            ToastKind.Warning => Brush(0xFCE100),
            ToastKind.Error => Brush(0xFF99A4),
            _ => White(),
        };
        var content = Row(10, Text(toast.Text, 14, foreground: White()).With(t => t.VerticalAlignment = VerticalAlignment.Center));
        if (toast.Glyph != null) content.Children.Insert(0, Icon(toast.Glyph, 16).With(i => i.Foreground = accent));

        var card = new Border
        {
            Tag = toast.Text,
            Child = content,
            Background = Brush(0x202020, 0xEE),
            BorderBrush = White(0x22),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(20),
            Padding = new Thickness(16, 10, 18, 10),
            HorizontalAlignment = HorizontalAlignment.Center,
            Translation = new System.Numerics.Vector3(0, 0, 24),
            Shadow = new ThemeShadow(),
        };
        card.Tapped += (_, e) =>
        {
            e.Handled = true;
            Dismiss(card);
        };
        Children.Add(card);

        var timer = DispatcherQueue.CreateTimer();
        timer.Interval = Duration;
        timer.IsRepeating = false;
        timer.Tick += (_, _) => Dismiss(card);
        _timers[card] = timer;
        timer.Start();
    }

    private void Dismiss(UIElement card)
    {
        Children.Remove(card);
        if (_timers.Remove(card, out var timer)) timer.Stop();
    }
}
