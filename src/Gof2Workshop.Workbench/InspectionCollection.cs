using System.Collections.ObjectModel;
using Gof2Workshop.Core;

namespace Gof2Workshop.Workbench;

public sealed record InspectionCollectionUpdate(
    IReadOnlyList<IndexedAsset> AddedAssets,
    IReadOnlyList<string> CompanionFiles,
    IReadOnlyList<ProblemEntry> Problems);

public sealed class InspectionCollection
{
    private const int MaximumFiles = 20_000;
    private readonly AssetIndexService index = new();
    private readonly Dictionary<string, IndexedAsset> assets =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> companions = new(StringComparer.OrdinalIgnoreCase);

    public InspectionCollection(AssetPlatformProfile profile)
    {
        Profile = profile ?? throw new ArgumentNullException(nameof(profile));
    }

    public AssetPlatformProfile Profile { get; private set; }

    public IReadOnlyList<IndexedAsset> Assets =>
        new ReadOnlyCollection<IndexedAsset>(assets.Values
            .OrderBy(asset => asset.FileName, StringComparer.OrdinalIgnoreCase)
            .ToArray());

    public IReadOnlyList<string> CompanionFiles => companions
        .Order(StringComparer.OrdinalIgnoreCase)
        .ToArray();

    public void ChangeProfile(AssetPlatformProfile profile)
    {
        Profile = profile ?? throw new ArgumentNullException(nameof(profile));
    }

    public Task<InspectionCollectionUpdate> AddAsync(
        IEnumerable<string> paths,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(paths);
        return Task.Run(() => AddCore(paths, cancellationToken), cancellationToken);
    }

    public WorkspaceDefinition CreateTransientWorkspace()
    {
        return new WorkspaceDefinition
        {
            Name = "Quick Inspect",
            ModId = "local.quick-inspect",
            ProfileId = Profile.Id,
            GameAssetRoot = null,
            FilePath = null,
        };
    }

    private InspectionCollectionUpdate AddCore(
        IEnumerable<string> paths,
        CancellationToken cancellationToken)
    {
        List<string> expanded = Expand(paths, cancellationToken);
        if (expanded.Count > MaximumFiles)
        {
            throw new InvalidOperationException(
                $"Quick Inspect accepts at most {MaximumFiles:N0} files per collection.");
        }

        List<IndexedAsset> added = [];
        List<string> addedCompanions = [];
        List<ProblemEntry> problems = [];
        foreach (string path in expanded)
        {
            cancellationToken.ThrowIfCancellationRequested();
            string extension = Path.GetExtension(path).ToLowerInvariant();
            if (extension is ".aei" or ".aem" or ".lang" or ".bin")
            {
                try
                {
                    IndexedAsset asset = index.ProbeFile(
                        path,
                        AssetOwnership.Game,
                        Profile,
                        Path.GetFileName(path));
                    if (assets.TryAdd(asset.StableKey, asset))
                    {
                        added.Add(asset);
                    }

                    if (asset.Warning is not null)
                    {
                        problems.Add(new ProblemEntry(
                            ProblemSeverity.Warning,
                            asset.FileName,
                            asset.FullPath,
                            asset.Classification,
                            asset.Warning,
                            null,
                            "profile",
                            "Select the correct profile or inspect the technical details."));
                    }
                }
                catch (Exception exception) when (exception is IOException or NotSupportedException)
                {
                    problems.Add(new ProblemEntry(
                        ProblemSeverity.Error,
                        Path.GetFileName(path),
                        path,
                        "Quick Inspect",
                        exception.Message,
                        null,
                        null,
                        "Verify the file and selected profile."));
                }
            }
            else if (extension is ".png" or ".gltf" or ".glb" or ".obj" or ".mtl")
            {
                string fullPath = Path.GetFullPath(path);
                if (companions.Add(fullPath))
                {
                    addedCompanions.Add(fullPath);
                }
            }
        }

        return new InspectionCollectionUpdate(added, addedCompanions, problems);
    }

    private static List<string> Expand(
        IEnumerable<string> paths,
        CancellationToken cancellationToken)
    {
        List<string> files = [];
        foreach (string value in paths.Where(value => !string.IsNullOrWhiteSpace(value)))
        {
            cancellationToken.ThrowIfCancellationRequested();
            string path = Path.GetFullPath(value);
            if (File.Exists(path))
            {
                files.Add(path);
            }
            else if (Directory.Exists(path))
            {
                foreach (string file in Directory.EnumerateFiles(
                    path,
                    "*",
                    new EnumerationOptions
                    {
                        RecurseSubdirectories = true,
                        IgnoreInaccessible = true,
                        AttributesToSkip = FileAttributes.ReparsePoint,
                    }))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    files.Add(file);
                    if (files.Count > MaximumFiles)
                    {
                        return files;
                    }
                }
            }
        }

        return files.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }
}
