using System.Runtime.InteropServices.JavaScript;
using System.Runtime.Versioning;
using System.Text.Json;
using System.Text.Json.Serialization;
using Gof2Workshop.Core;
using Gof2Workshop.Scene;

namespace Gof2Workshop.Browser;

internal sealed class BrowserWebGlSceneRenderer : IDisposable
{
    private bool initialized;
    private bool disposed;

    public string Initialize()
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        string result = BrowserWebGlInterop.Initialize();
        initialized = result.StartsWith("WebGL 2", StringComparison.Ordinal);
        return result;
    }

    public string Show(
        BrowserAssetItem model,
        RgbaImage? texture,
        BrowserRenderOptions options)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        ArgumentNullException.ThrowIfNull(model);
        if (model.Scene is null)
        {
            throw new ArgumentException("The selected browser asset has no normalized scene.", nameof(model));
        }

        if (!initialized)
        {
            string init = Initialize();
            if (!initialized)
            {
                return init;
            }
        }

        BrowserScenePayload payload = BrowserScenePayload.From(model.Scene);
        string sceneJson = JsonSerializer.Serialize(payload, BrowserWebGlJsonContext.Default.BrowserScenePayload);
        string result = BrowserWebGlInterop.LoadScene(sceneJson);
        if (texture is not null)
        {
            const int maximumTextureDimension = 8192;
            if (texture.Width > maximumTextureDimension || texture.Height > maximumTextureDimension)
            {
                result += $"; texture {texture.Width}x{texture.Height} exceeds the browser upload policy";
            }
            else
            {
                string rgba = Convert.ToBase64String(texture.ReadOnlyPixelBytes);
                result += "; " + BrowserWebGlInterop.SetTexture(texture.Width, texture.Height, rgba);
            }
        }
        else
        {
            BrowserWebGlInterop.ClearTexture();
        }

        ApplyOptions(options);
        BrowserWebGlInterop.SetVisible(true);
        return result;
    }

    public void ApplyOptions(BrowserRenderOptions options)
    {
        if (!initialized || disposed)
        {
            return;
        }

        BrowserWebGlInterop.SetOptions(
            options.Lit,
            options.Wireframe,
            options.Bounds,
            options.Pivots,
            options.CullBackFaces,
            options.Orthographic,
            options.LinearFiltering,
            options.IsolateSelection);
    }

    public void SetAnimation(bool playing, float timeSeconds)
    {
        if (initialized && !disposed)
        {
            BrowserWebGlInterop.SetAnimation(playing, Math.Max(0, timeSeconds));
        }
    }

    public void FrameAll()
    {
        if (initialized && !disposed)
        {
            BrowserWebGlInterop.FrameAll();
        }
    }

    public void FrameSelected()
    {
        if (initialized && !disposed)
        {
            BrowserWebGlInterop.FrameSelected();
        }
    }

    public void Hide()
    {
        if (initialized && !disposed)
        {
            BrowserWebGlInterop.SetVisible(false);
        }
    }

    public string Diagnostics => initialized && !disposed
        ? BrowserWebGlInterop.GetDiagnostics()
        : "WebGL renderer is not initialized.";

    public static string? QueryParameter(string name) => BrowserWebGlInterop.GetQueryParameter(name);

    public void Dispose()
    {
        if (disposed)
        {
            return;
        }

        disposed = true;
        if (initialized)
        {
            BrowserWebGlInterop.DisposeRenderer();
            initialized = false;
        }
    }
}

internal sealed record BrowserRenderOptions(
    bool Lit = true,
    bool Wireframe = false,
    bool Bounds = true,
    bool Pivots = true,
    bool CullBackFaces = false,
    bool Orthographic = false,
    bool LinearFiltering = true,
    bool IsolateSelection = false);

internal sealed record BrowserScenePayload(
    string Name,
    float[] BoundsMinimum,
    float[] BoundsMaximum,
    BrowserPrimitivePayload[] Primitives,
    BrowserAnimationPayload[] Animations)
{
    public static BrowserScenePayload From(SceneDocument scene) => new(
        scene.Name,
        [scene.Bounds.Minimum.X, scene.Bounds.Minimum.Y, scene.Bounds.Minimum.Z],
        [scene.Bounds.Maximum.X, scene.Bounds.Maximum.Y, scene.Bounds.Maximum.Z],
        scene.Primitives.Select((primitive, index) => BrowserPrimitivePayload.From(primitive, index)).ToArray(),
        scene.Animations.Select(BrowserAnimationPayload.From).ToArray());
}

internal sealed record BrowserPrimitivePayload(
    int Id,
    string Name,
    float[] Positions,
    float[] Normals,
    float[] Uvs,
    ushort[] Indices,
    float[] Color,
    float[] Pivot,
    float[] Sphere)
{
    public static BrowserPrimitivePayload From(ScenePrimitive primitive, int index) => new(
        index,
        primitive.Name,
        primitive.Positions.SelectMany(value => new[] { value.X, value.Y, value.Z }).ToArray(),
        (primitive.Normals ?? []).SelectMany(value => new[] { value.X, value.Y, value.Z }).ToArray(),
        (primitive.TextureCoordinates ?? []).SelectMany(value => new[] { value.X, value.Y }).ToArray(),
        primitive.Indices,
        [
            primitive.Material.BaseColor.X,
            primitive.Material.BaseColor.Y,
            primitive.Material.BaseColor.Z,
            primitive.Material.BaseColor.W,
        ],
        [primitive.SourcePivot.X, primitive.SourcePivot.Y, primitive.SourcePivot.Z],
        [
            primitive.BoundingSphereCenter.X,
            primitive.BoundingSphereCenter.Y,
            primitive.BoundingSphereCenter.Z,
            primitive.BoundingSphereRadius,
        ]);
}

internal sealed record BrowserAnimationPayload(
    string Name,
    float Duration,
    BrowserTrackPayload[] Tracks)
{
    public static BrowserAnimationPayload From(SceneAnimationClip clip) => new(
        clip.Name,
        clip.DurationSeconds,
        clip.Tracks.Select(BrowserTrackPayload.From).ToArray());
}

internal sealed record BrowserTrackPayload(int Primitive, BrowserKeyPayload[] Keys)
{
    public static BrowserTrackPayload From(SceneAnimationTrack track) => new(
        track.PrimitiveIndex,
        track.Keys.Select(BrowserKeyPayload.From).ToArray());
}

internal sealed record BrowserKeyPayload(float Time, float[] Translation, float[] Rotation, float[] Scale)
{
    public static BrowserKeyPayload From(SceneTransformKey key) => new(
        key.TimeSeconds,
        [key.Translation.X, key.Translation.Y, key.Translation.Z],
        [key.Rotation.X, key.Rotation.Y, key.Rotation.Z, key.Rotation.W],
        [key.Scale.X, key.Scale.Y, key.Scale.Z]);
}

[JsonSerializable(typeof(BrowserScenePayload))]
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
internal sealed partial class BrowserWebGlJsonContext : JsonSerializerContext;

[SupportedOSPlatform("browser")]
internal static partial class BrowserWebGlInterop
{
    [JSImport("initialize", "./workshopWebGl.js")]
    internal static partial string Initialize();

    [JSImport("loadScene", "./workshopWebGl.js")]
    internal static partial string LoadScene(string sceneJson);

    [JSImport("setTexture", "./workshopWebGl.js")]
    internal static partial string SetTexture(int width, int height, string rgbaBase64);

    [JSImport("clearTexture", "./workshopWebGl.js")]
    internal static partial void ClearTexture();

    [JSImport("setOptions", "./workshopWebGl.js")]
    internal static partial void SetOptions(
        bool lit,
        bool wireframe,
        bool bounds,
        bool pivots,
        bool cullBackFaces,
        bool orthographic,
        bool linearFiltering,
        bool isolateSelection);

    [JSImport("setAnimation", "./workshopWebGl.js")]
    internal static partial void SetAnimation(bool playing, float timeSeconds);

    [JSImport("frameAll", "./workshopWebGl.js")]
    internal static partial void FrameAll();

    [JSImport("frameSelected", "./workshopWebGl.js")]
    internal static partial void FrameSelected();

    [JSImport("setVisible", "./workshopWebGl.js")]
    internal static partial void SetVisible(bool visible);

    [JSImport("getDiagnostics", "./workshopWebGl.js")]
    internal static partial string GetDiagnostics();

    [JSImport("getQueryParameter", "./workshopWebGl.js")]
    internal static partial string? GetQueryParameter(string name);

    [JSImport("setSmokeStatus", "./workshopWebGl.js")]
    internal static partial void SetSmokeStatus(string value);

    [JSImport("disposeRenderer", "./workshopWebGl.js")]
    internal static partial void DisposeRenderer();
}
