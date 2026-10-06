using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace RaidReplay.Core.Encounters;

// JSON schema for encounter packs. Conventions:
//  * JSON numbers are decimal; JSON strings used as ids are hex (with or without "0x").
//  * Omitted shape dimensions mean "take it from the game's Action sheet".
//  * "conf" documents confidence: v = verified in logs, i = inferred, d = from guides only.

/// <summary>An id written either as a JSON number (decimal) or a string (hex).</summary>
[JsonConverter(typeof(HexIdConverter))]
public readonly record struct HexId(uint Value)
{
    public static implicit operator uint(HexId id) => id.Value;

    public override string ToString() => $"0x{Value:X}";

    public static bool TryParse(string? s, out uint value)
    {
        value = 0;
        if (string.IsNullOrWhiteSpace(s))
            return false;
        s = s.Trim();
        if (s.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
            s = s[2..];
        return uint.TryParse(s, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out value);
    }
}

public sealed class HexIdConverter : JsonConverter<HexId>
{
    public override HexId Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.Number)
            return new HexId(reader.GetUInt32());
        if (reader.TokenType == JsonTokenType.String && HexId.TryParse(reader.GetString(), out var v))
            return new HexId(v);
        throw new JsonException($"invalid id '{(reader.TokenType == JsonTokenType.String ? reader.GetString() : reader.TokenType)}'");
    }

    public override void Write(Utf8JsonWriter writer, HexId value, JsonSerializerOptions options) =>
        writer.WriteStringValue($"0x{value.Value:X}");
}

public sealed class EncounterDefinition
{
    public int Schema { get; set; } = 1;
    public string Key { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public EncounterMatch Match { get; set; } = new();
    public ArenaDef? Arena { get; set; }
    public List<ActorRule> Actors { get; set; } = [];
    public List<EobjDef> Eobjs { get; set; } = [];
    public List<PhaseDef> Phases { get; set; } = [];
    public List<MechanicDef> Mechanics { get; set; } = [];
    public Dictionary<string, AbilityDef> Abilities { get; set; } = new();
    public List<UnboundAbilityDef> UnboundAbilities { get; set; } = [];
    public List<TriggerDrawDef> Triggers { get; set; } = [];
    public Dictionary<string, HeadMarkerDef> HeadMarkers { get; set; } = new();
    public Dictionary<string, TetherDef> Tethers { get; set; } = new();
    public Dictionary<string, string> Statuses { get; set; } = new();
    public Dictionary<string, Dictionary<string, string>> Director { get; set; } = new();
    public List<HexId> FailureActions { get; set; } = [];
    public MitigationPlanDef? Mitigation { get; set; }
    public ArrowSquareDef? ArrowPuzzle { get; set; }
    public string? Notes { get; set; }
}

/// <summary>
/// An arrow-teleporter puzzle (e.g. DMU Tele-trouncing): every player drops directional teleporters when their arrow
/// debuffs expire, and Confused players later walk onto them and get chained from arrow to arrow. The intended layout
/// is a square of slots around <see cref="Center"/> with every arrow pointing along the perimeter; it is only used to
/// say whose arrow was out of place when a chain breaks (groups may use other layouts that still chain).
/// </summary>
public sealed class ArrowSquareDef
{
    public string Label { get; set; } = "Arrows";
    public HexId TeleporterEobj { get; set; }

    /// <summary>Name (prefix) of the debuff whose expiry drops a teleporter (e.g. "Tele-portent").</summary>
    public string ArrowStatus { get; set; } = string.Empty;

    /// <summary>Arrow direction (N/E/S/W) of each arrow debuff id, so arrows that never appeared still have one.</summary>
    public Dictionary<string, string> ArrowDirections { get; set; } = new();

    /// <summary>Name of the status that makes players walk into the arrows (e.g. "Confused").</summary>
    public string ConfusedStatus { get; set; } = "Confused";

    public float[] Center { get; set; } = [100, 100];
    public float HalfSize { get; set; } = 12;

    /// <summary>How far a teleporter moves a player along its arrow (also the slot spacing).</summary>
    public float Step { get; set; } = 6;

    public bool Clockwise { get; set; } = true;

    /// <summary>How far a teleporter may be from its slot.</summary>
    public float Tolerance { get; set; } = 1.75f;

    /// <summary>A player whose landing spot is within this distance of a teleporter is teleported again.</summary>
    public float TriggerRadius { get; set; } = 2f;

    /// <summary>Time per teleport in a chain.</summary>
    public int HopMs { get; set; } = 750;

    /// <summary>Most teleports one Confused player takes (0 = no limit).</summary>
    public int MaxChain { get; set; } = 4;

    public string? Conf { get; set; }
}

/// <summary>
/// A mitigation plan (e.g. a community mit sheet) used as a reference: which party slot is expected to have which
/// mitigation up at each damaging mechanic. Slots: MT, OT, healer job columns (WHM, AST, SCH, SGE), D1–D4 (= M1, M2,
/// R1, R2) and Extras. Action names are the in-game names; "@partyMit" and "@extra" resolve by job.
/// </summary>
public sealed class MitigationPlanDef
{
    public string Source { get; set; } = string.Empty;
    public string? Note { get; set; }
    public List<MitMechanicDef> Mechanics { get; set; } = [];
}

public sealed class MitMechanicDef
{
    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;

    /// <summary>Planned time from pull start (seconds).</summary>
    public float AtS { get; set; }

    /// <summary>Damaging abilities of this mechanic (the hit time is the first one within ±WindowS of AtS).</summary>
    public List<HexId> Hits { get; set; } = [];

    /// <summary>Ability names, for hits whose ids are not known yet.</summary>
    public List<string> HitNames { get; set; } = [];

    public float WindowS { get; set; } = 5;
    public List<MitPlanItem> Plan { get; set; } = [];
}

public sealed class MitPlanItem
{
    public string Slot { get; set; } = string.Empty;

    /// <summary>Pressed for this mechanic.</summary>
    public List<string> Use { get; set; } = [];

    /// <summary>Still active from an earlier press (sheet "➔").</summary>
    public List<string> Carry { get; set; } = [];

    /// <summary>Only applies if the slot's player has one of these jobs.</summary>
    public List<string>? Jobs { get; set; }

    public string? Note { get; set; }
}

public sealed class EncounterMatch
{
    public List<HexId> TerritoryIds { get; set; } = [];
    public List<int> MapIds { get; set; } = [];
}

public sealed class ArenaDef
{
    public float[] Center { get; set; } = [100, 100];

    /// <summary>circle | square | rect</summary>
    public string Shape { get; set; } = "circle";

    public float Radius { get; set; } = 20;
    public float Width { get; set; }
    public float Height { get; set; }
    public float ViewRadius { get; set; }
    public bool UseMapTexture { get; set; } = true;

    /// <summary>Players beyond this distance from center are considered to have fallen off (0 = disabled).</summary>
    public float FallRadius { get; set; }

    public Dictionary<string, float[]> Points { get; set; } = new();
    public Dictionary<string, Dictionary<string, float[]>> PointSets { get; set; } = new();
    public string? Conf { get; set; }
}

public sealed class ActorRule
{
    public uint? BnpcBase { get; set; }
    public List<uint>? BnpcBases { get; set; }
    public string? Name { get; set; }

    /// <summary>boss | enemy | helper | clone | emitter | pet | ignore</summary>
    public string Role { get; set; } = "enemy";

    public string? Label { get; set; }
    public float? Hitbox { get; set; }
    public bool Render { get; set; } = true;
    public string? Conf { get; set; }
}

public sealed class EobjDef
{
    public HexId Base { get; set; }
    public string Label { get; set; } = string.Empty;
    public ShapeDef? Draw { get; set; }
    public string? Category { get; set; }
    public string? Color { get; set; }
    public bool Render { get; set; } = true;
    public string? Conf { get; set; }
}

public sealed class ShapeDef
{
    /// <summary>circle | donut | cone | rect | cross | halfRoom | knockback | gaze | arrow | none</summary>
    public string Type { get; set; } = "circle";

    public float? Radius { get; set; }
    public float? InnerRadius { get; set; }
    public float? AngleDeg { get; set; }
    public float? Length { get; set; }
    public float? Width { get; set; }
    public float? BackLength { get; set; }

    /// <summary>caster | castLoc | target | animTarget | location | fixed | point | statusHolder | headMarkerHolder | eobj</summary>
    public string? Origin { get; set; }

    public float[]? At { get; set; }
    public string? Point { get; set; }

    /// <summary>cast | resolution | towardTarget | fixed | actor</summary>
    public string? Heading { get; set; }

    public float? HeadingDeg { get; set; }
    public float? HeadingOffsetDeg { get; set; }
    public HexId? Status { get; set; }
    public HexId? Marker { get; set; }

    /// <summary>For target-following shapes: stop following this many seconds before resolution.</summary>
    public float? SnapshotS { get; set; }
}

public sealed class TelegraphDef
{
    /// <summary>cast (default: telegraph = cast bar) | lookback (draw S seconds before the hit) | none</summary>
    public string Mode { get; set; } = "cast";

    public float S { get; set; }
}

public sealed class AbilityDef
{
    public string? Name { get; set; }
    public string? Label { get; set; }
    public string? Mech { get; set; }
    public ShapeDef? Shape { get; set; }

    /// <summary>danger | fake | hiddenDanger | tower | stack | spread | tankbuster | raidwide | knockback | gaze | failure | bait | info | ignore</summary>
    public string? Category { get; set; }

    public TelegraphDef? Telegraph { get; set; }
    public bool? Damage { get; set; }
    public int? Soakers { get; set; }

    /// <summary>
    /// For stacks with <see cref="Soakers"/>: who is supposed to take it. "role" = the holder's role group (tanks and
    /// healers, or DPS); a stack short of soakers is blamed on the group members who stayed out.
    /// </summary>
    public string? SoakGroup { get; set; }

    /// <summary>For stacks/knockbacks: where the holder and the soakers are supposed to stand, per occurrence and group.</summary>
    public List<StackPositionDef>? Positions { get; set; }

    public List<HexId>? ResolvesWith { get; set; }
    public bool ExcludesHolder { get; set; }
    public bool Knockback { get; set; }
    public string? Color { get; set; }
    public string? Conf { get; set; }

    /// <summary>For failure abilities: what sets them off, so the analyzer can name who caused it.</summary>
    public FailureCauseDef? FailureCause { get; set; }

    /// <summary>
    /// For soaks (category tower/soak): the earliest pull time (s) at which resolving it is intended, e.g. puddles that
    /// must only be soaked after a knockback. A player touching it before then sets it off early.
    /// </summary>
    public float? NotBeforeS { get; set; }

    /// <summary>Text for the intended timing, e.g. "after the 2nd confetti knockback".</summary>
    public string? NotBeforeLabel { get; set; }
}

/// <summary>
/// Fixed spots for one resolution of a stack/knockback, e.g. "holder on the top-left corner of marker 1, the other three
/// on its bottom-right corner". A knockback that sends someone somewhere they shouldn't be is blamed on whoever was off
/// their spot.
/// </summary>
public sealed class StackPositionDef
{
    /// <summary>Which resolution of the ability (1-based; resolutions less than 2s apart count as one). 0 = every one.</summary>
    public int Occurrence { get; set; }

    /// <summary>The holder's role group this applies to: "support" (tanks, healers), "dps" or "any".</summary>
    public string Group { get; set; } = "any";

    public SpotDef Holder { get; set; } = new();
    public SpotDef Soakers { get; set; } = new();

    /// <summary>How far from the spot still counts as in position.</summary>
    public float Tolerance { get; set; } = 2.5f;

    public string? Note { get; set; }
}

/// <summary>A spot relative to a waymark (A–D, 1–4), with a fallback position for when the waymark isn't logged.</summary>
public sealed class SpotDef
{
    public string? Waymark { get; set; }
    public float[]? Default { get; set; }
    public float[] Offset { get; set; } = [0, 0];
}

/// <summary>
/// A failure that happens when something comes into contact with a hazard, e.g. a knock-off AoE ("rock") touching a
/// puddle detonates it. The analyzer finds the trigger (or player) closest to a hazard at the moment it resolved.
/// </summary>
public sealed class FailureCauseDef
{
    /// <summary>EObj bases of the hazard (e.g. the puddle).</summary>
    public List<HexId> HazardEobjs { get; set; } = [];

    /// <summary>AoE abilities that set the hazard off on contact (origin = the ability's target).</summary>
    public List<HexId> TriggerActions { get; set; } = [];

    /// <summary>Trigger-origin ↔ hazard-center distance that counts as contact (trigger radius + hazard radius).</summary>
    public float ContactDistance { get; set; } = 10;

    /// <summary>Players standing in the hazard also set it off (contact = hazard radius).</summary>
    public bool PlayerContact { get; set; }

    public float HazardRadius { get; set; } = 5;

    /// <summary>How long before the failure to look for the trigger.</summary>
    public float WindowS { get; set; } = 2;

    /// <summary>Short noun for the trigger, e.g. "rock (Vitrophyre, yellow tether)".</summary>
    public string? TriggerName { get; set; }

    /// <summary>Short noun for the hazard, e.g. "Gravitas puddle".</summary>
    public string? HazardName { get; set; }
}

public sealed class UnboundAbilityDef
{
    public string NameMatch { get; set; } = string.Empty;
    public string? Phase { get; set; }
    public AbilityDef? As { get; set; }
    public string? Conf { get; set; }
}

public sealed class TriggerDrawDef
{
    public string Id { get; set; } = string.Empty;
    public TriggerDef On { get; set; } = new();
    public ShapeDef Draw { get; set; } = new();
    public float DurationS { get; set; } = 5;
    public string? Label { get; set; }
    public string? Category { get; set; }
    public int? Soakers { get; set; }
    public string? Color { get; set; }
    public string? Conf { get; set; }
}

public sealed class HeadMarkerDef
{
    public string Label { get; set; } = string.Empty;
    public string? Color { get; set; }
    public bool OnBoss { get; set; }
    public float DurationS { get; set; } = 5;
    public string? Conf { get; set; }
}

public sealed class TetherDef
{
    public string Label { get; set; } = string.Empty;
    public float ResolveWindowS { get; set; } = 15;

    /// <summary>Label the tether by the next ability (hex id) used by the tether source within the window.</summary>
    public Dictionary<string, string>? LabelsByNextAbility { get; set; }

    public float MaxS { get; set; } = 10;
    public string? Color { get; set; }
    public string? Conf { get; set; }
}

public sealed class PhaseDef
{
    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public TriggerDef? Start { get; set; }
    public List<SegmentDef> Segments { get; set; } = [];
    public float? ExpectedS { get; set; }
    public string? Conf { get; set; }
}

public sealed class SegmentDef
{
    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public TriggerDef Start { get; set; } = new();
}

public sealed class MechanicDef
{
    public string Id { get; set; } = string.Empty;
    public string Label { get; set; } = string.Empty;
    public string? Phase { get; set; }
    public TriggerDef Trigger { get; set; } = new();
    public float DurationS { get; set; }
    public string? Conf { get; set; }
}

public sealed class TriggerDef
{
    public bool? PullStart { get; set; }
    public IdMatch? CastStart { get; set; }
    public IdMatch? Ability { get; set; }
    public BaseMatch? Spawn { get; set; }
    public BaseMatch? Despawn { get; set; }
    public DirectorMatch? Director { get; set; }
    public MapEffectMatch? MapEffect { get; set; }
    public IdMatch? HeadMarker { get; set; }
    public IdMatch? StatusGain { get; set; }
    public IdMatch? StatusLose { get; set; }
    public IdMatch? Tether { get; set; }
    public NameToggleMatch? NameToggle { get; set; }
    public HpMatch? HpBelow { get; set; }
    public ActorControlMatch? ActorControl { get; set; }
    public List<TriggerDef>? AnyOf { get; set; }

    /// <summary>Fire only on the Nth match (1-based) within the pull.</summary>
    public int? Occurrence { get; set; }

    public float? DelayS { get; set; }
}

public sealed class IdMatch
{
    public List<HexId>? Ids { get; set; }
    public List<string>? Names { get; set; }
    public List<uint>? SourceBnpcBase { get; set; }
}

public sealed class BaseMatch
{
    public List<uint>? BnpcBase { get; set; }
    public List<HexId>? EobjBase { get; set; }
    public List<string>? Names { get; set; }
}

public sealed class DirectorMatch
{
    public HexId Command { get; set; }
    public List<HexId>? P1 { get; set; }
    public List<HexId>? P2 { get; set; }
}

public sealed class MapEffectMatch
{
    public HexId? Flags { get; set; }
    public List<HexId>? Location { get; set; }
}

public sealed class NameToggleMatch
{
    public List<uint>? BnpcBase { get; set; }
    public bool? Targetable { get; set; }
}

public sealed class HpMatch
{
    public List<uint>? BnpcBase { get; set; }
    public float Pct { get; set; }
}

public sealed class ActorControlMatch
{
    public HexId Category { get; set; }
    public List<HexId>? P1 { get; set; }
    public List<HexId>? P2 { get; set; }
    public List<uint>? BnpcBase { get; set; }
    public List<HexId>? EobjBase { get; set; }
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
                             ReadCommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true,
                             DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull, WriteIndented = true,
                             UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow)]
[JsonSerializable(typeof(EncounterDefinition))]
internal sealed partial class EncounterJsonContext : JsonSerializerContext;
