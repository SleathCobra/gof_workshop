using System.Numerics;
using System.Globalization;
using Gof2Workshop.Core;
using Gof2Workshop.Scene;

namespace Gof2Workshop.Export;

public sealed record ScenePreviewOptions(
    int Width = 1024,
    int Height = 1024,
    bool Solid = true,
    bool Wireframe = true,
    bool ShowNormals = true,
    bool ShowPivots = true,
    bool ShowBoundingSpheres = true,
    int MaximumTriangles = 250_000,
    int MaximumNormalLines = 3_000,
    SceneCamera? Camera = null,
    int? IsolatedPrimitiveIndex = null,
    bool ShowFaceWinding = false,
    float? AnimationTimeSeconds = null,
    IReadOnlyDictionary<int, RgbaImage>? Textures = null);

public sealed record SceneCamera(
    float Yaw = -0.610865238f,
    float Pitch = 0.436332313f,
    float PanX = 0,
    float PanY = 0,
    float Zoom = 1,
    bool Perspective = true);

public sealed record ScenePreviewResult(
    RgbaImage Image,
    long SourceTriangleCount,
    long RenderedTriangleCount,
    long NormalLineCount);

public sealed class ScenePreviewRenderer
{
    public ScenePreviewResult Render(
        SceneDocument scene,
        ScenePreviewOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scene);
        options ??= new ScenePreviewOptions();
        ValidateOptions(options);

        RgbaImage image = new(options.Width, options.Height);
        FillBackground(image);
        float[] depth = Enumerable.Repeat(float.PositiveInfinity, checked(options.Width * options.Height)).ToArray();
        Projection projection = Projection.Create(
            scene,
            options.Width,
            options.Height,
            options.Camera ?? new SceneCamera());

        long sourceTriangleCount = scene.Primitives.Sum(
            primitive => (long)primitive.Indices.Length / 3);
        int triangleStride = sourceTriangleCount > options.MaximumTriangles
            ? checked((int)Math.Ceiling(sourceTriangleCount / (double)options.MaximumTriangles))
            : 1;
        long globalTriangle = 0;
        long renderedTriangles = 0;

        for (int primitiveIndex = 0; primitiveIndex < scene.Primitives.Count; primitiveIndex++)
        {
            if (options.IsolatedPrimitiveIndex is not null &&
                options.IsolatedPrimitiveIndex.Value != primitiveIndex)
            {
                continue;
            }

            ScenePrimitive primitive = scene.Primitives[primitiveIndex];
            cancellationToken.ThrowIfCancellationRequested();
            SceneTransform transform = GetAnimationTransform(scene, primitiveIndex, options);
            Vector3[] transformedPositions = primitive.Positions
                .Select(position => transform.TransformPosition(position, primitive.SourcePivot))
                .ToArray();
            ScreenVertex[] projected = transformedPositions
                .Select(projection.Project)
                .ToArray();
            Rgba32 baseColor = ToColor(primitive.Material.BaseColor);

            for (int index = 0; index + 2 < primitive.Indices.Length; index += 3)
            {
                if (globalTriangle++ % triangleStride != 0)
                {
                    continue;
                }

                ScreenVertex a = projected[primitive.Indices[index]];
                ScreenVertex b = projected[primitive.Indices[index + 1]];
                ScreenVertex c = projected[primitive.Indices[index + 2]];
                float shade = CalculateShade(
                    transformedPositions[primitive.Indices[index]],
                    transformedPositions[primitive.Indices[index + 1]],
                    transformedPositions[primitive.Indices[index + 2]]);
                Rgba32 shaded = ScaleColor(baseColor, shade);
                if (options.ShowFaceWinding && primitive.Normals is not null)
                {
                    shaded = WindingColor(
                        primitive,
                        index,
                        shade);
                }

                if (options.Solid)
                {
                    RgbaImage? texture = options.Textures is not null &&
                        options.Textures.TryGetValue(primitiveIndex, out RgbaImage? assigned)
                        ? assigned
                        : null;
                    Vector2[]? uvs = primitive.TextureCoordinates;
                    FillTriangle(
                        image,
                        depth,
                        a,
                        b,
                        c,
                        shaded,
                        texture,
                        uvs is null ? null : uvs[primitive.Indices[index]],
                        uvs is null ? null : uvs[primitive.Indices[index + 1]],
                        uvs is null ? null : uvs[primitive.Indices[index + 2]],
                        shade);
                }

                if (options.Wireframe)
                {
                    Rgba32 wireColor = new(230, 240, 250, 190);
                    RasterDrawing.DrawLine(
                        image,
                        Round(a.X),
                        Round(a.Y),
                        Round(b.X),
                        Round(b.Y),
                        wireColor);
                    RasterDrawing.DrawLine(
                        image,
                        Round(b.X),
                        Round(b.Y),
                        Round(c.X),
                        Round(c.Y),
                        wireColor);
                    RasterDrawing.DrawLine(
                        image,
                        Round(c.X),
                        Round(c.Y),
                        Round(a.X),
                        Round(a.Y),
                        wireColor);
                }

                renderedTriangles++;
            }
        }

        long normalLines = options.ShowNormals
            ? DrawNormals(
                scene,
                image,
                projection,
                options.MaximumNormalLines,
                options.IsolatedPrimitiveIndex,
                options,
                cancellationToken)
            : 0;

        if (options.ShowBoundingSpheres || options.ShowPivots)
        {
            DrawDiagnostics(scene, image, projection, options);
        }

        DrawLegend(image, scene.Primitives.Count, sourceTriangleCount, renderedTriangles);
        return new ScenePreviewResult(image, sourceTriangleCount, renderedTriangles, normalLines);
    }

    public ScenePreviewResult RenderToPng(
        SceneDocument scene,
        string path,
        ScenePreviewOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ScenePreviewResult result = Render(scene, options, cancellationToken);
        PngWriter.Write(result.Image, path, cancellationToken);
        return result;
    }

    private static void FillBackground(RgbaImage image)
    {
        for (int y = 0; y < image.Height; y++)
        {
            byte value = (byte)(15 + ((long)y * 18 / image.Height));
            Rgba32 color = new(value, (byte)(value + 3), (byte)(value + 9), byte.MaxValue);
            for (int x = 0; x < image.Width; x++)
            {
                image.SetPixel(x, y, color);
            }
        }

        int gridSpacing = Math.Max(32, Math.Min(image.Width, image.Height) / 16);
        Rgba32 grid = new(52, 59, 70, 90);
        for (int x = 0; x < image.Width; x += gridSpacing)
        {
            RasterDrawing.DrawLine(image, x, 0, x, image.Height - 1, grid);
        }

        for (int y = 0; y < image.Height; y += gridSpacing)
        {
            RasterDrawing.DrawLine(image, 0, y, image.Width - 1, y, grid);
        }
    }

    private static void FillTriangle(
        RgbaImage image,
        float[] depth,
        ScreenVertex a,
        ScreenVertex b,
        ScreenVertex c,
        Rgba32 color,
        RgbaImage? texture = null,
        Vector2? uvA = null,
        Vector2? uvB = null,
        Vector2? uvC = null,
        float shade = 1)
    {
        float area = Edge(a.X, a.Y, b.X, b.Y, c.X, c.Y);
        if (MathF.Abs(area) < 1e-5f)
        {
            return;
        }

        int minX = Math.Clamp((int)MathF.Floor(MathF.Min(a.X, MathF.Min(b.X, c.X))), 0, image.Width - 1);
        int maxX = Math.Clamp((int)MathF.Ceiling(MathF.Max(a.X, MathF.Max(b.X, c.X))), 0, image.Width - 1);
        int minY = Math.Clamp((int)MathF.Floor(MathF.Min(a.Y, MathF.Min(b.Y, c.Y))), 0, image.Height - 1);
        int maxY = Math.Clamp((int)MathF.Ceiling(MathF.Max(a.Y, MathF.Max(b.Y, c.Y))), 0, image.Height - 1);

        for (int y = minY; y <= maxY; y++)
        {
            for (int x = minX; x <= maxX; x++)
            {
                float sampleX = x + 0.5f;
                float sampleY = y + 0.5f;
                float weightA = Edge(b.X, b.Y, c.X, c.Y, sampleX, sampleY) / area;
                float weightB = Edge(c.X, c.Y, a.X, a.Y, sampleX, sampleY) / area;
                float weightC = 1.0f - weightA - weightB;
                if (weightA < 0 || weightB < 0 || weightC < 0)
                {
                    continue;
                }

                float z = (weightA * a.Z) + (weightB * b.Z) + (weightC * c.Z);
                int pixelIndex = (y * image.Width) + x;
                if (z >= depth[pixelIndex])
                {
                    continue;
                }

                depth[pixelIndex] = z;
                Rgba32 outputColor = color;
                if (texture is not null && uvA is not null && uvB is not null && uvC is not null)
                {
                    Vector2 uv = (uvA.Value * weightA) +
                        (uvB.Value * weightB) +
                        (uvC.Value * weightC);
                    float wrappedU = uv.X - MathF.Floor(uv.X);
                    float wrappedV = uv.Y - MathF.Floor(uv.Y);
                    int textureX = Math.Clamp((int)(wrappedU * texture.Width), 0, texture.Width - 1);
                    int textureY = Math.Clamp((int)(wrappedV * texture.Height), 0, texture.Height - 1);
                    outputColor = ScaleColor(texture.GetPixel(textureX, textureY), shade);
                }

                image.SetPixel(x, y, outputColor);
            }
        }
    }

    private static long DrawNormals(
        SceneDocument scene,
        RgbaImage image,
        Projection projection,
        int maximumLines,
        int? isolatedPrimitiveIndex,
        ScenePreviewOptions options,
        CancellationToken cancellationToken)
    {
        long available = scene.Primitives
            .Where((_, index) => isolatedPrimitiveIndex is null || isolatedPrimitiveIndex == index)
            .Sum(primitive => primitive.Normals is null ? 0 : primitive.Positions.Length);
        int stride = available > maximumLines
            ? checked((int)Math.Ceiling(available / (double)maximumLines))
            : 1;
        float normalLength = Math.Max(projection.WorldSpan * 0.035f, 0.001f);
        long global = 0;
        long drawn = 0;

        for (int primitiveIndex = 0; primitiveIndex < scene.Primitives.Count; primitiveIndex++)
        {
            if (isolatedPrimitiveIndex is not null && isolatedPrimitiveIndex != primitiveIndex)
            {
                continue;
            }

            ScenePrimitive primitive = scene.Primitives[primitiveIndex];
            SceneTransform transform = GetAnimationTransform(scene, primitiveIndex, options);
            if (primitive.Normals is null)
            {
                continue;
            }

            for (int index = 0; index < primitive.Positions.Length; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (global++ % stride != 0)
                {
                    continue;
                }

                Vector3 normal = transform.TransformDirection(primitive.Normals[index]);
                if (normal.LengthSquared() < 1e-12f)
                {
                    continue;
                }

                normal = Vector3.Normalize(normal);
                Vector3 position = transform.TransformPosition(
                    primitive.Positions[index],
                    primitive.SourcePivot);
                ScreenVertex start = projection.Project(position);
                ScreenVertex end = projection.Project(position + (normal * normalLength));
                RasterDrawing.DrawLine(
                    image,
                    Round(start.X),
                    Round(start.Y),
                    Round(end.X),
                    Round(end.Y),
                    new Rgba32(72, 255, 128, 185));
                drawn++;
            }
        }

        return drawn;
    }

    private static void DrawDiagnostics(
        SceneDocument scene,
        RgbaImage image,
        Projection projection,
        ScenePreviewOptions options)
    {
        for (int primitiveIndex = 0; primitiveIndex < scene.Primitives.Count; primitiveIndex++)
        {
            if (options.IsolatedPrimitiveIndex is not null &&
                options.IsolatedPrimitiveIndex.Value != primitiveIndex)
            {
                continue;
            }

            ScenePrimitive primitive = scene.Primitives[primitiveIndex];
            SceneTransform transform = GetAnimationTransform(scene, primitiveIndex, options);
            if (options.ShowPivots)
            {
                ScreenVertex pivot = projection.Project(
                    primitive.SourcePivot + transform.Translation);
                int x = Round(pivot.X);
                int y = Round(pivot.Y);
                const int size = 7;
                Rgba32 color = new(255, 218, 64, 240);
                RasterDrawing.DrawLine(image, x - size, y, x + size, y, color);
                RasterDrawing.DrawLine(image, x, y - size, x, y + size, color);
            }

            if (options.ShowBoundingSpheres && primitive.BoundingSphereRadius > 0)
            {
                Vector3 centerPosition = transform.TransformPosition(
                    primitive.BoundingSphereCenter,
                    primitive.SourcePivot);
                ScreenVertex center = projection.Project(centerPosition);
                float boundScale = Math.Max(
                    MathF.Abs(transform.Scale.X),
                    Math.Max(MathF.Abs(transform.Scale.Y), MathF.Abs(transform.Scale.Z)));
                int radius = Math.Clamp(
                    Round(MathF.Abs(
                        primitive.BoundingSphereRadius * boundScale * projection.Scale)),
                    1,
                    Math.Max(image.Width, image.Height) * 2);
                RasterDrawing.DrawCircle(
                    image,
                    Round(center.X),
                    Round(center.Y),
                    radius,
                    new Rgba32(255, 118, 72, 180));
            }
        }
    }

    private static SceneTransform GetAnimationTransform(
        SceneDocument scene,
        int primitiveIndex,
        ScenePreviewOptions options)
    {
        return options.AnimationTimeSeconds is float time && scene.Animations.Count > 0
            ? SceneAnimationEvaluator.Evaluate(scene.Animations[0], primitiveIndex, time)
            : SceneTransform.Identity;
    }

    private static void DrawLegend(
        RgbaImage image,
        int primitiveCount,
        long sourceTriangles,
        long renderedTriangles)
    {
        RasterDrawing.FillRectangle(image, 12, 12, 250, 48, new Rgba32(0, 0, 0, 175));
        RasterDrawing.DrawText(
            image,
            20,
            19,
            primitiveCount.ToString(CultureInfo.InvariantCulture),
            new Rgba32(80, 205, 255, 255));
        RasterDrawing.DrawText(
            image,
            20,
            32,
            sourceTriangles.ToString(CultureInfo.InvariantCulture),
            new Rgba32(255, 255, 255, 255));
        if (sourceTriangles != renderedTriangles)
        {
            RasterDrawing.DrawText(
                image,
                20,
                45,
                renderedTriangles.ToString(CultureInfo.InvariantCulture),
                new Rgba32(255, 196, 72, 255));
        }
    }

    private static float CalculateShade(Vector3 first, Vector3 second, Vector3 third)
    {
        Vector3 normal = Vector3.Cross(second - first, third - first);
        if (normal.LengthSquared() < 1e-12f)
        {
            return 0.25f;
        }

        normal = Vector3.Normalize(normal);
        Vector3 light = Vector3.Normalize(new Vector3(-0.4f, 0.7f, -0.8f));
        return 0.25f + (0.75f * MathF.Abs(Vector3.Dot(normal, light)));
    }

    private static Rgba32 ToColor(Vector4 value)
    {
        return new Rgba32(
            ToByte(value.X),
            ToByte(value.Y),
            ToByte(value.Z),
            byte.MaxValue);
    }

    private static Rgba32 ScaleColor(Rgba32 value, float scale)
    {
        return new Rgba32(
            ToByte((value.R / 255.0f) * scale),
            ToByte((value.G / 255.0f) * scale),
            ToByte((value.B / 255.0f) * scale),
            byte.MaxValue);
    }

    private static Rgba32 WindingColor(
        ScenePrimitive primitive,
        int indexOffset,
        float shade)
    {
        int first = primitive.Indices[indexOffset];
        int second = primitive.Indices[indexOffset + 1];
        int third = primitive.Indices[indexOffset + 2];
        Vector3 geometric = Vector3.Cross(
            primitive.Positions[second] - primitive.Positions[first],
            primitive.Positions[third] - primitive.Positions[first]);
        Vector3 stored = primitive.Normals![first] +
            primitive.Normals[second] +
            primitive.Normals[third];
        if (geometric.LengthSquared() < 1e-12f || stored.LengthSquared() < 1e-12f)
        {
            return new Rgba32(195, 178, 74, 255);
        }

        bool aligned = Vector3.Dot(geometric, stored) >= 0;
        return ScaleColor(
            aligned
                ? new Rgba32(70, 205, 125, 255)
                : new Rgba32(235, 82, 78, 255),
            Math.Max(shade, 0.45f));
    }

    private static byte ToByte(float value)
    {
        return (byte)Math.Clamp(Round(value * 255.0f), 0, 255);
    }

    private static int Round(float value) => (int)MathF.Round(value);

    private static float Edge(
        float ax,
        float ay,
        float bx,
        float by,
        float px,
        float py)
    {
        return ((px - ax) * (by - ay)) - ((py - ay) * (bx - ax));
    }

    private static void ValidateOptions(ScenePreviewOptions options)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(options.Width);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(options.Height);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(options.MaximumTriangles);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(options.MaximumNormalLines);

        if (options.Width > 4096 || options.Height > 4096)
        {
            throw new ArgumentOutOfRangeException(
                nameof(options),
                "Preview dimensions may not exceed 4096.");
        }
    }

    private readonly record struct ScreenVertex(float X, float Y, float Z);

    private sealed record Projection(
        Vector3 Center,
        float Scale,
        int Width,
        int Height,
        float WorldSpan,
        SceneCamera Camera)
    {
        public ScreenVertex Project(Vector3 source)
        {
            Vector3 rotated = Rotate(source - Center, Camera);
            float perspectiveScale = 1;
            if (Camera.Perspective)
            {
                float distance = WorldSpan * 3.5f;
                perspectiveScale = distance / Math.Max(distance + rotated.Z, distance * 0.15f);
            }

            return new ScreenVertex(
                (Width * (0.5f + Camera.PanX)) + (rotated.X * Scale * perspectiveScale),
                (Height * (0.5f + Camera.PanY)) - (rotated.Y * Scale * perspectiveScale),
                rotated.Z);
        }

        public static Projection Create(
            SceneDocument scene,
            int width,
            int height,
            SceneCamera camera)
        {
            Vector3 center = scene.Bounds.Center;
            Vector2 minimum = new(float.PositiveInfinity);
            Vector2 maximum = new(float.NegativeInfinity);
            foreach (ScenePrimitive primitive in scene.Primitives)
            {
                foreach (Vector3 position in primitive.Positions)
                {
                    Vector3 rotated = Rotate(position - center, camera);
                    minimum = Vector2.Min(minimum, new Vector2(rotated.X, rotated.Y));
                    maximum = Vector2.Max(maximum, new Vector2(rotated.X, rotated.Y));
                }
            }

            float projectedWidth = Math.Max(maximum.X - minimum.X, 1e-4f);
            float projectedHeight = Math.Max(maximum.Y - minimum.Y, 1e-4f);
            float scale = Math.Min(
                width * 0.80f / projectedWidth,
                height * 0.80f / projectedHeight) * Math.Clamp(camera.Zoom, 0.05f, 40);
            float worldSpan = Math.Max(
                scene.Bounds.Size.X,
                Math.Max(scene.Bounds.Size.Y, scene.Bounds.Size.Z));
            return new Projection(
                center,
                scale,
                width,
                height,
                Math.Max(worldSpan, 1e-4f),
                camera);
        }

        private static Vector3 Rotate(Vector3 value, SceneCamera camera)
        {
            float yawCos = MathF.Cos(camera.Yaw);
            float yawSin = MathF.Sin(camera.Yaw);
            float x = (yawCos * value.X) + (yawSin * value.Z);
            float yawZ = (-yawSin * value.X) + (yawCos * value.Z);

            float pitchCos = MathF.Cos(camera.Pitch);
            float pitchSin = MathF.Sin(camera.Pitch);
            float y = (pitchCos * value.Y) - (pitchSin * yawZ);
            float z = (pitchSin * value.Y) + (pitchCos * yawZ);
            return new Vector3(x, y, z);
        }
    }
}
