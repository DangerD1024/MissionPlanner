using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using MissionPlanner.App.Core.ViewModels.FlightData;
using MissionPlanner.App.Core.ViewModels.Video;

namespace MissionPlanner.App.Controls;

/// <summary>
/// Primary flight display. Draws from a <see cref="HudModel"/>; the owner calls
/// <see cref="Refresh"/> after updating it. Pure Avalonia drawing (Skia underneath), no OpenGL.
/// With a <see cref="VideoFrame"/> set, the video replaces the sky and ground and the overlays
/// are drawn on top of it, as in Mission Planner.
/// </summary>
public sealed class Hud : Control
{
    public static readonly StyledProperty<HudModel?> ModelProperty =
        AvaloniaProperty.Register<Hud, HudModel?>(nameof(Model));

    /// <summary>Video frame drawn behind the overlays (instead of sky and ground); null for none.</summary>
    public static readonly StyledProperty<IImage?> VideoFrameProperty =
        AvaloniaProperty.Register<Hud, IImage?>(nameof(VideoFrame));

    public static readonly StyledProperty<VideoAspect> VideoAspectProperty =
        AvaloniaProperty.Register<Hud, VideoAspect>(nameof(VideoAspect));

    /// <summary>Video state line ("Connecting video…", "No video", errors), drawn top left; empty for none.</summary>
    public static readonly StyledProperty<string?> VideoStatusProperty =
        AvaloniaProperty.Register<Hud, string?>(nameof(VideoStatus));

    private static readonly Typeface Face = new(FontFamily.Default, FontStyle.Normal, FontWeight.SemiBold);
    private static readonly Typeface FaceBold = new(FontFamily.Default, FontStyle.Normal, FontWeight.Bold);

    private static readonly IBrush Sky = new LinearGradientBrush
    {
        StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
        EndPoint = new RelativePoint(0, 1, RelativeUnit.Relative),
        GradientStops = { new GradientStop(Color.Parse("#1f5f9e"), 0), new GradientStop(Color.Parse("#6fb3e6"), 1) },
    };

    private static readonly IBrush Ground = new LinearGradientBrush
    {
        StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
        EndPoint = new RelativePoint(0, 1, RelativeUnit.Relative),
        GradientStops = { new GradientStop(Color.Parse("#7c9a33"), 0), new GradientStop(Color.Parse("#4b621c"), 1) },
    };

    private static readonly IBrush White = Brushes.White;
    private static readonly IBrush Shadow = new SolidColorBrush(Color.FromArgb(150, 0, 0, 0));
    private static readonly IBrush TapeBg = new SolidColorBrush(Color.FromArgb(110, 10, 14, 18));
    private static readonly IBrush BoxBg = new SolidColorBrush(Color.FromArgb(230, 12, 15, 19));
    private static readonly IBrush Red = new SolidColorBrush(Color.Parse("#ff4d4d"));
    private static readonly IBrush Amber = new SolidColorBrush(Color.Parse("#ffb020"));
    private static readonly IBrush Green = new SolidColorBrush(Color.Parse("#7ddc3a"));
    private static readonly IBrush Magenta = new SolidColorBrush(Color.Parse("#ff5cd6"));
    private static readonly IBrush Yellow = new SolidColorBrush(Color.Parse("#ffd21f"));
    private static readonly IPen WhitePen = new Pen(Brushes.White, 1.6);
    private static readonly IPen WhiteThin = new Pen(Brushes.White, 1);
    private static readonly IPen ShadowPen = new Pen(new SolidColorBrush(Color.FromArgb(120, 0, 0, 0)), 3.2);
    private static readonly IPen AircraftPen = new Pen(Yellow, 4, lineCap: PenLineCap.Round, lineJoin: PenLineJoin.Round);
    private static readonly IPen AircraftShadow = new Pen(new SolidColorBrush(Color.FromArgb(160, 0, 0, 0)), 7, lineCap: PenLineCap.Round, lineJoin: PenLineJoin.Round);
    private static readonly IPen MagentaPen = new Pen(Magenta, 2);
    private static readonly IBrush VideoBars = Brushes.Black;

    static Hud()
    {
        AffectsRender<Hud>(ModelProperty, VideoFrameProperty, VideoAspectProperty, VideoStatusProperty);
        ClipToBoundsProperty.OverrideDefaultValue<Hud>(true);
    }

    public HudModel? Model
    {
        get => GetValue(ModelProperty);
        set => SetValue(ModelProperty, value);
    }

    public IImage? VideoFrame
    {
        get => GetValue(VideoFrameProperty);
        set => SetValue(VideoFrameProperty, value);
    }

    public VideoAspect VideoAspect
    {
        get => GetValue(VideoAspectProperty);
        set => SetValue(VideoAspectProperty, value);
    }

    public string? VideoStatus
    {
        get => GetValue(VideoStatusProperty);
        set => SetValue(VideoStatusProperty, value);
    }

    private long _drawnVersion = -1;

    /// <summary>Redraws if the model changed since the last frame.</summary>
    public void Refresh()
    {
        if (Model is { } m && m.Version != _drawnVersion)
            InvalidateVisual();
    }

    public override void Render(DrawingContext ctx)
    {
        var m = Model ?? new HudModel();
        _drawnVersion = m.Version;

        var w = Bounds.Width;
        var h = Bounds.Height;
        if (w < 50 || h < 50)
            return;

        var scale = Math.Clamp(Math.Min(w / 480, h / 360), 0.6, 2.2);
        var cx = w / 2;
        var cy = h / 2 + 8 * scale;
        var ppd = h / 50; // pixels per degree of pitch

        var video = VideoFrame;
        var hasVideo = video != null && DrawVideo(ctx, video, VideoAspect, w, h);

        DrawHorizon(ctx, m, w, h, cx, cy, ppd, scale, sky: !hasVideo);
        DrawRollScale(ctx, m, cx, cy, h, scale);
        DrawAircraft(ctx, cx, cy, scale);
        DrawHeadingTape(ctx, m, w, scale);
        DrawSpeedTape(ctx, m, h, cy, scale);
        DrawAltTape(ctx, m, w, h, cy, scale);
        DrawText(ctx, m, w, h, cx, cy, scale);
        DrawVideoStatus(ctx, VideoStatus, w, scale);
    }

    /// <summary>The frame scaled to fill (cropped) or fit (with bars); false if it has no size.</summary>
    private static bool DrawVideo(DrawingContext ctx, IImage frame, VideoAspect aspect, double w, double h)
    {
        var size = frame.Size;
        if (size.Width <= 0 || size.Height <= 0)
            return false;
        var (src, dst) = VideoLayout.Place(size.Width, size.Height, w, h, aspect);
        if (aspect == VideoAspect.Fit)
            ctx.FillRectangle(VideoBars, new Rect(0, 0, w, h));
        // bilinear is enough down to half size; beyond that mipmaps avoid shimmer, at a price per frame
        var mode = dst.Width < src.Width * 0.5
            ? Avalonia.Media.Imaging.BitmapInterpolationMode.MediumQuality
            : Avalonia.Media.Imaging.BitmapInterpolationMode.LowQuality;
        using (ctx.PushRenderOptions(new RenderOptions { BitmapInterpolationMode = mode }))
        {
            ctx.DrawImage(frame, new Rect(src.X, src.Y, src.Width, src.Height), new Rect(dst.X, dst.Y, dst.Width, dst.Height));
        }

        return true;
    }

    private static void DrawVideoStatus(DrawingContext ctx, string? status, double w, double scale)
    {
        if (string.IsNullOrEmpty(status))
            return;
        // top left, between the speed tape and the middle, under the link quality
        var x = 64 * scale;
        var y = 48 * scale;
        var t = new FormattedText(status, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, Face, 12 * scale, Amber)
        {
            MaxTextWidth = Math.Max(80, w / 2 - x),
            MaxLineCount = 3,
            Trimming = TextTrimming.CharacterEllipsis,
        };
        var pad = 4 * scale;
        ctx.FillRectangle(BoxBg, new Rect(x - pad, y - pad / 2, t.Width + 2 * pad, t.Height + pad), 3);
        ctx.DrawText(t, new Point(x, y));
    }

    private static void DrawHorizon(DrawingContext ctx, HudModel m, double w, double h, double cx, double cy, double ppd, double scale, bool sky = true)
    {
        var big = Math.Sqrt(w * w + h * h) * 2;
        var transform = Matrix.CreateTranslation(0, m.Pitch * ppd) * Matrix.CreateRotation(-m.Roll * Math.PI / 180) *
                        Matrix.CreateTranslation(cx, cy);
        using (ctx.PushTransform(transform))
        {
            if (sky)
            {
                ctx.FillRectangle(Sky, new Rect(-big / 2, -big, big, big));
                ctx.FillRectangle(Ground, new Rect(-big / 2, 0, big, big));
            }

            ctx.DrawLine(WhitePen, new Point(-big / 2, 0), new Point(big / 2, 0));

            // pitch ladder, 5 degree steps, labelled every 10
            for (var deg = -60; deg <= 60; deg += 5)
            {
                if (deg == 0 || Math.Abs(deg - m.Pitch) > 26)
                    continue;
                var y = -deg * ppd;
                var half = (deg % 10 == 0 ? 46 : 22) * scale;
                ctx.DrawLine(WhiteThin, new Point(-half, y), new Point(half, y));
                if (deg % 10 == 0)
                {
                    var label = Text(deg.ToString(CultureInfo.InvariantCulture), 11 * scale, White, Face);
                    ctx.DrawText(label.Fill, new Point(half + 5 * scale, y - label.Height / 2));
                    ctx.DrawText(label.Fill, new Point(-half - 5 * scale - label.Width, y - label.Height / 2));
                }
            }
        }
    }

    private static void DrawRollScale(DrawingContext ctx, HudModel m, double cx, double cy, double h, double scale)
    {
        var r = h * 0.38;
        foreach (var deg in new[] { -60, -45, -30, -20, -10, 0, 10, 20, 30, 45, 60 })
        {
            var a = (deg - 90) * Math.PI / 180;
            var len = (deg % 30 == 0 ? 12 : 7) * scale;
            var p1 = new Point(cx + Math.Cos(a) * r, cy + Math.Sin(a) * r);
            var p2 = new Point(cx + Math.Cos(a) * (r + len), cy + Math.Sin(a) * (r + len));
            ctx.DrawLine(ShadowPen, p1, p2);
            ctx.DrawLine(WhitePen, p1, p2);
        }

        // pointer turns with the aircraft
        var ra = (-m.Roll - 90) * Math.PI / 180;
        var tip = new Point(cx + Math.Cos(ra) * (r - 2), cy + Math.Sin(ra) * (r - 2));
        var baseL = new Point(cx + Math.Cos(ra - 0.05) * (r - 14 * scale), cy + Math.Sin(ra - 0.05) * (r - 14 * scale));
        var baseR = new Point(cx + Math.Cos(ra + 0.05) * (r - 14 * scale), cy + Math.Sin(ra + 0.05) * (r - 14 * scale));
        var tri = new StreamGeometry();
        using (var g = tri.Open())
        {
            g.BeginFigure(tip, true);
            g.LineTo(baseL);
            g.LineTo(baseR);
            g.EndFigure(true);
        }

        ctx.DrawGeometry(Yellow, null, tri);
    }

    private static void DrawAircraft(DrawingContext ctx, double cx, double cy, double scale)
    {
        var s = scale;
        var wing = new StreamGeometry();
        using (var g = wing.Open())
        {
            g.BeginFigure(new Point(cx - 70 * s, cy), false);
            g.LineTo(new Point(cx - 30 * s, cy));
            g.LineTo(new Point(cx - 15 * s, cy + 12 * s));
            g.LineTo(new Point(cx, cy));
            g.LineTo(new Point(cx + 15 * s, cy + 12 * s));
            g.LineTo(new Point(cx + 30 * s, cy));
            g.LineTo(new Point(cx + 70 * s, cy));
            g.EndFigure(false);
        }

        ctx.DrawGeometry(null, AircraftShadow, wing);
        ctx.DrawGeometry(null, AircraftPen, wing);
    }

    private static void DrawHeadingTape(DrawingContext ctx, HudModel m, double w, double scale)
    {
        var th = 24 * scale;
        ctx.FillRectangle(TapeBg, new Rect(0, 0, w, th));
        var span = 90.0; // degrees visible across the width
        var ppd = w / span;
        var hdg = Normalize(m.Heading);

        for (var d = (int)Math.Floor(hdg - span / 2) - 1; d <= hdg + span / 2 + 1; d++)
        {
            if (d % 5 != 0)
                continue;
            var x = w / 2 + (d - hdg) * ppd;
            var n = (int)Normalize(d);
            var tall = n % 15 == 0;
            ctx.DrawLine(WhiteThin, new Point(x, th), new Point(x, th - (tall ? 8 : 4) * scale));
            if (tall)
            {
                var label = n switch
                {
                    0 => "N", 45 => "NE", 90 => "E", 135 => "SE", 180 => "S", 225 => "SW", 270 => "W", 315 => "NW",
                    _ => n.ToString(CultureInfo.InvariantCulture),
                };
                var t = Text(label, 11 * scale, White, label.Length <= 2 && !char.IsDigit(label[0]) ? FaceBold : Face);
                ctx.DrawText(t.Fill, new Point(x - t.Width / 2, 2 * scale));
            }
        }

        // target heading and bearing to waypoint
        Marker(m.TargetHeading, Red);
        Marker(m.NavBearing, Magenta);

        var box = Text(((int)Math.Round(hdg) % 360).ToString("000", CultureInfo.InvariantCulture), 13 * scale, White, FaceBold);
        var br = new Rect(w / 2 - box.Width / 2 - 6 * scale, 0, box.Width + 12 * scale, th + 2 * scale);
        ctx.FillRectangle(BoxBg, br, 3);
        ctx.DrawText(box.Fill, new Point(br.X + 6 * scale, (th - box.Height) / 2 + 1));

        void Marker(double bearing, IBrush brush)
        {
            var delta = NormalizeSigned(bearing - hdg);
            if (Math.Abs(delta) > span / 2)
                return;
            var x = w / 2 + delta * ppd;
            ctx.FillRectangle(brush, new Rect(x - 2 * scale, th - 5 * scale, 4 * scale, 5 * scale));
        }
    }

    private static void DrawSpeedTape(DrawingContext ctx, HudModel m, double h, double cy, double scale)
    {
        var tw = 54 * scale;
        var top = 34 * scale;
        var bottom = h - 64 * scale;
        DrawVerticalTape(ctx, new Rect(4 * scale, top, tw, bottom - top), cy, m.Groundspeed, 5, 2, scale, leftAligned: true);
        if (m.TargetSpeed > 0)
            TapeBug(ctx, 4 * scale + tw, cy, m.TargetSpeed - m.Groundspeed, (bottom - top) / 40, scale);
        Label(ctx, m.SpeedUnit, new Point(6 * scale, top - 2), 10 * scale, White);
    }

    private static void DrawAltTape(DrawingContext ctx, HudModel m, double w, double h, double cy, double scale)
    {
        var tw = 54 * scale;
        var top = 34 * scale;
        var bottom = h - 64 * scale;
        var x = w - tw - 10 * scale;
        DrawVerticalTape(ctx, new Rect(x, top, tw, bottom - top), cy, m.Altitude, 10, 2, scale, leftAligned: false);
        Label(ctx, m.AltUnit, new Point(x + 2 * scale, top - 2), 10 * scale, White);

        // vertical speed bar beside the tape, ±5 m/s full scale
        var vsH = (bottom - top) / 2;
        var vs = Math.Clamp(m.VerticalSpeed, -5, 5) / 5 * vsH;
        var bx = w - 8 * scale;
        ctx.FillRectangle(TapeBg, new Rect(bx, top, 5 * scale, bottom - top));
        ctx.FillRectangle(m.VerticalSpeed >= 0 ? Green : Amber,
            vs >= 0 ? new Rect(bx, cy - vs, 5 * scale, vs) : new Rect(bx, cy, 5 * scale, -vs));
    }

    private static void DrawVerticalTape(DrawingContext ctx, Rect r, double cy, double value, int major, int minor, double scale, bool leftAligned)
    {
        ctx.FillRectangle(TapeBg, r, 4);
        var ppu = r.Height / (major * 4.0);
        using (ctx.PushClip(r))
        {
            var first = (int)Math.Floor((value - r.Height / 2 / ppu) / minor) * minor;
            for (var v = first; v <= value + r.Height / 2 / ppu + minor; v += minor)
            {
                var y = cy - (v - value) * ppu;
                var isMajor = v % major == 0;
                var len = (isMajor ? 10 : 5) * scale;
                var x0 = leftAligned ? r.Right - len : r.Left;
                ctx.DrawLine(WhiteThin, new Point(x0, y), new Point(x0 + len, y));
                if (isMajor)
                {
                    var t = Text(v.ToString(CultureInfo.InvariantCulture), 11 * scale, White, Face);
                    var tx = leftAligned ? r.Right - len - 3 * scale - t.Width : r.Left + len + 3 * scale;
                    ctx.DrawText(t.Fill, new Point(tx, y - t.Height / 2));
                }
            }
        }

        var box = Text(value.ToString(Math.Abs(value) < 100 ? "0.0" : "0", CultureInfo.InvariantCulture), 14 * scale, White, FaceBold);
        var bh = box.Height + 6 * scale;
        var br = new Rect(r.X - 2 * scale, cy - bh / 2, r.Width + 4 * scale, bh);
        ctx.FillRectangle(BoxBg, br, 3);
        ctx.DrawRectangle(null, WhiteThin, br, 3, 3);
        ctx.DrawText(box.Fill, new Point(leftAligned ? br.Right - box.Width - 6 * scale : br.X + 6 * scale, cy - box.Height / 2));
    }

    private static void TapeBug(DrawingContext ctx, double x, double cy, double delta, double ppu, double scale)
    {
        var y = cy - Math.Clamp(delta * ppu, -cy, cy);
        ctx.FillRectangle(Magenta, new Rect(x, y - 4 * scale, 4 * scale, 8 * scale));
    }

    private static void DrawText(DrawingContext ctx, HudModel m, double w, double h, double cx, double cy, double scale)
    {
        var line = 17 * scale;
        var fs = 13 * scale;

        // armed state, centre
        var armed = Text(m.Armed ? "ARMED" : "DISARMED", 22 * scale, Red, FaceBold);
        Outlined(ctx, armed, new Point(cx - armed.Width / 2, cy - h * 0.2 - armed.Height / 2));

        if (!m.Connected)
        {
            var nl = Text("NO LINK", 14 * scale, Amber, FaceBold);
            Outlined(ctx, nl, new Point(cx - nl.Width / 2, cy - h * 0.2 + armed.Height / 2 + 2));
        }
        else if (m.LinkLost)
        {
            var ll = Text(m.LinkLostText, 15 * scale, Red, FaceBold);
            Outlined(ctx, ll, new Point(cx - ll.Width / 2, cy - h * 0.2 + armed.Height / 2 + 2));
        }
        else if (!m.Armed && !m.ReadyToArm)
        {
            var nr = Text("Not Ready to Arm", 13 * scale, Red, FaceBold);
            Outlined(ctx, nr, new Point(cx - nr.Width / 2, h - 2 * line - 4 * scale));
        }

        if (!string.IsNullOrEmpty(m.Message))
        {
            var msg = Text(m.Message, 13 * scale, Red, FaceBold);
            Outlined(ctx, msg, new Point(cx - msg.Width / 2, h - 3 * line - 8 * scale));
        }

        // left column: airspeed / groundspeed
        var left = 64 * scale;
        Label(ctx, $"AS {m.Airspeed:0.0}{m.SpeedUnit}", new Point(left, h - 3 * line - 6 * scale), fs, White);
        Label(ctx, $"GS {m.Groundspeed:0.0}{m.SpeedUnit}", new Point(left, h - 2 * line - 6 * scale), fs, White);

        // right column: mode, waypoint, time in air
        var mode = Text(string.IsNullOrEmpty(m.Mode) ? "Unknown" : m.Mode, 15 * scale, m.Armed ? Green : Red, FaceBold);
        Outlined(ctx, mode, new Point(w - 72 * scale - mode.Width, h - 3 * line - 8 * scale));
        var wp = Text($"{m.WpDistance:0}{m.AltUnit}>{m.WpNumber}", fs, White, Face);
        Outlined(ctx, wp, new Point(w - 72 * scale - wp.Width, h - 2 * line - 6 * scale));
        var tia = Text(m.TimeInAir.ToString(@"hh\:mm\:ss", CultureInfo.InvariantCulture), fs, White, Face);
        Outlined(ctx, tia, new Point(w - 72 * scale - tia.Width, 30 * scale));

        // bottom row: battery, EKF, vibe, GPS
        var y = h - line - 2 * scale;
        var batt = m.BatteryRemaining >= 0
            ? $"Bat {m.BatteryVoltage:0.00}v {m.BatteryCurrent:0.0}A {m.BatteryRemaining}%"
            : $"Bat {m.BatteryVoltage:0.00}v {m.BatteryCurrent:0.0}A";
        var battBrush = m.BatteryRemaining is >= 0 and < 20 || (m.BatteryVoltage > 0 && m.BatteryVoltage < 3) ? Red : White;
        var bt = Text(batt, fs, battBrush, FaceBold);
        Outlined(ctx, bt, new Point(8 * scale, y));

        var gps = Text("GPS: " + m.GpsText, fs, Level(m.GpsLevel), FaceBold);
        Outlined(ctx, gps, new Point(w - 8 * scale - gps.Width, y));

        var ekf = Text("EKF", fs, Level(m.EkfLevel), FaceBold);
        var vibe = Text("Vibe", fs, Level(m.VibeLevel), FaceBold);
        var mid = (8 * scale + bt.Width + (w - 8 * scale - gps.Width)) / 2;
        Outlined(ctx, ekf, new Point(mid - ekf.Width - 8 * scale, y));
        Outlined(ctx, vibe, new Point(mid + 8 * scale, y));

        // link quality, top left under the heading tape
        if (m.Connected)
            Label(ctx, $"{m.LinkQuality}%", new Point(64 * scale, 30 * scale), 11 * scale, m.LinkQuality < 80 ? Amber : White);
    }

    private static IBrush Level(HudLevel l) => l switch
    {
        HudLevel.Ok => White,
        HudLevel.Warning => Amber,
        _ => Red,
    };

    private static void Label(DrawingContext ctx, string text, Point at, double size, IBrush brush) =>
        Outlined(ctx, Text(text, size, brush, Face), at);

    private static void Outlined(DrawingContext ctx, HudText t, Point at)
    {
        ctx.DrawText(t.Shadow, at + new Point(1, 1));
        ctx.DrawText(t.Fill, at);
    }

    private static HudText Text(string s, double size, IBrush brush, Typeface face) => new(
        new FormattedText(s, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, face, size, brush),
        new FormattedText(s, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, face, size, Shadow));

    /// <summary>A label and its drop shadow, measured once.</summary>
    private sealed record HudText(FormattedText Fill, FormattedText Shadow)
    {
        public double Width => Fill.Width;

        public double Height => Fill.Height;
    }

    private static double Normalize(double deg) => ((deg % 360) + 360) % 360;

    private static double NormalizeSigned(double deg)
    {
        var d = Normalize(deg);
        return d > 180 ? d - 360 : d;
    }
}
