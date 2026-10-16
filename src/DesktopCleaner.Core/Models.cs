namespace DesktopCleaner.Core.Duplicates;

public sealed record FileEntry(string Path, long Size, DateTime LastWriteUtc);

/// <summary>Files with identical content. Hash is the SHA-256 hex digest.</summary>
public sealed record DuplicateGroup(string Hash, long FileSize, IReadOnlyList<FileEntry> Files)
{
    /// <summary>Space recovered if all but one copy were removed.</summary>
    public long WastedBytes => FileSize * (Files.Count - 1);
}

public sealed record ScanOptions
{
    public long MinimumSize { get; init; } = 1;
    public bool IncludeSubfolders { get; init; } = true;
    public bool SkipHiddenAndSystem { get; init; } = true;
    public int MaxParallelHashing { get; init; } = Math.Max(1, Environment.ProcessorCount / 2);
}

public enum ScanPhase { Enumerating, Hashing, Done }

public sealed record ScanProgress(ScanPhase Phase, long FilesSeen, long FilesToHash, long FilesHashed, string? CurrentPath);

public sealed record ScanIssue(string Path, string Message);

public sealed class ScanResult
{
    public required IReadOnlyList<DuplicateGroup> Groups { get; init; }
    public required IReadOnlyList<ScanIssue> Issues { get; init; }
    public long FilesScanned { get; init; }
    /// <summary>Cloud-only files left alone so they would not be downloaded.</summary>
    public long CloudOnlySkipped { get; init; }
    public long WastedBytes => Groups.Sum(g => g.WastedBytes);
}

