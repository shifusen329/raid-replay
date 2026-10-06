using System.Numerics;
using RaidReplay.Core.Encounters;
using RaidReplay.Core.Model;

namespace RaidReplay.Core.Analysis;

/// <summary>Where the holder and soakers of one stack/knockback resolution were supposed to stand.</summary>
public sealed class StackSpots
{
    public Actor? Holder { get; init; }
    public Vector2 HolderSpot { get; init; }
    public Vector2 SoakerSpot { get; init; }
    public float Tolerance { get; init; }
    public string? Note { get; init; }
    public int Occurrence { get; init; }

    /// <summary>Distance from the spot this player should have been on (holder or soaker spot), at time t; -1 if unknown.</summary>
    public float Off(Actor p, int t) =>
        p.Track.TrySample(t, out var pos, out _) ? Vector2.Distance(pos, SpotOf(p)) : -1;

    public Vector2 SpotOf(Actor p) => p == Holder ? HolderSpot : SoakerSpot;
}

public static class StackPositions
{
    public static StackSpots? For(PullReplay r, AoeInstance a)
    {
        var id = a.Action?.ActionId ?? a.ActionId;
        var def = r.Encounter?.AbilityFor(id, a.Action?.Name);
        if (def?.Positions is not { Count: > 0 } list)
            return null;
        var holder = a.ExcludeActor ?? a.Follow;
        var occurrence = Occurrence(r, id, a.ResolveMs);
        var group = holder == null ? "any" : IsSupport(holder) ? "support" : "dps";
        var p = list.FirstOrDefault(x => (x.Occurrence == 0 || x.Occurrence == occurrence) && (x.Group is "any" || x.Group == group));
        if (p == null || Spot(r, p.Holder, a.ResolveMs) is not { } hs || Spot(r, p.Soakers, a.ResolveMs) is not { } ss)
            return null;
        return new StackSpots { Holder = holder, HolderSpot = hs, SoakerSpot = ss, Tolerance = p.Tolerance, Note = p.Note, Occurrence = occurrence };
    }

    private static Vector2? Spot(PullReplay r, SpotDef s, int t)
    {
        Vector2? basePos = null;
        if (s.Waymark != null && PullReplay.WaymarkSlot(s.Waymark) is var slot and >= 0)
            basePos = r.WaymarkAt(slot, t);
        if (basePos == null && s.Default is [var x, var y])
            basePos = new Vector2(x, y);
        if (basePos == null)
            return null;
        return basePos.Value + (s.Offset is [var ox, var oy] ? new Vector2(ox, oy) : Vector2.Zero);
    }

    /// <summary>1-based index of the resolution of this ability at time t (resolutions less than 2s apart count as one).</summary>
    public static int Occurrence(PullReplay r, uint actionId, int t)
    {
        var n = 0;
        var last = int.MinValue;
        foreach (var a in r.Actions)
        {
            if (a.ActionId != actionId)
                continue;
            if (n == 0 || a.T - last > 2000)
                n++;
            last = a.T;
            if (a.T >= t - 100)
                return n;
        }

        return n;
    }

    public static bool IsSupport(Actor a) => GameData.Jobs.Get(a.Job)?.Role is 1 or 4;
}
