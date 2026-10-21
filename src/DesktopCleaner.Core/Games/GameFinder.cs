using System.Text;
using System.Text.Json;
using Microsoft.Win32;

namespace DesktopCleaner.Core.Games;

public enum GameLauncher { Steam, Epic }

public sealed record GameInfo(GameLauncher Launcher, string Name, string InstallPath, long SizeBytes, DateTime? LastPlayedUtc, string? AppId, bool IsTool = false)
{
    public bool NeverPlayed => Launcher == GameLauncher.Steam && LastPlayedUtc is null;
    /// <summary>Days since it was last played, or null when the launcher does not record that (Epic) or it was never played.</summary>
    public int? DaysSincePlayed(DateTime nowUtc) => LastPlayedUtc is null ? null : (int)Math.Max(0, (nowUtc - LastPlayedUtc.Value).TotalDays);
}

/// <summary>A tiny reader for Valve's text format (used by Steam's library and manifest files): nested "key" "value" pairs and { } blocks.</summary>
public static class Vdf
{
    public sealed class Node : Dictionary<string, object>
    {
        public Node() : base(StringComparer.OrdinalIgnoreCase) { }
        public string? Str(string key) => TryGetValue(key, out var v) ? v as string : null;
        public Node? Child(string key) => TryGetValue(key, out var v) ? v as Node : null;
    }

    public static Node Parse(string text)
    {
        int i = 0;
        var root = new Node();
        ReadPairs(text, ref i, root, top: true);
        return root;
    }

    private static void ReadPairs(string s, ref int i, Node into, bool top)
    {
        while (true)
        {
            var key = NextToken(s, ref i);
            if (key is null) return;                       // end of input
            if (key == "}") { if (top) continue; return; } // end of block
            var next = Peek(s, i);
            if (next == '{')
            {
                NextToken(s, ref i);                       // consume {
                var child = new Node();
                ReadPairs(s, ref i, child, top: false);
                into[key] = child;
            }
            else
            {
                var value = NextToken(s, ref i);
                if (value is null) return;
                into[key] = value;
            }
        }
    }

    private static char? Peek(string s, int i)
    {
        while (i < s.Length && char.IsWhiteSpace(s[i])) i++;
        return i < s.Length ? s[i] : null;
    }

    private static string? NextToken(string s, ref int i)
    {
        while (i < s.Length)
        {
            char c = s[i];
            if (char.IsWhiteSpace(c)) { i++; continue; }
            if (c == '/' && i + 1 < s.Length && s[i + 1] == '/') { while (i < s.Length && s[i] != '\n') i++; continue; }   // comment
            if (c == '{') { i++; return "{"; }
            if (c == '}') { i++; return "}"; }
            if (c == '"')
            {
                i++;
                var sb = new StringBuilder();
                while (i < s.Length && s[i] != '"')
                {
                    if (s[i] == '\\' && i + 1 < s.Length) { i++; sb.Append(s[i] switch { 'n' => '\n', 't' => '\t', _ => s[i] }); }
                    else sb.Append(s[i]);
                    i++;
                }
                i++;                                        // closing quote
                return sb.ToString();
            }
            var start = i;                                  // unquoted token
            while (i < s.Length && !char.IsWhiteSpace(s[i]) && s[i] != '{' && s[i] != '}' && s[i] != '"') i++;
            return s[start..i];
        }
        return null;
    }
}

/// <summary>
/// Lists installed games from Steam (every library folder, with size and the time you last played) and the Epic Games Launcher
/// (size only: Epic does not record when a game was played). Read-only: we never touch game files. Uninstalling is left to the launcher.
/// </summary>
public sealed class GameFinder(string? steamRoot = null, string? epicManifestFolder = null)
{
    private static readonly HashSet<string> ToolAppIds = ["228980", "1070560", "1391110", "1493710", "1628350", "1826330", "2180100", "2348590"];

    public static string? SteamRoot()
    {
        foreach (var (hive, key, value) in new[]
                 {
                     (Registry.CurrentUser, @"Software\Valve\Steam", "SteamPath"),
                     (Registry.LocalMachine, @"SOFTWARE\WOW6432Node\Valve\Steam", "InstallPath"),
                     (Registry.LocalMachine, @"SOFTWARE\Valve\Steam", "InstallPath"),
                 })
        {
            try
            {
                if (hive.OpenSubKey(key)?.GetValue(value) is string p && p.Length > 0)
                {
                    var full = Path.GetFullPath(p.Replace('/', '\\'));
                    if (Directory.Exists(full)) return full;
                }
            }
            catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException) { }
        }
        return null;
    }

    public static string EpicManifestFolder => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "Epic", "EpicGamesLauncher", "Data", "Manifests");

    public IReadOnlyList<GameInfo> Find()
    {
        var games = new List<GameInfo>();
        games.AddRange(FindSteam(steamRoot ?? SteamRoot()));
        games.AddRange(FindEpic(epicManifestFolder ?? EpicManifestFolder));
        return games;
    }

    public static IReadOnlyList<string> SteamLibraries(string steamRoot)
    {
        var libs = new List<string> { Path.Combine(steamRoot, "steamapps") };
        var file = Path.Combine(steamRoot, "steamapps", "libraryfolders.vdf");
        try
        {
            if (File.Exists(file))
                foreach (var entry in Vdf.Parse(File.ReadAllText(file)).Child("libraryfolders")?.Values.OfType<Vdf.Node>() ?? [])
                    if (entry.Str("path") is { Length: > 0 } p)
                    {
                        var lib = Path.Combine(p, "steamapps");
                        if (!libs.Contains(lib, StringComparer.OrdinalIgnoreCase)) libs.Add(lib);
                    }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        return libs.Where(Directory.Exists).ToList();
    }

    private static IEnumerable<GameInfo> FindSteam(string? root)
    {
        if (root is null) yield break;
        foreach (var lib in SteamLibraries(root))
        {
            string[] manifests;
            try { manifests = Directory.GetFiles(lib, "appmanifest_*.acf"); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { continue; }
            foreach (var m in manifests)
            {
                GameInfo? g = null;
                try { g = ParseSteamManifest(File.ReadAllText(m), lib); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
                if (g is not null) yield return g;
            }
        }
    }

    public static GameInfo? ParseSteamManifest(string text, string libraryFolder)
    {
        var state = Vdf.Parse(text).Child("AppState");
        if (state?.Str("name") is not { Length: > 0 } name) return null;
        var appId = state.Str("appid");
        long.TryParse(state.Str("SizeOnDisk"), out var size);
        long.TryParse(state.Str("LastPlayed"), out var last);
        var installDir = state.Str("installdir") ?? name;
        bool tool = (appId is not null && ToolAppIds.Contains(appId)) ||
                    name.StartsWith("Steamworks Common", StringComparison.OrdinalIgnoreCase) || name.StartsWith("Steam Linux Runtime", StringComparison.OrdinalIgnoreCase) ||
                    name.StartsWith("Proton", StringComparison.OrdinalIgnoreCase);
        return new GameInfo(GameLauncher.Steam, name, Path.Combine(libraryFolder, "common", installDir), size,
            last > 0 ? DateTimeOffset.FromUnixTimeSeconds(last).UtcDateTime : null, appId, tool);
    }

    private static IEnumerable<GameInfo> FindEpic(string folder)
    {
        if (!Directory.Exists(folder)) yield break;
        string[] items;
        try { items = Directory.GetFiles(folder, "*.item"); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { yield break; }
        foreach (var f in items)
        {
            GameInfo? g = null;
            try { g = ParseEpicManifest(File.ReadAllText(f)); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { }
            if (g is not null) yield return g;
        }
    }

    public static GameInfo? ParseEpicManifest(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var r = doc.RootElement;
        string? Get(string n) => r.TryGetProperty(n, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
        var name = Get("DisplayName");
        var location = Get("InstallLocation");
        if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(location)) return null;
        if (r.TryGetProperty("bIsIncompleteInstall", out var inc) && inc.ValueKind == JsonValueKind.True) return null;
        long size = r.TryGetProperty("InstallSize", out var s) && s.TryGetInt64(out var n2) ? n2 : 0;
        return new GameInfo(GameLauncher.Epic, name, location.Replace('/', '\\'), size, null, Get("AppName"));
    }
}
