using System.Runtime.CompilerServices;
using RaidReplay.Core.Model;

namespace RaidReplay.Core.Analysis;

/// <summary>
/// Damage each party member dealt to enemies over a pull, at any moment of it: hits and DoT ticks, with pets' and
/// summons' damage credited to their owner. Built once per pull in one pass; a query is a binary search per player.
/// </summary>
/// <remarks>
/// Damage is counted in full, overkill included, like ACT. DoT ticks the log doesn't attribute to anyone (rare) are
/// left out.
/// </remarks>
public sealed class DamageMeter
{
    private static readonly ConditionalWeakTable<PullReplay, DamageMeter> Cache = new();

    private readonly Dictionary<Actor, (int[] T, long[] Total)> series = [];

    private DamageMeter(PullReplay r)
    {
        var party = r.Party.GroupBy(p => p.Id).ToDictionary(g => g.Key, g => g.First());
        Actor? Credit(Actor? a) => a switch
        {
            { IsPlayer: true } => party.GetValueOrDefault(a.Id),
            { Kind: ActorKind.Pet } => party.GetValueOrDefault(a.OwnerId),
            _ => null,
        };
        static bool IsEnemy(Actor a) => a is { IsPlayer: false } and not { Kind: ActorKind.Pet };

        var events = r.Party.ToDictionary(p => p, _ => new List<(int T, int Amount)>());
        foreach (var a in r.Actions)
        {
            if (Credit(a.Source) is not { } p)
                continue;
            foreach (var h in a.Hits)
            {
                if (h.Damage > 0 && IsEnemy(h.Target))
                    events[p].Add((h.T, h.Damage));
            }
        }

        foreach (var k in r.Ticks)
        {
            if (!k.IsHeal && k.Amount > 0 && IsEnemy(k.Target) && Credit(k.Source) is { } p)
                events[p].Add((k.T, k.Amount));
        }

        foreach (var (p, list) in events)
        {
            list.Sort((x, y) => x.T.CompareTo(y.T));
            var t = new int[list.Count];
            var total = new long[list.Count];
            long sum = 0;
            for (var i = 0; i < list.Count; i++)
            {
                sum += list[i].Amount;
                t[i] = list[i].T;
                total[i] = sum;
            }

            series[p] = (t, total);
        }
    }

    /// <summary>The meter for a pull, built on first use and cached for the pull's lifetime. Thread-safe.</summary>
    public static DamageMeter For(PullReplay r) => Cache.GetValue(r, x => new DamageMeter(x));

    /// <summary>Damage <paramref name="player"/> dealt up to and including <paramref name="t"/> (pull ms).</summary>
    public long DamageUntil(Actor player, int t)
    {
        if (!series.TryGetValue(player, out var s))
            return 0;
        // Number of events at or before t (upper bound; several hits can share a timestamp).
        int lo = 0, hi = s.T.Length;
        while (lo < hi)
        {
            var mid = (lo + hi) >>> 1;
            if (s.T[mid] <= t)
                lo = mid + 1;
            else
                hi = mid;
        }

        return lo == 0 ? 0 : s.Total[lo - 1];
    }

    /// <summary>Damage <paramref name="player"/> dealt after <paramref name="from"/>, up to and including <paramref name="to"/>.</summary>
    public long Damage(Actor player, int from, int to) => to <= from ? 0 : DamageUntil(player, to) - DamageUntil(player, from);
}
