using System.Numerics;
using RaidReplay.Core.Encounters;
using RaidReplay.Core.Geometry;

namespace RaidReplay.Core.Model;

public sealed class PhaseSpan
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public int StartMs { get; init; }
    public int EndMs { get; set; }
    public bool IsSegment { get; init; }
}

public sealed class MechanicMarker
{
    public required string Id { get; init; }
    public required string Label { get; init; }
    public int T { get; init; }
    public int DurationMs { get; init; }
    public string? Phase { get; init; }
}

public enum AoeCategory : byte
{
    Danger,
    Fake,
    HiddenDanger,
    Tower,
    Stack,
    Spread,
    Tankbuster,
    Raidwide,
    Knockback,
    Gaze,
    Failure,
    Bait,
    Info,
    Hazard,
}

/// <summary>An area-of-effect drawn on the replay (from a cast, an instant action, or a pack trigger).</summary>
public sealed class AoeInstance
{
    public int Index { get; internal set; }
    public AoeShape Shape { get; set; }
    public Vector2 Origin { get; set; }
    public float Heading { get; set; }

    /// <summary>If set, the shape follows this actor until <see cref="FollowUntilMs"/>.</summary>
    public Actor? Follow { get; set; }

    public int FollowUntilMs { get; set; }

    /// <summary>For caster→target rects: the end point (follows <see cref="EndFollow"/> if set).</summary>
    public Actor? EndFollow { get; set; }

    /// <summary>Heading points from origin to this actor at time t (towardTarget).</summary>
    public Actor? FaceToward { get; set; }

    public Actor? Source { get; set; }
    public int StartMs { get; set; }
    public int ResolveMs { get; set; }
    public int EndMs { get; set; }

    /// <summary>Telegraph not observed in the log (lookback or pack trigger).</summary>
    public bool Inferred { get; set; }

    public AoeCategory Category { get; set; }
    public string Label { get; set; } = string.Empty;
    public uint ActionId { get; set; }
    public CastEvent? Cast { get; set; }
    public ActionEvent? Action { get; set; }
    public string ShapeSource { get; set; } = string.Empty;
    public int Soakers { get; set; }
    public string? Color { get; set; }
    public string? Conf { get; set; }
    public Actor? ExcludeActor { get; set; }

    /// <summary>Resolved origin/heading at time t (accounts for following actors).</summary>
    public (Vector2 Origin, float Heading) Placement(int t)
    {
        var origin = Origin;
        var heading = Heading;
        if (Follow != null)
        {
            var at = Math.Min(t, FollowUntilMs);
            if (Follow.Track.TrySample(at, out var p, out var h))
            {
                origin = p;
                if (FaceToward == null && Shape.Type is ShapeType.Cone or ShapeType.Rect && Follow == Source)
                    heading = h;
            }
        }

        if (FaceToward != null && FaceToward.Track.TrySample(Math.Min(t, ResolveMs), out var tp, out _))
        {
            if (Vector2.DistanceSquared(tp, origin) > 0.01f)
                heading = Angles.Toward(origin, tp);
        }

        return (origin, heading);
    }
}

public sealed class PullReplay
{
    public required PullSummary Summary { get; init; }
    public long StartTicks { get; init; }
    public int FirstMs { get; init; }
    public int EndMs { get; init; }
    public int LastMs { get; init; }
    public CompiledEncounter? Encounter { get; init; }

    public List<Actor> Actors { get; } = [];
    public List<CastEvent> Casts { get; } = [];
    public List<ActionEvent> Actions { get; } = [];
    public List<TickEvent> Ticks { get; } = [];
    public List<StatusInterval> Statuses { get; } = [];
    public List<DeathEvent> Deaths { get; } = [];
    public List<HeadMarkerEvent> HeadMarkers { get; } = [];
    public List<TetherEvent> Tethers { get; } = [];
    public List<WaymarkEvent> Waymarks { get; } = [];
    public List<WaymarkState> InitialWaymarks { get; } = [];
    public List<SignEvent> Signs { get; } = [];
    public List<MapEffectEvent> MapEffects { get; } = [];
    public List<DirectorEvent> Directors { get; } = [];
    public List<ActorControlEvent> ActorControls { get; } = [];
    public List<NameToggleEvent> NameToggles { get; } = [];
    public List<MapChangeEvent> MapChanges { get; } = [];
    public int InitialMapId { get; init; }

    public List<PhaseSpan> Phases { get; } = [];
    public List<MechanicMarker> Mechanics { get; } = [];
    public List<AoeInstance> Aoes { get; } = [];
    public List<string> Diagnostics { get; } = [];

    public IEnumerable<Actor> Players => Actors.Where(a => a.Kind == ActorKind.Player);

    /// <summary>Party members (the pull's party list), ordered tank/healer/melee/ranged/caster.</summary>
    public List<Actor> Party { get; } = [];

    public int MapIdAt(int t)
    {
        var id = InitialMapId;
        foreach (var m in MapChanges)
        {
            if (m.T <= t)
                id = m.MapId;
        }

        return id;
    }

    public int ToMs(long ticks) => (int)((ticks - StartTicks) / TimeSpan.TicksPerMillisecond);

    public string PhaseAt(int t)
    {
        string? name = null;
        foreach (var p in Phases)
        {
            if (!p.IsSegment && p.StartMs <= t)
                name = p.Name;
        }

        return name ?? string.Empty;
    }

    /// <summary>Where waymark <paramref name="slot"/> (0–3 = A–D, 4–7 = 1–4) was at time t, if placed.</summary>
    public Vector2? WaymarkAt(int slot, int t)
    {
        Vector2? pos = null;
        foreach (var w in InitialWaymarks)
        {
            if (w.Slot == slot)
                pos = new Vector2(w.X, w.Y);
        }

        foreach (var e in Waymarks)
        {
            if (e.T > t)
                break;
            if (e.Slot == slot)
                pos = e.Add ? new Vector2(e.Pos.X, e.Pos.Y) : null;
        }

        return pos;
    }

    /// <summary>Waymark slot for a name ("A"–"D", "1"–"4"), or -1.</summary>
    public static int WaymarkSlot(string name) => name.Trim().ToUpperInvariant() switch
    {
        "A" => 0, "B" => 1, "C" => 2, "D" => 3, "1" => 4, "2" => 5, "3" => 6, "4" => 7, _ => -1,
    };
}
