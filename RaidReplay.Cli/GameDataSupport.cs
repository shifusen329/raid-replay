using RaidReplay.Core.GameData;

namespace RaidReplay.Cli;

internal static partial class CliApp
{
    private const string DefaultGamePath = @"C:\Program Files (x86)\SquareEnix\FINAL FANTASY XIV - A Realm Reborn\game";

    private static IGameData? gameData;

    internal static IGameData GameDataFor(Options o)
    {
        if (gameData != null)
            return gameData;
        if (o.Has("no-game"))
            return gameData = NullGameData.Instance;
#if LUMINA
        var game = o.Get("game") ?? Environment.GetEnvironmentVariable("FFXIV_GAME") ?? DefaultGamePath;
        var sqpack = Path.Combine(game, "sqpack");
        if (Directory.Exists(sqpack))
        {
            try
            {
                var lumina = new Lumina.GameData(sqpack, new Lumina.LuminaOptions { PanicOnSheetChecksumMismatch = false });
                return gameData = new RaidReplay.GameData.LuminaGameData(lumina.Excel);
            }
            catch (Exception e)
            {
                Console.Error.WriteLine($"warning: could not open game data at {sqpack}: {e.Message}");
            }
        }
        else
        {
            Console.Error.WriteLine($"warning: game data not found at {sqpack}; shapes limited to encounter packs (--game <dir>)");
        }
#endif
        return gameData = NullGameData.Instance;
    }
}
