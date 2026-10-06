using System.Diagnostics;
using RaidReplay.Core.Analysis;
using RaidReplay.Core.Loading;
using RaidReplay.Core.Model;

namespace RaidReplay.Cli;

internal static partial class CliApp
{
    /// <summary>rr learn [file|all]: builds the per-encounter position profile from successful mechanic moments.</summary>
    private static int Learn(Options o)
    {
        var arg = o.OptionalArg(0) ?? "latest";
        var files = arg == "all" ? Core.Indexing.LogLibrary.EnumerateLogs(o.LogsDir).ToList() : [o.ResolveFile(arg)];
        var profiles = new Dictionary<string, PositionProfile>();
        var data = GameDataFor(o);
        var reg = Registry(o);
        var sw = Stopwatch.StartNew();
        var loaded = 0;
        foreach (var file in files)
        {
            var index = IndexOne(o, file);
            var pulls = index.Pulls.Where(p => p.EncounterKey != null && p.DurationMs > 10000).ToList();
            Parallel.ForEach(pulls, new ParallelOptions { MaxDegreeOfParallelism = Math.Max(1, Environment.ProcessorCount / 2) }, p =>
            {
                PositionProfile profile;
                lock (profiles)
                {
                    if (!profiles.TryGetValue(p.EncounterKey!, out profile!))
                        profiles[p.EncounterKey!] = profile = PositionProfile.Load(o.CacheDir, p.EncounterKey!);
                }

                if (profile.Contains(p.Key) && !o.Has("rebuild"))
                    return;
                var r = PullLoader.Load(p, data, reg);
                profile.Add(PositionProfile.Extract(r));
                Interlocked.Increment(ref loaded);
            });
        }

        foreach (var p in profiles.Values)
        {
            p.Save(o.CacheDir);
            Console.WriteLine($"{p.Encounter}: {p.PullCount} pulls in profile");
        }

        Console.WriteLine($"loaded {loaded} pulls in {sw.Elapsed.TotalSeconds:N1}s");
        return 0;
    }

    private static int Analyze(Options o)
    {
        var r = LoadPull(o, 0, 1);
        PositionProfile? profile = null;
        if (r.Encounter != null && !o.Has("no-profile"))
            profile = PositionProfile.Load(o.CacheDir, r.Encounter.Key);
        var sw = Stopwatch.StartNew();
        var report = WipeAnalyzer.Analyze(r, profile);
        Console.Error.WriteLine($"analyzed in {sw.ElapsedMilliseconds} ms (profile: {report.LearnedPulls} pulls)");
        PrintReport(report, o.Has("all"));
        return 0;
    }

    internal static void PrintReport(WipeReport report, bool allSnapshots)
    {
        Console.WriteLine(report.ChatLine);
        Console.WriteLine();
        Console.WriteLine($"VERDICT: {report.Headline}");
        foreach (var n in report.Notes)
            Console.WriteLine($"  · {n}");
        Console.WriteLine();
        foreach (var inc in report.Incidents)
        {
            var mark = inc.IsRootCause ? ">>" : "  ";
            Console.WriteLine($"{mark} {FormatDuration(inc.T),8} [{inc.Kind}] {inc.Title}{(inc.Mechanic != null ? $"  <{inc.Mechanic}>" : "")}");
            if (inc.Detail.Length > 0)
                Console.WriteLine($"            {inc.Detail}");
            if (!inc.IsRootCause && !allSnapshots)
                continue;
            foreach (var s in inc.Snapshot)
            {
                var exp = s.Expected is { } e
                              ? $" -> should be ({e.X:0.0},{e.Y:0.0}) {s.MissDistance:0.0}y {Direction(s.Pos, e)} [{s.ExpectedSource}{(s.ExpectedNote != null ? $": {s.ExpectedNote}" : "")}]"
                              : "";
                Console.WriteLine($"            {(s.Involved ? "*" : " ")} {Core.GameData.Jobs.Abbrev(s.Player.Job),-4} {s.Player.Name,-22} " +
                                  $"({s.Pos.X,6:0.0},{s.Pos.Y,6:0.0}) facing {Compass(s.Heading),-2} hp={(s.HpPct >= 0 ? $"{s.HpPct:0}%" : "?"),-4}" +
                                  $"{(s.Alive ? "" : " DEAD")}{exp}");
            }
        }
    }

    internal static string Compass(float heading)
    {
        // heading 0 = south (+Y), π/2 = east (+X), ±π = north.
        string[] names = ["S", "SE", "E", "NE", "N", "NW", "W", "SW"];
        var idx = (int)Math.Round(heading / (Math.PI / 4));
        return names[((idx % 8) + 8) % 8];
    }

    internal static string Direction(System.Numerics.Vector2 from, System.Numerics.Vector2 to)
    {
        var d = to - from;
        return d.LengthSquared() < 0.01f ? "" : Compass(MathF.Atan2(d.X, d.Y));
    }
}
