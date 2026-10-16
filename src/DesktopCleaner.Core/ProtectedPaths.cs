namespace DesktopCleaner.Core.Deletion;

/// <summary>Folders the tool will never delete from.</summary>
public static class ProtectedPaths
{
    public static bool IsProtected(string path)
    {
        var full = Path.GetFullPath(path);
        foreach (var root in Roots())
        {
            if (string.IsNullOrEmpty(root)) continue;
            var r = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root)) + Path.DirectorySeparatorChar;
            if (full.StartsWith(r, StringComparison.OrdinalIgnoreCase)) return true;
        }
        return false;
    }

    private static IEnumerable<string> Roots()
    {
        yield return Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        yield return Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        yield return Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
        yield return Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
    }
}
