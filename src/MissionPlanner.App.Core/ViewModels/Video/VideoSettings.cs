using System.Globalization;
using System.Text.Json;
using log4net;
using MissionPlanner.Utilities;

namespace MissionPlanner.App.Core.ViewModels.Video;

/// <summary>Where the video settings live; the app uses <see cref="AppVideoSettingsStore"/>.</summary>
public interface IVideoSettingsStore
{
    string? Get(string key);

    /// <summary>Null removes the key.</summary>
    void Set(string key, string? value);

    /// <summary>Writes the settings to disk.</summary>
    void Save();
}

/// <summary>The app's settings file (config.next.xml).</summary>
public sealed class AppVideoSettingsStore : IVideoSettingsStore
{
    private static readonly ILog Log = LogManager.GetLogger(typeof(AppVideoSettingsStore));

    public string? Get(string key) => Settings.Instance[key];

    public void Set(string key, string? value)
    {
        if (value == null)
            Settings.Instance.Remove(key);
        else
            Settings.Instance[key] = value;
    }

    public void Save()
    {
        try
        {
            Settings.Instance.Save();
        }
        catch (Exception ex)
        {
            Log.Error("settings save", ex);
        }
    }
}

/// <summary>
/// Saved video sources and options, kept under <c>next_video_*</c> keys: the source list as JSON,
/// the selected source's id, auto-start, aspect and the last hand-typed pipeline.
/// </summary>
public sealed class VideoSettings
{
    public const string SourcesKey = "next_video_sources";
    public const string SelectedKey = "next_video_selected";
    public const string AutoStartKey = "next_video_autostart";
    public const string AspectKey = "next_video_aspect";
    public const string LastPipelineKey = "next_video_gstreamer";

    /// <summary>Id of the source "Set GStreamer source…" writes to.</summary>
    public const string QuickPipelineId = "gstreamer";

    private static readonly ILog Log = LogManager.GetLogger(typeof(VideoSettings));
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = false };

    public List<VideoSourceConfig> Sources { get; set; } = new();

    public string? SelectedId { get; set; }

    public bool AutoStart { get; set; }

    public VideoAspect Aspect { get; set; } = VideoAspect.Fill;

    /// <summary>The pipeline last typed into "Set GStreamer source…".</summary>
    public string? LastPipeline { get; set; }

    public VideoSourceConfig? Selected => Sources.FirstOrDefault(s => s.Id == SelectedId);

    /// <summary>The usual starting points: ArduPilot's UDP ports and a test pattern.</summary>
    public static List<VideoSourceConfig> DefaultSources() => new()
    {
        new() { Id = "udp5600", Name = "UDP H.264 (5600)", Type = VideoSourceType.UdpH264, Port = VideoSourceConfig.DefaultH264Port },
        new() { Id = "udp5601", Name = "UDP H.265 (5601)", Type = VideoSourceType.UdpH265, Port = VideoSourceConfig.DefaultH265Port },
        new() { Id = "test", Name = "Test pattern", Type = VideoSourceType.TestPattern },
    };

    public static VideoSettings Load(IVideoSettingsStore store)
    {
        var settings = new VideoSettings
        {
            SelectedId = store.Get(SelectedKey),
            AutoStart = bool.TryParse(store.Get(AutoStartKey), out var auto) && auto,
            Aspect = Enum.TryParse<VideoAspect>(store.Get(AspectKey), ignoreCase: true, out var aspect) ? aspect : VideoAspect.Fill,
            LastPipeline = store.Get(LastPipelineKey),
        };

        var json = store.Get(SourcesKey);
        if (json == null)
        {
            settings.Sources = DefaultSources();
        }
        else
        {
            try
            {
                settings.Sources = (JsonSerializer.Deserialize<List<VideoSourceConfig>>(json, Json) ?? new())
                    .Where(s => s != null)
                    .ToList();
            }
            catch (JsonException ex)
            {
                Log.Warn("video sources unreadable, using defaults", ex);
                settings.Sources = DefaultSources();
            }
        }

        // ids must be unique for selection to mean anything
        var seen = new HashSet<string>();
        foreach (var s in settings.Sources)
        {
            if (string.IsNullOrEmpty(s.Id) || !seen.Add(s.Id))
            {
                s.Id = VideoSourceConfig.NewId();
                seen.Add(s.Id);
            }
        }

        if (settings.Selected == null)
            settings.SelectedId = settings.Sources.FirstOrDefault()?.Id;
        return settings;
    }

    public void Save(IVideoSettingsStore store)
    {
        store.Set(SourcesKey, JsonSerializer.Serialize(Sources, Json));
        store.Set(SelectedKey, SelectedId);
        store.Set(AutoStartKey, AutoStart.ToString(CultureInfo.InvariantCulture));
        store.Set(AspectKey, Aspect.ToString());
        store.Set(LastPipelineKey, LastPipeline);
        store.Save();
    }
}
