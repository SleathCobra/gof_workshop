using System.Diagnostics;
using System.Numerics;
using System.Runtime.InteropServices;
using Avalonia.OpenGL;
using Gof2Workshop.Scene;

namespace Gof2Workshop.App.Rendering;

public sealed class OpenGlSceneRenderer : ISceneViewportRenderer
{
    private const int ArrayBuffer = 0x8892;
    private const int ElementArrayBuffer = 0x8893;
    private const int StaticDraw = 0x88E4;
    private const int Float = 0x1406;
    private const int UnsignedShort = 0x1403;
    private const int Triangles = 0x0004;
    private const int Lines = 0x0001;
    private const int Framebuffer = 0x8D40;
    private const int VertexShader = 0x8B31;
    private const int FragmentShader = 0x8B30;
    private const int ColorBufferBit = 0x00004000;
    private const int DepthBufferBit = 0x00000100;
    private const int DepthTest = 0x0B71;
    private const int Blend = 0x0BE2;
    private const int CullFace = 0x0B44;
    private const int Lequal = 0x0203;
    private const int SrcAlpha = 0x0302;
    private const int OneMinusSrcAlpha = 0x0303;
    private const int Texture2D = 0x0DE1;
    private const int Texture0 = 0x84C0;
    private const int TextureMinFilter = 0x2801;
    private const int TextureMagFilter = 0x2800;
    private const int TextureWrapS = 0x2802;
    private const int TextureWrapT = 0x2803;
    private const int Linear = 0x2601;
    private const int LinearMipmapLinear = 0x2703;
    private const int Repeat = 0x2901;
    private const int Rgba = 0x1908;
    private const int UnsignedByte = 0x1401;
    private const int MaxTextureSize = 0x0D33;

    private readonly GlInterface gl;
    private readonly GlApiExtra extra;
    private readonly List<MeshResource> meshes = [];
    private readonly Dictionary<string, int> textures =
        new(StringComparer.OrdinalIgnoreCase);
    private int program;
    private int fallbackTexture;
    private int matrixLocation;
    private int baseColorLocation;
    private int selectedLocation;
    private int modeLocation;
    private int useTextureLocation;
    private int samplerLocation;
    private SceneDocument? uploadedScene;
    private long frames;
    private bool disposed;

    public OpenGlSceneRenderer(GlInterface gl, GlVersion version)
    {
        this.gl = gl;
        extra = new GlApiExtra(gl);
        program = CreateProgram(version);
        matrixLocation = RequireUniform("uMvp");
        baseColorLocation = RequireUniform("uBaseColor");
        selectedLocation = RequireUniform("uSelected");
        modeLocation = RequireUniform("uMode");
        useTextureLocation = RequireUniform("uUseTexture");
        samplerLocation = RequireUniform("uTexture");
        fallbackTexture = CreateFallbackTexture();

        int maximumTextureSize = extra.GetInteger(MaxTextureSize);
        string api = $"{version.Type} {version.Major}.{version.Minor}";
        Info = new SceneViewportRendererInfo(
            "Avalonia OpenGL",
            api,
            gl.Vendor ?? "Unknown vendor",
            gl.Renderer ?? "Unknown renderer",
            maximumTextureSize,
            HardwareAccelerated: true);
    }

    public SceneViewportRendererInfo Info { get; }

    public SceneViewportFrameMetrics Render(
        SceneViewportRequest request,
        int framebuffer,
        int pixelWidth,
        int pixelHeight)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        ArgumentNullException.ThrowIfNull(request);
        Stopwatch stopwatch = Stopwatch.StartNew();
        EnsureSceneUploaded(request.Scene);
        PruneUnusedTextures(request);

        gl.BindFramebuffer(Framebuffer, framebuffer);
        gl.Viewport(0, 0, pixelWidth, pixelHeight);
        gl.ClearColor(
            request.BackgroundColor.X,
            request.BackgroundColor.Y,
            request.BackgroundColor.Z,
            request.BackgroundColor.W);
        gl.ClearDepth(1);
        gl.Clear(ColorBufferBit | DepthBufferBit);
        gl.Enable(DepthTest);
        gl.DepthFunc(Lequal);
        gl.Enable(Blend);
        extra.BlendFunc(SrcAlpha, OneMinusSrcAlpha);
        if (request.BackFaceCulling && request.Mode != SceneViewportMode.Winding)
        {
            gl.Enable(CullFace);
        }
        else
        {
            gl.Disable(CullFace);
        }

        gl.UseProgram(program);
        gl.ActiveTexture(Texture0);
        gl.BindTexture(Texture2D, fallbackTexture);
        gl.Uniform1i(samplerLocation, 0);
        Matrix4x4 viewProjection = SceneViewportMatrices.CreateViewProjection(
            request,
            pixelWidth,
            pixelHeight);

        int drawCalls = 0;
        int triangles = 0;
        for (int index = 0; index < meshes.Count; index++)
        {
            if (request.IsolatedPrimitiveIndex is int isolated && isolated != index)
            {
                continue;
            }

            MeshResource mesh = meshes[index];
            ScenePrimitive primitive = request.Scene.Primitives[index];
            Matrix4x4 model = SceneViewportMatrices.CreatePrimitiveTransform(request, index);
            SetMatrix(model * viewProjection);
            bool texturedMode = request.Mode is
                SceneViewportMode.LitTextured or
                SceneViewportMode.UnlitTextured;
            SceneTextureBinding? binding = null;
            bool hasTexture = texturedMode &&
                request.TextureBindings.TryGetValue(index, out binding);
            if (hasTexture)
            {
                gl.BindTexture(Texture2D, EnsureTexture(binding!));
                SetColor(Vector4.One);
            }
            else
            {
                gl.BindTexture(Texture2D, fallbackTexture);
                SetColor(primitive.Material.BaseColor);
            }

            gl.Uniform1f(selectedLocation, request.SelectedPrimitiveIndex == index ? 1f : 0f);
            gl.Uniform1i(modeLocation, (int)request.Mode);
            gl.Uniform1i(useTextureLocation, hasTexture ? 1 : 0);
            BindMesh(mesh);
            gl.DrawElements(Triangles, mesh.IndexCount, UnsignedShort, IntPtr.Zero);
            drawCalls++;
            triangles += mesh.IndexCount / 3;

            if (request.Wireframe)
            {
                SetColor(request.SelectedPrimitiveIndex == index
                    ? new Vector4(1f, 0.72f, 0.12f, 1f)
                    : new Vector4(0.78f, 0.86f, 0.94f, 0.75f));
                gl.Uniform1i(modeLocation, (int)SceneViewportMode.SolidDiagnostic);
                gl.BindBuffer(ElementArrayBuffer, mesh.EdgeBuffer);
                gl.DrawElements(Lines, mesh.EdgeIndexCount, UnsignedShort, IntPtr.Zero);
                drawCalls++;
            }

            if (request.ShowNormals && mesh.NormalLineBuffer != 0)
            {
                SetColor(new Vector4(0.25f, 1f, 0.48f, 0.9f));
                gl.BindBuffer(ArrayBuffer, mesh.NormalLineBuffer);
                SetVertexLayout();
                gl.DrawArrays(Lines, 0, mesh.NormalLineVertexCount);
                drawCalls++;
            }

            if ((request.ShowPivots || request.ShowBounds) && mesh.DiagnosticBuffer != 0)
            {
                gl.BindBuffer(ArrayBuffer, mesh.DiagnosticBuffer);
                SetVertexLayout();
                if (request.ShowPivots)
                {
                    SetColor(new Vector4(1f, 0.82f, 0.18f, 1f));
                    gl.DrawArrays(Lines, 0, mesh.PivotVertexCount);
                    drawCalls++;
                }

                if (request.ShowBounds && mesh.BoundVertexCount > 0)
                {
                    SetColor(new Vector4(1f, 0.34f, 0.2f, 0.8f));
                    gl.DrawArrays(
                        Lines,
                        mesh.PivotVertexCount,
                        mesh.BoundVertexCount);
                    drawCalls++;
                }
            }
        }

        gl.BindBuffer(ArrayBuffer, 0);
        gl.BindBuffer(ElementArrayBuffer, 0);
        gl.UseProgram(0);
        gl.Flush();
        stopwatch.Stop();
        frames++;
        return new SceneViewportFrameMetrics(
            stopwatch.Elapsed.TotalMilliseconds,
            drawCalls,
            triangles,
            TextureCount: textures.Count,
            frames);
    }

    public void DisposeCurrentContext()
    {
        if (disposed)
        {
            return;
        }

        DeleteSceneResources();
        if (fallbackTexture != 0)
        {
            gl.DeleteTexture(fallbackTexture);
            fallbackTexture = 0;
        }

        if (program != 0)
        {
            gl.DeleteProgram(program);
            program = 0;
        }

        disposed = true;
    }

    private void EnsureSceneUploaded(SceneDocument scene)
    {
        if (ReferenceEquals(scene, uploadedScene))
        {
            return;
        }

        DeleteSceneResources();
        foreach (ScenePrimitive primitive in scene.Primitives)
        {
            meshes.Add(UploadMesh(primitive));
        }

        uploadedScene = scene;
    }

    private MeshResource UploadMesh(ScenePrimitive primitive)
    {
        float[] vertices = BuildVertices(primitive.Positions, primitive.Normals, primitive.TextureCoordinates, primitive.Colors);
        ushort[] edges = BuildEdgeIndices(primitive.Indices);
        float[] normalLines = BuildNormalLines(primitive);
        float[] diagnostics = BuildDiagnosticLines(primitive, out int pivotVertices);

        int vertexBuffer = UploadBuffer(ArrayBuffer, vertices);
        int indexBuffer = UploadBuffer(ElementArrayBuffer, primitive.Indices);
        int edgeBuffer = UploadBuffer(ElementArrayBuffer, edges);
        int normalBuffer = normalLines.Length == 0 ? 0 : UploadBuffer(ArrayBuffer, normalLines);
        int diagnosticBuffer = diagnostics.Length == 0 ? 0 : UploadBuffer(ArrayBuffer, diagnostics);
        return new MeshResource(
            vertexBuffer,
            indexBuffer,
            primitive.Indices.Length,
            edgeBuffer,
            edges.Length,
            normalBuffer,
            normalLines.Length / 12,
            diagnosticBuffer,
            pivotVertices,
            (diagnostics.Length / 12) - pivotVertices);
    }

    private int UploadBuffer<T>(int target, T[] data)
        where T : struct
    {
        int buffer = gl.GenBuffer();
        gl.BindBuffer(target, buffer);
        GCHandle handle = GCHandle.Alloc(data, GCHandleType.Pinned);
        try
        {
            int byteLength = checked(data.Length * Marshal.SizeOf<T>());
            gl.BufferData(target, (IntPtr)byteLength, handle.AddrOfPinnedObject(), StaticDraw);
        }
        finally
        {
            handle.Free();
        }

        return buffer;
    }

    private void BindMesh(MeshResource mesh)
    {
        gl.BindBuffer(ArrayBuffer, mesh.VertexBuffer);
        gl.BindBuffer(ElementArrayBuffer, mesh.IndexBuffer);
        SetVertexLayout();
    }

    private void SetVertexLayout()
    {
        const int stride = 12 * sizeof(float);
        gl.EnableVertexAttribArray(0);
        gl.EnableVertexAttribArray(1);
        gl.EnableVertexAttribArray(2);
        gl.EnableVertexAttribArray(3);
        gl.VertexAttribPointer(0, 3, Float, 0, stride, IntPtr.Zero);
        gl.VertexAttribPointer(1, 3, Float, 0, stride, (IntPtr)(3 * sizeof(float)));
        gl.VertexAttribPointer(2, 2, Float, 0, stride, (IntPtr)(6 * sizeof(float)));
        gl.VertexAttribPointer(3, 4, Float, 0, stride, (IntPtr)(8 * sizeof(float)));
    }

    private void SetMatrix(Matrix4x4 matrix)
    {
        // System.Numerics composes row-vector matrices. OpenGL consumes the
        // same contiguous values as a column-major matrix, which supplies the
        // transpose required by the column-vector shader without a CPU copy.
        extra.UniformMatrix4(matrixLocation, matrix);
    }

    private void SetColor(Vector4 color)
    {
        extra.Uniform4(
            baseColorLocation,
            color.X,
            color.Y,
            color.Z,
            color.W);
    }

    private int CreateProgram(GlVersion version)
    {
        (string vertexSource, string fragmentSource) = ShaderSources.Create(version);
        int vertex = gl.CreateShader(VertexShader);
        int fragment = gl.CreateShader(FragmentShader);
        try
        {
            string? vertexError = gl.CompileShaderAndGetError(vertex, vertexSource);
            if (!string.IsNullOrWhiteSpace(vertexError))
            {
                throw new InvalidOperationException($"OpenGL vertex shader failed: {vertexError}");
            }

            string? fragmentError = gl.CompileShaderAndGetError(fragment, fragmentSource);
            if (!string.IsNullOrWhiteSpace(fragmentError))
            {
                throw new InvalidOperationException($"OpenGL fragment shader failed: {fragmentError}");
            }

            int created = gl.CreateProgram();
            gl.AttachShader(created, vertex);
            gl.AttachShader(created, fragment);
            gl.BindAttribLocationString(created, 0, "aPosition");
            gl.BindAttribLocationString(created, 1, "aNormal");
            gl.BindAttribLocationString(created, 2, "aUv");
            gl.BindAttribLocationString(created, 3, "aAuxiliary");
            string? linkError = gl.LinkProgramAndGetError(created);
            if (!string.IsNullOrWhiteSpace(linkError))
            {
                gl.DeleteProgram(created);
                throw new InvalidOperationException($"OpenGL shader link failed: {linkError}");
            }

            return created;
        }
        finally
        {
            gl.DeleteShader(vertex);
            gl.DeleteShader(fragment);
        }
    }

    private int RequireUniform(string name)
    {
        int location = gl.GetUniformLocationString(program, name);
        if (location < 0)
        {
            throw new InvalidOperationException($"OpenGL shader uniform '{name}' was optimized out or missing.");
        }

        return location;
    }

    private int CreateFallbackTexture()
    {
        byte[] pixels =
        [
            56, 61, 72, 255, 170, 62, 185, 255,
            170, 62, 185, 255, 56, 61, 72, 255,
        ];
        int texture = gl.GenTexture();
        gl.BindTexture(Texture2D, texture);
        gl.TexParameteri(Texture2D, TextureMinFilter, Linear);
        gl.TexParameteri(Texture2D, TextureMagFilter, Linear);
        gl.TexParameteri(Texture2D, TextureWrapS, Repeat);
        gl.TexParameteri(Texture2D, TextureWrapT, Repeat);
        GCHandle handle = GCHandle.Alloc(pixels, GCHandleType.Pinned);
        try
        {
            gl.TexImage2D(
                Texture2D,
                0,
                Rgba,
                2,
                2,
                0,
                Rgba,
                UnsignedByte,
                handle.AddrOfPinnedObject());
        }
        finally
        {
            handle.Free();
        }

        return texture;
    }

    private int EnsureTexture(SceneTextureBinding binding)
    {
        if (textures.TryGetValue(binding.CacheKey, out int existing))
        {
            return existing;
        }

        if (binding.MipImages.Count == 0)
        {
            return fallbackTexture;
        }

        int texture = gl.GenTexture();
        gl.BindTexture(Texture2D, texture);
        gl.TexParameteri(
            Texture2D,
            TextureMinFilter,
            binding.MipImages.Count > 1 ? LinearMipmapLinear : Linear);
        gl.TexParameteri(Texture2D, TextureMagFilter, Linear);
        gl.TexParameteri(Texture2D, TextureWrapS, Repeat);
        gl.TexParameteri(Texture2D, TextureWrapT, Repeat);
        for (int level = 0; level < binding.MipImages.Count; level++)
        {
            Gof2Workshop.Core.RgbaImage image = binding.MipImages[level];
            if (image.Width > Info.MaximumTextureSize ||
                image.Height > Info.MaximumTextureSize)
            {
                gl.DeleteTexture(texture);
                throw new PlatformNotSupportedException(
                    $"Texture {binding.DisplayName} is {image.Width}x{image.Height}, " +
                    $"larger than this GPU's {Info.MaximumTextureSize} limit.");
            }

            byte[] pixels = binding.FlipVertically
                ? FlipRows(image)
                : image.ReadOnlyPixelBytes.ToArray();
            GCHandle handle = GCHandle.Alloc(pixels, GCHandleType.Pinned);
            try
            {
                gl.TexImage2D(
                    Texture2D,
                    level,
                    Rgba,
                    image.Width,
                    image.Height,
                    0,
                    Rgba,
                    UnsignedByte,
                    handle.AddrOfPinnedObject());
            }
            finally
            {
                handle.Free();
            }
        }

        textures.Add(binding.CacheKey, texture);
        return texture;
    }

    private void PruneUnusedTextures(SceneViewportRequest request)
    {
        HashSet<string> required = request.TextureBindings.Values
            .Select(binding => binding.CacheKey)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (string key in textures.Keys.Where(key => !required.Contains(key)).ToArray())
        {
            gl.DeleteTexture(textures[key]);
            textures.Remove(key);
        }
    }

    private void DeleteSceneResources()
    {
        foreach (MeshResource mesh in meshes)
        {
            gl.DeleteBuffer(mesh.VertexBuffer);
            gl.DeleteBuffer(mesh.IndexBuffer);
            gl.DeleteBuffer(mesh.EdgeBuffer);
            if (mesh.NormalLineBuffer != 0)
            {
                gl.DeleteBuffer(mesh.NormalLineBuffer);
            }

            if (mesh.DiagnosticBuffer != 0)
            {
                gl.DeleteBuffer(mesh.DiagnosticBuffer);
            }
        }

        meshes.Clear();
        foreach (int texture in textures.Values)
        {
            gl.DeleteTexture(texture);
        }

        textures.Clear();
        uploadedScene = null;
    }

    private static byte[] FlipRows(Gof2Workshop.Core.RgbaImage image)
    {
        int stride = checked(image.Width * 4);
        byte[] output = new byte[image.ReadOnlyPixelBytes.Length];
        for (int y = 0; y < image.Height; y++)
        {
            image.ReadOnlyPixelBytes.Slice(y * stride, stride)
                .CopyTo(output.AsSpan((image.Height - 1 - y) * stride, stride));
        }

        return output;
    }

    private static float[] BuildVertices(
        Vector3[] positions,
        Vector3[]? normals,
        Vector2[]? uvs,
        Vector4[]? auxiliary)
    {
        float[] output = new float[checked(positions.Length * 12)];
        for (int index = 0; index < positions.Length; index++)
        {
            int offset = index * 12;
            Vector3 position = positions[index];
            Vector3 normal = normals is not null && index < normals.Length
                ? normals[index]
                : Vector3.Zero;
            Vector2 uv = uvs is not null && index < uvs.Length
                ? uvs[index]
                : Vector2.Zero;
            Vector4 aux = auxiliary is not null && index < auxiliary.Length
                ? auxiliary[index]
                : Vector4.One;
            output[offset] = position.X;
            output[offset + 1] = position.Y;
            output[offset + 2] = position.Z;
            output[offset + 3] = normal.X;
            output[offset + 4] = normal.Y;
            output[offset + 5] = normal.Z;
            output[offset + 6] = uv.X;
            output[offset + 7] = uv.Y;
            output[offset + 8] = aux.X;
            output[offset + 9] = aux.Y;
            output[offset + 10] = aux.Z;
            output[offset + 11] = aux.W;
        }

        return output;
    }

    private static ushort[] BuildEdgeIndices(ushort[] triangles)
    {
        ushort[] output = new ushort[checked(triangles.Length * 2)];
        int write = 0;
        for (int index = 0; index + 2 < triangles.Length; index += 3)
        {
            ushort a = triangles[index];
            ushort b = triangles[index + 1];
            ushort c = triangles[index + 2];
            output[write++] = a;
            output[write++] = b;
            output[write++] = b;
            output[write++] = c;
            output[write++] = c;
            output[write++] = a;
        }

        return output;
    }

    private static float[] BuildNormalLines(ScenePrimitive primitive)
    {
        if (primitive.Normals is null || primitive.Normals.Length == 0)
        {
            return [];
        }

        const int maximumLines = 3_000;
        int stride = Math.Max(1, (int)Math.Ceiling(primitive.Positions.Length / (double)maximumLines));
        float length = Math.Max(
            primitive.BoundingSphereRadius * 0.08f,
            0.001f);
        List<Vector3> points = [];
        for (int index = 0; index < primitive.Positions.Length; index += stride)
        {
            Vector3 normal = primitive.Normals[index];
            if (normal.LengthSquared() < 1e-10f)
            {
                continue;
            }

            Vector3 start = primitive.Positions[index];
            points.Add(start);
            points.Add(start + (Vector3.Normalize(normal) * length));
        }

        return BuildPositionOnlyVertices(points);
    }

    private static float[] BuildDiagnosticLines(
        ScenePrimitive primitive,
        out int pivotVertexCount)
    {
        float radius = Math.Max(primitive.BoundingSphereRadius, 0);
        float marker = Math.Max(radius * 0.08f, 0.002f);
        List<Vector3> points =
        [
            primitive.SourcePivot - (Vector3.UnitX * marker),
            primitive.SourcePivot + (Vector3.UnitX * marker),
            primitive.SourcePivot - (Vector3.UnitY * marker),
            primitive.SourcePivot + (Vector3.UnitY * marker),
            primitive.SourcePivot - (Vector3.UnitZ * marker),
            primitive.SourcePivot + (Vector3.UnitZ * marker),
        ];
        pivotVertexCount = points.Count;
        if (radius > 0)
        {
            const int segments = 64;
            for (int axis = 0; axis < 3; axis++)
            {
                for (int segment = 0; segment < segments; segment++)
                {
                    float first = MathF.Tau * segment / segments;
                    float second = MathF.Tau * (segment + 1) / segments;
                    points.Add(CirclePoint(primitive.BoundingSphereCenter, radius, axis, first));
                    points.Add(CirclePoint(primitive.BoundingSphereCenter, radius, axis, second));
                }
            }
        }

        return BuildPositionOnlyVertices(points);
    }

    private static Vector3 CirclePoint(
        Vector3 center,
        float radius,
        int axis,
        float angle)
    {
        float a = MathF.Cos(angle) * radius;
        float b = MathF.Sin(angle) * radius;
        return center + axis switch
        {
            0 => new Vector3(0, a, b),
            1 => new Vector3(a, 0, b),
            _ => new Vector3(a, b, 0),
        };
    }

    private static float[] BuildPositionOnlyVertices(List<Vector3> points)
    {
        float[] vertices = new float[checked(points.Count * 12)];
        for (int index = 0; index < points.Count; index++)
        {
            int offset = index * 12;
            vertices[offset] = points[index].X;
            vertices[offset + 1] = points[index].Y;
            vertices[offset + 2] = points[index].Z;
            vertices[offset + 8] = 1;
            vertices[offset + 9] = 1;
            vertices[offset + 10] = 1;
            vertices[offset + 11] = 1;
        }

        return vertices;
    }

    private sealed record MeshResource(
        int VertexBuffer,
        int IndexBuffer,
        int IndexCount,
        int EdgeBuffer,
        int EdgeIndexCount,
        int NormalLineBuffer,
        int NormalLineVertexCount,
        int DiagnosticBuffer,
        int PivotVertexCount,
        int BoundVertexCount);

    private static class ShaderSources
    {
        public static (string Vertex, string Fragment) Create(GlVersion version)
        {
            bool es = version.Type == GlProfileType.OpenGLES;
            bool modern = version.Major >= 3;
            if (modern)
            {
                string header = es
                    ? "#version 300 es\nprecision highp float;\n"
                    : "#version 330 core\n";
                return (
                    header + ModernVertex,
                    header + ModernFragment);
            }

            string legacyHeader = es
                ? "#version 100\nprecision highp float;\n"
                : "#version 120\n";
            return (
                legacyHeader + LegacyVertex,
                legacyHeader + LegacyFragment);
        }

        private const string ModernVertex = """
            in vec3 aPosition;
            in vec3 aNormal;
            in vec2 aUv;
            in vec4 aAuxiliary;
            uniform mat4 uMvp;
            out vec3 vNormal;
            out vec2 vUv;
            out vec4 vAuxiliary;
            void main()
            {
                gl_Position = uMvp * vec4(aPosition, 1.0);
                vNormal = aNormal;
                vUv = aUv;
                vAuxiliary = aAuxiliary;
            }
            """;

        private const string ModernFragment = """
            in vec3 vNormal;
            in vec2 vUv;
            in vec4 vAuxiliary;
            uniform vec4 uBaseColor;
            uniform float uSelected;
            uniform int uMode;
            uniform int uUseTexture;
            uniform sampler2D uTexture;
            out vec4 outColor;
            void main()
            {
                vec4 color = uUseTexture != 0 ? texture(uTexture, vUv) : uBaseColor;
                if (uMode == 3)
                    color = clamp(vAuxiliary, 0.0, 1.0);
                else if (uMode == 4)
                    color = gl_FrontFacing ? vec4(0.20, 0.82, 0.38, 1.0) : vec4(0.92, 0.22, 0.18, 1.0);
                else if (uMode == 0)
                {
                    vec3 normal = normalize(vNormal);
                    float light = 0.28 + 0.72 * abs(dot(normal, normalize(vec3(-0.4, 0.7, 0.6))));
                    color.rgb *= light;
                }
                color.rgb = mix(color.rgb, vec3(1.0, 0.68, 0.08), uSelected * 0.42);
                outColor = color;
            }
            """;

        private const string LegacyVertex = """
            attribute vec3 aPosition;
            attribute vec3 aNormal;
            attribute vec2 aUv;
            attribute vec4 aAuxiliary;
            uniform mat4 uMvp;
            varying vec3 vNormal;
            varying vec2 vUv;
            varying vec4 vAuxiliary;
            void main()
            {
                gl_Position = uMvp * vec4(aPosition, 1.0);
                vNormal = aNormal;
                vUv = aUv;
                vAuxiliary = aAuxiliary;
            }
            """;

        private const string LegacyFragment = """
            varying vec3 vNormal;
            varying vec2 vUv;
            varying vec4 vAuxiliary;
            uniform vec4 uBaseColor;
            uniform float uSelected;
            uniform int uMode;
            uniform int uUseTexture;
            uniform sampler2D uTexture;
            void main()
            {
                vec4 color = uUseTexture != 0 ? texture2D(uTexture, vUv) : uBaseColor;
                if (uMode == 3)
                    color = clamp(vAuxiliary, 0.0, 1.0);
                else if (uMode == 4)
                    color = gl_FrontFacing ? vec4(0.20, 0.82, 0.38, 1.0) : vec4(0.92, 0.22, 0.18, 1.0);
                else if (uMode == 0)
                {
                    vec3 normal = normalize(vNormal);
                    float light = 0.28 + 0.72 * abs(dot(normal, normalize(vec3(-0.4, 0.7, 0.6))));
                    color.rgb *= light;
                }
                color.rgb = mix(color.rgb, vec3(1.0, 0.68, 0.08), uSelected * 0.42);
                gl_FragColor = color;
            }
            """;
    }

    private sealed class GlApiExtra
    {
        private readonly UniformMatrix4Delegate uniformMatrix4;
        private readonly Uniform4Delegate uniform4;
        private readonly BlendFuncDelegate blendFunc;
        private readonly GetIntegerDelegate getInteger;

        public GlApiExtra(GlInterface gl)
        {
            uniformMatrix4 = Load<UniformMatrix4Delegate>(gl, "glUniformMatrix4fv");
            uniform4 = Load<Uniform4Delegate>(gl, "glUniform4f");
            blendFunc = Load<BlendFuncDelegate>(gl, "glBlendFunc");
            getInteger = Load<GetIntegerDelegate>(gl, "glGetIntegerv");
        }

        public void UniformMatrix4(int location, Matrix4x4 value)
        {
            float[] values =
            [
                value.M11, value.M12, value.M13, value.M14,
                value.M21, value.M22, value.M23, value.M24,
                value.M31, value.M32, value.M33, value.M34,
                value.M41, value.M42, value.M43, value.M44,
            ];
            GCHandle handle = GCHandle.Alloc(values, GCHandleType.Pinned);
            try
            {
                uniformMatrix4(location, 1, 0, handle.AddrOfPinnedObject());
            }
            finally
            {
                handle.Free();
            }
        }

        public void Uniform4(int location, float x, float y, float z, float w) =>
            uniform4(location, x, y, z, w);

        public void BlendFunc(int source, int destination) =>
            blendFunc(source, destination);

        public int GetInteger(int name)
        {
            int[] values = new int[1];
            GCHandle handle = GCHandle.Alloc(values, GCHandleType.Pinned);
            try
            {
                getInteger(name, handle.AddrOfPinnedObject());
                return values[0];
            }
            finally
            {
                handle.Free();
            }
        }

        private static T Load<T>(GlInterface gl, string name)
            where T : Delegate
        {
            IntPtr address = gl.GetProcAddress(name);
            if (address == IntPtr.Zero)
            {
                throw new PlatformNotSupportedException(
                    $"The OpenGL driver does not expose required function {name}.");
            }

            return Marshal.GetDelegateForFunctionPointer<T>(address);
        }

        [UnmanagedFunctionPointer(CallingConvention.Winapi)]
        private delegate void UniformMatrix4Delegate(
            int location,
            int count,
            byte transpose,
            IntPtr values);

        [UnmanagedFunctionPointer(CallingConvention.Winapi)]
        private delegate void Uniform4Delegate(
            int location,
            float x,
            float y,
            float z,
            float w);

        [UnmanagedFunctionPointer(CallingConvention.Winapi)]
        private delegate void BlendFuncDelegate(int source, int destination);

        [UnmanagedFunctionPointer(CallingConvention.Winapi)]
        private delegate void GetIntegerDelegate(int name, IntPtr value);
    }
}
