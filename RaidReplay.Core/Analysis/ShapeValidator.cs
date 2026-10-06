using System.Numerics;
using RaidReplay.Core.Geometry;
using RaidReplay.Core.Model;

namespace RaidReplay.Core.Analysis;

/// <summary>Accuracy of one action's inferred shape, accumulated over AoE resolutions.</summary>
public sealed class ShapeStats
{
    public uint ActionId { get; init; }
    public string Name { get; set; } = string.Empty;
    public string Shape { get; set; } = string.Empty;
    public string Source { get; set; } = string.Empty;
    public int Instances { get; set; }
    public int TruePositive { get; set; }
    public int FalsePositive { get; set; }
    public int FalseNegative { get; set; }
    public int TrueNegative { get; set; }

    /// <summary>Largest origin distance of a player that was hit.</summary>
    public float MaxHitDist { get; set; }

    /// <summary>Smallest origin distance of a player that was not hit.</summary>
    public float MinMissDist { get; set; } = float.MaxValue;

    /// <summary>Largest |angle from heading| (deg) among hit players.</summary>
    public float MaxHitAngle { get; set; }

    /// <summary>Smallest |angle from heading| (deg) among non-hit players within the radius.</summary>
    public float MinMissAngle { get; set; } = float.MaxValue;

    /// <summary>Largest perpendicular offset among hit players (rect half-width estimate).</summary>
    public float MaxHitSide { get; set; }

    /// <summary>Smallest perpendicular offset among non-hit players inside the rect length.</summary>
    public float MinMissSide { get; set; } = float.MaxValue;

    public double Precision => TruePositive + FalsePositive == 0 ? 1 : (double)TruePositive / (TruePositive + FalsePositive);
    public double Recall => TruePositive + FalseNegative == 0 ? 1 : (double)TruePositive / (TruePositive + FalseNegative);
}

/// <summary>
/// Compares predicted "inside" sets (player position at resolution vs inferred shape) with the actual hit list,
/// which is ground truth. Used to tune the CastType mapping and encounter overrides.
/// </summary>
public static class ShapeValidator
{
    // Empirically the game tests the player's center point (players 0.5y outside a cone edge are not hit).
    public const float PlayerHitbox = 0f;

    public static void Accumulate(PullReplay r, Dictionary<uint, ShapeStats> stats, uint detailId = 0, Action<string>? log = null)
    {
        // Group AoEs by resolving action so multi-instance AoEs (one per holder) are evaluated as a union.
        foreach (var group in r.Aoes.Where(a => a.Action != null && a.Shape.IsArea &&
                                                a.Category is not (AoeCategory.Fake or AoeCategory.Info))
                                    .GroupBy(a => a.Action!))
        {
            var action = group.Key;
            var first = group.First();
            if (!stats.TryGetValue(action.ActionId, out var s))
            {
                s = new ShapeStats { ActionId = action.ActionId };
                stats[action.ActionId] = s;
            }

            s.Name = first.Label;
            s.Shape = first.Shape.ToString();
            s.Source = first.ShapeSource;
            s.Instances++;
            var t = action.T;
            var hit = new HashSet<Actor>(action.Hits.Where(h => h.Target.IsPlayer).Select(h => h.Target));
            foreach (var p in r.Party)
            {
                if (!IsAlive(r, p, t) || !p.Track.TrySample(t, out var pos, out _))
                    continue;
                var inside = false;
                foreach (var aoe in group)
                {
                    if (aoe.ExcludeActor == p)
                        continue;
                    var (origin, heading) = aoe.Placement(t);
                    if (ShapeMath.Contains(aoe.Shape, origin, heading, pos, PlayerHitbox))
                        inside = true;
                    Measure(s, aoe, origin, heading, pos, hit.Contains(p));
                }

                var wasHit = hit.Contains(p);
                if (log != null && action.ActionId == detailId && inside != wasHit)
                {
                    var (o0, h0) = first.Placement(t);
                    var dd = pos - o0;
                    var ang = Angles.Wrap(MathF.Atan2(dd.X, dd.Y) - h0) * 180 / MathF.PI;
                    log($"#{r.Summary.Ordinal} t={t / 1000.0:0.0} {(inside ? "FP" : "FN")} {p.Name,-22} pos=({pos.X:0.0},{pos.Y:0.0}) origin=({o0.X:0.0},{o0.Y:0.0}) h={h0 * 180 / MathF.PI:0} dist={dd.Length():0.0} ang={ang:0} hits={hit.Count} instances={group.Count()}");
                }

                if (inside && wasHit)
                    s.TruePositive++;
                else if (inside)
                    s.FalsePositive++;
                else if (wasHit)
                    s.FalseNegative++;
                else
                    s.TrueNegative++;
            }
        }
    }

    private static void Measure(ShapeStats s, AoeInstance aoe, Vector2 origin, float heading, Vector2 pos, bool wasHit)
    {
        var d = pos - origin;
        var dist = d.Length();
        var angle = MathF.Abs(Angles.Wrap(MathF.Atan2(d.X, d.Y) - heading)) * 180 / MathF.PI;
        var fwd = Angles.Dir(heading);
        var side = MathF.Abs((d.X * fwd.Y) - (d.Y * fwd.X));
        var along = Vector2.Dot(d, fwd);
        if (wasHit)
        {
            s.MaxHitDist = Math.Max(s.MaxHitDist, dist);
            s.MaxHitAngle = Math.Max(s.MaxHitAngle, angle);
            s.MaxHitSide = Math.Max(s.MaxHitSide, side);
        }
        else
        {
            s.MinMissDist = Math.Min(s.MinMissDist, dist);
            if (aoe.Shape.Type == ShapeType.Cone && dist <= aoe.Shape.Radius)
                s.MinMissAngle = Math.Min(s.MinMissAngle, angle);
            if (aoe.Shape.Type == ShapeType.Rect && along >= -aoe.Shape.BackLength && along <= aoe.Shape.Length)
                s.MinMissSide = Math.Min(s.MinMissSide, side);
        }
    }

    public static bool IsAlive(PullReplay r, Actor a, int t)
    {
        foreach (var d in r.Deaths)
        {
            if (d.Victim == a && d.T <= t && (d.RaisedMs < 0 || d.RaisedMs > t))
                return false;
        }

        return a.IsPresent(t);
    }
}
