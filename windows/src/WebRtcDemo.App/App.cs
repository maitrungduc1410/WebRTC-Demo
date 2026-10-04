using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Markup;
using Microsoft.UI.Xaml.XamlTypeInfo;
using WebRtcDemo.Core;
using WebRtcDemo.Core.Settings;
using WebRtcDemo.Core.Signaling;
using WebRtcDemo.Interop;

namespace WebRtcDemo.App;

/// <summary>
/// The composition root. With no App.xaml, the WinUI control metadata and theme resources are
/// registered here instead of by the XAML compiler.
/// </summary>
public sealed partial class App : Application, IXamlMetadataProvider
{
    private readonly XamlControlsXamlMetaDataProvider _controlsMetadata = new();
    private MainWindow? _window;

    public App()
    {
        UnhandledException += (_, e) => Log("Unhandled: " + e.Exception);
    }

    public static new App Current => (App)Application.Current;

    public IDispatcher Dispatcher { get; private set; } = null!;
    public SettingsStore Settings { get; } = new(SettingsStore.DefaultPath);
    public SignalingClient Signaling { get; private set; } = null!;
    public HttpServerProbe ServerProbe { get; } = new();
    /// <summary>Null when the native WebRTC libraries could not be loaded.</summary>
    public PeerConnectionFactory? Factory { get; private set; }
    public string? StartupError { get; private set; }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        Resources.MergedDictionaries.Add(new XamlControlsResources());
        Dispatcher = new UiDispatcher(DispatcherQueue.GetForCurrentThread());
        Signaling = new SignalingClient(url => new WebSocketMessageSocket(url), Dispatcher);

        try
        {
            WebRtcRuntime.Initialize();
            WebRtcRuntime.CallbackException += e => Log("Native callback: " + e);
#if DEBUG
            // WEBRTC_DEMO_LOG=info (or verbose) also shows camera formats and capture state.
            var logLevel = Environment.GetEnvironmentVariable("WEBRTC_DEMO_LOG")?.Trim().ToLowerInvariant() switch
            {
                "verbose" => LogSeverity.Verbose,
                "info" => LogSeverity.Info,
                _ => LogSeverity.Warning,
            };
            WebRtcRuntime.SetLogSink(message => System.Diagnostics.Debug.WriteLine(message), logLevel);
#endif
            Factory = new PeerConnectionFactory();
            EffectsAcceleration.Start();
        }
        catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException or BadImageFormatException or WebRtcException)
        {
            StartupError = e is WebRtcException
                ? e.Message
                : "rtc_shim.dll or libwebrtc.dll is missing next to WebRtcDemo.exe. Build them with native/RtcShim/scripts/build-shim.ps1.";
            Log(e.ToString());
        }

        _window = new MainWindow();
        _window.Closed += (_, _) => Shutdown();
        _window.Activate();
    }

    private void Shutdown()
    {
        // Leaving sends "leave" and closes the socket; give it a moment to get out.
        var closing = _window?.EndCall() ?? Task.CompletedTask;
        try
        {
            closing.Wait(TimeSpan.FromSeconds(1));
        }
        catch (AggregateException)
        {
        }
        Signaling.Dispose();
        ServerProbe.Dispose();
        Factory?.Dispose();
        if (Factory != null) WebRtcRuntime.Terminate();
    }

    internal static void Log(string message) => System.Diagnostics.Debug.WriteLine("[WebRtcDemo] " + message);

    public IXamlType GetXamlType(Type type) => _controlsMetadata.GetXamlType(type);

    public IXamlType GetXamlType(string fullName) => _controlsMetadata.GetXamlType(fullName);

    public XmlnsDefinition[] GetXmlnsDefinitions() => _controlsMetadata.GetXmlnsDefinitions();
}

internal sealed class UiDispatcher(DispatcherQueue queue) : IDispatcher
{
    public bool HasThreadAccess => queue.HasThreadAccess;

    public void Post(Action action) => queue.TryEnqueue(() => action());
}