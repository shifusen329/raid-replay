using System.Numerics;
using RaidReplay.Core.Model;

namespace RaidReplay.Core.Analysis;

public enum IncidentKind : byte
{
    Death,
    FellOff,
    FailureAction,
    AvoidableHit,
    TowerUnderSoaked,
    MissedStack,
    SpreadOverlap,
    Enrage,
}

public enum ExpectedSource : byte
{
    None,

    /// <summary>Nearest point outside every damaging AoE resolving at that moment.</summary>
    SafeSpot,

    /// <summary>Center of a tower/stack the player should have been in.</summary>
    Soak,

    /// <summary>Where this player usually stood here in pulls where the mechanic went fine.</summary>
    Learned,
}

public sealed class PlayerSnapshot
{
    public required Actor Player { get; init; }
    public Vector2 Pos { get; init; }
    public float Heading { get; init; }
    public float HpPct { get; init; }
    public bool Alive { get; init; }
    public bool Involved { get; set; }
    public Vector2? Expected { get; set; }
    public ExpectedSource ExpectedSource { get; set; }
    public string? ExpectedNote { get; set; }

    public float? MissDistance => Expected is { } e ? Vector2.Distance(e, Pos) : null;
}

public sealed class Incident
{
    public IncidentKind Kind { get; init; }
    public int T { get; init; }
    public required string Title { get; init; }
    public string Detail { get; set; } = string.Empty;
    public string? Mechanic { get; set; }
    public List<Actor> Players { get; } = [];
    public List<AoeInstance> Aoes { get; } = [];
    public DeathEvent? Death { get; init; }
    public int Severity { get; init; }
    public bool IsRootCause { get; set; }
    public List<PlayerSnapshot> Snapshot { get; } = [];
}

public sealed class WipeReport
{
    public required PullReplay Pull { get; init; }
    public string Verdict { get; set; } = string.Empty;
    public string Headline { get; set; } = string.Empty;
    public string Phase { get; set; } = string.Empty;
    public string? Segment { get; set; }
    public List<Incident> Incidents { get; } = [];
    public Incident? RootCause { get; set; }
    public List<string> Notes { get; } = [];
    public int LearnedPulls { get; set; }

    /// <summary>One-line summary suitable for chat.</summary>
    public string ChatLine { get; set; } = string.Empty;
}
