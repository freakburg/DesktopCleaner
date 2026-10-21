using DesktopCleaner.Core.Downloads;
using Xunit;

namespace DesktopCleaner.Tests;

public sealed class DownloadOriginTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("dc-origin").FullName;
    public void Dispose() => Directory.Delete(_root, true);

    [Fact]
    public void Parse_reads_zone_host_and_referrer_and_extracts_a_clean_domain()
    {
        var o = DownloadOrigin.Parse("[ZoneTransfer]\r\nZoneId=3\r\nReferrerUrl=https://www.example.com/downloads/page\r\nHostUrl=https://cdn.files.example.org/setup.exe\r\n");
        Assert.Equal(3, o.ZoneId);
        Assert.Equal("cdn.files.example.org", o.Domain);
        Assert.Equal("cdn.files.example.org", o.Display);
        Assert.Contains("page", o.Details);
    }

    [Fact]
    public void Falls_back_to_the_referrer_and_to_a_plain_message_when_no_site_is_recorded()
    {
        Assert.Equal("example.com", DownloadOrigin.Parse("ZoneId=3\nReferrerUrl=https://www.example.com/x\nHostUrl=about:internet").Domain);
        var bare = DownloadOrigin.Parse("[ZoneTransfer]\nZoneId=3\nHostUrl=about:internet");
        Assert.Null(bare.Domain);
        Assert.Equal("The internet (site not recorded)", bare.Display);
        Assert.Equal("", DownloadOrigin.Parse("ZoneId=0").Display);
    }

    [Fact]
    public void Parse_ignores_garbage()
    {
        var o = DownloadOrigin.Parse("not an ini file\n=\n\u0000\u0001 ZoneId = abc");
        Assert.Null(o.ZoneId); Assert.Null(o.Domain);
    }

    [Fact]
    public async Task Reads_the_real_stream_from_disk_and_returns_null_for_files_without_one()
    {
        var withMark = Path.Combine(_root, "setup.exe"); var plain = Path.Combine(_root, "plain.exe");
        File.WriteAllBytes(withMark, new byte[10]); File.WriteAllBytes(plain, new byte[10]);
        File.WriteAllText(withMark + ":Zone.Identifier", "[ZoneTransfer]\r\nZoneId=3\r\nHostUrl=https://get.example.net/setup.exe\r\n");
        Assert.Equal("get.example.net", DownloadOrigin.Read(withMark)!.Domain);
        Assert.Null(DownloadOrigin.Read(plain));
        Assert.Null(DownloadOrigin.Read(Path.Combine(_root, "missing.exe")));

        File.SetLastWriteTimeUtc(withMark, DateTime.UtcNow.AddDays(-90));
        var items = await new DownloadsReviewer().ScanAsync(_root);
        Assert.Equal("get.example.net", items.Single(i => i.Path == withMark).Origin!.Domain);
    }
}
