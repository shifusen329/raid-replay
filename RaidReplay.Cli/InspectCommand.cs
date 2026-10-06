using RaidReplay.Core.Geometry;
using RaidReplay.Core.Model;

namespace RaidReplay.Cli;

internal static partial class CliApp
{
    /// <summary>rr inspect file n actionHex [--limit k]: per-instance geometry vs actual hits.</summary>
    private static int Inspect(Options o)
    {
        var r = LoadPull(o, 0, 1);
        var id = Convert.ToUInt32(o.Arg(2, "actionId").Replace("0x", ""), 16);
        var limit = o.Int("limit", 6);
        var shown = 0;
        foreach (var a in r.Actions.Where(a => a.ActionId == id))
        {
            if (shown++ >= limit)
                break;
            var cast = a.Cast;
            Console.WriteLine($"{FormatDuration(a.T)} {a.Name} src={a.Source.DisplayName}({a.Source.Id:X}) srcPos=({a.SourcePos.X:0.00},{a.SourcePos.Y:0.00}) srcH={Deg(a.SourceHeading):0} rot={(a.RotationHeading is { } rh ? Deg(rh).ToString("0") : "-")} x264={(a.ExtraHeading is { } eh ? Deg(eh).ToString("0") : "-")} loc={a.Location} prim={a.PrimaryTarget?.Name} anim={a.AnimationTarget?.Name} n={a.TargetCount}");
            if (cast != null)
                Console.WriteLine($"   cast {FormatDuration(cast.StartMs)} pos=({cast.Pos.X:0.00},{cast.Pos.Y:0.00}) h={Deg(cast.Heading):0} extra={cast.HasExtra} tgt={cast.Target?.Name}");
            foreach (var aoe in r.Aoes.Where(x => x.Action == a))
            {
                var (origin, heading) = aoe.Placement(a.T);
                Console.WriteLine($"   aoe {aoe.Shape} origin=({origin.X:0.00},{origin.Y:0.00}) h={Deg(heading):0} follow={aoe.Follow?.Name} excl={aoe.ExcludeActor?.Name}");
            }

            var hitSet = a.Hits.Where(h => h.Target.IsPlayer).Select(h => h.Target).ToHashSet();
            var refOrigin = r.Aoes.FirstOrDefault(x => x.Action == a)?.Placement(a.T).Origin ?? (cast?.Pos ?? a.SourcePos);
            var refHeading = r.Aoes.FirstOrDefault(x => x.Action == a)?.Placement(a.T).Heading ?? (cast?.Heading ?? a.SourceHeading);
            foreach (var p in r.Party)
            {
                if (!p.Track.TrySample(a.T, out var pos, out var ph))
                    continue;
                var d = pos - refOrigin;
                var ang = Angles.Wrap(MathF.Atan2(d.X, d.Y) - refHeading);
                var hit = a.Hits.FirstOrDefault(h => h.Target == p);
                Console.WriteLine($"     {(hitSet.Contains(p) ? "HIT " : "    ")}{JobOf(p),-4} {p.Name,-22} ({pos.X,6:0.00},{pos.Y,6:0.00}) dist={d.Length(),5:0.0} ang={Deg(ang),5:0} hitPos={(hit != null ? $"({hit.TargetPos.X:0.00},{hit.TargetPos.Y:0.00})" : "")} dmg={hit?.Damage}");
            }
        }

        return 0;
    }

    private static string JobOf(Actor a) => RaidReplay.Core.GameData.Jobs.Abbrev(a.Job);
}

internal static partial class CliApp
{
    /// <summary>rr track file n actorHex fromSec toSec: raw track samples.</summary>
    private static int Track(Options o)
    {
        var r = LoadPull(o, 0, 1);
        var id = Convert.ToUInt32(o.Arg(2, "actorId").Replace("0x", ""), 16);
        var from = (int)(double.Parse(o.Arg(3, "from"), System.Globalization.CultureInfo.InvariantCulture) * 1000);
        var to = (int)(double.Parse(o.Arg(4, "to"), System.Globalization.CultureInfo.InvariantCulture) * 1000);
        foreach (var a in r.Actors.Where(a => a.Id == id))
        {
            Console.WriteLine($"{a} kind={a.Kind} spawn={a.SpawnMs} despawn={a.DespawnMs}");
            var tr = a.Track;
            for (var i = 0; i < tr.Count; i++)
            {
                if (tr.T[i] >= from && tr.T[i] <= to)
                    Console.WriteLine($"  {FormatDuration(tr.T[i])} ({tr.X[i]:0.00},{tr.Y[i]:0.00}) h={Deg(tr.H[i]):0} {tr.Flags[i]}");
            }
        }

        return 0;
    }
}
