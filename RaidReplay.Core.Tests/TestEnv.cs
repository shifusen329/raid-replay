using RaidReplay.Core.Encounters;
using RaidReplay.Core.Indexing;
using RaidReplay.Core.Model;

namespace RaidReplay.Core.Tests;

internal static class TestEnv
{
    public static string Fixture(string name) => Path.Combine(AppContext.BaseDirectory, "Fixtures", name);

    public static EncounterRegistry Registry { get; } = EncounterRegistry.Load();

    public static string TempDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "raidreplay-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    /// <summary>Indexes a fixture (no cache) with encounter phase detection.</summary>
    public static FileIndex Index(string fixture) =>
        IndexStore.IndexFile(Fixture(fixture), null, new EncounterObserverFactory(Registry), Registry.HashFor);

    public static PullSummary OnlyPull(string fixture) => Assert.Single(Index(fixture).Pulls);

    /// <summary>LOGS_PATH from the environment or a .env file above the test directory.</summary>
    public static string? LogsPath
    {
        get
        {
            var env = Environment.GetEnvironmentVariable("LOGS_PATH");
            if (!string.IsNullOrEmpty(env))
                return env;
            for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir != null; dir = dir.Parent)
            {
                var file = Path.Combine(dir.FullName, ".env");
                if (!File.Exists(file))
                    continue;
                foreach (var line in File.ReadAllLines(file))
                {
                    var t = line.Trim();
                    if (t.StartsWith("LOGS_PATH=", StringComparison.Ordinal))
                        return t["LOGS_PATH=".Length..].Trim().Trim('"');
                }
            }

            return null;
        }
    }
}

/// <summary>A test that needs the real ACT log folder (LOGS_PATH); skipped when it is unavailable (e.g. CI).</summary>
public sealed class LogsFactAttribute : FactAttribute
{
    public LogsFactAttribute(string? file = null)
    {
        var dir = TestEnv.LogsPath;
        if (dir == null || !Directory.Exists(dir))
            Skip = "LOGS_PATH not available";
        else if (file != null && !File.Exists(Path.Combine(dir, file)))
            Skip = $"{file} not found in LOGS_PATH";
    }
}
