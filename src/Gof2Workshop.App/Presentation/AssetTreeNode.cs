using System.Collections.ObjectModel;
using Gof2Workshop.Workbench;

namespace Gof2Workshop.App.Presentation;

public sealed class AssetTreeNode
{
    public AssetTreeNode(string name, string relativePath, IndexedAsset? asset = null)
    {
        Name = name;
        RelativePath = relativePath;
        Asset = asset;
    }

    public string Name { get; }

    public string RelativePath { get; }

    public IndexedAsset? Asset { get; }

    public bool IsFolder => Asset is null;

    public string Detail => Asset is null
        ? $"{Children.Count} item{(Children.Count == 1 ? string.Empty : "s")}"
        : $"{Asset.Kind.ToString().ToUpperInvariant()} · {FormatSize(Asset.Size)} · {Asset.Classification}";

    public string StatusGlyph => Asset?.Support switch
    {
        AssetSupport.Supported => "●",
        AssetSupport.RecognizedUnsupported => "◇",
        AssetSupport.Unknown => "?",
        AssetSupport.Unreadable => "!",
        _ => "▾",
    };

    public ObservableCollection<AssetTreeNode> Children { get; } = [];

    public static IReadOnlyList<AssetTreeNode> Build(IEnumerable<IndexedAsset> assets)
    {
        AssetTreeNode root = new("root", string.Empty);
        foreach (IndexedAsset asset in assets.OrderBy(
            value => value.RelativePath,
            StringComparer.OrdinalIgnoreCase))
        {
            string[] segments = asset.RelativePath.Split(
                [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
                StringSplitOptions.RemoveEmptyEntries);
            AssetTreeNode parent = root;
            string currentPath = string.Empty;
            for (int index = 0; index < segments.Length - 1; index++)
            {
                currentPath = currentPath.Length == 0
                    ? segments[index]
                    : Path.Combine(currentPath, segments[index]);
                AssetTreeNode? folder = parent.Children.FirstOrDefault(
                    child => child.IsFolder &&
                    string.Equals(child.Name, segments[index], StringComparison.OrdinalIgnoreCase));
                if (folder is null)
                {
                    folder = new AssetTreeNode(segments[index], currentPath);
                    parent.Children.Add(folder);
                }

                parent = folder;
            }

            parent.Children.Add(
                new AssetTreeNode(
                    segments.Length == 0 ? asset.FileName : segments[^1],
                    asset.RelativePath,
                    asset));
        }

        SortRecursively(root);
        return root.Children;
    }

    private static void SortRecursively(AssetTreeNode node)
    {
        List<AssetTreeNode> sorted = node.Children
            .OrderByDescending(child => child.IsFolder)
            .ThenBy(child => child.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
        node.Children.Clear();
        foreach (AssetTreeNode child in sorted)
        {
            SortRecursively(child);
            node.Children.Add(child);
        }
    }

    private static string FormatSize(long bytes)
    {
        return bytes switch
        {
            >= 1024 * 1024 => $"{bytes / (1024d * 1024d):N1} MB",
            >= 1024 => $"{bytes / 1024d:N1} KB",
            _ => $"{bytes:N0} B",
        };
    }
}
