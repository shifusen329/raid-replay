using System.Numerics;
using RaidReplay.Core.Loading;

namespace RaidReplay.Cli;

internal static partial class CliApp
{
    /// <summary>rr stack-survey &lt;file&gt; &lt;actionHex&gt;: every resolution of a stack — holder, absorbers, damage, deaths right after, knockbacks.</summary>
    private static int StackSurvey(Options o)
    {
        var path = o.ResolveFile(o.Arg(0, "file"));
        var action = ParseHex(o.Arg(1, "action"));
        var index = IndexOne(o, path);
        var data = GameDataFor(o);
        var reg = Registry(o);
        foreach (var p in index.Pulls.Where(p => p.EncounterKey != null && p.DurationMs > 40000))
        {
            var r = PullLoader.Load(p, data, reg);
            foreach (var a in r.Actions.Where(a => a.ActionId == action))
            {
                var holder = a.AnimationTarget ?? a.PrimaryTarget;
                var hits = a.Hits.Where(h => h.Target.IsPlayer).ToList();
                var died = r.Deaths.Where(d => d.T >= a.T - 200 && d.T <= a.T + 3000 && d.Victim.IsPlayer).Select(d => d.Victim).ToList();
                var hp = Vector2.Zero;
                holder?.Track.TrySample(a.T, out hp, out _);
                var hitText = string.Join(" ", hits.Select(h => $"{h.Target.Name.Split(' ')[0]}:{h.Damage / 1000}k{(h.Knockback ? "kb" : "")}{(died.Contains(h.Target) ? "+DIED" : "")}"));
                var tp = r.Actors.Where(x => x.BNpcBaseId == 0x1EC023 && x.DespawnMs >= a.T && x.DespawnMs <= a.T + 3000).Select(x => FormatDuration(x.DespawnMs));
                var geo = string.Empty;
                if (o.Has("geo") && holder != null)
                {
                    // Where the holder and each soaker stood, relative to the nearer of waymarks 1 and 3.
                    var w1 = r.WaymarkAt(4, a.T);
                    var w3 = r.WaymarkAt(6, a.T);
                    var mark = (Vector2 v) => w1 is { } m1 && w3 is { } m3
                                                  ? Vector2.Distance(v, m1) < Vector2.Distance(v, m3) ? $"1{v - m1:+0.0;-0.0}" : $"3{v - m3:+0.0;-0.0}"
                                                  : "";
                    geo = $" w1={w1} w3={w3} holder@({hp.X:0.0},{hp.Y:0.0}) {mark(hp)}" + string.Concat(hits.Select(h =>
                    {
                        h.Target.Track.TrySample(a.T, out var v, out _);
                        return $" {h.Target.Name.Split(' ')[0]}@({v.X:0.0},{v.Y:0.0}) {mark(v)}";
                    }));
                }

                Console.WriteLine($"#{p.Ordinal,-4} {FormatDuration(a.T)} holder {holder?.Name,-20} hits={hits.Count} [{hitText}] died=[{string.Join(",", died.Select(d => d.Name.Split(' ')[0]))}]" +
                                  (tp.Any() ? $" arrows gone {string.Join(",", tp)}" : "") + geo);
            }
        }

        return 0;
    }
}
