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
    MissingMitigation,

    /// <summary>An arrow-teleporter puzzle mistake (misplaced, stacked or early-used arrow, broken chain, unused arrows).</summary>
    ArrowPuzzle,

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

    /// <summary>A fixed spot the player was assigned (e.g. their arrow spot, the knockback holder corner).</summary>
    Assigned,
}

public sealed class PlayerSnapshot
{
    public required Actor Player { get; init; }
    public string Slot { get; init; } = string.Empty;
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
    public required string Title { get; set; }
    public string Detail { get; set; } = string.Empty;
    public string? Mechanic { get; set; }
    /// <summary>Who is at fault (shown as culprits).</summary>
    public List<Actor> Players { get; } = [];
    public List<AoeInstance> Aoes { get; } = [];
    public DeathEvent? Death { get; init; }

    /// <summary>Who it happened to, when that can differ from who is at fault (a player hit by someone else's AoE).</summary>
    public Actor? Victim { get; set; }

    /// <summary>Earlier incidents this one is a consequence of (e.g. the deaths of a tower's soakers); root causes are traced through them.</summary>
    public List<Incident> Causes { get; } = [];

    /// <summary>The mitigation-plan check of the mechanic this incident happened at, if any.</summary>
    public MitCheck? Mitigation { get; set; }

    /// <summary>Overrides the verdict when this incident is the root cause (e.g. "Missing mitigation").</summary>
    public string? VerdictHint { get; set; }

    /// <summary>A deliberate death, e.g. jumping off to swap a Damage Down for Weakness after a raise.</summary>
    public bool Intentional { get; set; }

    /// <summary>
    /// A deliberate death that ended a lost pull (e.g. jumping off once the whole party has Damage Down): not a reset,
    /// and no part of the collapse.
    /// </summary>
    public bool DeliberateWipe { get; set; }

    /// <summary>For Damage Down resets: when the Damage Down was applied (-1 if not a reset).</summary>
    public int DamageDownAt { get; set; } = -1;

    /// <summary>For deaths: total damage taken in the final second (simultaneous hits) and HP before it.</summary>
    public int BurstDamage { get; set; }

    public int HpBeforeBurst { get; set; }

    public int Severity { get; set; }
    public bool IsRootCause { get; set; }
    public List<PlayerSnapshot> Snapshot { get; } = [];

    /// <summary>Incident-specific "should have been" positions that take precedence over learned/safe-spot ones.</summary>
    public Dictionary<Actor, (Vector2 Pos, ExpectedSource Source, string Note)> Expected { get; } = new();
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

    /// <summary>Party slots (MT, OT, H1, H2, M1, M2, R1, R2).</summary>
    public Dictionary<Actor, string> Slots { get; set; } = new();

    /// <summary>Mitigation plan checks up to the end of the pull (empty if the encounter has no plan).</summary>
    public List<MitCheck> Mitigation { get; set; } = [];

    /// <summary>The arrow-teleporter puzzle of this pull, if the encounter has one and it was reached.</summary>
    public ArrowSquareResult? Arrows { get; set; }

    public string SlotOf(Actor a) => Slots.TryGetValue(a, out var s) ? s : string.Empty;

    /// <summary>One-line summary suitable for chat.</summary>
    public string ChatLine { get; set; } = string.Empty;
}
