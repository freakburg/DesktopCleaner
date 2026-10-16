using System.Runtime.Versioning;

namespace DesktopCleaner.Core.Deletion;

public enum RestoreStatus { Restored, NotInRecycleBin, AlreadyExists, Failed }

public sealed record RestoreResult(string Path, RestoreStatus Status, string? Detail = null);

/// <summary>Puts a file back from the Windows Recycle Bin by finding it via its original location.</summary>
[SupportedOSPlatform("windows")]
public static class RecycleBinRestorer
{
    /// <summary>Runs on a dedicated STA thread because the Shell COM objects require it.</summary>
    public static Task<RestoreResult> RestoreAsync(string originalPath) => OnSta(() => Restore(originalPath));

    private static RestoreResult Restore(string originalPath)
    {
        try
        {
            if (File.Exists(originalPath)) return new(originalPath, RestoreStatus.AlreadyExists, "A file with this name is already there.");
            var shellType = Type.GetTypeFromProgID("Shell.Application") ?? throw new PlatformNotSupportedException();
            dynamic shell = Activator.CreateInstance(shellType)!;
            dynamic bin = shell.NameSpace(10); // ssfBITBUCKET

            var wantedFolder = Clean(Path.GetDirectoryName(originalPath) ?? "");
            var wantedName = Path.GetFileName(originalPath);
            var wantedStem = Path.GetFileNameWithoutExtension(originalPath);

            dynamic? best = null; DateTime bestDate = DateTime.MinValue;
            foreach (dynamic item in bin.Items())
            {
                string name = Clean((string)item.Name);
                if (!name.Equals(wantedName, StringComparison.OrdinalIgnoreCase) && !name.Equals(wantedStem, StringComparison.OrdinalIgnoreCase)) continue;
                string folder = Clean((string)bin.GetDetailsOf(item, 1)); // "Original Location"
                if (!folder.Equals(wantedFolder, StringComparison.OrdinalIgnoreCase)) continue;
                DateTime.TryParse(Clean((string)bin.GetDetailsOf(item, 2)), out var deleted); // "Date Deleted"
                if (best is null || deleted >= bestDate) { best = item; bestDate = deleted; }
            }
            if (best is null) return new(originalPath, RestoreStatus.NotInRecycleBin, "Not in the Recycle Bin (it may have been emptied).");

            best.InvokeVerb("undelete");
            // The shell restores asynchronously; wait briefly for the file to reappear.
            for (int i = 0; i < 40 && !File.Exists(originalPath); i++) Thread.Sleep(50);
            return File.Exists(originalPath)
                ? new(originalPath, RestoreStatus.Restored)
                : new(originalPath, RestoreStatus.Failed, "Windows did not restore the file.");
        }
        catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException or InvalidOperationException or Microsoft.CSharp.RuntimeBinder.RuntimeBinderException or PlatformNotSupportedException or IOException)
        {
            return new(originalPath, RestoreStatus.Failed, ex.Message);
        }
    }

    /// <summary>Shell strings contain invisible left-to-right marks that break comparisons.</summary>
    private static string Clean(string s) => s.Replace("\u200e", "").Replace("\u200f", "").Trim();

    private static Task<T> OnSta<T>(Func<T> work)
    {
        var tcs = new TaskCompletionSource<T>();
        var t = new Thread(() => { try { tcs.SetResult(work()); } catch (Exception ex) { tcs.SetException(ex); } }) { IsBackground = true };
        t.SetApartmentState(ApartmentState.STA);
        t.Start();
        return tcs.Task;
    }
}
