using DesktopCleaner.Core.Games;
using Xunit;

namespace DesktopCleaner.Tests;

public sealed class GameTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("dc-games").FullName;
    public void Dispose() => Directory.Delete(_root, true);

    private const string Manifest = "\"AppState\"\n{\n\t\"appid\"\t\t\"2019300\"\n\t\"name\"\t\t\"Dokimon\"\n\t\"installdir\"\t\t\"Dokimon\"\n\t\"LastPlayed\"\t\t\"1787884883\"\n\t\"SizeOnDisk\"\t\t\"80879036\"\n\t\"InstalledDepots\"\n\t{\n\t\t\"2019301\"\n\t\t{\n\t\t\t\"size\"\t\t\"80879036\"\n\t\t}\n\t}\n}\n";

    [Fact]
    public void Vdf_parser_handles_nesting_escapes_comments_and_unquoted_tokens()
    {
        var n = Vdf.Parse("// header\n\"root\"\n{\n  \"a\" \"1\"\n  \"path\" \"C:\\\\Games\\\\Steam\"\n  \"inner\" { \"b\" \"2\" \"c\" { \"d\" \"3\" } }\n  plain value\n}\n");
        var root = n.Child("root")!;
        Assert.Equal("1", root.Str("a"));
        Assert.Equal(@"C:\Games\Steam", root.Str("path"));
        Assert.Equal("3", root.Child("inner")!.Child("c")!.Str("d"));
        Assert.Equal("value", root.Str("plain"));
        Assert.Empty(Vdf.Parse(""));
        Vdf.Parse("\"unterminated\" {");   // must not hang or throw
    }

    [Fact]
    public void Steam_manifest_gives_name_size_and_last_played_in_UTC()
    {
        var g = GameFinder.ParseSteamManifest(Manifest, @"D:\SteamLibrary\steamapps")!;
        Assert.Equal(("Dokimon", 80879036L, "2019300"), (g.Name, g.SizeBytes, g.AppId));
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1787884883).UtcDateTime, g.LastPlayedUtc);
        Assert.Equal(@"D:\SteamLibrary\steamapps\common\Dokimon", g.InstallPath);
        Assert.False(g.IsTool);
        Assert.Equal(40, g.DaysSincePlayed(g.LastPlayedUtc!.Value.AddDays(40)));
    }

    [Fact]
    public void Never_played_games_and_steam_tools_are_recognised()
    {
        var never = GameFinder.ParseSteamManifest("\"AppState\" { \"appid\" \"1\" \"name\" \"Fresh\" \"installdir\" \"Fresh\" \"LastPlayed\" \"0\" \"SizeOnDisk\" \"5\" }", @"C:\s")!;
        Assert.True(never.NeverPlayed); Assert.Null(never.DaysSincePlayed(DateTime.UtcNow));
        Assert.True(GameFinder.ParseSteamManifest("\"AppState\" { \"appid\" \"228980\" \"name\" \"Steamworks Common Redistributables\" }", @"C:\s")!.IsTool);
        Assert.Null(GameFinder.ParseSteamManifest("\"AppState\" { \"appid\" \"2\" }", @"C:\s"));   // no name: ignore
    }

    [Fact]
    public void Epic_manifest_gives_name_location_and_size_and_skips_incomplete_installs()
    {
        var g = GameFinder.ParseEpicManifest("{\"DisplayName\":\"Fortnite\",\"InstallLocation\":\"C:/Program Files/Epic Games/Fortnite\",\"InstallSize\":52029419070,\"AppName\":\"Fortnite\"}")!;
        Assert.Equal(("Fortnite", @"C:\Program Files\Epic Games\Fortnite", 52029419070L), (g.Name, g.InstallPath, g.SizeBytes));
        Assert.Equal(GameLauncher.Epic, g.Launcher); Assert.Null(g.LastPlayedUtc); Assert.False(g.NeverPlayed);
        Assert.Null(GameFinder.ParseEpicManifest("{\"DisplayName\":\"X\",\"InstallLocation\":\"C:/x\",\"bIsIncompleteInstall\":true}"));
        Assert.Null(GameFinder.ParseEpicManifest("{\"DisplayName\":\"No location\"}"));
    }

    [Fact]
    public void Finds_games_across_every_steam_library_and_epic_folder_and_survives_bad_files()
    {
        var steam = Path.Combine(_root, "Steam"); var lib2 = Path.Combine(_root, "Lib2");
        Directory.CreateDirectory(Path.Combine(steam, "steamapps")); Directory.CreateDirectory(Path.Combine(lib2, "steamapps"));
        File.WriteAllText(Path.Combine(steam, "steamapps", "libraryfolders.vdf"),
            $"\"libraryfolders\"\n{{\n \"0\" {{ \"path\" \"{steam.Replace("\\", "\\\\")}\" }}\n \"1\" {{ \"path\" \"{lib2.Replace("\\", "\\\\")}\" }}\n}}\n");
        File.WriteAllText(Path.Combine(steam, "steamapps", "appmanifest_2019300.acf"), Manifest);
        File.WriteAllText(Path.Combine(lib2, "steamapps", "appmanifest_5.acf"), Manifest.Replace("Dokimon", "Second Game").Replace("2019300", "5"));
        File.WriteAllText(Path.Combine(lib2, "steamapps", "appmanifest_6.acf"), "garbage { not vdf");
        var epic = Path.Combine(_root, "Epic"); Directory.CreateDirectory(epic);
        File.WriteAllText(Path.Combine(epic, "a.item"), "{\"DisplayName\":\"Epic Game\",\"InstallLocation\":\"C:/e\",\"InstallSize\":10}");
        File.WriteAllText(Path.Combine(epic, "bad.item"), "{ not json");

        var games = new GameFinder(steam, epic).Find();
        Assert.Equal(["Dokimon", "Epic Game", "Second Game"], games.Select(g => g.Name).Order());
        Assert.Equal(2, GameFinder.SteamLibraries(steam).Count);
        Assert.Empty(new GameFinder(Path.Combine(_root, "none"), Path.Combine(_root, "none2")).Find());
    }
}
