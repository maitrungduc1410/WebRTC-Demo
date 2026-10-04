using System.Text.Json;
using System.Text.Json.Serialization;

namespace WebRtcDemo.Core.Settings;

public sealed record AppSettings
{
    /// <summary>Null means <see cref="ServerAddress.DefaultUrl"/>, like the web client's storage.</summary>
    public string? ServerUrl { get; init; }
    /// <summary>The group call server; null means port 4001 on the signaling server's host.</summary>
    public string? SfuUrl { get; init; }
    public bool E2ee { get; init; }
    public string? CameraId { get; init; }
    public string? MicrophoneId { get; init; }
    public string? SpeakerId { get; init; }
    /// <summary>Background and sticker ids from the effects catalog; null means none.</summary>
    public string? EffectsBackground { get; init; }
    public string? EffectsSticker { get; init; }

    [JsonIgnore]
    public string EffectiveServerUrl => ServerAddress.Normalize(ServerUrl) ?? ServerAddress.DefaultUrl;

    [JsonIgnore]
    public string EffectiveSfuUrl => SfuAddress.Normalize(SfuUrl) ?? SfuAddress.DefaultFor(EffectiveServerUrl);
}

[JsonSerializable(typeof(AppSettings))]
[JsonSourceGenerationOptions(WriteIndented = true, PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
internal sealed partial class SettingsJsonContext : JsonSerializerContext;

/// <summary>Settings as JSON in %LOCALAPPDATA%\WebRtcDemo\settings.json (any path in tests).</summary>
public sealed class SettingsStore(string path)
{
    public static string DefaultPath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "WebRtcDemo", "settings.json");

    public string FilePath { get; } = path;

    public AppSettings Load()
    {
        try
        {
            if (!File.Exists(FilePath)) return new AppSettings();
            using var stream = File.OpenRead(FilePath);
            return JsonSerializer.Deserialize(stream, SettingsJsonContext.Default.AppSettings) ?? new AppSettings();
        }
        catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException)
        {
            // A damaged file must not keep the app from starting.
            return new AppSettings();
        }
    }

    public void Save(AppSettings settings)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            var stored = settings with
            {
                ServerUrl = ServerAddress.Normalize(settings.ServerUrl) is { } url && url != ServerAddress.DefaultUrl ? url : null,
                // Kept even when it is localhost: null means "on the signaling host", which can differ.
                SfuUrl = SfuAddress.Normalize(settings.SfuUrl),
            };
            var temp = FilePath + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(stored, SettingsJsonContext.Default.AppSettings));
            File.Move(temp, FilePath, overwrite: true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // Settings are a convenience; the values still apply to this session.
        }
    }

    public AppSettings Update(Func<AppSettings, AppSettings> change)
    {
        var updated = change(Load());
        Save(updated);
        return updated;
    }
}
