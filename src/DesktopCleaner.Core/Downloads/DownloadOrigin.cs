namespace DesktopCleaner.Core.Downloads;

/// <summary>
/// Where a downloaded file came from, read from the hidden "Zone.Identifier" stream that browsers and Windows attach to files
/// fetched from the internet (the "Mark of the Web"). Not every file has one, and not every one names a website.
/// </summary>
public sealed record DownloadOrigin(int? ZoneId, string? HostUrl, string? ReferrerUrl)
{
    /// <summary>The website to show: the address the file was fetched from, else the page that led to it.</summary>
    public string? Domain => DomainOf(HostUrl) ?? DomainOf(ReferrerUrl);

    /// <summary>One short phrase for a table cell.</summary>
    public string Display => Domain ?? (ZoneId is 3 ? "The internet (site not recorded)" : ZoneId is 0 or null ? "" : "Another computer or network");

    public string Details => string.Join("\n", new[]
    {
        HostUrl is { Length: > 0 } && HostUrl != "about:internet" ? $"Downloaded from: {HostUrl}" : null,
        ReferrerUrl is { Length: > 0 } ? $"Came from the page: {ReferrerUrl}" : null,
    }.Where(x => x is not null));

    private static string? DomainOf(string? url)
    {
        if (string.IsNullOrWhiteSpace(url) || url == "about:internet") return null;
        if (!Uri.TryCreate(url.Trim(), UriKind.Absolute, out var u) || string.IsNullOrEmpty(u.Host)) return null;
        var host = u.Host;
        return host.StartsWith("www.", StringComparison.OrdinalIgnoreCase) ? host[4..] : host;
    }

    /// <summary>Reads the stream; returns null when the file has none (or the drive cannot hold one, as on FAT/exFAT).</summary>
    public static DownloadOrigin? Read(string path)
    {
        if (!OperatingSystem.IsWindows()) return null;
        try
        {
            using var fs = new FileStream(path + ":Zone.Identifier", FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(fs);
            return Parse(reader.ReadToEnd());
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException or ArgumentException) { return null; }
    }

    public static DownloadOrigin Parse(string text)
    {
        int? zone = null; string? host = null, referrer = null;
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.Trim();
            int i = line.IndexOf('=');
            if (i <= 0) continue;
            var key = line[..i].Trim(); var value = line[(i + 1)..].Trim();
            if (key.Equals("ZoneId", StringComparison.OrdinalIgnoreCase) && int.TryParse(value, out var z)) zone = z;
            else if (key.Equals("HostUrl", StringComparison.OrdinalIgnoreCase)) host = value;
            else if (key.Equals("ReferrerUrl", StringComparison.OrdinalIgnoreCase)) referrer = value;
        }
        return new DownloadOrigin(zone, host, referrer);
    }
}
