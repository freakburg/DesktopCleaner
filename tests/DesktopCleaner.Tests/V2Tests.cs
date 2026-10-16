using System.Runtime.Versioning;
using DesktopCleaner.Core;
using DesktopCleaner.Core.Apps;
using DesktopCleaner.Core.Cloud;
using DesktopCleaner.Core.Deletion;
using DesktopCleaner.Core.History;
using DesktopCleaner.Core.Shell;
using DesktopCleaner.Core.Storage;
using Xunit;

namespace DesktopCleaner.Tests;

[SupportedOSPlatform("windows")]
public sealed class V2Tests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("dc-v2").FullName;
    public void Dispose() => Directory.Delete(_root, true);

    private static IReadOnlyDictionary<string, object?> Reg(params (string K, object V)[] kv) =>
        kv.ToDictionary(x => x.K, x => (object?)x.V);

    [Fact]
    public void InstalledApps_filters_components_and_updates_sorts_by_size_and_parses_date()
    {
        var apps = InstalledApps.List(() =>
        [
            Reg(("DisplayName", "Big Game"), ("EstimatedSize", 5_000_000), ("InstallDate", "20230115"), ("Publisher", "Acme")),
            Reg(("DisplayName", "Small Tool"), ("EstimatedSize", 100)),
            Reg(("DisplayName", "Hidden Thing"), ("SystemComponent", 1), ("EstimatedSize", 999_999_999)),
            Reg(("DisplayName", "Patch"), ("ParentKeyName", "Big Game")),
            Reg(("DisplayName", "Security Update for X"), ("EstimatedSize", 1)),
            Reg(("DisplayName", "Big Game"), ("DisplayVersion", "1.0"), ("EstimatedSize", 4_000_000)),
            Reg(("DisplayName", "Big Game"), ("DisplayVersion", "1.0"), ("EstimatedSize", 5_000_000)), // duplicate listing (32/64-bit)
            Reg(("Publisher", "no name")),
        ]);
        Assert.Equal(["Big Game", "Big Game", "Small Tool"], apps.Select(a => a.Name));
        Assert.Equal(5_000_000L * 1024, apps[0].SizeBytes);
        Assert.Equal(new DateTime(2023, 1, 15), apps.First(a => a.Publisher == "Acme").InstalledOn);
        Assert.Equal(3, apps.First(a => a.Publisher == "Acme").YearsSinceInstall(new DateTime(2026, 10, 1)));
    }

    [Fact]
    public async Task InstalledApps_measures_install_folder_when_size_is_missing()
    {
        File.WriteAllBytes(Path.Combine(_root, "a.bin"), new byte[700]);
        var apps = InstalledApps.List(() => [Reg(("DisplayName", "NoSize"), ("InstallLocation", $"\"{_root}\""))]);
        Assert.Equal(0, apps[0].SizeBytes);
        var filled = await InstalledApps.FillMissingSizesAsync(apps);
        Assert.Equal(700, filled[0].SizeBytes);
    }

    [Fact]
    public void Cloud_attribute_helpers()
    {
        Assert.True(CloudFiles.IsCloudOnly((FileAttributes)0x400000 | FileAttributes.Archive)); // recall on data access
        Assert.True(CloudFiles.IsCloudOnly(FileAttributes.Offline));
        Assert.False(CloudFiles.IsCloudOnly(FileAttributes.Archive | FileAttributes.ReparsePoint)); // downloaded placeholder
        Assert.True(CloudFiles.IsPinned((FileAttributes)0x80000));
        Assert.True(CloudFiles.IsUnpinned((FileAttributes)0x100000));
    }

    [Fact]
    public void FsWalker_follows_normal_folders_but_not_junction_loops()
    {
        Directory.CreateDirectory(Path.Combine(_root, "a")); File.WriteAllText(Path.Combine(_root, "a", "f.txt"), "x");
        var psi = new System.Diagnostics.ProcessStartInfo("cmd.exe", $"/c mklink /J \"{Path.Combine(_root, "a", "loop")}\" \"{Path.Combine(_root, "a")}\"")
        { CreateNoWindow = true, UseShellExecute = false, RedirectStandardOutput = true };
        System.Diagnostics.Process.Start(psi)!.WaitForExit();
        Assert.True(Directory.Exists(Path.Combine(_root, "a", "loop")));
        var files = FsWalker.Files(_root).Select(f => f.FullName).ToList();
        Assert.Equal([Path.Combine(_root, "a", "f.txt")], files);
        Directory.Delete(Path.Combine(_root, "a", "loop")); // removes the junction only, not its target
        Assert.True(File.Exists(Path.Combine(_root, "a", "f.txt")));
    }

    [Fact]
    public void FreeUp_only_touches_files_inside_OneDrive_and_reports_outcomes()
    {
        var od = Directory.CreateDirectory(Path.Combine(_root, "OneDrive")).FullName;
        var inside = Path.Combine(od, "big.bin"); var outside = Path.Combine(_root, "other.bin");
        File.WriteAllBytes(inside, new byte[50]); File.WriteAllBytes(outside, new byte[50]);
        LocalCloudFile L(string p) { var fi = new FileInfo(p); return new(p, fi.Length, fi.LastWriteTimeUtc); }
        var ran = new List<string>();
        var res = OneDriveSpace.FreeUp([L(inside), L(outside)], null, p => { ran.Add(p); return 0; }, [od]);
        Assert.Equal([inside], ran);
        Assert.Equal(DeleteStatus.FreedFromPc, res[0].Status);
        Assert.True(res[0].Succeeded);
        Assert.Equal(DeleteStatus.SkippedProtected, res[1].Status);
        File.AppendAllText(inside, "x");
        Assert.Equal(DeleteStatus.SkippedChanged, OneDriveSpace.FreeUp([L(inside) with { Size = 1 }], null, _ => 0, [od])[0].Status);
    }

    [Fact]
    public void Snapshots_roundtrip_and_compare_growth()
    {
        var store = new SnapshotStore(Path.Combine(_root, "snap.jsonl"));
        var before = new SpaceSnapshot(DateTime.UtcNow.AddDays(-7), @"C:\x", 1000, 10, new() { ["Videos"] = 600, ["Pictures"] = 400 }, new() { ["a"] = 700, ["b"] = 300 });
        var after = new SpaceSnapshot(DateTime.UtcNow, @"C:\x", 1900, 12, new() { ["Videos"] = 1500, ["Pictures"] = 400 }, new() { ["a"] = 1600, ["b"] = 300 });
        store.Save(before); store.Save(after);
        store.Save(before with { Root = @"C:\other" });
        Assert.Equal(2, store.For(@"c:\X").Count);
        var cmp = SnapshotStore.Compare(store.For(@"C:\x")[0], after)!;
        Assert.Equal(900, cmp.TotalDelta);
        Assert.Equal(("a", 900L), (cmp.FolderChanges[0].Name, cmp.FolderChanges[0].Delta));
        Assert.Equal("Videos", cmp.CategoryChanges[0].Name);
        Assert.Null(SnapshotStore.Compare(null, after));
    }

    [Fact]
    public async Task Snapshot_from_real_scan_and_exports_are_safe()
    {
        File.WriteAllBytes(Path.Combine(_root, "=HYPERLINK(1).bin"), new byte[500]);
        Directory.CreateDirectory(Path.Combine(_root, "dir, with comma")); File.WriteAllBytes(Path.Combine(_root, "dir, with comma", "x.mp4"), new byte[1500]);
        var r = await new StorageAnalyzer().AnalyzeAsync(_root);
        Assert.Equal(2000, SnapshotStore.From(r).TotalBytes);
        var csv = ReportExporter.ToCsv(r);
        Assert.Contains("'=HYPERLINK(1).bin", csv);               // formula defused
        Assert.Contains("\"dir, with comma\"", csv);               // commas are quoted
        Assert.Equal("\"say \"\"hi\"\"\"", ReportExporter.Csv("say \"hi\""));   // embedded quotes are doubled
        var html = ReportExporter.ToHtml(r);
        Assert.Contains("Storage report", html);
        Assert.DoesNotContain("<script", html);
    }

    [Fact]
    public async Task Parallel_and_sequential_scans_agree()
    {
        for (int d = 0; d < 6; d++)
            for (int s = 0; s < 4; s++)
            {
                var dir = Directory.CreateDirectory(Path.Combine(_root, $"d{d}", $"s{s}")).FullName;
                for (int f = 0; f < 5; f++) File.WriteAllBytes(Path.Combine(dir, $"f{f}.dat"), new byte[(d + 1) * (s + 1) * (f + 1)]);
            }
        var seq = await new StorageAnalyzer().AnalyzeAsync(_root, new StorageOptions { MaxParallel = 1, MinFolderFraction = 0 });
        var par = await new StorageAnalyzer().AnalyzeAsync(_root, new StorageOptions { MaxParallel = 8, MinFolderFraction = 0 });
        Assert.Equal(seq.TotalBytes, par.TotalBytes);
        Assert.Equal(seq.FileCount, par.FileCount);
        Assert.Equal(seq.LargestFiles.Select(f => f.Size), par.LargestFiles.Select(f => f.Size));
        Assert.Equal(seq.Root.Children.Select(c => c.Bytes), par.Root.Children.Select(c => c.Bytes));
    }

    [Fact]
    public void ExplorerMenu_adds_and_removes_registry_entries_under_a_test_key()
    {
        var key = $@"Software\DesktopCleanerTest\{Guid.NewGuid():N}";
        try
        {
            var menu = new ExplorerMenu(key);
            Assert.False(menu.IsEnabled);
            menu.Enable(@"C:\Apps\dc.exe");
            Assert.True(menu.IsEnabled);
            using (var k = Microsoft.Win32.Registry.CurrentUser.OpenSubKey($@"{key}\Directory\shell\DesktopCleaner\command"))
                Assert.Equal("\"C:\\Apps\\dc.exe\" --folder \"%1\"", k!.GetValue(""));
            menu.Disable();
            Assert.False(menu.IsEnabled);
        }
        finally { Microsoft.Win32.Registry.CurrentUser.DeleteSubKeyTree(@"Software\DesktopCleanerTest", false); }
    }
}

