namespace Gof2Workshop.Workbench;

public static class PathPolicy
{
    public static bool IsWithin(string candidatePath, string rootPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(candidatePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(rootPath);
        string candidate = Path.TrimEndingDirectorySeparator(Path.GetFullPath(candidatePath));
        string root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(rootPath));
        if (string.Equals(candidate, root, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return candidate.StartsWith(
            root + Path.DirectorySeparatorChar,
            StringComparison.OrdinalIgnoreCase);
    }

    public static string ValidateExportDestination(
        string destination,
        string? gameAssetRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(destination);
        string fullDestination = Path.GetFullPath(destination);
        if (!string.IsNullOrWhiteSpace(gameAssetRoot) &&
            IsWithin(fullDestination, gameAssetRoot))
        {
            throw new InvalidOperationException(
                "Exports cannot be written beneath the original game asset root. " +
                "Choose the workspace Generated folder or another destination.");
        }

        return fullDestination;
    }
}
