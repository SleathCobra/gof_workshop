using System.Numerics;
using Gof2Workshop.Core;
using Gof2Workshop.Formats.Aem;
using Gof2Workshop.Scene;

namespace Gof2Workshop.Import;

public sealed record AemAuthoringKey(float Time, float Value);

public sealed record AemAuthoringTrack(
    AemAnimationChannel Channel,
    IReadOnlyList<AemAuthoringKey> Keys);

public sealed record AemAuthoringSubmesh(
    string StableId,
    string Name,
    ImportedPrimitive Geometry,
    Vector3 Pivot,
    AemBoundingSphere Bounds,
    IReadOnlyList<AemAuthoringTrack> AnimationTracks,
    string? MaterialAsset,
    bool Hidden = false,
    bool Locked = false);

public sealed record AemAuthoringProjectSnapshot(
    string Name,
    string TargetProfile,
    AemVersion Version,
    IReadOnlyList<AemAuthoringSubmesh> Submeshes);

public sealed record AemAuthoringOperation(string Description, AemAuthoringProjectSnapshot Before, AemAuthoringProjectSnapshot After);

/// <summary>
/// Mutable authoring coordinator over immutable project snapshots. Raw parser
/// objects remain outside the editing surface; every build converts through a
/// validated target AEM and reparses it before returning bytes.
/// </summary>
public sealed class AemAuthoringProject
{
    private readonly List<AemAuthoringOperation> history = [];
    private int appliedOperations;
    private AemAuthoringProjectSnapshot snapshot;

    public AemAuthoringProject(
        string name,
        AemVersion version = AemVersion.V4,
        string targetProfile = "gof2-pc-1x")
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        if (version is not (AemVersion.V4 or AemVersion.V5))
        {
            throw new NotSupportedException("Authoring targets are currently limited to validated GOF2 PC AEM v4/v5.");
        }

        if (!string.Equals(targetProfile, ProfileCatalog.Pc1X.Id, StringComparison.Ordinal))
        {
            throw new NotSupportedException("Cross-platform AEM writing remains disabled until unchanged real-corpus reconstruction is validated.");
        }

        snapshot = new AemAuthoringProjectSnapshot(name, targetProfile, version, []);
    }

    public AemAuthoringProjectSnapshot Current => snapshot;

    public IReadOnlyList<AemAuthoringOperation> AppliedOperations => history.Take(appliedOperations).ToArray();

    public bool CanUndo => appliedOperations > 0;

    public bool CanRedo => appliedOperations < history.Count;

    public void AddPrimitive(ImportedPrimitive primitive, Vector3? pivot = null, string? stableId = null)
    {
        ArgumentNullException.ThrowIfNull(primitive);
        Vector3 chosenPivot = pivot ?? Vector3.Zero;
        AemAuthoringSubmesh submesh = new(
            stableId ?? $"workshop-{Guid.NewGuid():N}",
            primitive.Name,
            Clone(primitive),
            chosenPivot,
            CalculateBounds(primitive.Positions),
            [],
            primitive.MaterialName);
        Apply("Add submesh " + primitive.Name, state => state with { Submeshes = [.. state.Submeshes, submesh] });
    }

    public void AddImportedScene(ImportedScene scene, IReadOnlyCollection<int>? primitiveIndices = null)
    {
        ArgumentNullException.ThrowIfNull(scene);
        HashSet<int>? selected = primitiveIndices?.ToHashSet();
        Dictionary<int, string> nodeTargets = [];
        foreach ((ImportedPrimitive primitive, int index) in scene.Primitives.Select((value, index) => (value, index)))
        {
            if (selected is null || selected.Contains(index))
            {
                string stableId = primitive.StableId ?? $"import-{index}-{Guid.NewGuid():N}";
                AddPrimitive(primitive, stableId: stableId);
                if (primitive.SourceNodeIndex >= 0)
                {
                    nodeTargets.TryAdd(primitive.SourceNodeIndex, stableId);
                }
            }
        }

        foreach (ImportedAnimation animation in scene.Animations ?? [])
        {
            foreach (ImportedAnimationTrack track in animation.Tracks)
            {
                if (nodeTargets.TryGetValue(track.TargetNodeIndex, out string? stableId))
                {
                    ImportTrack(stableId, track);
                }
            }
        }
    }

    public void AddFromAem(AemFile source, IReadOnlyCollection<int>? submeshIndices = null)
    {
        ArgumentNullException.ThrowIfNull(source);
        HashSet<int>? selected = submeshIndices?.ToHashSet();
        foreach (AemSubmesh value in source.Submeshes)
        {
            if (selected is not null && !selected.Contains(value.Index))
            {
                continue;
            }

            Vector2[]? importedUvs = value.TextureCoordinates?
                .Select(uv => new Vector2(uv.X, 1f - uv.Y))
                .ToArray();
            ImportedPrimitive primitive = new(
                $"submesh_{value.Index:D2}",
                [.. value.Positions],
                value.Normals is null ? null : [.. value.Normals],
                importedUvs,
                value.AuxiliaryFloat4 is null ? null : [.. value.AuxiliaryFloat4],
                [.. value.Indices],
                null);
            AemAuthoringSubmesh submesh = new(
                $"aem-{value.Index}-{Guid.NewGuid():N}",
                primitive.Name,
                primitive,
                value.Pivot,
                value.BoundingSphere,
                FromAnimation(value.Animation),
                null);
            Apply("Import AEM submesh " + value.Index, state => state with { Submeshes = [.. state.Submeshes, submesh] });
        }
    }

    public void Duplicate(string stableId)
    {
        AemAuthoringSubmesh source = Find(stableId);
        AemAuthoringSubmesh copy = source with
        {
            StableId = $"workshop-{Guid.NewGuid():N}",
            Name = source.Name + " Copy",
            Geometry = Clone(source.Geometry),
            AnimationTracks = source.AnimationTracks.Select(Clone).ToArray(),
        };
        Apply("Duplicate " + source.Name, state => state with { Submeshes = [.. state.Submeshes, copy] });
    }

    public void Remove(string stableId)
    {
        AemAuthoringSubmesh removed = Find(stableId);
        Apply("Remove " + removed.Name, state => state with
        {
            Submeshes = state.Submeshes.Where(value => value.StableId != stableId).ToArray(),
        });
    }

    public void Move(string stableId, int targetIndex)
    {
        AemAuthoringSubmesh moved = Find(stableId);
        if ((uint)targetIndex >= snapshot.Submeshes.Count)
        {
            throw new ArgumentOutOfRangeException(nameof(targetIndex));
        }

        Apply("Reorder " + moved.Name, state =>
        {
            List<AemAuthoringSubmesh> values = [.. state.Submeshes];
            values.RemoveAll(value => value.StableId == stableId);
            values.Insert(targetIndex, moved);
            return state with { Submeshes = values };
        });
    }

    public void Rename(string stableId, string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        Update(stableId, "Rename submesh", value => value with { Name = name, Geometry = value.Geometry with { Name = name } });
    }

    public void ReplaceGeometry(string stableId, ImportedPrimitive primitive)
    {
        ArgumentNullException.ThrowIfNull(primitive);
        Update(stableId, "Replace geometry", value => value with
        {
            Geometry = Clone(primitive),
            Bounds = CalculateBounds(primitive.Positions),
        });
    }

    public void SetPivot(string stableId, Vector3 pivot) =>
        Update(stableId, "Change pivot", value => value with { Pivot = pivot });

    public void RecalculateBounds(string stableId) =>
        Update(stableId, "Recalculate bounds", value => value with { Bounds = CalculateBounds(value.Geometry.Positions) });

    public void AssignMaterial(string stableId, string? materialAsset) =>
        Update(stableId, "Assign material", value => value with { MaterialAsset = materialAsset });

    public void SetHidden(string stableId, bool hidden) =>
        Update(stableId, hidden ? "Hide submesh" : "Show submesh", value => value with { Hidden = hidden });

    public void SetLocked(string stableId, bool locked) =>
        Update(stableId, locked ? "Lock submesh" : "Unlock submesh", value => value with { Locked = locked });

    public void AddKey(string stableId, AemAnimationChannel channel, AemAuthoringKey key)
    {
        AemAuthoringSubmesh submesh = Find(stableId);
        AemAuthoringTrack? existing = submesh.AnimationTracks.FirstOrDefault(track => track.Channel == channel);
        if (existing?.Keys.Any(value => Math.Abs(value.Time - key.Time) < 0.000001f) == true)
        {
            throw new InvalidOperationException($"{channel} already contains a key at {key.Time:F6} seconds.");
        }

        ReplaceTrack(stableId, channel, [.. (existing?.Keys ?? []), key]);
    }

    public void DeleteKey(string stableId, AemAnimationChannel channel, int keyIndex)
    {
        AemAuthoringSubmesh submesh = Find(stableId);
        AemAuthoringTrack track = submesh.AnimationTracks.FirstOrDefault(value => value.Channel == channel)
            ?? throw new KeyNotFoundException($"Submesh '{submesh.Name}' has no {channel} track.");
        if ((uint)keyIndex >= (uint)track.Keys.Count)
        {
            throw new ArgumentOutOfRangeException(nameof(keyIndex));
        }

        ReplaceTrack(stableId, channel, track.Keys.Where((_, index) => index != keyIndex));
    }

    public void ReplaceTrack(string stableId, AemAnimationChannel channel, IEnumerable<AemAuthoringKey> keys)
    {
        if (!IsSupportedTransformChannel(channel))
        {
            throw new NotSupportedException($"Channel {channel} is preservation-only because its semantics are unresolved.");
        }

        AemAuthoringKey[] ordered = keys.OrderBy(key => key.Time).ToArray();
        if (ordered.Any(key => !float.IsFinite(key.Time) || !float.IsFinite(key.Value) || key.Time < 0))
        {
            throw new InvalidDataException("Animation keys require finite, non-negative times and finite values.");
        }

        Update(stableId, "Replace animation track " + channel, value => value with
        {
            AnimationTracks = [
                .. value.AnimationTracks.Where(track => track.Channel != channel),
                new AemAuthoringTrack(channel, ordered),
            ],
        });
    }

    public bool Undo()
    {
        if (!CanUndo)
        {
            return false;
        }

        appliedOperations--;
        snapshot = history[appliedOperations].Before;
        return true;
    }

    public bool Redo()
    {
        if (!CanRedo)
        {
            return false;
        }

        snapshot = history[appliedOperations].After;
        appliedOperations++;
        return true;
    }

    public AemAuthoringResult Build(CancellationToken cancellationToken = default) =>
        new AemAuthoringService().Author(this, cancellationToken);

    private void Update(string stableId, string description, Func<AemAuthoringSubmesh, AemAuthoringSubmesh> transform)
    {
        _ = Find(stableId);
        Apply(description, state => state with
        {
            Submeshes = state.Submeshes.Select(value => value.StableId == stableId ? transform(value) : value).ToArray(),
        });
    }

    private void Apply(string description, Func<AemAuthoringProjectSnapshot, AemAuthoringProjectSnapshot> operation)
    {
        AemAuthoringProjectSnapshot before = snapshot;
        AemAuthoringProjectSnapshot after = operation(before);
        if (appliedOperations < history.Count)
        {
            history.RemoveRange(appliedOperations, history.Count - appliedOperations);
        }

        history.Add(new AemAuthoringOperation(description, before, after));
        appliedOperations++;
        snapshot = after;
    }

    private AemAuthoringSubmesh Find(string stableId) => snapshot.Submeshes.FirstOrDefault(
        value => string.Equals(value.StableId, stableId, StringComparison.Ordinal))
        ?? throw new KeyNotFoundException($"Authoring submesh '{stableId}' does not exist.");

    internal static AemAnimation ToAnimation(AemAuthoringSubmesh submesh, AemVersion version)
    {
        AemAnimationChannel[] scalarChannels =
        [
            AemAnimationChannel.TranslationX,
            AemAnimationChannel.TranslationY,
            AemAnimationChannel.TranslationZ,
            AemAnimationChannel.RotationX,
            AemAnimationChannel.RotationY,
            AemAnimationChannel.RotationZ,
            AemAnimationChannel.ScaleX,
            AemAnimationChannel.ScaleY,
            AemAnimationChannel.ScaleZ,
        ];
        AemAnimationCurve[] curves = scalarChannels
            .Select(channel =>
            {
                AemAuthoringTrack? track = submesh.AnimationTracks.FirstOrDefault(value => value.Channel == channel);
                return new AemAnimationCurve(
                    channel,
                    track?.Keys.Select(key => new AemAnimationKey(key.Time * 1000f, new Vector3(key.Value, 0, 0), 1)).ToArray() ?? []);
            })
            .ToArray();
        return new AemAnimation(
            0,
            0,
            0,
            -1,
            version == AemVersion.V5 ? (short)0 : null,
            0,
            curves,
            [],
            0);
    }

    private static AemAuthoringTrack[] FromAnimation(AemAnimation animation) => animation.Curves
        .Where(curve => IsSupportedTransformChannel(curve.Channel))
        .Select(curve => new AemAuthoringTrack(
            curve.Channel,
            curve.Keys.Select(key => new AemAuthoringKey(key.Time / 1000f, key.Value.X)).ToArray()))
        .ToArray();

    private void ImportTrack(string stableId, ImportedAnimationTrack track)
    {
        if (track.Translations.Count > 0)
        {
            ReplaceTrack(stableId, AemAnimationChannel.TranslationX,
                track.Translations.Select(key => new AemAuthoringKey(key.TimeSeconds, key.Value.X)));
            ReplaceTrack(stableId, AemAnimationChannel.TranslationY,
                track.Translations.Select(key => new AemAuthoringKey(key.TimeSeconds, -key.Value.Z)));
            ReplaceTrack(stableId, AemAnimationChannel.TranslationZ,
                track.Translations.Select(key => new AemAuthoringKey(key.TimeSeconds, key.Value.Y)));
        }

        if (track.Scales.Count > 0)
        {
            ReplaceTrack(stableId, AemAnimationChannel.ScaleX,
                track.Scales.Select(key => new AemAuthoringKey(key.TimeSeconds, key.Value.X)));
            ReplaceTrack(stableId, AemAnimationChannel.ScaleY,
                track.Scales.Select(key => new AemAuthoringKey(key.TimeSeconds, key.Value.Y)));
            ReplaceTrack(stableId, AemAnimationChannel.ScaleZ,
                track.Scales.Select(key => new AemAuthoringKey(key.TimeSeconds, key.Value.Z)));
        }

        if (track.Rotations.Count > 0)
        {
            (float Time, Vector3 Euler)[] values = track.Rotations
                .Select(key => (key.TimeSeconds, EngineEulerFromQuaternion(key.Value)))
                .ToArray();
            ReplaceTrack(stableId, AemAnimationChannel.RotationX,
                values.Select(key => new AemAuthoringKey(key.Time, key.Euler.X)));
            ReplaceTrack(stableId, AemAnimationChannel.RotationY,
                values.Select(key => new AemAuthoringKey(key.Time, key.Euler.Y)));
            ReplaceTrack(stableId, AemAnimationChannel.RotationZ,
                values.Select(key => new AemAuthoringKey(key.Time, key.Euler.Z)));
        }
    }

    private static Vector3 EngineEulerFromQuaternion(Quaternion source)
    {
        Quaternion value = Quaternion.Normalize(source);
        float x = MathF.Atan2(
            2f * ((value.W * value.X) + (value.Y * value.Z)),
            1f - (2f * ((value.X * value.X) + (value.Y * value.Y))));
        float standardY = MathF.Asin(Math.Clamp(
            2f * ((value.W * value.Y) - (value.Z * value.X)),
            -1f,
            1f));
        float z = MathF.Atan2(
            2f * ((value.W * value.Z) + (value.X * value.Y)),
            1f - (2f * ((value.Y * value.Y) + (value.Z * value.Z))));
        return new Vector3(x, -standardY, z);
    }

    private static bool IsSupportedTransformChannel(AemAnimationChannel channel) => channel is
        AemAnimationChannel.TranslationX or AemAnimationChannel.TranslationY or AemAnimationChannel.TranslationZ or
        AemAnimationChannel.RotationX or AemAnimationChannel.RotationY or AemAnimationChannel.RotationZ or
        AemAnimationChannel.ScaleX or AemAnimationChannel.ScaleY or AemAnimationChannel.ScaleZ;

    private static AemAuthoringTrack Clone(AemAuthoringTrack track) => track with { Keys = [.. track.Keys] };

    private static ImportedPrimitive Clone(ImportedPrimitive primitive) => primitive with
    {
        Positions = [.. primitive.Positions],
        Normals = primitive.Normals is null ? null : [.. primitive.Normals],
        TextureCoordinates = primitive.TextureCoordinates is null ? null : [.. primitive.TextureCoordinates],
        Colors = primitive.Colors is null ? null : [.. primitive.Colors],
        Indices = [.. primitive.Indices],
    };

    private static AemBoundingSphere CalculateBounds(Vector3[] positions)
    {
        if (positions.Length == 0)
        {
            return new AemBoundingSphere(Vector3.Zero, 0);
        }

        Vector3 minimum = new(float.PositiveInfinity);
        Vector3 maximum = new(float.NegativeInfinity);
        foreach (Vector3 position in positions)
        {
            minimum = Vector3.Min(minimum, position);
            maximum = Vector3.Max(maximum, position);
        }

        Vector3 center = (minimum + maximum) * 0.5f;
        return new AemBoundingSphere(center, positions.Max(position => Vector3.Distance(position, center)));
    }
}
