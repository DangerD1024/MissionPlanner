using MissionPlanner.App.Core.Video;

namespace MissionPlanner.App.Core.ViewModels.Video;

/// <summary>A rectangle in plain numbers (the core has no UI types).</summary>
public readonly record struct VideoRect(double X, double Y, double Width, double Height);

/// <summary>Placement of the video frame in the HUD, and the HUD's video status line.</summary>
public static class VideoLayout
{
    /// <summary>
    /// Which part of a <paramref name="frameWidth"/>×<paramref name="frameHeight"/> frame to draw where in
    /// a <paramref name="width"/>×<paramref name="height"/> area. Fill crops the frame's overhanging
    /// edges (centred); Fit shrinks it to fit, centred, leaving bars.
    /// </summary>
    public static (VideoRect Source, VideoRect Dest) Place(double frameWidth, double frameHeight, double width, double height, VideoAspect aspect)
    {
        var full = new VideoRect(0, 0, frameWidth, frameHeight);
        if (frameWidth <= 0 || frameHeight <= 0 || width <= 0 || height <= 0)
            return (full, new VideoRect(0, 0, Math.Max(width, 0), Math.Max(height, 0)));

        var frameAspect = frameWidth / frameHeight;
        var areaAspect = width / height;

        if (aspect == VideoAspect.Fill)
        {
            if (frameAspect > areaAspect)
            {
                // frame wider than the area: crop left and right
                var w = frameHeight * areaAspect;
                return (new VideoRect((frameWidth - w) / 2, 0, w, frameHeight), new VideoRect(0, 0, width, height));
            }

            var h = frameWidth / areaAspect;
            return (new VideoRect(0, (frameHeight - h) / 2, frameWidth, h), new VideoRect(0, 0, width, height));
        }

        if (frameAspect > areaAspect)
        {
            var h = width / frameAspect;
            return (full, new VideoRect(0, (height - h) / 2, width, h));
        }

        var dw = height * frameAspect;
        return (full, new VideoRect((width - dw) / 2, 0, dw, height));
    }

    /// <summary>The HUD's video status line; empty when there is nothing to say.</summary>
    public static string StatusText(VideoStreamState state, string? error)
    {
        // GStreamer's messages end with a full stop; ours add their own
        var why = error?.Trim().TrimEnd('.');
        return state switch
        {
            VideoStreamState.Connecting => "Connecting video…",
            VideoStreamState.Waiting => "No video",
            VideoStreamState.Reconnecting => string.IsNullOrEmpty(why) ? "Video lost, reconnecting…" : $"Video lost: {why}. Reconnecting…",
            VideoStreamState.Failed => "Video error: " + (string.IsNullOrEmpty(why) ? "unknown" : why),
            _ => "",
        };
    }

    /// <summary>Whether the last frame belongs on screen: while playing, and through a short stall.</summary>
    public static bool ShowsFrame(VideoStreamState state) => state is VideoStreamState.Playing or VideoStreamState.Waiting;
}
