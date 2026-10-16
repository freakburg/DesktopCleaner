using DesktopCleaner.Core.Deletion;

namespace DesktopCleaner.Core.Cleanup;

/// <summary>Decides whether a rule may ever touch a folder. Automatic cleanup stays out of personal, synced and system areas.</summary>
public static class RuleSafety
{
    public static string? ValidateFolder(string folder)
    {
        string full;
        try { full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(folder)); }
        catch (Exception ex) { return ex.Message; }

        if (Path.GetPathRoot(full) is { } r && string.Equals(Path.TrimEndingDirectorySeparator(r), full, StringComparison.OrdinalIgnoreCase))
            return "Drive roots are not allowed.";
        if (ProtectedPaths.IsProtected(full + Path.DirectorySeparatorChar)) return "System folder.";

        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (Same(full, profile)) return "The user profile root is not allowed.";

        foreach (var blocked in BlockedRoots(profile))
            if (Inside(full, blocked) || Inside(blocked, full)) return $"Overlaps personal or cloud-sync folder '{blocked}'.";
        return null;
    }

    private static IEnumerable<string> BlockedRoots(string profile)
    {
        foreach (var f in new[] { Environment.SpecialFolder.MyDocuments, Environment.SpecialFolder.MyPictures,
                                  Environment.SpecialFolder.MyMusic, Environment.SpecialFolder.MyVideos,
                                  Environment.SpecialFolder.DesktopDirectory })
        {
            var p = Environment.GetFolderPath(f);
            if (!string.IsNullOrEmpty(p)) yield return p;
        }
        foreach (var n in new[] { "OneDrive", "Dropbox", "Google Drive", "iCloudDrive", "Box", "Documents", "Pictures" })
            yield return Path.Combine(profile, n);
        // OneDrive variants like "OneDrive - Contoso"
        if (Directory.Exists(profile))
            foreach (var dir in Directory.EnumerateDirectories(profile, "OneDrive*")) yield return dir;
    }

    private static bool Same(string a, string b) =>
        string.Equals(Path.TrimEndingDirectorySeparator(a), Path.TrimEndingDirectorySeparator(b), StringComparison.OrdinalIgnoreCase);

    private static bool Inside(string child, string parent) =>
        Same(child, parent) || child.StartsWith(Path.TrimEndingDirectorySeparator(parent) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
}
