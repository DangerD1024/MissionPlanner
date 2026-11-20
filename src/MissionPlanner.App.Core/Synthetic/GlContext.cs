using Silk.NET.Maths;
using Silk.NET.OpenGL;
using Silk.NET.Windowing;

namespace MissionPlanner.App.Core.Synthetic;

/// <summary>
/// An OpenGL 3.3 core context of a hidden GLFW window, for rendering off screen into framebuffers.
/// Create, use and dispose it on one thread (the 3D view's render thread).
/// </summary>
public sealed unsafe class GlContext : IDisposable
{
    private readonly IWindow _window;

    private GlContext(IWindow window, GL gl)
    {
        _window = window;
        Gl = gl;
    }

    public GL Gl { get; }

    /// <summary>"OpenGL 4.6 … / Intel(R) Iris(R) Xe Graphics".</summary>
    public string Description { get; private set; } = "";

    /// <summary>Creates the context on the calling thread; throws <see cref="InvalidOperationException"/> with a reason if it cannot.</summary>
    public static GlContext Create()
    {
        PreloadGlfw();
        IWindow? window = null;
        try
        {
            var options = WindowOptions.Default with
            {
                IsVisible = false,
                Size = new Vector2D<int>(16, 16),
                Title = "Mission Planner 3D",
                VSync = false,
                ShouldSwapAutomatically = false,
                API = new GraphicsAPI(ContextAPI.OpenGL, ContextProfile.Core, ContextFlags.ForwardCompatible, new APIVersion(3, 3)),
            };
            window = Window.Create(options);
            window.Initialize();
            window.MakeCurrent();
            var gl = GL.GetApi(window);
            var ctx = new GlContext(window, gl)
            {
                Description = $"OpenGL {gl.GetStringS(StringName.Version)} / {gl.GetStringS(StringName.Renderer)}",
            };
            return ctx;
        }
        catch (Exception ex) when (ex is not InvalidOperationException)
        {
            window?.Dispose();
            throw new InvalidOperationException("The 3D view needs OpenGL 3.3: " + ex.Message + GlfwDetail(), ex);
        }
    }

    private static bool _preloaded;

    /// <summary>
    /// On Linux and macOS Silk.NET asks the system loader for "libglfw.so.3" by name, which does not
    /// look in the app's own folders: load the bundled GLFW by path first (the loader then reuses
    /// it). A published build has it next to the executable, a build output under runtimes/.
    /// </summary>
    private static void PreloadGlfw()
    {
        if (_preloaded || OperatingSystem.IsWindows())
            return;
        _preloaded = true;
        var name = OperatingSystem.IsMacOS() ? "libglfw.3.dylib" : "libglfw.so.3";
        var arch = System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture.ToString().ToLowerInvariant();
        var os = OperatingSystem.IsMacOS() ? "osx" : "linux";
        foreach (var path in new[]
                 {
                     Path.Combine(AppContext.BaseDirectory, name),
                     Path.Combine(AppContext.BaseDirectory, "runtimes", $"{os}-{arch}", "native", name),
                 })
        {
            if (File.Exists(path) && System.Runtime.InteropServices.NativeLibrary.TryLoad(path, out _))
                return;
        }
    }

    /// <summary>Why GLFW itself would not start ("not applicable" alone says nothing), for the error message.</summary>
    private static string GlfwDetail()
    {
        try
        {
            var glfw = Silk.NET.GLFW.Glfw.GetApi();
            if (!glfw.Init())
            {
                glfw.GetError(out byte* description);
                return " (GLFW: " + (System.Runtime.InteropServices.Marshal.PtrToStringUTF8((IntPtr)description) ?? "init failed") + ")";
            }

            return "";
        }
        catch (Exception ex)
        {
            return " (GLFW: " + ex.GetBaseException().Message + ")";
        }
    }

    public void MakeCurrent() => _window.MakeCurrent();

    public void Dispose()
    {
        try
        {
            _window.Reset();
            _window.Dispose();
        }
        catch (Exception)
        {
            // the window system is going away anyway
        }
    }
}
