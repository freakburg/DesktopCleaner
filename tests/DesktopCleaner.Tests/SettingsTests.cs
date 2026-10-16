using DesktopCleaner.Core.Settings;
using Xunit;

namespace DesktopCleaner.Tests;

public sealed class SettingsTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("dc-set").FullName;
    public void Dispose() => Directory.Delete(_dir, true);
    private const long GB = 1024L * 1024 * 1024;

    [Fact]
    public void Settings_roundtrip_and_fall_back_to_defaults_on_garbage()
    {
        var p = Path.Combine(_dir, "s.json");
        Assert.True(AppSettings.Load(p).LowDiskAlert);
        new AppSettings { LowDiskAlert = false, LowDiskPercent = 5 }.Save(p);
        var s = AppSettings.Load(p);
        Assert.False(s.LowDiskAlert); Assert.Equal(5, s.LowDiskPercent);
        File.WriteAllText(p, "{ nonsense");
        Assert.True(AppSettings.Load(p).LowDiskAlert);
    }

    [Fact]
    public void Low_disk_rule_triggers_on_low_percent_or_low_absolute_space()
    {
        var defaults = new AppSettings(); // 15% or 15 GB
        Assert.True(DiskSpaceCheck.IsLow(new DiskStatus("C:\\", 100 * GB, 1000 * GB), defaults));   // 10% free
        Assert.True(DiskSpaceCheck.IsLow(new DiskStatus("C:\\", 12 * GB, 100 * GB), defaults));     // 12% free
        Assert.False(DiskSpaceCheck.IsLow(new DiskStatus("C:\\", 200 * GB, 1000 * GB), defaults));  // 20% free, plenty of GB
    }
    [Fact]
    public void Alert_can_be_turned_off()
    {
        var empty = new DiskStatus("C:\\", 1 * GB, 100 * GB);
        Assert.True(DiskSpaceCheck.IsLow(empty, new AppSettings()));
        Assert.False(DiskSpaceCheck.IsLow(empty, new AppSettings { LowDiskAlert = false }));
        Assert.True(DiskSpaceCheck.IsLow(new DiskStatus("C:\\", 10 * GB, 1000 * GB), new AppSettings()));  // 10 GB < 15 GB absolute
        Assert.False(DiskSpaceCheck.IsLow(new DiskStatus("C:\\", 400 * GB, 1000 * GB), new AppSettings()));
    }
}

