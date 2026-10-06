using System.Diagnostics;
using System.Numerics;
using RaidReplay.Core.Encounters;
using RaidReplay.Core.GameData;
using RaidReplay.Core.Geometry;
using RaidReplay.Core.Loading;
using RaidReplay.Core.Model;

namespace RaidReplay.Cli;

internal static partial class CliApp
{
    private static PullReplay LoadPull(Options o, int fileArg, int pullArg)
    {
        var summary = FindPull(o, fileArg, pullArg, out _);
        var sw = Stopwatch.StartNew();
        var replay = PullLoader.Load(summary, GameDataFor(o), Registry(o));
        Console.Error.WriteLine($"loaded pull #{summary.Ordinal} in {sw.ElapsedMilliseconds} ms");
        return replay;
    }

    private static int DumpPull(Options o)
    {
        var r = LoadPull(o, 0, 1);
        var s = r.Summary;
        Console.WriteLine(FormatPull(s));
        Console.WriteLine($"encounter: {r.Encounter?.Def.Name ?? "(none)"}  window {FormatDuration(r.FirstMs)} .. {FormatDuration(r.LastMs)}  map {r.InitialMapId}");
        var kinds = r.Actors.GroupBy(a => a.Kind).Select(g => $"{g.Key}={g.Count()}");
        Console.WriteLine($"actors: {string.Join(" ", kinds)}  casts={r.Casts.Count} actions={r.Actions.Count} statuses={r.Statuses.Count} " +
                          $"aoes={r.Aoes.Count} tethers={r.Tethers.Count} markers={r.HeadMarkers.Count} mapfx={r.MapEffects.Count}");
        Console.WriteLine();
        Console.WriteLine("party:");
        foreach (var p in r.Party)
        {
            var deaths = r.Deaths.Count(d => d.Victim == p);
            Console.WriteLine($"  {Jobs.Abbrev(p.Job),-4} {p.Name,-24} samples={p.Track.Count,-5} deaths={deaths}");
        }

        Console.WriteLine();
        Console.WriteLine("phases:");
        foreach (var ph in r.Phases)
            Console.WriteLine($"  {(ph.IsSegment ? "  " : "")}{FormatDuration(ph.StartMs),8} .. {FormatDuration(ph.EndMs),8}  {ph.Name}");
        Console.WriteLine();
        Console.WriteLine("deaths:");
        foreach (var d in r.Deaths)
        {
            var raised = d.RaisedMs >= 0 ? $" raised {FormatDuration(d.RaisedMs)}" : "";
            Console.WriteLine($"  {FormatDuration(d.T),8}  {d.Victim.DisplayName,-24} at ({d.Pos.X:0.0},{d.Pos.Y:0.0})  {d.Cause}{raised}");
        }

        Console.WriteLine();
        Console.WriteLine("enemy casts:");
        foreach (var c in r.Casts)
        {
            if (c.Source.IsPlayer || c.Source.Kind == ActorKind.Pet)
                continue;
            var res = c.Resolution != null ? $"-> {FormatDuration(c.Resolution.T)} hits={c.Resolution.Hits.Count}" : c.Outcome.ToString();
            Console.WriteLine($"  {FormatDuration(c.StartMs),8}  {c.ActionId:X4} {c.Name,-28} {c.Source.Kind,-6} {c.DurationMs / 1000.0,4:0.0}s " +
                              $"@({c.Pos.X:0.0},{c.Pos.Y:0.0}) h={Deg(c.Heading),4:0}° {res}");
        }

        if (o.Has("events"))
        {
            Console.WriteLine();
            Console.WriteLine("enemy actions:");
            foreach (var a in r.Actions)
            {
                if (a.Source.IsPlayer || a.Source.Kind == ActorKind.Pet)
                    continue;
                var dmg = a.Hits.Sum(h => h.Damage);
                Console.WriteLine($"  {FormatDuration(a.T),8}  {a.ActionId:X4} {a.Name,-28} {a.Source.Kind,-6} hits={a.Hits.Count,-2} dmg={dmg,-8} " +
                                  $"src=({a.SourcePos.X:0.0},{a.SourcePos.Y:0.0}) rot={(a.RotationHeading is { } rh ? Deg(rh).ToString("0") : "-"),4} " +
                                  $"{(a.Cast != null ? "cast" : "")}");
            }

            Console.WriteLine();
            Console.WriteLine("head markers:");
            foreach (var m in r.HeadMarkers)
                Console.WriteLine($"  {FormatDuration(m.T),8}  {m.MarkerId:X4} {m.Target.DisplayName,-24} {m.Label}");
            Console.WriteLine();
            Console.WriteLine("tethers:");
            foreach (var t in r.Tethers)
                Console.WriteLine($"  {FormatDuration(t.StartMs),8}..{FormatDuration(t.EndMs),-8} {t.TetherId:X4} {t.Source.DisplayName} -> {t.Target.DisplayName} {t.Label}");
            Console.WriteLine();
            Console.WriteLine("mechanics:");
            foreach (var m in r.Mechanics)
                Console.WriteLine($"  {FormatDuration(m.T),8}  {m.Label}");
        }

        foreach (var d in r.Diagnostics)
            Console.WriteLine($"diag: {d}");
        return 0;
    }

    private static int DumpFrame(Options o)
    {
        var r = LoadPull(o, 0, 1);
        var t = (int)(o.Double("at", 0) * 1000);
        Console.WriteLine($"t={FormatDuration(t)} phase={r.PhaseAt(t)}");
        Console.WriteLine("party:");
        foreach (var p in r.Party)
            Console.WriteLine("  " + DescribeActor(r, p, t));
        Console.WriteLine("enemies:");
        foreach (var a in r.Actors)
        {
            if (a.Kind is ActorKind.Boss or ActorKind.Enemy && a.IsPresent(t))
                Console.WriteLine("  " + DescribeActor(r, a, t) + (a.IsHidden(t) ? " [hidden]" : "") + (!a.IsTargetable(t) ? " [untargetable]" : ""));
        }

        Console.WriteLine("eobjs:");
        foreach (var a in r.Actors)
        {
            if (a.Kind == ActorKind.EventObject && a.Render && a.IsPresent(t))
                Console.WriteLine("  " + DescribeActor(r, a, t));
        }

        Console.WriteLine("aoes:");
        foreach (var aoe in r.Aoes)
        {
            if (t < aoe.StartMs || t > aoe.EndMs)
                continue;
            var (origin, heading) = aoe.Placement(t);
            var inside = r.Party.Where(p => p.Track.TrySample(Math.Min(t, aoe.ResolveMs), out var pp, out _) &&
                                            ShapeMath.Contains(aoe.Shape, origin, heading, pp, 0.5f))
                          .Select(p => p.Name.Split(' ')[0]);
            Console.WriteLine($"  {aoe.Label,-28} {aoe.Category,-12} {aoe.Shape,-22} @({origin.X:0.0},{origin.Y:0.0}) h={Deg(heading),4:0}° " +
                              $"{FormatDuration(aoe.StartMs)}->{FormatDuration(aoe.ResolveMs)} {(aoe.Inferred ? "inferred " : "")}[{aoe.ShapeSource}] in: {string.Join(",", inside)}");
        }

        Console.WriteLine("markers:");
        foreach (var m in r.HeadMarkers)
        {
            if (t >= m.T && t <= m.T + m.DurationMs)
                Console.WriteLine($"  {m.MarkerId:X4} {m.Target.DisplayName} {m.Label}");
        }

        foreach (var te in r.Tethers)
        {
            if (t >= te.StartMs && t <= te.EndMs)
                Console.WriteLine($"  tether {te.TetherId:X4} {te.Source.DisplayName} -> {te.Target.DisplayName} {te.Label}");
        }

        return 0;
    }

    internal static string DescribeActor(PullReplay r, Actor a, int t)
    {
        var pos = a.Track.TrySample(t, out var p, out var h) ? $"({p.X,6:0.00},{p.Y,6:0.00}) h={Deg(h),4:0}°" : "(no position)";
        var hp = a.Hp.At(t);
        var hpText = a.MaxHp > 0 && hp >= 0 ? $"{100.0 * hp / a.MaxHp,5:0.0}%" : "     ";
        var dead = r.Deaths.Any(d => d.Victim == a && d.T <= t && (d.RaisedMs < 0 || d.RaisedMs > t)) ? " DEAD" : "";
        var statuses = r.Statuses.Where(s => s.Target == a && s.Active(t) && s.Source is { IsPlayer: false })
                        .Select(s => s.Name).Distinct().Take(4);
        return $"{Jobs.Abbrev(a.Job),-4} {a.DisplayName,-24} {pos} hp={hpText}{dead} {string.Join(", ", statuses)}";
    }

    internal static float Deg(float rad) => rad * 180f / MathF.PI;

    private static int DumpAoes(Options o)
    {
        var r = LoadPull(o, 0, 1);
        foreach (var aoe in r.Aoes)
        {
            var (origin, heading) = aoe.Placement(aoe.ResolveMs);
            var hits = aoe.Action?.Hits.Count(h => h.Target.IsPlayer) ?? -1;
            Console.WriteLine($"{FormatDuration(aoe.StartMs),8}->{FormatDuration(aoe.ResolveMs),-8} {aoe.ActionId:X4} {aoe.Label,-30} {aoe.Category,-12} " +
                              $"{aoe.Shape,-24} @({origin.X:0.0},{origin.Y:0.0}) h={Deg(heading),4:0}° hits={hits,-2} [{aoe.ShapeSource}]{(aoe.Inferred ? " inferred" : "")}");
        }

        return 0;
    }

    private static int Phases(Options o)
    {
        var path = o.ResolveFile(o.Arg(0, "file"));
        var index = IndexOne(o, path);
        foreach (var p in index.Pulls.Where(p => p.HasDirector))
        {
            var phases = string.Join(" | ", p.Phases.Select(ph => $"{ph.Name} @{FormatDuration((int)((ph.Ticks - p.StartTicks) / 10000))}"));
            Console.WriteLine($"#{p.Ordinal,-4} {FormatDuration(p.DurationMs),8} {p.Outcome,-8} {phases}");
        }

        var groups = index.Pulls.Where(p => p.HasDirector).GroupBy(p => p.FurthestPhase ?? "-").Select(g => $"{g.Key}: {g.Count()}");
        Console.WriteLine(string.Join(", ", groups));
        return 0;
    }

    private static int ValidateEncounters(Options o)
    {
        var registry = Registry(o);
        foreach (var e in registry.Encounters)
        {
            Console.WriteLine($"{e.Key,-12} {e.Def.Name,-32} {e.Source} abilities={e.Abilities.Count} phases={e.Def.Phases.Count} " +
                              $"mechanics={e.Def.Mechanics.Count} triggers={e.Def.Triggers.Count} hash={e.Hash}");
        }

        foreach (var err in registry.Errors)
            Console.WriteLine($"ERROR {err.Source}: {err.Message}");
        return registry.Errors.Count == 0 ? 0 : 1;
    }

    private static EncounterRegistry? registry;

    internal static EncounterRegistry Registry(Options o) =>
        registry ??= EncounterRegistry.Load(o.Get("packs"));

    private static (Core.Indexing.IPullObserverFactory?, Func<IEnumerable<uint>, string>?) EncounterSupport(Options o)
    {
        var reg = Registry(o);
        return (new EncounterObserverFactory(reg), reg.HashFor);
    }
}
