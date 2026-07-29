using System.Numerics;
using Gof2Workshop.Core;
using Gof2Workshop.Export;
using Gof2Workshop.Scene;

namespace Gof2Workshop.App.Rendering;

public enum SceneViewportMode
{
    LitTextured,
    UnlitTextured,
    SolidDiagnostic,
    AuxiliaryChannel,
    Winding,
}

public sealed record SceneViewportRequest(
    SceneDocument Scene,
    SceneCamera Camera,
    SceneViewportMode Mode,
    bool Wireframe,
    bool ShowNormals,
    bool ShowPivots,
    bool ShowBounds,
    bool BackFaceCulling,
    int? SelectedPrimitiveIndex,
    int? FocusedPrimitiveIndex,
    int? IsolatedPrimitiveIndex,
    float? AnimationTimeSeconds,
    IReadOnlyDictionary<int, SceneTextureBinding> TextureBindings,
    Vector4 BackgroundColor);

public sealed record SceneTextureBinding(
    string CacheKey,
    string DisplayName,
    string SourcePath,
    IReadOnlyList<RgbaImage> MipImages,
    bool FlipVertically,
    bool HasAlpha);

public sealed record SceneViewportRendererInfo(
    string Name,
    string ApiVersion,
    string Vendor,
    string Device,
    int MaximumTextureSize,
    bool HardwareAccelerated);

public sealed record SceneViewportFrameMetrics(
    double FrameMilliseconds,
    int DrawCalls,
    int TriangleCount,
    int TextureCount,
    long FramesRendered);

public interface ISceneViewportRenderer
{
    public SceneViewportRendererInfo Info { get; }

    public SceneViewportFrameMetrics Render(
        SceneViewportRequest request,
        int framebuffer,
        int pixelWidth,
        int pixelHeight);
}

internal static class SceneViewportMatrices
{
    public static Matrix4x4 CreateViewProjection(
        SceneViewportRequest request,
        int width,
        int height)
    {
        SceneBounds focus = GetFocusBounds(request);
        float span = Math.Max(
            focus.Size.X,
            Math.Max(focus.Size.Y, focus.Size.Z));
        span = Math.Max(span, 1e-3f);

        float yaw = request.Camera.Yaw;
        float pitch = request.Camera.Pitch;
        float cosPitch = MathF.Cos(pitch);
        Vector3 direction = Vector3.Normalize(new Vector3(
            cosPitch * MathF.Sin(yaw),
            MathF.Sin(pitch),
            cosPitch * MathF.Cos(yaw)));
        Vector3 right = Vector3.Cross(Vector3.UnitY, direction);
        if (right.LengthSquared() < 1e-8f)
        {
            right = Vector3.UnitX;
        }
        else
        {
            right = Vector3.Normalize(right);
        }

        Vector3 up = Vector3.Normalize(Vector3.Cross(direction, right));
        Vector3 target = focus.Center -
            (right * request.Camera.PanX * span * 1.5f) +
            (up * request.Camera.PanY * span * 1.5f);
        float zoom = Math.Clamp(request.Camera.Zoom, 0.05f, 40f);
        float distance = Math.Max(span * 1.9f / zoom, span * 0.05f);
        Vector3 eye = target + (direction * distance);
        Matrix4x4 view = Matrix4x4.CreateLookAt(eye, target, up);

        float aspect = Math.Max(width / (float)Math.Max(height, 1), 1e-4f);
        float near = Math.Max(span * 0.001f, 1e-5f);
        float far = Math.Max(distance + (span * 8f), near + 1f);
        Matrix4x4 projection = request.Camera.Perspective
            ? Matrix4x4.CreatePerspectiveFieldOfView(
                MathF.PI / 4f,
                aspect,
                near,
                far)
            : Matrix4x4.CreateOrthographic(
                span * 1.35f * aspect / zoom,
                span * 1.35f / zoom,
                near,
                far);
        return view * projection;
    }

    public static Matrix4x4 CreatePrimitiveTransform(
        SceneViewportRequest request,
        int primitiveIndex)
    {
        if (request.AnimationTimeSeconds is not float time ||
            request.Scene.Animations.Count == 0)
        {
            return Matrix4x4.Identity;
        }

        ScenePrimitive primitive = request.Scene.Primitives[primitiveIndex];
        SceneTransform transform = SceneAnimationEvaluator.Evaluate(
            request.Scene.Animations[0],
            primitiveIndex,
            time);
        return Matrix4x4.CreateTranslation(-primitive.SourcePivot) *
            Matrix4x4.CreateScale(transform.Scale) *
            Matrix4x4.CreateFromQuaternion(transform.Rotation) *
            Matrix4x4.CreateTranslation(primitive.SourcePivot + transform.Translation);
    }

    public static SceneBounds GetFocusBounds(SceneViewportRequest request)
    {
        if (request.FocusedPrimitiveIndex is int selected &&
            selected >= 0 &&
            selected < request.Scene.Primitives.Count)
        {
            ScenePrimitive primitive = request.Scene.Primitives[selected];
            if (primitive.Positions.Length > 0)
            {
                Vector3 minimum = new(float.PositiveInfinity);
                Vector3 maximum = new(float.NegativeInfinity);
                foreach (Vector3 position in primitive.Positions)
                {
                    minimum = Vector3.Min(minimum, position);
                    maximum = Vector3.Max(maximum, position);
                }

                return new SceneBounds(minimum, maximum);
            }
        }

        return request.Scene.Bounds;
    }
}

public static class SceneViewportPicking
{
    public static int? PickPrimitive(
        SceneViewportRequest request,
        double x,
        double y,
        double viewportWidth,
        double viewportHeight)
    {
        if (viewportWidth <= 0 || viewportHeight <= 0)
        {
            return null;
        }

        int width = Math.Max(1, (int)Math.Round(viewportWidth));
        int height = Math.Max(1, (int)Math.Round(viewportHeight));
        Matrix4x4 viewProjection = SceneViewportMatrices.CreateViewProjection(
            request,
            width,
            height);
        float ndcX = (float)((x / viewportWidth * 2.0) - 1.0);
        float ndcY = (float)(1.0 - (y / viewportHeight * 2.0));
        float nearest = float.PositiveInfinity;
        int? result = null;

        for (int primitiveIndex = 0;
             primitiveIndex < request.Scene.Primitives.Count;
             primitiveIndex++)
        {
            if (request.IsolatedPrimitiveIndex is int isolated &&
                isolated != primitiveIndex)
            {
                continue;
            }

            Matrix4x4 model = SceneViewportMatrices.CreatePrimitiveTransform(
                request,
                primitiveIndex);
            if (!Matrix4x4.Invert(model * viewProjection, out Matrix4x4 inverse))
            {
                continue;
            }

            Vector3 near = Unproject(ndcX, ndcY, 0, inverse);
            Vector3 far = Unproject(ndcX, ndcY, 1, inverse);
            Vector3 direction = far - near;
            if (direction.LengthSquared() < 1e-12f)
            {
                continue;
            }

            direction = Vector3.Normalize(direction);
            ScenePrimitive primitive = request.Scene.Primitives[primitiveIndex];
            if (!IntersectsSphere(
                    near,
                    direction,
                    primitive.BoundingSphereCenter,
                    primitive.BoundingSphereRadius))
            {
                continue;
            }

            for (int index = 0; index + 2 < primitive.Indices.Length; index += 3)
            {
                if (IntersectsTriangle(
                        near,
                        direction,
                        primitive.Positions[primitive.Indices[index]],
                        primitive.Positions[primitive.Indices[index + 1]],
                        primitive.Positions[primitive.Indices[index + 2]],
                        out float distance) &&
                    distance < nearest)
                {
                    nearest = distance;
                    result = primitiveIndex;
                }
            }
        }

        return result;
    }

    private static Vector3 Unproject(
        float x,
        float y,
        float z,
        Matrix4x4 inverse)
    {
        Vector4 value = Vector4.Transform(new Vector4(x, y, z, 1), inverse);
        return MathF.Abs(value.W) < 1e-8f
            ? new Vector3(value.X, value.Y, value.Z)
            : new Vector3(value.X, value.Y, value.Z) / value.W;
    }

    private static bool IntersectsSphere(
        Vector3 origin,
        Vector3 direction,
        Vector3 center,
        float radius)
    {
        if (radius <= 0)
        {
            return true;
        }

        Vector3 delta = origin - center;
        float b = Vector3.Dot(delta, direction);
        float c = Vector3.Dot(delta, delta) - (radius * radius);
        return (b * b) - c >= 0;
    }

    private static bool IntersectsTriangle(
        Vector3 origin,
        Vector3 direction,
        Vector3 a,
        Vector3 b,
        Vector3 c,
        out float distance)
    {
        const float epsilon = 1e-7f;
        Vector3 edge1 = b - a;
        Vector3 edge2 = c - a;
        Vector3 p = Vector3.Cross(direction, edge2);
        float determinant = Vector3.Dot(edge1, p);
        if (MathF.Abs(determinant) < epsilon)
        {
            distance = 0;
            return false;
        }

        float inverse = 1f / determinant;
        Vector3 t = origin - a;
        float u = Vector3.Dot(t, p) * inverse;
        if (u < 0 || u > 1)
        {
            distance = 0;
            return false;
        }

        Vector3 q = Vector3.Cross(t, edge1);
        float v = Vector3.Dot(direction, q) * inverse;
        if (v < 0 || u + v > 1)
        {
            distance = 0;
            return false;
        }

        distance = Vector3.Dot(edge2, q) * inverse;
        return distance > epsilon;
    }
}
