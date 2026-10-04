using System.Collections.Specialized;
using System.ComponentModel;

namespace WebRtcDemo.App.Ui;

/// <summary>
/// One-way bindings in code: runs an update now and whenever one of the named properties changes.
/// Disposing unsubscribes everything, so a view never outlives its view model's events.
/// </summary>
internal sealed class Bindings : IDisposable
{
    private readonly List<Action> _unsubscribe = [];

    public void Observe(INotifyPropertyChanged source, Action update, params string[] properties)
    {
        void Handler(object? sender, PropertyChangedEventArgs e)
        {
            if (string.IsNullOrEmpty(e.PropertyName) || properties.Contains(e.PropertyName)) update();
        }

        source.PropertyChanged += Handler;
        _unsubscribe.Add(() => source.PropertyChanged -= Handler);
        update();
    }

    public void Observe(INotifyCollectionChanged source, NotifyCollectionChangedEventHandler handler)
    {
        source.CollectionChanged += handler;
        _unsubscribe.Add(() => source.CollectionChanged -= handler);
    }

    public void Add(Action unsubscribe) => _unsubscribe.Add(unsubscribe);

    public void Dispose()
    {
        foreach (var unsubscribe in _unsubscribe) unsubscribe();
        _unsubscribe.Clear();
    }
}
