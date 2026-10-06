using System.Globalization;
using System.Numerics;
using RaidReplay.Core.Loading;
using RaidReplay.Core.Model;

namespace RaidReplay.Cli;

internal static partial class CliApp
{
    /// <summary>
    /// rr validate-contact &lt;file&gt; &lt;triggerAction&gt; &lt;eobjBase&gt; &lt;failureAction&gt; [--window s]: for every resolution of
    /// the trigger AoE (e.g. a knock-off "rock"), the closest distance from its origin to any hazard EObj (e.g. a puddle),
    /// and whether the failure action (the detonation) followed. Calibrates the contact distance used for blame.
    /// </summary>
    private static int ValidateContact(Options o)
    {
        var path = o.ResolveFile(o.Arg(0, "file"));
        var trigger = ParseHex(o.Arg(1, "triggerAction"));
        var eobj = ParseHex(o.Arg(2, "eobjBase"));
        var failure = ParseHex(o.Arg(3, "failureAction"));
        var window = (int)(o.Double("window", 1.5) * 1000);
        var index = IndexOne(o, path);
        var data = GameDataFor(o);
        var reg = Registry(o);
        var rows = new List<(int Pull, int T, string Player, float Dist, bool Detonated)>();
        var playerRows = new List<(int Pull, int T, float Dist, bool Detonated)>();
        foreach (var p in index.Pulls.Where(p => p.EncounterKey != null && p.DurationMs > 10000))
        {
            var r = PullLoader.Load(p, data, reg);
            var detonations = r.Actions.Where(a => a.ActionId == failure).Select(a => a.T).ToList();
            foreach (var group in r.Actions.Where(a => a.ActionId == trigger).GroupBy(a => a.T / 500))
            {
                var t = group.First().T;
                var hazards = r.Actors.Where(a => a.BNpcBaseId == eobj && a.SpawnMs <= t && a.DespawnMs >= t - 200)
                               .Select(a => a.Track.TrySample(t, out var pos, out _) ? pos : (Vector2?)null)
                               .Where(v => v != null).Select(v => v!.Value).ToList();
                if (hazards.Count == 0)
                    continue;
                var detonated = detonations.Any(d => d >= t - 100 && d - t <= window);
                var playerMin = r.Party.Where(p => Core.Analysis.ShapeValidator.IsAlive(r, p, t))
                                 .Select(p => p.Track.TrySample(t, out var pp, out _) ? hazards.Min(h => Vector2.Distance(h, pp)) : float.MaxValue)
                                 .DefaultIfEmpty(float.MaxValue).Min();
                playerRows.Add((p.Ordinal, t, playerMin, detonated));
                foreach (var a in group)
                {
                    var target = a.AnimationTarget ?? a.PrimaryTarget;
                    if (target == null || !target.Track.TrySample(a.T, out var origin, out _))
                        continue;
                    var dist = hazards.Min(h => Vector2.Distance(h, origin));
                    rows.Add((p.Ordinal, a.T, target.Name, dist, detonated));
                }
            }
        }

        // Per resolution, the closest trigger decides whether a detonation happened.
        var byMoment = rows.GroupBy(x => (x.Pull, x.T / 500)).Select(g => g.MinBy(x => x.Dist)).ToList();
        var det = byMoment.Where(x => x.Detonated).OrderByDescending(x => x.Dist).ToList();
        var safe = byMoment.Where(x => !x.Detonated).OrderBy(x => x.Dist).ToList();
        Console.WriteLine($"{byMoment.Count} resolutions: {det.Count} followed by a detonation, {safe.Count} not");
        Console.WriteLine("closest trigger when it detonated (largest distances):");
        foreach (var x in det.Take(8))
            Console.WriteLine($"  #{x.Pull,-4} {FormatDuration(x.T),8} {x.Player,-22} {x.Dist,5:0.00}y");
        Console.WriteLine("closest trigger when nothing happened (smallest distances):");
        foreach (var x in safe.Take(8))
            Console.WriteLine($"  #{x.Pull,-4} {FormatDuration(x.T),8} {x.Player,-22} {x.Dist,5:0.00}y");
        if (det.Count > 0 && safe.Count > 0)
        {
            Console.WriteLine($"=> contact distance between {det[0].Dist.ToString("0.00", CultureInfo.InvariantCulture)}y (max detonating) " +
                              $"and {safe[0].Dist.ToString("0.00", CultureInfo.InvariantCulture)}y (min safe)");
        }

        var inPuddleSafe = playerRows.Count(x => !x.Detonated && x.Dist < 5);
        var inPuddleDet = playerRows.Count(x => x.Detonated && x.Dist < 5);
        Console.WriteLine($"player within 5y of a hazard at trigger time: {inPuddleSafe}/{playerRows.Count(x => !x.Detonated)} non-detonating moments, {inPuddleDet}/{playerRows.Count(x => x.Detonated)} detonating");
        foreach (var x in playerRows.Where(x => !x.Detonated).OrderBy(x => x.Dist).Take(5))
            Console.WriteLine($"  safe moment #{x.Pull} {FormatDuration(x.T)} closest player {x.Dist:0.00}y");

        return 0;
    }

    private static uint ParseHex(string s) => Convert.ToUInt32(s.Replace("0x", "", StringComparison.OrdinalIgnoreCase), 16);
}
