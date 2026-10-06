using System.Numerics;
using RaidReplay.Core.Encounters;
using RaidReplay.Core.GameData;
using RaidReplay.Core.Geometry;
using RaidReplay.Core.Model;

namespace RaidReplay.Core.Aoe;

/// <summary>
/// Derives drawable AoEs: enemy casts (telegraph from cast start to resolution), instant enemy actions (flash or
/// pack "lookback" telegraph), encounter-pack trigger draws (e.g. MapEffect towers) and pack EObj hazards.
/// </summary>
public static class AoeInference
{
    public const int FlashMs = 600;

    public static void Run(PullReplay r, CompiledEncounter? enc, IGameData data, IReadOnlyList<EncounterMark> marks)
    {
        FillNames(r, data);

        foreach (var cast in r.Casts)
        {
            if (!IsEnemySource(cast.Source))
                continue;
            var def = enc?.AbilityFor(cast.ActionId, cast.Name);
            if (def?.Category == "ignore")
                continue;
            var info = data.GetAction(cast.ActionId);
            var shape = ShapeResolver.Resolve(def?.Shape, info, cast.Source.Radius, out var source);
            if (shape.Type == ShapeType.None)
                continue;
            var res = cast.Resolution;
            var resolveMs = res?.T ?? cast.EndMs;
            foreach (var aoe in Place(r, def, info, shape, cast, res, resolveMs))
            {
                aoe.StartMs = def?.Telegraph?.Mode == "none" ? resolveMs : cast.StartMs;
                if (cast.Outcome == CastOutcome.Cancelled)
                {
                    aoe.ResolveMs = cast.EndMs;
                    aoe.EndMs = cast.EndMs;
                }

                aoe.ShapeSource = source;
                r.Aoes.Add(aoe);
            }
        }

        foreach (var action in r.Actions)
        {
            if (action.Cast != null || !IsEnemySource(action.Source))
                continue;
            var def = enc?.AbilityFor(action.ActionId, action.Name);
            if (def?.Category == "ignore")
                continue;
            var info = data.GetAction(action.ActionId);
            if (def == null && (info == null || info.CastType <= 1))
                continue;
            var shape = ShapeResolver.Resolve(def?.Shape, info, action.Source.Radius, out var source);
            if (shape.Type == ShapeType.None)
                continue;
            foreach (var aoe in Place(r, def, info, shape, null, action, action.T))
            {
                if (def?.Telegraph is { Mode: "lookback" } tg)
                {
                    aoe.StartMs = action.T - (int)(tg.S * 1000);
                    aoe.Inferred = true;
                }
                else
                {
                    aoe.StartMs = action.T;
                }

                aoe.ShapeSource = source;
                r.Aoes.Add(aoe);
            }
        }

        if (enc != null)
        {
            TriggerDraws(r, enc, data, marks);
            EobjDraws(r, enc);
        }

        RefineGenericCategories(r);
        r.Aoes.Sort((a, b) => a.StartMs.CompareTo(b.StartMs));
        for (var i = 0; i < r.Aoes.Count; i++)
            r.Aoes[i].Index = i;
    }

    /// <summary>
    /// Without an encounter definition every enemy AoE starts as "danger". Use what actually happened to tell raidwides,
    /// stacks and spreads apart so only plausibly avoidable damage is reported as such.
    /// </summary>
    private static void RefineGenericCategories(PullReplay r)
    {
        foreach (var aoe in r.Aoes)
        {
            if (aoe.Conf != null || aoe.Category != AoeCategory.Danger || !aoe.ShapeSource.StartsWith("sheet", StringComparison.Ordinal) ||
                aoe.Action == null)
                continue;
            var alive = r.Party.Count(p => Analysis.ShapeValidator.IsAlive(r, p, aoe.ResolveMs));
            var hits = aoe.Action.Hits.Count(h => h.Target.IsPlayer);
            var huge = aoe.Shape.Type is ShapeType.Circle && aoe.Shape.Radius >= 30;
            if (huge || (alive >= 4 && hits >= alive))
                aoe.Category = AoeCategory.Raidwide;
            else if (aoe.Follow is { IsPlayer: true } && hits >= 3)
                aoe.Category = AoeCategory.Stack;
            else if (aoe.Follow is { IsPlayer: true } && hits <= 1)
                aoe.Category = AoeCategory.Spread;
        }
    }

    private static bool IsEnemySource(Actor a) => !a.IsPlayer && a.Kind != ActorKind.Pet;

    private static void FillNames(PullReplay r, IGameData data)
    {
        foreach (var c in r.Casts)
        {
            if (NeedsName(c.Name))
                c.Name = data.GetAction(c.ActionId)?.Name is { Length: > 0 } n ? n : c.Name;
        }

        foreach (var a in r.Actions)
        {
            if (NeedsName(a.Name))
                a.Name = data.GetAction(a.ActionId)?.Name is { Length: > 0 } n ? n : a.Name;
        }
    }

    private static bool NeedsName(string name) =>
        name.Length == 0 || name.StartsWith("unknown_", StringComparison.OrdinalIgnoreCase) ||
        (name.Length <= 5 && name.All(char.IsAsciiHexDigit));

    private static AoeCategory Category(AbilityDef? def, ActionEvent? res)
    {
        if (def?.Category is { } c)
        {
            return c switch
            {
                "fake" => AoeCategory.Fake,
                "hiddenDanger" => AoeCategory.HiddenDanger,
                "tower" or "soak" => AoeCategory.Tower,
                "stack" => AoeCategory.Stack,
                "spread" => AoeCategory.Spread,
                "tankbuster" => AoeCategory.Tankbuster,
                "raidwide" or "enrage" => AoeCategory.Raidwide,
                "knockback" => AoeCategory.Knockback,
                "gaze" => AoeCategory.Gaze,
                "failure" => AoeCategory.Failure,
                "bait" => AoeCategory.Bait,
                "info" or "debuff" => AoeCategory.Info,
                "hazard" => AoeCategory.Hazard,
                _ => AoeCategory.Danger,
            };
        }

        if (def?.Damage == false)
            return AoeCategory.Fake;
        return AoeCategory.Danger;
    }

    private static IEnumerable<AoeInstance> Place(
        PullReplay r, AbilityDef? def, ActionInfo? info, AoeShape shape, CastEvent? cast, ActionEvent? res, int resolveMs)
    {
        var sd = def?.Shape;
        var source = cast?.Source ?? res!.Source;
        var target = cast?.Target ?? res?.AnimationTarget ?? res?.PrimaryTarget;
        if (target == source)
            target = null;

        var origin = sd?.Origin ?? DefaultOrigin(shape, info, cast, res, target);
        var label = def?.Label ?? def?.Name ?? cast?.Name ?? res?.Name ?? string.Empty;

        AoeInstance Make() => new()
        {
            Shape = shape,
            Source = source,
            ResolveMs = resolveMs,
            EndMs = resolveMs + FlashMs,
            Category = Category(def, res),
            Label = label,
            ActionId = cast?.ActionId ?? res!.ActionId,
            Cast = cast,
            Action = res,
            Soakers = def?.Soakers ?? 0,
            Color = def?.Color,
            Conf = def?.Conf,
        };

        // Multi-holder origins produce one AoE per holder.
        if (origin is "statusHolder" or "headMarkerHolder")
        {
            var holders = origin == "statusHolder"
                              ? StatusHolders(r, sd?.Status?.Value ?? 0, resolveMs)
                              : MarkerHolders(r, sd?.Marker?.Value ?? 0, resolveMs);
            foreach (var h in holders)
            {
                var aoe = Make();
                aoe.Follow = h;
                aoe.FollowUntilMs = resolveMs;
                if (h.Track.TrySample(resolveMs, out var p, out _))
                    aoe.Origin = p;
                if (def?.ExcludesHolder == true)
                    aoe.ExcludeActor = h;
                aoe.Heading = HeadingFor(sd, cast, res, aoe.Origin, target, resolveMs);
                yield return aoe;
            }

            yield break;
        }

        var one = Make();
        switch (origin)
        {
            case "target":
            case "animTarget":
            {
                var t = origin == "animTarget" ? res?.AnimationTarget ?? target : target;
                if (t != null)
                {
                    one.Follow = t;
                    one.FollowUntilMs = resolveMs - (int)((sd?.SnapshotS ?? 0) * 1000);
                    if (def?.ExcludesHolder == true)
                        one.ExcludeActor = t;
                    if (t.Track.TrySample(resolveMs, out var p, out _))
                        one.Origin = p;
                }
                else
                {
                    one.Origin = CasterPos(cast, res, resolveMs);
                }

                break;
            }
            case "location":
                one.Origin = res?.Location ?? cast?.Pos ?? CasterPos(cast, res, resolveMs);
                break;
            case "fixed" when sd?.At is { Length: >= 2 } at:
                one.Origin = new Vector2(at[0], at[1]);
                break;
            case "point" when sd?.Point != null && r.Encounter?.Def.Arena?.Points.TryGetValue(sd.Point, out var pt) == true:
                one.Origin = new Vector2(pt[0], pt[1]);
                break;
            case "castLoc":
                one.Origin = cast is { HasExtra: true } ? cast.Pos : CasterPos(cast, res, resolveMs);
                break;
            default:
                one.Origin = CasterPos(cast, res, resolveMs);
                break;
        }

        if (shape.Type == ShapeType.Rect && info is { CastType: 8 } && sd?.Length == null && target != null)
        {
            // Charge: rect from caster to target.
            one.FaceToward = target;
            if (target.Track.TrySample(resolveMs, out var tp, out _))
                one.Shape = shape with { Length = Vector2.Distance(one.Origin, tp) };
        }

        one.Heading = HeadingFor(sd, cast, res, one.Origin, target, resolveMs);
        if (sd?.Heading == "towardTarget" && target != null)
            one.FaceToward = target;
        yield return one;
    }

    private static string DefaultOrigin(AoeShape shape, ActionInfo? info, CastEvent? cast, ActionEvent? res, Actor? target)
    {
        if (res?.Location != null && (info?.TargetArea == true || cast == null))
            return "location";
        if (shape.Type == ShapeType.Circle && info is { CastType: 2 } && target is { IsPlayer: true })
            return "target";
        if (cast is { HasExtra: true })
            return "castLoc";
        return "caster";
    }

    private static Vector2 CasterPos(CastEvent? cast, ActionEvent? res, int t)
    {
        if (cast is { HasExtra: true })
            return cast.Pos;
        var src = cast?.Source ?? res!.Source;
        // Helpers/NPCs: their track is built from network movement (270/271/261), which is fresher than the
        // ACT-memory source position on 21/22 lines (often stale for invisible helpers).
        if (!src.IsPlayer && src.Track.Count > 0 && src.Track.TrySample(t, out var tp, out _))
            return tp;
        if (res != null && TrackerOk(res.SourcePos))
            return res.SourcePos;
        if (src.Track.TrySample(t, out var p, out _))
            return p;
        return cast?.Pos ?? Vector2.Zero;
    }

    private static bool TrackerOk(Vector2 p) => !float.IsNaN(p.X) && !float.IsNaN(p.Y) && (p.X != 0 || p.Y != 0);

    private static float HeadingFor(ShapeDef? sd, CastEvent? cast, ActionEvent? res, Vector2 origin, Actor? target, int t)
    {
        float h;
        switch (sd?.Heading)
        {
            case "fixed":
                h = (sd.HeadingDeg ?? 0) * MathF.PI / 180f;
                break;
            case "cast":
                h = cast?.Heading ?? res?.BestHeading ?? 0;
                break;
            case "resolution":
                h = res?.BestHeading ?? cast?.Heading ?? 0;
                break;
            case "towardTarget" when target != null && target.Track.TrySample(t, out var tp, out _):
                h = Angles.Toward(origin, tp);
                break;
            default:
                h = res?.RotationHeading ?? cast?.Heading ?? res?.BestHeading ?? 0;
                if (float.IsNaN(h))
                    h = 0;
                break;
        }

        if (sd?.HeadingOffsetDeg is { } off)
            h = Angles.Wrap(h + (off * MathF.PI / 180f));
        return h;
    }

    private static List<Actor> StatusHolders(PullReplay r, uint statusId, int t)
    {
        // Prefer holders whose status expires at the resolution (timed debuffs that "pop"); otherwise anyone holding it.
        var expiring = new List<Actor>();
        var active = new List<Actor>();
        foreach (var s in r.Statuses)
        {
            if (s.StatusId != statusId || s.StartMs > t + 50)
                continue;
            if (Math.Abs(s.EndMs - t) <= 1500 && !expiring.Contains(s.Target))
                expiring.Add(s.Target);
            else if (s.EndMs >= t && !active.Contains(s.Target))
                active.Add(s.Target);
        }

        return expiring.Count > 0 ? expiring : active;
    }

    private static List<Actor> MarkerHolders(PullReplay r, uint markerId, int t)
    {
        // The marker a player held *before* the newest one (icons can be reassigned just before the hit).
        var latest = new Dictionary<Actor, HeadMarkerEvent>();
        var previous = new Dictionary<Actor, HeadMarkerEvent>();
        foreach (var m in r.HeadMarkers)
        {
            if (m.T > t + 50)
                break;
            if (m.T < t - 15000)
                continue;
            if (latest.TryGetValue(m.Target, out var prev))
                previous[m.Target] = prev;
            latest[m.Target] = m;
        }

        var list = new List<Actor>();
        foreach (var (actor, m) in latest)
        {
            var effective = t - m.T < 200 && previous.TryGetValue(actor, out var p) ? p : m;
            if (effective.MarkerId == markerId)
                list.Add(actor);
        }

        return list;
    }

    private static void TriggerDraws(PullReplay r, CompiledEncounter enc, IGameData data, IReadOnlyList<EncounterMark> marks)
    {
        foreach (var m in marks)
        {
            if (m.Kind != MarkKind.Draw || m.Def is not TriggerDrawDef def)
                continue;
            var shape = ShapeResolver.Resolve(def.Draw, null, 0, out _);
            if (shape.Type == ShapeType.None)
                continue;
            var t = r.ToMs(m.Ticks);
            var aoe = new AoeInstance
            {
                Shape = shape,
                StartMs = t,
                ResolveMs = t + (int)(def.DurationS * 1000),
                Inferred = true,
                Category = Category(new AbilityDef { Category = def.Category }, null),
                Label = def.Label ?? def.Id,
                Soakers = def.Soakers ?? 0,
                Color = def.Color,
                Conf = def.Conf,
                ShapeSource = "trigger:" + def.Id,
            };
            aoe.EndMs = aoe.ResolveMs + FlashMs;

            var sd = def.Draw;
            switch (sd.Origin)
            {
                case "point":
                    if (ResolvePoint(enc, sd.Point, m.Event) is { } pointPos)
                        aoe.Origin = pointPos;
                    else
                        continue;
                    break;
                case "fixed" when sd.At is { Length: >= 2 } at:
                    aoe.Origin = new Vector2(at[0], at[1]);
                    break;
                case "statusHolder":
                case "target":
                {
                    var actor = r.Actors.LastOrDefault(a => a.Id == m.Event.TargetId && a.IsPresent(t));
                    if (actor == null)
                        continue;
                    aoe.Follow = actor;
                    aoe.FollowUntilMs = aoe.ResolveMs;
                    if (actor.Track.TrySample(t, out var p, out _))
                        aoe.Origin = p;
                    break;
                }
                default:
                {
                    var actor = r.Actors.LastOrDefault(a => a.Id == m.Event.ActorId && a.IsPresent(t));
                    if (actor?.Track.TrySample(t, out var p, out var h) == true)
                    {
                        aoe.Origin = p;
                        aoe.Heading = h;
                    }

                    break;
                }
            }

            if (sd.Heading == "fixed")
                aoe.Heading = (sd.HeadingDeg ?? 0) * MathF.PI / 180f;
            r.Aoes.Add(aoe);
        }
    }

    /// <summary>Named point, with "set[location]" substituting the event's location/param (e.g. MapEffect slot).</summary>
    private static Vector2? ResolvePoint(CompiledEncounter enc, string? name, in TriggerEvent e)
    {
        var arena = enc.Def.Arena;
        if (arena == null || name == null)
            return null;
        var open = name.IndexOf('[');
        if (open > 0 && name.EndsWith(']'))
        {
            var set = name[..open];
            var key = name[(open + 1)..^1] switch
            {
                "location" or "p1" => $"{e.P1:X2}",
                "p2" => $"{e.P2:X2}",
                var other => other,
            };
            if (arena.PointSets.TryGetValue(set, out var points) &&
                (points.TryGetValue(key, out var pt) || points.TryGetValue(key.TrimStart('0'), out pt)))
                return new Vector2(pt[0], pt[1]);
            return null;
        }

        return arena.Points.TryGetValue(name, out var p) ? new Vector2(p[0], p[1]) : null;
    }

    private static void EobjDraws(PullReplay r, CompiledEncounter enc)
    {
        foreach (var a in r.Actors)
        {
            if (a.Kind != ActorKind.EventObject || !enc.Eobjs.TryGetValue(a.BNpcBaseId, out var def) || def.Draw == null)
                continue;
            var shape = ShapeResolver.Resolve(def.Draw, null, 0, out _);
            if (shape.Type == ShapeType.None)
                continue;
            var start = Math.Max(a.SpawnMs, r.FirstMs);
            var end = Math.Min(a.DespawnMs, r.LastMs);
            a.Track.TrySample(start, out var p, out var h);
            r.Aoes.Add(new AoeInstance
            {
                Shape = shape,
                Origin = p,
                Heading = h,
                Follow = a,
                FollowUntilMs = int.MaxValue,
                Source = a,
                StartMs = start,
                ResolveMs = end,
                EndMs = end,
                Category = Category(new AbilityDef { Category = def.Category ?? "hazard" }, null),
                Label = def.Label,
                Color = def.Color,
                Conf = def.Conf,
                ShapeSource = "eobj",
            });
        }
    }
}
