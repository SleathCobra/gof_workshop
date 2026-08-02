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
        Dictionary<int, List<string>> nodeTargets = [];
        foreach ((ImportedPrimitive primitive, int index) in scene.Primitives.Select((value, index) => (value, index)))
        {
            if (selected is null || selected.Contains(index))
            {
                string stableId = primitive.StableId ?? $"import-{index}-{Guid.NewGuid():N}";
                AddPrimitive(primitive, stableId: stableId);
                if (primitive.SourceNodeIndex >= 0)
                {
                    if (!nodeTargets.TryGetValue(primitive.SourceNodeIndex, out List<string>? targets))
                    {
                        targets = [];
                        nodeTargets.Add(primitive.SourceNodeIndex, targets);
                    }
                    targets.Add(stableId);
                }
            }
        }

        foreach (ImportedAnimation animation in scene.Animations ?? [])
        {
            foreach (ImportedAnimationTrack track in animation.Tracks)
            {
                if (nodeTargets.TryGetValue(track.TargetNodeIndex, out List<string>? targets))
                {
                    foreach (string stableId in targets)
                    {
                        ImportTrack(stableId, track);
                    }
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

    public void CenterPivot(string stableId) =>
        Update(stableId, "Center pivot", value => value with { Pivot = CalculateBounds(value.Geometry.Positions).Center });

    public void ReverseWinding(string stableId) => Update(stableId, "Reverse winding", value =>
    {
        ushort[] indices = [.. value.Geometry.Indices];
        for (int index = 0; index < indices.Length; index += 3)
        {
            (indices[index + 1], indices[index + 2]) = (indices[index + 2], indices[index + 1]);
        }

        return value with { Geometry = value.Geometry with { Indices = indices } };
    });

    public void NormalizeNormals(string stableId) => Update(stableId, "Normalize normals", value =>
    {
        Vector3[] normals = value.Geometry.Normals is { } present
            ? present.Select(normal => normal.LengthSquared() < 1e-12f ? Vector3.UnitY : Vector3.Normalize(normal)).ToArray()
            : GenerateNormals(value.Geometry.Positions, value.Geometry.Indices);
        return value with { Geometry = value.Geometry with { Normals = normals } };
    });

    public void FlipTextureV(string stableId) => Update(stableId, "Flip texture V", value =>
    {
        if (value.Geometry.TextureCoordinates is null)
        {
            throw new InvalidOperationException($"Submesh '{value.Name}' has no texture coordinates to flip.");
        }
        return value with
        {
            Geometry = value.Geometry with
            {
                TextureCoordinates = value.Geometry.TextureCoordinates
                    .Select(uv => new Vector2(uv.X, 1f - uv.Y))
                    .ToArray(),
            },
        };
    });

    public void RemoveDegenerateTriangles(string stableId) => Update(stableId, "Remove degenerate triangles", value =>
    {
        List<ushort> indices = [];
        for (int index = 0; index < value.Geometry.Indices.Length; index += 3)
        {
            ushort a = value.Geometry.Indices[index];
            ushort b = value.Geometry.Indices[index + 1];
            ushort c = value.Geometry.Indices[index + 2];
            Vector3 cross = Vector3.Cross(
                value.Geometry.Positions[b] - value.Geometry.Positions[a],
                value.Geometry.Positions[c] - value.Geometry.Positions[a]);
            if (a != b && b != c && a != c && cross.LengthSquared() > 1e-12f)
            {
                indices.Add(a);
                indices.Add(b);
                indices.Add(c);
            }
        }

        return value with { Geometry = value.Geometry with { Indices = indices.ToArray() } };
    });

    public void WeldDuplicateVertices(string stableId) => Update(stableId, "Weld duplicate vertices", value =>
    {
        ImportedPrimitive source = value.Geometry;
        Dictionary<VertexKey, ushort> remap = [];
        ushort[] sourceToTarget = new ushort[source.Positions.Length];
        List<Vector3> positions = [];
        List<Vector3>? normals = source.Normals is null ? null : [];
        List<Vector2>? uvs = source.TextureCoordinates is null ? null : [];
        List<Vector4>? colors = source.Colors is null ? null : [];
        for (int index = 0; index < source.Positions.Length; index++)
        {
            VertexKey key = new(
                source.Positions[index],
                source.Normals?[index] ?? default,
                source.TextureCoordinates?[index] ?? default,
                source.Colors?[index] ?? default,
                source.Normals is not null,
                source.TextureCoordinates is not null,
                source.Colors is not null);
            if (!remap.TryGetValue(key, out ushort mapped))
            {
                if (positions.Count > ushort.MaxValue)
                {
                    throw new InvalidDataException("Welded mesh exceeds the AEM 16-bit vertex limit.");
                }
                mapped = checked((ushort)positions.Count);
                remap.Add(key, mapped);
                positions.Add(source.Positions[index]);
                normals?.Add(source.Normals![index]);
                uvs?.Add(source.TextureCoordinates![index]);
                colors?.Add(source.Colors![index]);
            }
            sourceToTarget[index] = mapped;
        }

        ushort[] indices = source.Indices.Select(index => sourceToTarget[index]).ToArray();
        Vector3[] weldedPositions = positions.ToArray();
        return value with
        {
            Geometry = source with
            {
                Positions = weldedPositions,
                Normals = normals?.ToArray(),
                TextureCoordinates = uvs?.ToArray(),
                Colors = colors?.ToArray(),
                Indices = indices,
            },
            Bounds = CalculateBounds(weldedPositions),
        };
    });

    public void TransformGeometry(string stableId, Matrix4x4 transform) => Update(stableId, "Apply geometry transform", value =>
    {
        if (!Matrix4x4.Invert(transform, out Matrix4x4 inverse))
        {
            throw new ArgumentException("Geometry transform must be invertible.", nameof(transform));
        }

        Matrix4x4 normalTransform = Matrix4x4.Transpose(inverse);
        Vector3[] positions = value.Geometry.Positions.Select(position => Vector3.Transform(position, transform)).ToArray();
        Vector3[]? normals = value.Geometry.Normals?.Select(normal =>
        {
            Vector3 changed = Vector3.TransformNormal(normal, normalTransform);
            return changed.LengthSquared() < 1e-12f ? Vector3.UnitY : Vector3.Normalize(changed);
        }).ToArray();
        return value with
        {
            Geometry = value.Geometry with { Positions = positions, Normals = normals },
            Bounds = CalculateBounds(positions),
        };
    });

    public void ImportAnimationFromAem(
        AemSubmesh source,
        string targetStableId,
        IReadOnlyCollection<AemAnimationChannel>? channels = null,
        bool merge = false)
    {
        ArgumentNullException.ThrowIfNull(source);
        HashSet<AemAnimationChannel>? selected = channels?.ToHashSet();
        AemAuthoringTrack[] incoming = FromAnimation(source.Animation)
            .Where(track => selected is null || selected.Contains(track.Channel))
            .ToArray();
        _ = Find(targetStableId);
        Update(targetStableId, "Import AEM animation", value =>
        {
            IReadOnlyList<AemAuthoringTrack> tracks = merge
                ? value.AnimationTracks.Select(existing =>
                    {
                        AemAuthoringTrack? addition = incoming.FirstOrDefault(candidate => candidate.Channel == existing.Channel);
                        return addition is null
                            ? existing
                            : existing with
                            {
                                Keys = existing.Keys.Concat(addition.Keys)
                                    .GroupBy(key => key.Time)
                                    .Select(group => group.Last())
                                    .OrderBy(key => key.Time)
                                    .ToArray(),
                            };
                    }).Concat(incoming.Where(candidate => value.AnimationTracks.All(existing => existing.Channel != candidate.Channel))).ToArray()
                : value.AnimationTracks
                    .Where(track => selected is not null && !selected.Contains(track.Channel))
                    .Concat(incoming)
                    .ToArray();
            return value with { AnimationTracks = tracks };
        });
    }

    public void AssignMaterial(string stableId, string? materialAsset) =>
        Update(stableId, "Assign material", value => value with { MaterialAsset = materialAsset });

    public void SetHidden(string stableId, bool hidden) =>
        Update(stableId, hidden ? "Hide submesh" : "Show submesh", value => value with { Hidden = hidden });

    public void SetLocked(string stableId, bool locked) =>
        Update(stableId, locked ? "Lock submesh" : "Unlock submesh", value => value with { Locked = locked });

    public void ClearAnimation(string stableId) =>
        Update(stableId, "Clear transform animation", value => value with { AnimationTracks = [] });

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

    public void UpdateKey(string stableId, AemAnimationChannel channel, int keyIndex, AemAuthoringKey key)
    {
        AemAuthoringSubmesh submesh = Find(stableId);
        AemAuthoringTrack track = submesh.AnimationTracks.FirstOrDefault(value => value.Channel == channel)
            ?? throw new KeyNotFoundException($"Submesh '{submesh.Name}' has no {channel} track.");
        if ((uint)keyIndex >= (uint)track.Keys.Count)
        {
            throw new ArgumentOutOfRangeException(nameof(keyIndex));
        }
        if (track.Keys.Where((_, index) => index != keyIndex).Any(value => Math.Abs(value.Time - key.Time) < 0.000001f))
        {
            throw new InvalidOperationException($"{channel} already contains a key at {key.Time:F6} seconds.");
        }

        ReplaceTrack(stableId, channel, track.Keys.Select((value, index) => index == keyIndex ? key : value));
    }

    public void DuplicateKey(string stableId, AemAnimationChannel channel, int keyIndex, float timeOffset = 1f / 30f)
    {
        AemAuthoringSubmesh submesh = Find(stableId);
        AemAuthoringTrack track = submesh.AnimationTracks.FirstOrDefault(value => value.Channel == channel)
            ?? throw new KeyNotFoundException($"Submesh '{submesh.Name}' has no {channel} track.");
        if ((uint)keyIndex >= (uint)track.Keys.Count)
        {
            throw new ArgumentOutOfRangeException(nameof(keyIndex));
        }
        AemAuthoringKey source = track.Keys[keyIndex];
        AddKey(stableId, channel, source with { Time = source.Time + timeOffset });
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

    private static Vector3[] GenerateNormals(Vector3[] positions, ushort[] indices)
    {
        Vector3[] normals = new Vector3[positions.Length];
        for (int index = 0; index < indices.Length; index += 3)
        {
            ushort a = indices[index];
            ushort b = indices[index + 1];
            ushort c = indices[index + 2];
            Vector3 normal = Vector3.Cross(positions[b] - positions[a], positions[c] - positions[a]);
            if (normal.LengthSquared() > 1e-12f)
            {
                normals[a] += normal;
                normals[b] += normal;
                normals[c] += normal;
            }
        }

        for (int index = 0; index < normals.Length; index++)
        {
            normals[index] = normals[index].LengthSquared() < 1e-12f ? Vector3.UnitY : Vector3.Normalize(normals[index]);
        }

        return normals;
    }

    private readonly record struct VertexKey(
        Vector3 Position,
        Vector3 Normal,
        Vector2 Uv,
        Vector4 Color,
        bool HasNormal,
        bool HasUv,
        bool HasColor);
}
