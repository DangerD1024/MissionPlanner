using System.Diagnostics;
using System.Numerics;
using System.Runtime.CompilerServices;
using GMap.NET.MapProviders;
using log4net;
using MissionPlanner.App.Core.Services;
using MissionPlanner.App.Core.Video;

namespace MissionPlanner.App.Core.Synthetic;

public enum SyntheticViewKind
{
    /// <summary>From the vehicle (behind the HUD): attitude moves the horizon.</summary>
    FirstPerson,

    /// <summary>Third person, following the vehicle: the horizon stays level, the model turns; orbit with the mouse.</summary>
    Chase,
}

/// <summary>
/// One picture of the 3D scene at its own size, rendered off screen at up to 60 fps; frames land in
/// <see cref="Frames"/> like decoded video. Get one from <see cref="SyntheticScene.Acquire"/> and
/// dispose it when no longer shown.
/// </summary>
public sealed class SyntheticView : IFrameSource, IDisposable
{
    private readonly SyntheticScene _scene;

    internal SyntheticView(SyntheticScene scene, SyntheticViewKind kind)
    {
        _scene = scene;
        Kind = kind;
    }

    public SyntheticViewKind Kind { get; }

    public VideoFrameBuffer Frames { get; } = new();

    public event EventHandler? FrameArrived;

    /// <summary>Pixel size to render at (capped at 1920×1080); 0 renders nothing.</summary>
    public int Width { get; set; }

    public int Height { get; set; }

    /// <summary>First person: how far below the middle the horizon sits (the HUD draws it 8·scale px low).</summary>
    public float CentreDownFraction { get; set; }

    /// <summary>First person: horizontal field of view in degrees; null matches the HUD's pitch ladder.</summary>
    public double? HorizontalFov { get; set; }

    /// <summary>First person: camera tilted up on the frame, degrees (FPV uptilt).</summary>
    public double CameraTilt { get; set; }

    /// <summary>
    /// Chase: compass direction the camera looks along (it sits behind); NaN follows the vehicle's
    /// heading (smoothly), staying behind it as it turns.
    /// </summary>
    public double OrbitHeading { get; set; } = double.NaN;

    /// <summary>Chase: the heading the camera looked along in the last frame (to start an orbit from).</summary>
    public double ShownHeading { get; internal set; } = double.NaN;

    /// <summary>Chase: degrees above the vehicle (5–85).</summary>
    public double OrbitElevation { get; set; } = 18;

    /// <summary>Chase: metres from the vehicle.</summary>
    public double Distance { get; set; } = 12;

    internal OffscreenTarget? Target { get; set; }

    /// <summary>
    /// The attitude (roll, pitch, yaw) the newest frame was drawn with, so the HUD can draw its ladder
    /// from the same values and the two never slide against each other.
    /// </summary>
    public (double Roll, double Pitch, double Yaw)? FrameAttitude { get; private set; }

    // attitude of the frame being read back (one behind, like the pixels)
    private (double, double, double)? _pendingAttitude;

    internal void Publish(byte[]? bgra, int w, int h, (double, double, double) attitudeOfThisRender)
    {
        // the pixels are one frame behind the render (asynchronous readback): so is the attitude
        FrameAttitude = _pendingAttitude ?? attitudeOfThisRender;
        _pendingAttitude = attitudeOfThisRender;
        if (bgra == null)
            return; // the first frame is still on its way
        Frames.Write(bgra, w, h, w * 4);
        FrameArrived?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Puts the chase camera back behind the vehicle at the default height and distance.</summary>
    public void ResetOrbit()
    {
        OrbitHeading = double.NaN;
        OrbitElevation = 18;
        Distance = 12;
    }

    public void Dispose() => _scene.Release(this);
}

/// <summary>
/// The 3D view of the vehicle over terrain (one per app): AWS Terrarium heights with the map's
/// satellite imagery draped on them, the vehicle model, sky and haze — ArduDeck's synthetic vision
/// in C#. Rendered with OpenGL 3.3 off screen on its own thread, so it serves the HUD (first person),
/// the map's 3D panel (chase) and the stream alike, whichever window they are in.
/// </summary>
public sealed class SyntheticScene
{
    private static readonly ILog Log = LogManager.GetLogger(typeof(SyntheticScene));
    private static readonly ConditionalWeakTable<AppServices, SyntheticScene> Instances = new();
    private const int Fps = 60;
    private const int MaxWidth = 1920;
    private const int MaxHeight = 1080;
    public const double DefaultDrawDistance = 8000;
    public const double MinDrawDistance = 2000;
    public const double MaxDrawDistance = 32000;
    private const double NearRebuild = 400; // m from the near patch's centre
    private const double ImageryRetry = 5; // seconds between retries, including while stationary

    private readonly object _gate = new();
    private readonly List<SyntheticView> _views = new();
    private readonly PoseTracker _tracker = new();
    private readonly AltitudeDatum _datum = new();
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private Thread? _thread;
    private volatile bool _stop;

    public SyntheticScene(TerrainSource terrain) => Terrain = terrain;

    public static SyntheticScene For(AppServices services) =>
        Instances.GetValue(services, _ => new SyntheticScene(new TerrainSource(
            Path.Combine(MissionPlanner.Utilities.Settings.GetDataDirectory(), "terrain-rgb"))));

    public TerrainSource Terrain { get; }

    private double _drawDistance = DefaultDrawDistance;

    /// <summary>Terrain drawing distance in metres, shared by the HUD, chase view and stream.</summary>
    public double DrawDistance
    {
        get => Volatile.Read(ref _drawDistance);
        set => Volatile.Write(ref _drawDistance, double.IsFinite(value)
            ? Math.Clamp(value, MinDrawDistance, MaxDrawDistance) : DefaultDrawDistance);
    }

    /// <summary>Which map tiles to drape (the map's provider); null draws plain ground.</summary>
    public Func<GMapProvider?> Imagery { get; set; } = () => null;

    /// <summary>Why the 3D view cannot run (no OpenGL 3.3), or null.</summary>
    public string? Error { get; private set; }

    /// <summary>"OpenGL 4.6 / …" once running.</summary>
    public string? Renderer { get; private set; }

    /// <summary>Raised (on the render thread) when <see cref="Error"/> changes.</summary>
    public event EventHandler? StateChanged;

    /// <summary>Whether any view is open (the caller then feeds <see cref="UpdatePose"/>).</summary>
    public bool IsActive
    {
        get
        {
            lock (_gate)
                return _views.Count > 0;
        }
    }

    public SyntheticView Acquire(SyntheticViewKind kind)
    {
        var view = new SyntheticView(this, kind);
        lock (_gate)
        {
            _views.Add(view);
            if (_thread == null)
            {
                _stop = false;
                _thread = new Thread(Run) { IsBackground = true, Name = "3d-view" };
                _thread.Start();
            }
        }

        return view;
    }

    internal void Release(SyntheticView view)
    {
        lock (_gate)
            _views.Remove(view);
    }

    /// <summary>A full telemetry sample (any thread).</summary>
    public void UpdatePose(VehiclePose pose) => _tracker.Add(pose, _clock.Elapsed.TotalSeconds);

    /// <summary>An ATTITUDE message as it arrives (reader thread), degrees.</summary>
    public void UpdateAttitude(double roll, double pitch, double yaw)
    {
        Interlocked.Increment(ref _attitudeSamples);
        _tracker.AddAttitude(roll, pitch, yaw, _clock.Elapsed.TotalSeconds);
    }

    private int _attitudeSamples;
    private int _frames;
    private double _statsSince;

    /// <summary>Frames rendered per second (all views together count once) over the last second or so.</summary>
    public double RenderFps { get; private set; }

    /// <summary>Attitude samples per second received from the vehicle.</summary>
    public double AttitudeRate { get; private set; }

    private void CountFrame(double now)
    {
        _frames++;
        if (now - _statsSince < 1)
            return;
        var dt = now - _statsSince;
        RenderFps = _frames / dt;
        AttitudeRate = Interlocked.Exchange(ref _attitudeSamples, 0) / dt;
        _frames = 0;
        _statsSince = now;
    }

    /// <summary>A GLOBAL_POSITION_INT message as it arrives (reader thread).</summary>
    public void UpdatePosition(VehiclePose pose) => _tracker.AddPosition(pose, _clock.Elapsed.TotalSeconds);

    /// <summary>Armed, vehicle shape and home ground (UI thread, now and then).</summary>
    public void UpdateState(bool armed, VehicleShape shape, double groundGuessMsl) => _tracker.SetState(armed, shape, groundGuessMsl);

    /// <summary>Whether the tracker has had a sample.</summary>
    public bool HasPose => _tracker.Latest != null;

    // ---- render thread -----------------------------------------------------------------------

    private SceneRenderer.GpuPatch? _far, _near;
    private Task<TerrainPatch>? _farBuild, _nearBuild;
    private double _farRetryAt, _nearRetryAt;
    private CancellationTokenSource _builds = new();

    private void Run()
    {
        GlContext? gl = null;
        SceneRenderer? renderer = null;
        // Windows sleeps in 15.6 ms steps unless asked for 1 ms: 60 fps needs the finer timer
        var fineTimer = OperatingSystem.IsWindows() && TimeBeginPeriod(1) == 0;
        try
        {
            gl = GlContext.Create();
            renderer = new SceneRenderer(gl.Gl);
            Renderer = gl.Description;
            Error = null;
            Log.Info("3D view: " + Renderer);
            StateChanged?.Invoke(this, EventArgs.Empty);

            var period = TimeSpan.FromSeconds(1.0 / Fps);
            while (!_stop)
            {
                var started = _clock.Elapsed;
                SyntheticView[] views;
                lock (_gate)
                {
                    views = _views.ToArray();
                    if (views.Length == 0)
                    {
                        _thread = null;
                        break;
                    }
                }

                RenderFrame(gl, renderer, views);
                var wait = period - (_clock.Elapsed - started);
                if (wait > TimeSpan.Zero)
                    Thread.Sleep(wait);
            }
        }
        catch (Exception ex)
        {
            Error = ex is InvalidOperationException ? ex.Message : "3D view failed: " + ex.Message;
            Log.Error("3D view", ex);
            StateChanged?.Invoke(this, EventArgs.Empty);
            lock (_gate)
                _thread = null;
        }
        finally
        {
            try
            {
                lock (_gate)
                {
                    foreach (var v in _views)
                    {
                        v.Target?.Dispose();
                        v.Target = null;
                    }
                }

                renderer?.Free(_far);
                renderer?.Free(_near);
                _far = _near = null;
                _builds.Cancel();
                _builds = new CancellationTokenSource();
                _farBuild = _nearBuild = null;
                renderer?.Dispose();
            }
            catch (Exception)
            {
                // the context may already be gone
            }

            gl?.Dispose();
            if (fineTimer)
                TimeEndPeriod(1);
        }
    }

    [System.Runtime.InteropServices.DllImport("winmm.dll", EntryPoint = "timeBeginPeriod")]
    private static extern uint TimeBeginPeriod(uint ms);

    [System.Runtime.InteropServices.DllImport("winmm.dll", EntryPoint = "timeEndPeriod")]
    private static extern uint TimeEndPeriod(uint ms);

    private void RenderFrame(GlContext gl, SceneRenderer renderer, SyntheticView[] views)
    {
        var now = _clock.Elapsed.TotalSeconds;
        var pose = _tracker.At(now);
        if (pose is not { } p)
            return;
        CountFrame(now);

        UpdateTerrain(renderer, p);

        // the ground under the vehicle and the altitude to draw at
        var ground = GroundAt(p.Lat, p.Lng);
        _datum.Update(p, ground, 0.3);
        var alt = p.HasPosition ? _datum.EyeAltitude(p.AltMsl, ground, 0.3) : (ground ?? p.GroundGuessMsl) + 30;
        var vehicle = new Vector3(0, (float)alt, 0);
        var drawDistance = (float)DrawDistance;
        var clipDistance = drawDistance * 3;
        var fogRange = drawDistance * 1.125f;

        var patches = new List<(SceneRenderer.GpuPatch, Vector3)>();
        foreach (var g in new[] { _far, _near })
        {
            if (g == null)
                continue;
            var (e, n) = new LocalFrame(p.Lat, p.Lng).ToLocal(g.Patch.Frame.Lat, g.Patch.Frame.Lng);
            patches.Add((g, new Vector3((float)e, 0, (float)-n)));
        }

        foreach (var view in views)
        {
            var w = Math.Min(view.Width, MaxWidth);
            var h = Math.Min(view.Height, MaxHeight);
            if (w < 16 || h < 16)
                continue;
            if (view.Target == null || view.Target.Width != w || view.Target.Height != h)
            {
                view.Target?.Dispose();
                view.Target = new OffscreenTarget(gl.Gl, w, h);
            }

            SceneRenderer.FrameSetup setup;
            if (view.Kind == SyntheticViewKind.FirstPerson)
            {
                // never inside the hill: at least 2 m above the ground
                var eye = ground is { } gr ? vehicle with { Y = Math.Max(vehicle.Y, (float)gr + 2) } : vehicle;
                var vfov = view.HorizontalFov is { } hf ? SceneCamera.VerticalFromHorizontal(hf, (double)w / h) : SceneCamera.HudVerticalFov;
                setup = new SceneRenderer.FrameSetup(
                    SceneCamera.FirstPerson(eye, p.Roll, p.Pitch, p.Yaw, view.CameraTilt),
                    SceneCamera.Projection(vfov, (float)w / h, 1f, clipDistance, view.CentreDownFraction),
                    eye, fogRange, null, Matrix4x4.Identity);
            }
            else
            {
                // following: swing round behind the vehicle over about a second
                var heading = view.OrbitHeading;
                if (double.IsNaN(heading))
                {
                    heading = double.IsNaN(view.ShownHeading)
                        ? p.Yaw
                        : PoseTracker.Norm360(view.ShownHeading + PoseTracker.WrapDeg(p.Yaw - view.ShownHeading) * 0.06);
                }

                view.ShownHeading = heading;
                var scale = p.Shape == VehicleShape.Plane ? 2.2f : 1.6f;
                var model = Matrix4x4.CreateScale(scale) * SceneCamera.BodyToWorld(p.Roll, p.Pitch, p.Yaw) *
                            Matrix4x4.CreateTranslation(vehicle);
                var cam = SceneCamera.ChasePosition(vehicle, heading, view.OrbitElevation, view.Distance);
                // keep the camera above the ground
                if (GroundAt(cam, p) is { } cg && cam.Y < cg + 1.5f)
                    cam.Y = cg + 1.5f;
                setup = new SceneRenderer.FrameSetup(
                    Matrix4x4.CreateLookAt(cam, vehicle, Vector3.UnitY),
                    SceneCamera.Projection(60 * MathF.PI / 180, (float)w / h, 0.3f, clipDistance),
                    cam, fogRange, p.Shape, model);
            }

            view.Target.Bind();
            renderer.Draw(setup, patches, w, h);
            if (view.Target.Read())
                view.Publish(view.Target.Pixels, w, h, (p.Roll, p.Pitch, p.Yaw));
            else
                view.Publish(null, w, h, (p.Roll, p.Pitch, p.Yaw));
        }
    }

    private float? GroundAt(Vector3 local, VehiclePose p)
    {
        var (lat, lng) = new LocalFrame(p.Lat, p.Lng).ToGeo(local.X, -local.Z);
        return GroundAt(lat, lng);
    }

    /// <summary>Ground height from the finest patch that covers the point, else from loaded tiles.</summary>
    private float? GroundAt(double lat, double lng)
    {
        foreach (var g in new[] { _near, _far })
        {
            if (g == null || !g.Patch.HasHeights)
                continue;
            var (e, n) = g.Patch.Frame.ToLocal(lat, lng);
            if (g.Patch.HeightAt(e, n) is { } h)
                return h;
        }

        return Terrain.HeightAt(lat, lng, PatchSpec.Near.DemZoom);
    }

    private void UpdateTerrain(SceneRenderer renderer, VehiclePose p)
    {
        var now = _clock.Elapsed.TotalSeconds;
        if (_farBuild is { IsCompleted: true })
            _farRetryAt = now + ImageryRetry;
        if (_nearBuild is { IsCompleted: true })
            _nearRetryAt = now + ImageryRetry;
        _far = Swap(renderer, _far, ref _farBuild);
        _near = Swap(renderer, _near, ref _nearBuild);

        var fallback = p.HasPosition ? p.GroundGuessMsl : p.AltMsl;
        var drawDistance = DrawDistance;
        if (_farBuild == null && (NeedsRebuild(_far, p, drawDistance * 0.375, now >= _farRetryAt) || _far?.Patch.Spec.Half != drawDistance))
            _farBuild = Build(p, PatchSpec.FarFor(drawDistance), fallback);
        if (_nearBuild == null && NeedsRebuild(_near, p, NearRebuild, now >= _nearRetryAt))
            _nearBuild = Build(p, PatchSpec.Near, fallback);
    }

    private static bool NeedsRebuild(SceneRenderer.GpuPatch? g, VehiclePose p, double distance, bool retryImagery)
    {
        if (g == null)
            return true;
        var (e, n) = g.Patch.Frame.ToLocal(p.Lat, p.Lng);
        return Math.Sqrt(e * e + n * n) > distance || (retryImagery && !g.Patch.ImageryComplete);
    }

    private Task<TerrainPatch> Build(VehiclePose p, PatchSpec spec, double fallback)
    {
        var provider = Imagery();
        var cancel = _builds.Token;
        var imagery = spec == PatchSpec.Near ? _near?.Patch.Drape ?? _far?.Patch.Drape : _far?.Patch.Drape;
        return Task.Run(() => TerrainPatch.BuildAsync(Terrain, provider, p.Lat, p.Lng, spec, fallback, cancel, imagery), cancel);
    }

    private static SceneRenderer.GpuPatch? Swap(SceneRenderer renderer, SceneRenderer.GpuPatch? current, ref Task<TerrainPatch>? build)
    {
        if (build is not { IsCompleted: true } done)
            return current;
        build = null;
        if (!done.IsCompletedSuccessfully)
        {
            Log.Warn("3D terrain: " + done.Exception?.GetBaseException().Message);
            return current;
        }

        renderer.Free(current);
        return renderer.Upload(done.Result);
    }
}
