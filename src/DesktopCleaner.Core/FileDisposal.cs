using DesktopCleaner.Core.Settings;

namespace DesktopCleaner.Core.Deletion;

public enum DisposalMode { RecycleBin, SafetyNet }

/// <summary>
/// The one place that decides where a "delete" goes: the Recycle Bin (default) or the 30-day safety net. Every remover uses this
/// by default; set it once at startup from the saved settings.
/// </summary>
public static class FileDisposal
{
    private static SafetyNet? _net;
    public static DisposalMode Mode { get; private set; } = DisposalMode.RecycleBin;
    public static int SafetyNetDays { get; private set; } = 30;

    /// <summary>The shared safety net (one index in memory for the whole run).</summary>
    public static SafetyNet Net => _net ??= new SafetyNet(days: SafetyNetDays,
        fallbackRoot: Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DesktopCleaner", "Holding"));

    public static void Configure(AppSettings settings)
    {
        Mode = settings.SafetyNet ? DisposalMode.SafetyNet : DisposalMode.RecycleBin;
        if (SafetyNetDays != settings.SafetyNetDays) { SafetyNetDays = Math.Max(1, settings.SafetyNetDays); _net = null; }
    }

    public static void Send(string path)
    {
        if (Mode == DisposalMode.SafetyNet) Net.Hold(path);
        else RecycleBin.Send(path);
    }

    /// <summary>Puts something back, wherever it went: the safety net first, then the Recycle Bin.</summary>
    public static async Task<RestoreResult> RestoreAsync(string originalPath)
    {
        var archiver = new Archiving.Archiver();   // reads its own small index file
        if (archiver.FindByOriginalPath(originalPath) is { } archived) return await archiver.RestoreAsync(archived.Id).ConfigureAwait(false);
        if (Net.FindByOriginalPath(originalPath) is { } held) return Net.Restore(held.Id);
        return await RecycleBinRestorer.RestoreAsync(originalPath).ConfigureAwait(false);
    }
}


