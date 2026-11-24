using System.Numerics;
using Silk.NET.OpenGL;

namespace MissionPlanner.App.Core.Synthetic;

/// <summary>
/// The OpenGL side of the 3D view (render thread only): shaders, terrain patches and vehicle models
/// on the GPU, and drawing one frame (sky, terrain with satellite drape and fog, the vehicle).
/// </summary>
internal sealed unsafe class SceneRenderer : IDisposable
{
    private const string TerrainVs = """
        #version 330 core
        layout(location = 0) in vec3 a_pos;
        layout(location = 1) in vec3 a_normal;
        layout(location = 2) in vec2 a_uv;
        uniform mat4 u_vp;
        uniform vec3 u_offset;
        out vec3 v_world;
        out vec3 v_normal;
        out vec2 v_uv;
        void main() {
            vec3 p = a_pos + u_offset;
            v_world = p;
            v_normal = a_normal;
            v_uv = a_uv;
            gl_Position = u_vp * vec4(p, 1.0);
        }
        """;

    private const string TerrainFs = """
        #version 330 core
        in vec3 v_world;
        in vec3 v_normal;
        in vec2 v_uv;
        uniform sampler2D u_tex;
        uniform int u_hasTex;
        uniform int u_skipMissing;
        uniform vec3 u_eye;
        uniform vec3 u_fog;
        uniform float u_fogRange;
        uniform vec3 u_sun;
        out vec4 o_color;
        void main() {
            vec4 imagery = u_hasTex == 1 ? texture(u_tex, v_uv) : vec4(0.0);
            // A failed high-detail tile must not cover imagery already visible in the far patch.
            if (u_skipMissing == 1 && imagery.a < 0.99)
                discard;
            vec3 base = mix(vec3(0.43, 0.48, 0.33), imagery.rgb, imagery.a);
            float light = 0.72 + 0.4 * max(dot(normalize(v_normal), u_sun), 0.0);
            float d = length(v_world - u_eye) / u_fogRange;
            float fog = clamp(1.0 - exp(-d * d * 2.2), 0.0, 1.0);
            o_color = vec4(mix(base * light, u_fog, fog), 1.0);
        }
        """;

    private const string SkyVs = """
        #version 330 core
        out vec2 v_ndc;
        void main() {
            vec2 p[3] = vec2[](vec2(-1.0, -1.0), vec2(3.0, -1.0), vec2(-1.0, 3.0));
            v_ndc = p[gl_VertexID];
            gl_Position = vec4(p[gl_VertexID], 0.0, 1.0);
        }
        """;

    private const string SkyFs = """
        #version 330 core
        in vec2 v_ndc;
        uniform mat4 u_invVp;
        uniform vec3 u_fog;
        out vec4 o_color;
        void main() {
            vec4 a = u_invVp * vec4(v_ndc, -1.0, 1.0);
            vec4 b = u_invVp * vec4(v_ndc, 1.0, 1.0);
            vec3 dir = normalize(b.xyz / b.w - a.xyz / a.w);
            vec3 zenith = vec3(0.16, 0.36, 0.66);
            vec3 ground = vec3(0.47, 0.5, 0.42);
            vec3 c = dir.y >= 0.0 ? mix(u_fog, zenith, smoothstep(0.0, 0.55, dir.y)) : mix(u_fog, ground, smoothstep(0.0, 0.08, -dir.y));
            o_color = vec4(c, 1.0);
        }
        """;

    private const string ModelVs = """
        #version 330 core
        layout(location = 0) in vec3 a_pos;
        layout(location = 1) in vec3 a_normal;
        layout(location = 2) in vec3 a_color;
        uniform mat4 u_model;
        uniform mat4 u_vp;
        out vec3 v_normal;
        out vec3 v_color;
        void main() {
            v_normal = mat3(u_model) * a_normal;
            v_color = a_color;
            gl_Position = u_vp * (u_model * vec4(a_pos, 1.0));
        }
        """;

    private const string ModelFs = """
        #version 330 core
        in vec3 v_normal;
        in vec3 v_color;
        uniform vec3 u_sun;
        out vec4 o_color;
        void main() {
            float l = 0.45 + 0.6 * abs(dot(normalize(v_normal), u_sun));
            o_color = vec4(v_color * l, 1.0);
        }
        """;

    private static readonly Vector3 Fog = new(0.66f, 0.76f, 0.86f);
    private static readonly Vector3 Sun = Vector3.Normalize(new Vector3(0.35f, 0.85f, 0.4f));

    private readonly GL _gl;
    private readonly uint _terrainProgram;
    private readonly uint _skyProgram;
    private readonly uint _modelProgram;
    private readonly uint _emptyVao;
    private readonly Dictionary<VehicleShape, GpuMesh> _models = new();

    public SceneRenderer(GL gl)
    {
        _gl = gl;
        _terrainProgram = Program(TerrainVs, TerrainFs);
        _skyProgram = Program(SkyVs, SkyFs);
        _modelProgram = Program(ModelVs, ModelFs);
        _emptyVao = gl.GenVertexArray();
        if (gl.IsExtensionPresent("GL_EXT_texture_filter_anisotropic") || gl.IsExtensionPresent("GL_ARB_texture_filter_anisotropic"))
        {
            gl.GetFloat((GetPName)0x84FF, out var max); // MAX_TEXTURE_MAX_ANISOTROPY
            _anisotropy = Math.Min(max, 8f);
        }
    }

    private readonly float _anisotropy;

    /// <summary>A terrain patch on the GPU.</summary>
    public sealed class GpuPatch
    {
        public required TerrainPatch Patch { get; init; }
        public uint Vao, Vbo, Ebo, Texture;
        public uint IndexCount;
    }

    private sealed class GpuMesh
    {
        public uint Vao, Vbo;
        public uint VertexCount;
    }

    public GpuPatch Upload(TerrainPatch patch)
    {
        var g = new GpuPatch { Patch = patch, IndexCount = (uint)patch.Indices.Length };
        g.Vao = _gl.GenVertexArray();
        _gl.BindVertexArray(g.Vao);
        g.Vbo = _gl.GenBuffer();
        _gl.BindBuffer(BufferTargetARB.ArrayBuffer, g.Vbo);
        fixed (float* v = patch.Vertices)
            _gl.BufferData(BufferTargetARB.ArrayBuffer, (nuint)(patch.Vertices.Length * sizeof(float)), v, BufferUsageARB.StaticDraw);
        g.Ebo = _gl.GenBuffer();
        _gl.BindBuffer(BufferTargetARB.ElementArrayBuffer, g.Ebo);
        fixed (uint* i = patch.Indices)
            _gl.BufferData(BufferTargetARB.ElementArrayBuffer, (nuint)(patch.Indices.Length * sizeof(uint)), i, BufferUsageARB.StaticDraw);
        var stride = (uint)(TerrainPatch.FloatsPerVertex * sizeof(float));
        _gl.EnableVertexAttribArray(0);
        _gl.VertexAttribPointer(0, 3, VertexAttribPointerType.Float, false, stride, (void*)0);
        _gl.EnableVertexAttribArray(1);
        _gl.VertexAttribPointer(1, 3, VertexAttribPointerType.Float, false, stride, (void*)(3 * sizeof(float)));
        _gl.EnableVertexAttribArray(2);
        _gl.VertexAttribPointer(2, 2, VertexAttribPointerType.Float, false, stride, (void*)(6 * sizeof(float)));
        _gl.BindVertexArray(0);

        if (patch.Drape is { } d)
        {
            g.Texture = _gl.GenTexture();
            _gl.BindTexture(TextureTarget.Texture2D, g.Texture);
            fixed (byte* p = d.Rgba)
                _gl.TexImage2D(TextureTarget.Texture2D, 0, InternalFormat.Rgba8, (uint)d.Width, (uint)d.Height, 0, PixelFormat.Rgba, PixelType.UnsignedByte, p);
            _gl.GenerateMipmap(TextureTarget.Texture2D);
            _gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter, (int)TextureMinFilter.LinearMipmapLinear);
            _gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter, (int)TextureMagFilter.Linear);
            _gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapS, (int)TextureWrapMode.ClampToEdge);
            _gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapT, (int)TextureWrapMode.ClampToEdge);
            // ground seen at a shallow angle stays sharp (EXT_texture_filter_anisotropic, everywhere in practice)
            if (_anisotropy > 1)
                _gl.TexParameter(TextureTarget.Texture2D, (TextureParameterName)0x84FE, _anisotropy);
        }

        return g;
    }

    public void Free(GpuPatch? g)
    {
        if (g == null)
            return;
        _gl.DeleteVertexArray(g.Vao);
        _gl.DeleteBuffer(g.Vbo);
        _gl.DeleteBuffer(g.Ebo);
        if (g.Texture != 0)
            _gl.DeleteTexture(g.Texture);
    }

    /// <summary>What one frame shows.</summary>
    public readonly record struct FrameSetup(
        Matrix4x4 View,
        Matrix4x4 Projection,
        Vector3 Eye,
        float FogRange,
        VehicleShape? Model,
        Matrix4x4 ModelMatrix);

    /// <summary>Draws into the bound framebuffer. Patch offsets place each patch relative to the vehicle.</summary>
    public void Draw(in FrameSetup f, IEnumerable<(GpuPatch Patch, Vector3 Offset)> patches, int width, int height)
    {
        _gl.Viewport(0, 0, (uint)width, (uint)height);
        _gl.Disable(EnableCap.CullFace);
        _gl.ClearColor(Fog.X, Fog.Y, Fog.Z, 1);
        _gl.StencilMask(0xff);
        _gl.ClearStencil(0);
        _gl.Clear(ClearBufferMask.ColorBufferBit | ClearBufferMask.DepthBufferBit | ClearBufferMask.StencilBufferBit);
        var vp = f.View * f.Projection;

        // sky behind everything
        _gl.Disable(EnableCap.DepthTest);
        _gl.DepthMask(false);
        _gl.UseProgram(_skyProgram);
        Matrix4x4.Invert(vp, out var inv);
        SetMatrix(_skyProgram, "u_invVp", inv);
        SetVec3(_skyProgram, "u_fog", Fog);
        _gl.BindVertexArray(_emptyVao);
        _gl.DrawArrays(PrimitiveType.Triangles, 0, 3);

        // Draw detailed terrain first and mark its actual screen coverage. Near and far DEM
        // heights differ: cutting a geographic square out of the far mesh exposes the sky at
        // their boundary. Keep coarse terrain in every pixel the detailed mesh did not cover.
        _gl.Enable(EnableCap.DepthTest);
        _gl.Enable(EnableCap.StencilTest);
        _gl.DepthMask(true);
        _gl.DepthFunc(DepthFunction.Lequal);
        _gl.UseProgram(_terrainProgram);
        SetMatrix(_terrainProgram, "u_vp", vp);
        SetVec3(_terrainProgram, "u_eye", f.Eye);
        SetVec3(_terrainProgram, "u_fog", Fog);
        SetVec3(_terrainProgram, "u_sun", Sun);
        _gl.Uniform1(_gl.GetUniformLocation(_terrainProgram, "u_fogRange"), f.FogRange);
        _gl.Uniform1(_gl.GetUniformLocation(_terrainProgram, "u_tex"), 0);
        var list = patches.OrderBy(p => p.Patch.Patch.Spec.Half).ToArray();
        var first = true;
        foreach (var (p, offset) in list)
        {
            _gl.StencilMask(first ? 0xffu : 0u);
            _gl.StencilFunc(first ? StencilFunction.Always : StencilFunction.Equal, first ? 1 : 0, 0xff);
            _gl.StencilOp(StencilOp.Keep, StencilOp.Keep, first ? StencilOp.Replace : StencilOp.Keep);
            _gl.Uniform1(_gl.GetUniformLocation(_terrainProgram, "u_skipMissing"),
                first && list.Length > 1 && !p.Patch.ImageryComplete ? 1 : 0);
            SetVec3(_terrainProgram, "u_offset", offset);
            _gl.Uniform1(_gl.GetUniformLocation(_terrainProgram, "u_hasTex"), p.Texture != 0 ? 1 : 0);
            _gl.ActiveTexture(TextureUnit.Texture0);
            _gl.BindTexture(TextureTarget.Texture2D, p.Texture);
            _gl.BindVertexArray(p.Vao);
            _gl.DrawElements(PrimitiveType.Triangles, p.IndexCount, DrawElementsType.UnsignedInt, (void*)0);
            first = false;
        }

        _gl.Disable(EnableCap.StencilTest);
        _gl.StencilMask(0xff);

        // the vehicle (chase view)
        if (f.Model is { } shape)
        {
            var mesh = Model(shape);
            _gl.UseProgram(_modelProgram);
            SetMatrix(_modelProgram, "u_vp", vp);
            SetMatrix(_modelProgram, "u_model", f.ModelMatrix);
            SetVec3(_modelProgram, "u_sun", Sun);
            _gl.BindVertexArray(mesh.Vao);
            _gl.DrawArrays(PrimitiveType.Triangles, 0, mesh.VertexCount);
        }

        _gl.BindVertexArray(0);
    }

    private GpuMesh Model(VehicleShape shape)
    {
        if (_models.TryGetValue(shape, out var m))
            return m;
        var data = VehicleModels.For(shape);
        m = new GpuMesh { VertexCount = (uint)(data.Length / VehicleModels.FloatsPerVertex) };
        m.Vao = _gl.GenVertexArray();
        _gl.BindVertexArray(m.Vao);
        m.Vbo = _gl.GenBuffer();
        _gl.BindBuffer(BufferTargetARB.ArrayBuffer, m.Vbo);
        fixed (float* v = data)
            _gl.BufferData(BufferTargetARB.ArrayBuffer, (nuint)(data.Length * sizeof(float)), v, BufferUsageARB.StaticDraw);
        var stride = (uint)(VehicleModels.FloatsPerVertex * sizeof(float));
        for (uint i = 0; i < 3; i++)
        {
            _gl.EnableVertexAttribArray(i);
            _gl.VertexAttribPointer(i, 3, VertexAttribPointerType.Float, false, stride, (void*)(i * 3 * sizeof(float)));
        }

        _gl.BindVertexArray(0);
        _models[shape] = m;
        return m;
    }

    private void SetMatrix(uint program, string name, Matrix4x4 m) =>
        _gl.UniformMatrix4(_gl.GetUniformLocation(program, name), 1, false, (float*)&m);

    private void SetVec3(uint program, string name, Vector3 v) => _gl.Uniform3(_gl.GetUniformLocation(program, name), v.X, v.Y, v.Z);

    private uint Program(string vs, string fs)
    {
        var v = Shader(ShaderType.VertexShader, vs);
        var f = Shader(ShaderType.FragmentShader, fs);
        var p = _gl.CreateProgram();
        _gl.AttachShader(p, v);
        _gl.AttachShader(p, f);
        _gl.LinkProgram(p);
        _gl.GetProgram(p, ProgramPropertyARB.LinkStatus, out var ok);
        if (ok == 0)
            throw new InvalidOperationException("3D shader link: " + _gl.GetProgramInfoLog(p));
        _gl.DeleteShader(v);
        _gl.DeleteShader(f);
        return p;
    }

    private uint Shader(ShaderType type, string source)
    {
        var s = _gl.CreateShader(type);
        _gl.ShaderSource(s, source);
        _gl.CompileShader(s);
        _gl.GetShader(s, ShaderParameterName.CompileStatus, out var ok);
        if (ok == 0)
            throw new InvalidOperationException($"3D {type}: " + _gl.GetShaderInfoLog(s));
        return s;
    }

    public void Dispose()
    {
        foreach (var m in _models.Values)
        {
            _gl.DeleteVertexArray(m.Vao);
            _gl.DeleteBuffer(m.Vbo);
        }

        _gl.DeleteVertexArray(_emptyVao);
        _gl.DeleteProgram(_terrainProgram);
        _gl.DeleteProgram(_skyProgram);
        _gl.DeleteProgram(_modelProgram);
    }
}

/// <summary>
/// An off-screen render target (colour + depth/stencil) of one view, read back into BGRA rows top-down
/// through two pixel buffers: a frame's pixels are fetched while the next one renders, so the render
/// thread never waits for the GPU (one frame of latency, ~16 ms at 60 fps).
/// </summary>
internal sealed unsafe class OffscreenTarget : IDisposable
{
    private readonly GL _gl;
    private readonly uint _fbo, _color, _depthStencil;
    private readonly uint[] _pbo = new uint[2];
    private int _next;
    private int _filled;

    public OffscreenTarget(GL gl, int width, int height)
    {
        _gl = gl;
        Width = width;
        Height = height;
        _fbo = gl.GenFramebuffer();
        gl.BindFramebuffer(FramebufferTarget.Framebuffer, _fbo);
        _color = gl.GenRenderbuffer();
        gl.BindRenderbuffer(RenderbufferTarget.Renderbuffer, _color);
        gl.RenderbufferStorage(RenderbufferTarget.Renderbuffer, InternalFormat.Rgba8, (uint)width, (uint)height);
        gl.FramebufferRenderbuffer(FramebufferTarget.Framebuffer, FramebufferAttachment.ColorAttachment0, RenderbufferTarget.Renderbuffer, _color);
        _depthStencil = gl.GenRenderbuffer();
        gl.BindRenderbuffer(RenderbufferTarget.Renderbuffer, _depthStencil);
        gl.RenderbufferStorage(RenderbufferTarget.Renderbuffer, InternalFormat.Depth24Stencil8, (uint)width, (uint)height);
        gl.FramebufferRenderbuffer(FramebufferTarget.Framebuffer, FramebufferAttachment.DepthStencilAttachment, RenderbufferTarget.Renderbuffer, _depthStencil);
        var status = gl.CheckFramebufferStatus(FramebufferTarget.Framebuffer);
        if (status != GLEnum.FramebufferComplete)
            throw new InvalidOperationException("3D framebuffer: " + status);

        Pixels = new byte[width * height * 4];
        for (var i = 0; i < 2; i++)
        {
            _pbo[i] = gl.GenBuffer();
            gl.BindBuffer(BufferTargetARB.PixelPackBuffer, _pbo[i]);
            gl.BufferData(BufferTargetARB.PixelPackBuffer, (nuint)Pixels.Length, null, BufferUsageARB.StreamRead);
        }

        gl.BindBuffer(BufferTargetARB.PixelPackBuffer, 0);
    }

    public int Width { get; }
    public int Height { get; }
    public byte[] Pixels { get; }

    public void Bind() => _gl.BindFramebuffer(FramebufferTarget.Framebuffer, _fbo);

    /// <summary>
    /// Starts reading the frame just drawn and copies the previous one into <see cref="Pixels"/>;
    /// false until there is a previous one. The projection flips y, so GL's bottom-up rows come out
    /// top-down.
    /// </summary>
    public bool Read()
    {
        _gl.BindFramebuffer(FramebufferTarget.ReadFramebuffer, _fbo);
        _gl.PixelStore(PixelStoreParameter.PackAlignment, 4);
        _gl.BindBuffer(BufferTargetARB.PixelPackBuffer, _pbo[_next]);
        _gl.ReadPixels(0, 0, (uint)Width, (uint)Height, PixelFormat.Bgra, PixelType.UnsignedByte, null);

        var previous = _pbo[1 - _next];
        _next = 1 - _next;
        var ready = _filled > 0;
        _filled++;
        if (ready)
        {
            _gl.BindBuffer(BufferTargetARB.PixelPackBuffer, previous);
            var src = _gl.MapBufferRange(BufferTargetARB.PixelPackBuffer, 0, (nuint)Pixels.Length, MapBufferAccessMask.ReadBit);
            if (src != null)
            {
                fixed (byte* dst = Pixels)
                    System.Buffer.MemoryCopy(src, dst, Pixels.Length, Pixels.Length);
                _gl.UnmapBuffer(BufferTargetARB.PixelPackBuffer);
            }
            else
            {
                ready = false;
            }
        }

        _gl.BindBuffer(BufferTargetARB.PixelPackBuffer, 0);
        return ready;
    }

    public void Dispose()
    {
        _gl.DeleteBuffers(2, _pbo);
        _gl.DeleteFramebuffer(_fbo);
        _gl.DeleteRenderbuffer(_color);
        _gl.DeleteRenderbuffer(_depthStencil);
    }
}
