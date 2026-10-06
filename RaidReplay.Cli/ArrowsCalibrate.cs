using System.Numerics;
using RaidReplay.Core.Loading;
using RaidReplay.Core.Model;

namespace RaidReplay.Cli;

internal static partial class CliApp
{
    /// <summary>rr arrows-calibrate &lt;file&gt;: raw stacking / trigger-radius measurements over every pull with teleporters.</summary>
    private static int ArrowsCalibrate(Options o)
    {
        var path = o.ResolveFile(o.Arg(0, "file"));
        var index = IndexOne(o, path);
        var data = GameDataFor(o);
        var reg = Registry(o);
        const uint teleporterBase = 0x1EC023;
        var okNearest = new List<float>();
        foreach (var p in index.Pulls.Where(p => p.EncounterKey != null && p.DurationMs > 160000))
        {
            var r = PullLoader.Load(p, data, reg);
            var tps = r.Actors.Where(a => a.BNpcBaseId == teleporterBase).ToList();
            if (tps.Count == 0)
                continue;
            var pos = tps.ToDictionary(a => a, a => a.Track.TrySample(a.SpawnMs + 50, out var v, out _) ? v : new Vector2(float.NaN));
            foreach (var s in r.Statuses.Where(s => s.Target.IsPlayer && s.Name.StartsWith("Tele-portent", StringComparison.OrdinalIgnoreCase) && s.EndMs < r.EndMs))
            {
                if (!s.Target.Track.TrySample(s.EndMs, out var at, out _))
                    continue;
                var spawned = tps.FirstOrDefault(a => Math.Abs(a.SpawnMs - s.EndMs - 700) <= 1000 && Vector2.Distance(pos[a], at) <= 2.5f);
                var existing = tps.Where(a => a != spawned && a.SpawnMs < s.EndMs && a.DespawnMs >= s.EndMs).ToList();
                var alive = Core.Analysis.ShapeValidator.IsAlive(r, s.Target, s.EndMs);
                if (spawned != null)
                {
                    if (existing.Count > 0)
                        okNearest.Add(existing.Min(a => Vector2.Distance(pos[a], at)));
                    continue;
                }

                var near = existing.Select(a => (a, d: Vector2.Distance(pos[a], at), gone: a.DespawnMs - s.EndMs))
                                   .Where(x => x.d < 8).OrderBy(x => x.d)
                                   .Select(x => $"{x.d:0.0}y gone+{x.gone}ms");
                Console.WriteLine($"#{p.Ordinal,-4} {FormatDuration(s.EndMs)} {s.Target.Name,-20} alive={alive} NO TELEPORTER at ({at.X:0.0},{at.Y:0.0}); near: {string.Join(", ", near)}");
            }
        }

        // Simulation vs the game's own verdict (director 80000027 0C = arrows solved).
        var agree = 0;
        var total = 0;
        foreach (var p in index.Pulls.Where(p => p.EncounterKey != null && p.DurationMs > 185000))
        {
            var r = PullLoader.Load(p, data, reg);
            var sq = Core.Analysis.ArrowSquare.Evaluate(r);
            if (sq == null || sq.ConfusedStartMs < 0 || r.EndMs < sq.ConfusedEndMs + 10000)
                continue;
            var game = r.Directors.Any(d => d.Command == 0x80000027 && d.P1 == 0x0C);
            total++;
            if (game == sq.Solved)
                agree++;
            Console.WriteLine($"#{p.Ordinal,-4} game={(game ? "solved" : "failed"),-6} sim={(sq.Solved ? "solved" : "failed"),-6} unused={sq.Unused.Count()} " +
                              (game != sq.Solved ? "MISMATCH" : ""));
            foreach (var f in sq.AllFindings)
                Console.WriteLine($"      [{f.Fault}] {FormatDuration(f.T)} blame {string.Join(", ", f.Culprits.Select(c => c.Name))}: {f.Text}");
        }

        Console.WriteLine($"sim agrees with the game on {agree}/{total} pulls");
        okNearest.Sort();
        Console.WriteLine($"successful drops with an existing teleporter: n={okNearest.Count}, nearest distances: " +
                          string.Join(" ", okNearest.Take(25).Select(d => d.ToString("0.0"))));
        return 0;
    }
}
