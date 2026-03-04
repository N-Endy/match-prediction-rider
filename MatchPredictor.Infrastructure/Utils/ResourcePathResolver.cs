namespace MatchPredictor.Infrastructure.Utils;

/// <summary>
/// Resolves the path to the Resources directory used for file downloads and Excel data.
/// Centralizes the path-probing logic previously duplicated in WebScraperService and ExtractFromExcel.
/// </summary>
public static class ResourcePathResolver
{
    /// <summary>
    /// Finds or creates the Resources directory using a priority-based search:
    /// 1. AppDomain.BaseDirectory/Resources (publish/bin scenarios)
    /// 2. CurrentDirectory/Resources
    /// 3. Parent of CurrentDirectory/Resources (fallback)
    /// </summary>
    public static string GetResourcesDirectory()
    {
        var baseDirFolder = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Resources");
        var currentDirFolder = Path.Combine(Directory.GetCurrentDirectory(), "Resources");
        var parentDirFolder = Path.Combine(
            Directory.GetParent(Directory.GetCurrentDirectory())?.FullName ?? string.Empty, "Resources");

        string folder;
        if (Directory.Exists(baseDirFolder) ||
            AppDomain.CurrentDomain.BaseDirectory.Contains("publish") ||
            AppDomain.CurrentDomain.BaseDirectory.Contains("bin"))
        {
            folder = baseDirFolder;
        }
        else if (Directory.Exists(currentDirFolder))
        {
            folder = currentDirFolder;
        }
        else
        {
            folder = parentDirFolder;
        }

        Directory.CreateDirectory(folder);
        return folder;
    }
}
