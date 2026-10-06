using System.Numerics;
using RaidReplay.Core.Model;

namespace RaidReplay.Cli;

internal static partial class CliApp
{
    /// <summary>
    /// rr arrows &lt;file&gt; &lt;n&gt;: Tele-trouncing / Graven Image III inspection — teleporters (position, direction, who dropped
    /// it, who used it and when), arrow debuffs per player, confused players' paths and player-on-player kills.
    /// </summary>
    private static int Arrows(Options o)
    {
        var r = LoadPull(o, 0, 1);
        const uint teleporterBase = 0x1EC023;
        Console.WriteLine("arrow debuffs (Tele-portent):");
        foreach (var s in r.Statuses.Where(s => s.Target.IsPlayer && s.Name.StartsWith("Tele-portent", StringComparison.OrdinalIgnoreCase))
                                    .OrderBy(s => s.EndMs))
        {
            s.Target.Track.TrySample(s.EndMs, out var at, out _);
            Console.WriteLine($"  {s.Target.Name,-22} {s.Name,-24} {FormatDuration(s.StartMs)}..{FormatDuration(s.EndMs)} at ({at.X:0.0},{at.Y:0.0})");
        }

        Console.WriteLine("teleporters:");
        foreach (var a in r.Actors.Where(a => a.BNpcBaseId == teleporterBase).OrderBy(a => a.SpawnMs))
        {
            a.Track.TrySample(a.SpawnMs + 50, out var pos, out var h);
            var owner = r.Party.Select(p => (p, d: p.Track.TrySample(a.SpawnMs, out var pp, out _) ? Vector2.Distance(pp, pos) : 99))
                         .MinBy(x => x.d);
            var gone = a.DespawnMs == int.MaxValue ? "-" : FormatDuration(a.DespawnMs);
            var user = a.DespawnMs < r.LastMs
                           ? r.Party.Select(p => (p, d: p.Track.TrySample(a.DespawnMs, out var pp, out _) ? Vector2.Distance(pp, pos) : 99))
                              .MinBy(x => x.d)
                           : default;
            Console.WriteLine($"  {FormatDuration(a.SpawnMs),8} ({pos.X,6:0.0},{pos.Y,6:0.0}) points {Compass(h),-2} ({Deg(h),4:0}°) dropped by {owner.p?.Name} ({owner.d:0.0}y)" +
                              $"  gone {gone}{(user.p != null ? $" nearest {user.p.Name} {user.d:0.0}y" : "")}");
        }

        var confused = r.Statuses.Where(s => s.Target.IsPlayer && s.Name.Equals("Confused", StringComparison.OrdinalIgnoreCase)).ToList();
        foreach (var c in confused)
        {
            Console.WriteLine($"confused {c.Target.Name} {FormatDuration(c.StartMs)}..{FormatDuration(c.EndMs)}:");
            for (var t = c.StartMs - 3000; t <= Math.Min(c.EndMs, r.EndMs); t += 500)
            {
                if (c.Target.Track.TrySample(t, out var p, out var h))
                    Console.Write($" {FormatDuration(t)}({p.X:0},{p.Y:0})");
            }

            Console.WriteLine();
        }

        Console.WriteLine("player-on-player hits:");
        foreach (var a in r.Actions.Where(a => a.Source.IsPlayer && a.Hits.Any(h => h.Target.IsPlayer && h.Target != a.Source && h.Damage > 0)))
        {
            foreach (var h in a.Hits.Where(h => h.Target.IsPlayer && h.Target != a.Source && h.Damage > 0))
                Console.WriteLine($"  {FormatDuration(a.T)} {a.Source.Name} -> {h.Target.Name} {a.Name} {h.Damage:N0}");
        }

        foreach (var d in r.Directors.Where(d => d.Command == 0x80000027))
            Console.WriteLine($"director 80000027 {d.P1:X2} at {FormatDuration(d.T)}");

        var square = Core.Analysis.ArrowSquare.Evaluate(r);
        if (square != null)
            PrintArrowSquare(r, square);
        return 0;
    }

    private static void PrintArrowSquare(PullReplay r, Core.Analysis.ArrowSquareResult square)
    {
        Console.WriteLine($"arrow puzzle: {(square.Solved ? "SOLVED" : "not solved")}, {square.Drops.Count} drops, {square.Teleporters.Count} teleporters, " +
                          $"{square.Unused.Count()} unused");
        foreach (var t in square.Teleporters)
        {
            var a = t.Arrow;
            var spot = a?.Meant == null ? "no spot" : square.OnSpot(a) ? a.Meant.Name : $"OFF {a.Meant.Name} {a.Off:0.0}y";
            var state = t.ConsumedBy != null ? $"stacked by {t.ConsumedBy.Player.Name}" :
                        t.SetOffBy != null ? $"set off by {t.SetOffBy.Name}" :
                        t.UsedBy != null ? $"used by {t.UsedBy.Player.Name} {FormatDuration(t.UsedMs)}" :
                        t.Available ? "UNUSED" : "-";
            Console.WriteLine($"  ({t.Pos.X,6:0.0},{t.Pos.Y,6:0.0}) {Core.Analysis.ArrowSquare.Compass(t.Heading),-2} {t.Owner?.Name,-20} " +
                              $"{spot,-18} {state}");
        }

        foreach (var d in square.Drops.Where(d => d.Failed || d.Dead))
            Console.WriteLine($"  drop {d.Player.Name} {FormatDuration(d.T)} ({d.Pos.X:0.0},{d.Pos.Y:0.0}) {(d.Dead ? "DEAD" : "no teleporter")} meant {d.Arrow?.Meant?.Name}");
        foreach (var route in square.Routes)
        {
            Console.WriteLine($"  route {route.Player.Name}: start ({route.StartPos.X:0.0},{route.StartPos.Y:0.0}) " +
                              $"{route.Used.Count} arrows, end ({route.EndPos.X:0.0},{route.EndPos.Y:0.0}) {(route.Complete ? "complete" : "BROKEN")}");
        }

        foreach (var f in square.AllFindings)
            Console.WriteLine($"  [{f.Fault}] {FormatDuration(f.T)} blame {string.Join(", ", f.Culprits.Select(c => c.Name))}: {f.Text}");
    }
}
