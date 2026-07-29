using System.Collections.ObjectModel;
using System.Text.RegularExpressions;
using Gof2Workshop.Core;

namespace Gof2Workshop.Workbench;

public enum AssetRelationshipSource
{
    WorkspaceOverride,
    ExactFileStem,
    NamingConvention,
    NeighboringCategory,
    Unresolved,
}

public enum AssetRelationshipConfidence
{
    None,
    Low,
    Medium,
    High,
    Confirmed,
}

public sealed record AssetRelationshipCandidate(
    IndexedAsset Asset,
    AssetRelationshipSource Source,
    AssetRelationshipConfidence Confidence,
    string Reason,
    int Score);

public sealed record AssetRelationshipResolution(
    IndexedAsset SourceAsset,
    int PrimitiveIndex,
    AssetRelationshipSource Source,
    AssetRelationshipConfidence Confidence,
    IndexedAsset? SelectedAsset,
    IReadOnlyList<AssetRelationshipCandidate> Candidates,
    string Reason,
    IReadOnlyList<string> Warnings);

public interface IAssetRelationshipService
{
    public void UpdateAssets(IEnumerable<IndexedAsset> assets);

    public AssetRelationshipResolution ResolveMaterial(
        WorkspaceDefinition workspace,
        IndexedAsset aemAsset,
        int primitiveIndex);

    public void SetMaterialOverride(
        WorkspaceDefinition workspace,
        IndexedAsset aemAsset,
        int primitiveIndex,
        IndexedAsset aeiAsset);

    public void ClearMaterialOverride(
        WorkspaceDefinition workspace,
        IndexedAsset aemAsset,
        int primitiveIndex);

    public void DisableMaterial(
        WorkspaceDefinition workspace,
        IndexedAsset aemAsset,
        int primitiveIndex);
}

public sealed partial class AssetRelationshipService : IAssetRelationshipService
{
    private readonly object gate = new();
    private IndexedAsset[] textureAssets = [];

    public void UpdateAssets(IEnumerable<IndexedAsset> assets)
    {
        ArgumentNullException.ThrowIfNull(assets);
        IndexedAsset[] snapshot = assets
            .Where(asset => asset.Kind == AssetKind.Aei)
            .OrderBy(asset => asset.RelativePath, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        lock (gate)
        {
            textureAssets = snapshot;
        }
    }

    public AssetRelationshipResolution ResolveMaterial(
        WorkspaceDefinition workspace,
        IndexedAsset aemAsset,
        int primitiveIndex)
    {
        ArgumentNullException.ThrowIfNull(workspace);
        ArgumentNullException.ThrowIfNull(aemAsset);
        ArgumentOutOfRangeException.ThrowIfNegative(primitiveIndex);
        if (aemAsset.Kind != AssetKind.Aem)
        {
            throw new ArgumentException("Material resolution requires an AEM source asset.", nameof(aemAsset));
        }

        IndexedAsset[] textures;
        lock (gate)
        {
            textures = textureAssets;
        }

        string overrideKey = CreateOverrideKey(aemAsset, primitiveIndex);
        if (workspace.MaterialOverrides.TryGetValue(overrideKey, out string? configured))
        {
            if (configured.Equals("!none", StringComparison.Ordinal))
            {
                return new AssetRelationshipResolution(
                    aemAsset,
                    primitiveIndex,
                    AssetRelationshipSource.WorkspaceOverride,
                    AssetRelationshipConfidence.Confirmed,
                    null,
                    [],
                    "The material was explicitly cleared in this workspace.",
                    []);
            }

            IndexedAsset? selected = FindConfigured(textures, configured, workspace);
            if (selected is not null)
            {
                AssetRelationshipCandidate candidate = new(
                    selected,
                    AssetRelationshipSource.WorkspaceOverride,
                    AssetRelationshipConfidence.Confirmed,
                    "Workspace-level manual material assignment.",
                    10_000);
                return new AssetRelationshipResolution(
                    aemAsset,
                    primitiveIndex,
                    candidate.Source,
                    candidate.Confidence,
                    selected,
                    [candidate],
                    candidate.Reason,
                    []);
            }
        }

        string originalStem = Path.GetFileNameWithoutExtension(aemAsset.FileName);
        string familyStem = NormalizeMeshStem(originalStem);
        string pluralFamily = NormalizePluralFamily(familyStem);
        List<AssetRelationshipCandidate> candidates = [];
        foreach (IndexedAsset texture in textures)
        {
            string textureStem = Path.GetFileNameWithoutExtension(texture.FileName);
            int score = 0;
            AssetRelationshipSource source = AssetRelationshipSource.Unresolved;
            AssetRelationshipConfidence confidence = AssetRelationshipConfidence.None;
            string reason = string.Empty;

            if (textureStem.Equals(originalStem, StringComparison.OrdinalIgnoreCase))
            {
                score = 900;
                source = AssetRelationshipSource.ExactFileStem;
                confidence = AssetRelationshipConfidence.High;
                reason = "The AEI and AEM file stems match exactly.";
            }
            else if (textureStem.Equals(
                         familyStem + "_diffuse",
                         StringComparison.OrdinalIgnoreCase))
            {
                score = 850;
                source = AssetRelationshipSource.NamingConvention;
                confidence = AssetRelationshipConfidence.High;
                reason = "The diffuse texture matches the mesh family after a known LOD/effect suffix is removed.";
            }
            else if (!pluralFamily.Equals(familyStem, StringComparison.OrdinalIgnoreCase) &&
                     textureStem.Equals(
                         pluralFamily + "_diffuse",
                         StringComparison.OrdinalIgnoreCase))
            {
                score = 760;
                source = AssetRelationshipSource.NamingConvention;
                confidence = AssetRelationshipConfidence.Medium;
                reason = "The diffuse texture matches the shared pluralized asset family.";
            }
            else if (textureStem.StartsWith(
                         familyStem + "_",
                         StringComparison.OrdinalIgnoreCase))
            {
                score = textureStem.EndsWith("_diffuse", StringComparison.OrdinalIgnoreCase)
                    ? 700
                    : 400;
                source = AssetRelationshipSource.NamingConvention;
                confidence = score >= 700
                    ? AssetRelationshipConfidence.Medium
                    : AssetRelationshipConfidence.Low;
                reason = "The texture shares the normalized mesh-family prefix.";
            }

            if (score == 0)
            {
                continue;
            }

            string normalizedPath = texture.RelativePath.Replace('\\', '/');
            if (normalizedPath.Contains("/high/", StringComparison.OrdinalIgnoreCase))
            {
                score += 30;
                reason += " The high-resolution PC variant is preferred for preview.";
            }
            else if (normalizedPath.Contains("/low/", StringComparison.OrdinalIgnoreCase))
            {
                score += 10;
            }

            if (texture.Ownership == AssetOwnership.Mod)
            {
                score += 20;
                reason += " A mod-owned override is preferred.";
            }

            candidates.Add(new AssetRelationshipCandidate(
                texture,
                source,
                confidence,
                reason,
                score));
        }

        AssetRelationshipCandidate[] ordered = candidates
            .OrderByDescending(candidate => candidate.Score)
            .ThenBy(candidate => candidate.Asset.RelativePath, StringComparer.OrdinalIgnoreCase)
            .Take(12)
            .ToArray();
        AssetRelationshipCandidate? automatic = ordered.FirstOrDefault(
            candidate => candidate.Confidence >= AssetRelationshipConfidence.Medium);
        IReadOnlyList<string> warnings = automatic is null
            ? ["No medium- or high-confidence AEI material relationship was found."]
            : [];
        return new AssetRelationshipResolution(
            aemAsset,
            primitiveIndex,
            automatic?.Source ?? AssetRelationshipSource.Unresolved,
            automatic?.Confidence ?? AssetRelationshipConfidence.None,
            automatic?.Asset,
            new ReadOnlyCollection<AssetRelationshipCandidate>(ordered),
            automatic?.Reason ?? "Material relationship unresolved.",
            warnings);
    }

    public void SetMaterialOverride(
        WorkspaceDefinition workspace,
        IndexedAsset aemAsset,
        int primitiveIndex,
        IndexedAsset aeiAsset)
    {
        ArgumentNullException.ThrowIfNull(workspace);
        ArgumentNullException.ThrowIfNull(aemAsset);
        ArgumentNullException.ThrowIfNull(aeiAsset);
        if (aeiAsset.Kind != AssetKind.Aei)
        {
            throw new ArgumentException("A material override must reference an AEI asset.", nameof(aeiAsset));
        }

        workspace.MaterialOverrides[CreateOverrideKey(aemAsset, primitiveIndex)] =
            ToWorkspacePath(aeiAsset, workspace);
    }

    public void ClearMaterialOverride(
        WorkspaceDefinition workspace,
        IndexedAsset aemAsset,
        int primitiveIndex)
    {
        ArgumentNullException.ThrowIfNull(workspace);
        ArgumentNullException.ThrowIfNull(aemAsset);
        workspace.MaterialOverrides.Remove(CreateOverrideKey(aemAsset, primitiveIndex));
    }

    public void DisableMaterial(
        WorkspaceDefinition workspace,
        IndexedAsset aemAsset,
        int primitiveIndex)
    {
        ArgumentNullException.ThrowIfNull(workspace);
        ArgumentNullException.ThrowIfNull(aemAsset);
        workspace.MaterialOverrides[CreateOverrideKey(aemAsset, primitiveIndex)] = "!none";
    }

    public static string CreateOverrideKey(IndexedAsset aemAsset, int primitiveIndex)
    {
        ArgumentNullException.ThrowIfNull(aemAsset);
        ArgumentOutOfRangeException.ThrowIfNegative(primitiveIndex);
        return $"{aemAsset.RelativePath.Replace('\\', '/')}#primitive={primitiveIndex}";
    }

    public static string NormalizeMeshStem(string stem)
    {
        string result = stem;
        while (true)
        {
            string next = KnownMeshSuffix().Replace(result, string.Empty);
            if (next.Equals(result, StringComparison.Ordinal))
            {
                return result;
            }

            result = next;
        }
    }

    private static string NormalizePluralFamily(string familyStem)
    {
        if (familyStem.StartsWith("station_", StringComparison.OrdinalIgnoreCase))
        {
            string[] parts = familyStem.Split('_');
            return parts.Length >= 3 ? $"stations_{parts[^1]}" : familyStem;
        }

        return familyStem;
    }

    private static IndexedAsset? FindConfigured(
        IEnumerable<IndexedAsset> textures,
        string configured,
        WorkspaceDefinition workspace)
    {
        string candidate = Path.IsPathRooted(configured)
            ? Path.GetFullPath(configured)
            : Path.GetFullPath(Path.Combine(
                workspace.GameAssetRoot ?? Path.GetDirectoryName(workspace.FilePath) ?? ".",
                configured));
        return textures.FirstOrDefault(
            asset => Path.GetFullPath(asset.FullPath).Equals(
                candidate,
                StringComparison.OrdinalIgnoreCase));
    }

    private static string ToWorkspacePath(
        IndexedAsset asset,
        WorkspaceDefinition workspace)
    {
        if (!string.IsNullOrWhiteSpace(workspace.GameAssetRoot) &&
            PathPolicy.IsWithin(asset.FullPath, workspace.GameAssetRoot))
        {
            return Path.GetRelativePath(workspace.GameAssetRoot, asset.FullPath);
        }

        return Path.GetFullPath(asset.FullPath);
    }

    [GeneratedRegex(
        "(?:_lod_[0-9]+|_lights_(?:add|emissive)|_engine(?:_glow)?_add|_explosion_anim|_jump_anim_add|_anim(?:_add)?|_emissive|_add|_alpha|_dmg)$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex KnownMeshSuffix();
}
