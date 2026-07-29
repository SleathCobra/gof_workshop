using System.Numerics;
using Gof2Workshop.Core;
using Gof2Workshop.Formats.Aem;

namespace Gof2Workshop.Scene;

public sealed class AemSceneConverter
{
    private static readonly Vector4[] Palette =
    [
        new(0.20f, 0.62f, 0.86f, 1.0f),
        new(0.93f, 0.48f, 0.20f, 1.0f),
        new(0.38f, 0.76f, 0.38f, 1.0f),
        new(0.72f, 0.45f, 0.83f, 1.0f),
        new(0.92f, 0.76f, 0.24f, 1.0f),
        new(0.22f, 0.76f, 0.72f, 1.0f),
    ];

    public SceneDocument Convert(AemFile file)
    {
        ArgumentNullException.ThrowIfNull(file);
        List<FormatDiagnostic> diagnostics = [.. file.Diagnostics];
        List<ScenePrimitive> primitives = new(file.Submeshes.Count);

        foreach (AemSubmesh submesh in file.Submeshes)
        {
            Vector2[]? normalizedUvs = submesh.TextureCoordinates?
                .Select(uv => new Vector2(uv.X, 1.0f - uv.Y))
                .ToArray();
            string primitiveName = $"submesh_{submesh.Index:D2}";
            primitives.Add(new ScenePrimitive(
                primitiveName,
                [.. submesh.Positions],
                submesh.Normals is null ? null : [.. submesh.Normals],
                normalizedUvs,
                submesh.AuxiliaryFloat4 is null ? null : [.. submesh.AuxiliaryFloat4],
                [.. submesh.Indices],
                new SceneMaterial(
                    $"material_{submesh.Index:D2}",
                    Palette[submesh.Index % Palette.Length]),
                submesh.Pivot,
                submesh.BoundingSphere.Center,
                submesh.BoundingSphere.Radius));

            WindingStatistics winding = AnalyzeWinding(submesh);
            diagnostics.Add(new FormatDiagnostic(
                DiagnosticSeverity.Info,
                "AEM_WINDING",
                $"Submesh {submesh.Index}: {winding.Interpretation} "
                    + $"aligned={winding.AlignedWithNormals}, reversed={winding.ReversedAgainstNormals}, "
                    + $"unclassified={winding.DegenerateOrUnclassified}.",
                submesh.OriginalOffset,
                "scene"));
        }

        SceneBounds bounds = CalculateBounds(primitives);
        IReadOnlyList<SceneAnimationClip> animations = BuildAnimations(file, diagnostics);
        string name = string.IsNullOrWhiteSpace(file.SourcePath)
            ? "aem_scene"
            : Path.GetFileNameWithoutExtension(file.SourcePath);

        return new SceneDocument(
            name,
            "AEM source XYZ, float32, source winding retained.",
            "Right-handed XYZ retained provisionally; glTF Y-up declaration; UV V converted to 1-V.",
            primitives,
            bounds,
            diagnostics,
            animations);
    }

    private static IReadOnlyList<SceneAnimationClip> BuildAnimations(
        AemFile file,
        List<FormatDiagnostic> diagnostics)
    {
        List<SceneAnimationTrack> tracks = [];
        float duration = 0;
        bool hasUvAnimation = false;
        foreach (AemSubmesh submesh in file.Submeshes)
        {
            AemAnimationCurve[] transformCurves = submesh.Animation.Curves
                .Where(curve => IsTransformChannel(curve.Channel) && curve.Keys.Count > 0)
                .ToArray();
            hasUvAnimation |= submesh.Animation.Curves.Any(
                curve => IsUvChannel(curve.Channel) && curve.Keys.Count > 0);
            if (transformCurves.Length == 0)
            {
                continue;
            }

            float[] times = transformCurves
                .SelectMany(curve => curve.Keys)
                .Select(key => key.Time / 1000f)
                .Distinct()
                .Order()
                .ToArray();
            if (times.Length == 0 || times.Any(time => !float.IsFinite(time) || time < 0))
            {
                diagnostics.Add(new FormatDiagnostic(
                    DiagnosticSeverity.Warning,
                    "AEM_ANIMATION_TIME_INVALID",
                    $"Submesh {submesh.Index} contains invalid animation times.",
                    submesh.Animation.OriginalOffset,
                    "animation"));
                continue;
            }

            SceneTransformKey[] keys = times.Select(
                time =>
                {
                    float sourceTime = time * 1000f;
                    Vector3 translation = EvaluateVector(
                        transformCurves,
                        sourceTime,
                        AemAnimationChannel.TranslationXyz,
                        AemAnimationChannel.TranslationX,
                        AemAnimationChannel.TranslationY,
                        AemAnimationChannel.TranslationZ,
                        Vector3.Zero);
                    Vector3 euler = EvaluateVector(
                        transformCurves,
                        sourceTime,
                        AemAnimationChannel.RotationXyz,
                        AemAnimationChannel.RotationX,
                        AemAnimationChannel.RotationY,
                        AemAnimationChannel.RotationZ,
                        Vector3.Zero);
                    Vector3 scale = EvaluateVector(
                        transformCurves,
                        sourceTime,
                        AemAnimationChannel.ScaleXyz,
                        AemAnimationChannel.ScaleX,
                        AemAnimationChannel.ScaleY,
                        AemAnimationChannel.ScaleZ,
                        Vector3.One);
                    Quaternion rotation = Quaternion.Normalize(
                        Quaternion.CreateFromYawPitchRoll(euler.Y, euler.X, euler.Z));
                    return new SceneTransformKey(time, translation, rotation, scale);
                })
                .ToArray();
            duration = Math.Max(duration, times[^1]);
            tracks.Add(new SceneAnimationTrack(
                submesh.Index,
                keys,
                transformCurves.Any(curve => curve.Channel is
                    AemAnimationChannel.TranslationX or
                    AemAnimationChannel.TranslationY or
                    AemAnimationChannel.TranslationZ or
                    AemAnimationChannel.TranslationXyz),
                transformCurves.Any(curve => curve.Channel is
                    AemAnimationChannel.RotationX or
                    AemAnimationChannel.RotationY or
                    AemAnimationChannel.RotationZ or
                    AemAnimationChannel.RotationXyz),
                transformCurves.Any(curve => curve.Channel is
                    AemAnimationChannel.ScaleX or
                    AemAnimationChannel.ScaleY or
                    AemAnimationChannel.ScaleZ or
                    AemAnimationChannel.ScaleXyz)));
        }

        if (tracks.Count == 0)
        {
            return [];
        }

        List<string> limitations = [];
        if (hasUvAnimation)
        {
            limitations.Add("UV animation curves are preserved but not applied to geometry or glTF materials.");
        }

        diagnostics.Add(new FormatDiagnostic(
            DiagnosticSeverity.Info,
            "AEM_ANIMATION_TIME_UNIT",
            "Transform-key times are interpreted as milliseconds and converted to seconds; " +
            "transform components use linear interpolation and Euler XYZ source rotations.",
            0,
            "animation"));
        return
        [
            new SceneAnimationClip(
                "AEM Animation",
                duration,
                tracks,
                "milliseconds",
                limitations),
        ];
    }

    private static Vector3 EvaluateVector(
        IReadOnlyList<AemAnimationCurve> curves,
        float time,
        AemAnimationChannel vectorChannel,
        AemAnimationChannel xChannel,
        AemAnimationChannel yChannel,
        AemAnimationChannel zChannel,
        Vector3 fallback)
    {
        AemAnimationCurve? vector = curves.FirstOrDefault(
            curve => curve.Channel == vectorChannel);
        if (vector is not null)
        {
            return EvaluateCurve(vector, time, fallback);
        }

        return new Vector3(
            EvaluateScalar(curves, xChannel, time, fallback.X),
            EvaluateScalar(curves, yChannel, time, fallback.Y),
            EvaluateScalar(curves, zChannel, time, fallback.Z));
    }

    private static float EvaluateScalar(
        IReadOnlyList<AemAnimationCurve> curves,
        AemAnimationChannel channel,
        float time,
        float fallback)
    {
        AemAnimationCurve? curve = curves.FirstOrDefault(candidate => candidate.Channel == channel);
        return curve is null ? fallback : EvaluateCurve(curve, time, new Vector3(fallback, 0, 0)).X;
    }

    private static Vector3 EvaluateCurve(
        AemAnimationCurve curve,
        float time,
        Vector3 fallback)
    {
        if (curve.Keys.Count == 0)
        {
            return fallback;
        }

        IReadOnlyList<AemAnimationKey> keys = curve.Keys;
        if (time <= keys[0].Time)
        {
            return keys[0].Value;
        }

        for (int index = 1; index < keys.Count; index++)
        {
            if (time > keys[index].Time)
            {
                continue;
            }

            AemAnimationKey previous = keys[index - 1];
            AemAnimationKey next = keys[index];
            float span = next.Time - previous.Time;
            float amount = span <= 1e-7f ? 0 : (time - previous.Time) / span;
            return Vector3.Lerp(previous.Value, next.Value, amount);
        }

        return keys[^1].Value;
    }

    private static bool IsTransformChannel(AemAnimationChannel channel)
    {
        return channel is >= AemAnimationChannel.TranslationX
            and <= AemAnimationChannel.ScaleXyz;
    }

    private static bool IsUvChannel(AemAnimationChannel channel)
    {
        return channel is >= AemAnimationChannel.UvOffsetX
            and <= AemAnimationChannel.UvRotationZ;
    }

    public static WindingStatistics AnalyzeWinding(AemSubmesh submesh)
    {
        ArgumentNullException.ThrowIfNull(submesh);
        long aligned = 0;
        long reversed = 0;
        long unclassified = 0;

        for (int index = 0; index + 2 < submesh.Indices.Length; index += 3)
        {
            int index0 = submesh.Indices[index];
            int index1 = submesh.Indices[index + 1];
            int index2 = submesh.Indices[index + 2];
            Vector3 edge1 = submesh.Positions[index1] - submesh.Positions[index0];
            Vector3 edge2 = submesh.Positions[index2] - submesh.Positions[index0];
            Vector3 geometricNormal = Vector3.Cross(edge1, edge2);

            if (geometricNormal.LengthSquared() < 1e-12f || submesh.Normals is null)
            {
                unclassified++;
                continue;
            }

            Vector3 storedNormal = submesh.Normals[index0]
                + submesh.Normals[index1]
                + submesh.Normals[index2];
            if (storedNormal.LengthSquared() < 1e-12f)
            {
                unclassified++;
                continue;
            }

            float dot = Vector3.Dot(geometricNormal, storedNormal);
            if (dot > 1e-6f)
            {
                aligned++;
            }
            else if (dot < -1e-6f)
            {
                reversed++;
            }
            else
            {
                unclassified++;
            }
        }

        return new WindingStatistics(
            submesh.Indices.Length / 3,
            aligned,
            reversed,
            unclassified);
    }

    private static SceneBounds CalculateBounds(IReadOnlyList<ScenePrimitive> primitives)
    {
        Vector3 minimum = new(float.PositiveInfinity);
        Vector3 maximum = new(float.NegativeInfinity);

        foreach (ScenePrimitive primitive in primitives)
        {
            foreach (Vector3 position in primitive.Positions)
            {
                minimum = Vector3.Min(minimum, position);
                maximum = Vector3.Max(maximum, position);
            }
        }

        if (!float.IsFinite(minimum.X))
        {
            return new SceneBounds(Vector3.Zero, Vector3.Zero);
        }

        return new SceneBounds(minimum, maximum);
    }
}
