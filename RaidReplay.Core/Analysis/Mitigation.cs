using RaidReplay.Core.Encounters;
using RaidReplay.Core.GameData;
using RaidReplay.Core.Model;

namespace RaidReplay.Core.Analysis;

public enum MitTarget : byte
{
    Party,
    Enemy,
}

public enum MitKind : byte
{
    /// <summary>Reduces damage taken (party mit or enemy debuff).</summary>
    Mitigation,

    /// <summary>Barrier.</summary>
    Shield,

    /// <summary>Healing cooldown; tracked but never blamed for a death.</summary>
    Heal,
}

/// <summary>A mitigation ability: what to press (action names), what shows it is up (status names in the log).</summary>
public sealed record MitAbility(
    string Name, string[] Actions, string[] Statuses, MitTarget Target, MitKind Kind, float RecastS, string[] Jobs,
    string? Requires = null, float ActiveForS = 0);

/// <summary>Built-in catalog. Status names are the English log names; recasts are from the game's Action sheet.</summary>
public static class MitigationCatalog
{
    private static readonly string[] Tanks = ["PLD", "WAR", "DRK", "GNB"];
    private static readonly string[] Melee = ["MNK", "DRG", "NIN", "SAM", "RPR", "VPR"];
    private static readonly string[] CastersJ = ["BLM", "SMN", "RDM", "PCT"];

    public static readonly IReadOnlyList<MitAbility> All =
    [
        new("Reprisal", ["Reprisal"], ["Reprisal"], MitTarget.Enemy, MitKind.Mitigation, 60, Tanks),
        new("Feint", ["Feint"], ["Feint"], MitTarget.Enemy, MitKind.Mitigation, 90, Melee),
        new("Addle", ["Addle"], ["Addle"], MitTarget.Enemy, MitKind.Mitigation, 90, CastersJ),
        new("Dismantle", ["Dismantle"], ["Dismantled"], MitTarget.Enemy, MitKind.Mitigation, 120, ["MCH"]),
        new("Heart of Light", ["Heart of Light"], ["Heart of Light"], MitTarget.Party, MitKind.Mitigation, 90, ["GNB"]),
        new("Dark Missionary", ["Dark Missionary"], ["Dark Missionary"], MitTarget.Party, MitKind.Mitigation, 90, ["DRK"]),
        new("Shake It Off", ["Shake It Off"], ["Shake It Off", "Shake It Off (Over Time)"], MitTarget.Party, MitKind.Shield, 90, ["WAR"]),
        new("Divine Veil", ["Divine Veil"], ["Divine Veil"], MitTarget.Party, MitKind.Shield, 90, ["PLD"]),
        new("Temperance", ["Temperance"], ["Temperance"], MitTarget.Party, MitKind.Mitigation, 120, ["WHM"]),
        new("Divine Caress", ["Divine Caress"], ["Divine Caress", "Divine Aura"], MitTarget.Party, MitKind.Shield, 0, ["WHM"], "Temperance"),
        new("Plenary Indulgence", ["Plenary Indulgence"], ["Confession"], MitTarget.Party, MitKind.Mitigation, 60, ["WHM"]),
        new("Liturgy of the Bell", ["Liturgy of the Bell"], ["Liturgy of the Bell"], MitTarget.Party, MitKind.Heal, 180, ["WHM"], ActiveForS: 20),
        new("Neutral Sect", ["Neutral Sect"], ["Neutral Sect"], MitTarget.Party, MitKind.Heal, 120, ["AST"], ActiveForS: 20),
        new("Sun Sign", ["Sun Sign"], ["Sun Sign"], MitTarget.Party, MitKind.Mitigation, 0, ["AST"], "Neutral Sect"),
        new("Collective Unconscious", ["Collective Unconscious"], ["Collective Unconscious"], MitTarget.Party, MitKind.Mitigation, 60, ["AST"]),
        new("Macrocosmos", ["Macrocosmos"], ["Macrocosmos"], MitTarget.Party, MitKind.Heal, 180, ["AST"], ActiveForS: 15),
        new("Spreadlo", ["Deployment Tactics"], ["Galvanize", "Catalyze"], MitTarget.Party, MitKind.Shield, 120, ["SCH"]),
        new("Expedient", ["Expedient"], ["Desperate Measures"], MitTarget.Party, MitKind.Mitigation, 120, ["SCH"]),
        new("Sacred Soil", ["Sacred Soil"], ["Sacred Soil"], MitTarget.Party, MitKind.Mitigation, 30, ["SCH"]),
        new("Summon Seraph", ["Summon Seraph", "Consolation"], ["Seraphic Veil"], MitTarget.Party, MitKind.Heal, 120, ["SCH"], ActiveForS: 22),
        new("Fey Illumination", ["Fey Illumination", "Seraphic Illumination"], ["Fey Illumination", "Seraphic Illumination"],
            MitTarget.Party, MitKind.Mitigation, 120, ["SCH"]),
        new("Seraphism", ["Seraphism"], ["Seraphism"], MitTarget.Party, MitKind.Heal, 180, ["SCH"], ActiveForS: 20),
        new("Kerachole", ["Kerachole"], ["Kerachole"], MitTarget.Party, MitKind.Mitigation, 30, ["SGE"]),
        new("Zoe Shields", ["Eukrasian Prognosis", "Eukrasian Prognosis II"], ["Eukrasian Prognosis"], MitTarget.Party, MitKind.Shield, 0, ["SGE"]),
        new("Holos", ["Holos"], ["Holos", "Holosakos"], MitTarget.Party, MitKind.Mitigation, 120, ["SGE"]),
        new("Panhaima", ["Panhaima"], ["Panhaima"], MitTarget.Party, MitKind.Shield, 120, ["SGE"]),
        new("Philosophia", ["Philosophia"], ["Eudaimonia", "Philosophia"], MitTarget.Party, MitKind.Heal, 180, ["SGE"], ActiveForS: 20),
        new("Tactician", ["Tactician"], ["Tactician"], MitTarget.Party, MitKind.Mitigation, 120, ["MCH"]),
        new("Troubadour", ["Troubadour"], ["Troubadour"], MitTarget.Party, MitKind.Mitigation, 120, ["BRD"]),
        new("Shield Samba", ["Shield Samba"], ["Shield Samba"], MitTarget.Party, MitKind.Mitigation, 120, ["DNC"]),
        new("Magick Barrier", ["Magick Barrier"], ["Magick Barrier"], MitTarget.Party, MitKind.Mitigation, 120, ["RDM"]),
        new("Tempera Grassa", ["Tempera Coat", "Tempera Grassa"], ["Tempera Grassa"], MitTarget.Party, MitKind.Shield, 120, ["PCT"]),
    ];

    /// <summary>
    /// Approximate effect against magic damage (all logged DMU mechanics are magic): damage reduction fraction and
    /// barrier size as a fraction of the target's max HP. Used only to estimate whether missing mitigation would have
    /// changed a death, so the numbers are deliberately rough.
    /// </summary>
    private static readonly Dictionary<string, (float Reduction, float Shield)> Effect = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Reprisal"] = (0.10f, 0), ["Feint"] = (0.05f, 0), ["Addle"] = (0.10f, 0), ["Dismantle"] = (0.10f, 0),
        ["Heart of Light"] = (0.10f, 0), ["Dark Missionary"] = (0.10f, 0), ["Shake It Off"] = (0, 0.15f),
        ["Divine Veil"] = (0, 0.10f), ["Temperance"] = (0.10f, 0), ["Divine Caress"] = (0, 0.10f),
        ["Plenary Indulgence"] = (0.10f, 0), ["Sun Sign"] = (0.10f, 0), ["Collective Unconscious"] = (0.10f, 0),
        ["Spreadlo"] = (0, 0.15f), ["Expedient"] = (0.10f, 0), ["Sacred Soil"] = (0.10f, 0),
        ["Fey Illumination"] = (0.05f, 0), ["Kerachole"] = (0.10f, 0), ["Zoe Shields"] = (0, 0.15f),
        ["Holos"] = (0.10f, 0.10f), ["Panhaima"] = (0, 0.10f), ["Tactician"] = (0.15f, 0), ["Troubadour"] = (0.15f, 0),
        ["Shield Samba"] = (0.15f, 0), ["Magick Barrier"] = (0.10f, 0), ["Tempera Grassa"] = (0, 0.10f),
    };

    /// <summary>Estimated damage the hit would have done with these extra mitigations (null if they don't reduce damage).</summary>
    public static int? WithMitigation(int damage, int maxHp, IEnumerable<MitAbility> extra)
    {
        var factor = 1f;
        var shield = 0f;
        var any = false;
        foreach (var a in extra)
        {
            if (!Effect.TryGetValue(a.Name, out var e))
                continue;
            any = true;
            factor *= 1 - e.Reduction;
            shield += e.Shield * maxHp;
        }

        return any ? Math.Max(0, (int)Math.Round((damage * (double)factor) - shield)) : null;
    }

    private static readonly Dictionary<string, MitAbility> ByName =
        All.ToDictionary(a => a.Name, StringComparer.OrdinalIgnoreCase);

    private static readonly Dictionary<string, string> PartyMitByJob = new()
    {
        ["GNB"] = "Heart of Light", ["DRK"] = "Dark Missionary", ["WAR"] = "Shake It Off", ["PLD"] = "Divine Veil",
        ["MCH"] = "Tactician", ["BRD"] = "Troubadour", ["DNC"] = "Shield Samba",
    };

    private static readonly Dictionary<string, string> ExtraByJob = new()
    {
        ["MCH"] = "Dismantle", ["RDM"] = "Magick Barrier", ["PCT"] = "Tempera Grassa",
    };

    /// <summary>Resolves a plan token for a job: an ability name, "@partyMit" or "@extra". Null if not applicable.</summary>
    public static MitAbility? Resolve(string token, string job)
    {
        if (token.Equals("@partyMit", StringComparison.OrdinalIgnoreCase))
            return PartyMitByJob.TryGetValue(job, out var n) ? ByName[n] : null;
        if (token.Equals("@extra", StringComparison.OrdinalIgnoreCase))
            return ExtraByJob.TryGetValue(job, out var e) ? ByName[e] : null;
        return ByName.GetValueOrDefault(token);
    }

    public static bool IsKnown(string token) =>
        token.StartsWith('@') || ByName.ContainsKey(token);
}

public enum MitStatus : byte
{
    /// <summary>The effect was up at the hit.</summary>
    Active,

    /// <summary>Pressed, but the effect was not on anyone at the hit (too early/late, or out of range).</summary>
    UsedNotActive,

    /// <summary>Off cooldown and not used: actually missing.</summary>
    Missing,

    /// <summary>Still on cooldown from an earlier use — the plan can't be followed as written.</summary>
    OnCooldown,

    /// <summary>The plan lists it at two mechanics closer together than its recast; it was used for the other one.</summary>
    SheetConflict,

    /// <summary>Its prerequisite (e.g. Temperance for Divine Caress) wasn't up.</summary>
    NeedsPrerequisite,

    /// <summary>The player was dead.</summary>
    Dead,

    /// <summary>A carried-over effect (sheet "➔") that had already worn off: the earlier press was too early.</summary>
    Expired,
}

public sealed class MitEntry
{
    public required string SheetSlot { get; init; }
    public required Actor Player { get; init; }
    public required MitAbility Ability { get; init; }
    public bool Carry { get; init; }
    public MitStatus Status { get; set; }
    public string Detail { get; set; } = string.Empty;

    /// <summary>Counts against the player: off cooldown, alive, and not used.</summary>
    /// <remarks>Carried-over entries are informational: the press they depend on is judged at its own mechanic.</remarks>
    public bool Blamable => !Carry && Status is MitStatus.Missing or MitStatus.UsedNotActive && Ability.Kind != MitKind.Heal;
}

public sealed class MitCheck
{
    public required MitMechanicDef Mechanic { get; init; }
    public int T { get; init; }
    public bool HitFound { get; init; }
    public List<MitEntry> Entries { get; } = [];

    public IEnumerable<MitEntry> Missing => Entries.Where(e => e.Blamable);
}

/// <summary>
/// Checks a pull against an encounter's mitigation plan, treating it as a reference: an entry only counts as missing
/// if the ability was off cooldown, its prerequisite was met, and the player was alive. Plans that ask for the same
/// cooldown twice within its recast are satisfied by using it at either mechanic.
/// </summary>
public static class MitigationChecker
{
    private const int LeadMs = 1000;

    public static List<MitCheck> Check(PullReplay r, Dictionary<Actor, string> slots, IGameData? data = null)
    {
        var plan = r.Encounter?.Def.Mitigation;
        var result = new List<MitCheck>();
        if (plan == null)
            return result;

        // Resolve every mechanic's hit time first (conflict detection needs neighbours).
        var times = new Dictionary<MitMechanicDef, (int T, bool Found)>();
        foreach (var m in plan.Mechanics)
        {
            var at = (int)(m.AtS * 1000);
            if (m.Phase != null)
            {
                // Timed from a phase start (phases that begin on a kill drift against the pull clock).
                var phase = r.Phases.FirstOrDefault(p => !p.IsSegment && p.Id == m.Phase);
                if (phase == null)
                {
                    times[m] = (int.MaxValue, false);
                    continue;
                }

                at += phase.StartMs;
            }

            var window = (int)(m.WindowS * 1000);
            var hit = r.Actions.FirstOrDefault(a => !a.Source.IsPlayer && Math.Abs(a.T - at) <= window && a.Hits.Any(h => h.Target.IsPlayer && h.Damage > 0) &&
                                                    (m.Hits.Any(h => h.Value == a.ActionId) ||
                                                     m.HitNames.Any(n => string.Equals(n, a.Name, StringComparison.OrdinalIgnoreCase))));
            times[m] = hit != null ? (hit.T, true) : (at, false);
        }

        // Which tank is MT and which melee is M1 is a static convention, not something the log states. Try the swaps
        // and keep the assignment that matches what people actually pressed best (ties keep the default).
        var best = Run(r, plan, times, slots, data);
        var bestScore = Score(best);
        foreach (var (swapTanks, swapMelee) in new[] { (false, true), (true, false), (true, true) })
        {
            var alt = Swap(slots, swapTanks, swapMelee);
            if (alt == null)
                continue;
            var res = Run(r, plan, times, alt, data);
            var score = Score(res);
            if (score < bestScore - (swapTanks ? 1 : 0))
            {
                best = res;
                bestScore = score;
                slots.Clear();
                foreach (var (k, v) in alt)
                    slots[k] = v;
            }
        }

        return best;
    }

    private static int Score(List<MitCheck> checks) => checks.Sum(c => c.Entries.Count(e => e.Blamable));

    private static Dictionary<Actor, string>? Swap(Dictionary<Actor, string> slots, bool tanks, bool melee)
    {
        var copy = new Dictionary<Actor, string>(slots);
        bool Exchange(string a, string b)
        {
            var x = copy.FirstOrDefault(kv => kv.Value == a).Key;
            var y = copy.FirstOrDefault(kv => kv.Value == b).Key;
            if (x == null || y == null)
                return false;
            copy[x] = b;
            copy[y] = a;
            return true;
        }

        if (tanks && !Exchange("MT", "OT"))
            return null;
        if (melee && !Exchange("M1", "M2"))
            return null;
        return copy;
    }

    private static List<MitCheck> Run(
        PullReplay r, MitigationPlanDef plan, Dictionary<MitMechanicDef, (int T, bool Found)> times, Dictionary<Actor, string> slots,
        IGameData? data)
    {
        var result = new List<MitCheck>();
        foreach (var m in plan.Mechanics)
        {
            var (t, found) = times[m];
            if (t > r.EndMs || (!found && t > r.EndMs - 2000))
                continue;
            var check = new MitCheck { Mechanic = m, T = t, HitFound = found };
            foreach (var item in m.Plan)
            {
                foreach (var (token, carry) in item.Use.Select(u => (u, false)).Concat(item.Carry.Select(c => (c, true))))
                {
                    foreach (var player in Players(item.Slot, slots))
                    {
                        var job = Jobs.Abbrev(player.Job);
                        if (item.Jobs != null && !item.Jobs.Contains(job, StringComparer.OrdinalIgnoreCase))
                            continue;
                        var ability = MitigationCatalog.Resolve(token, job);
                        if (ability == null || (ability.Jobs.Length > 0 && !ability.Jobs.Contains(job)))
                            continue;
                        var entry = new MitEntry { SheetSlot = item.Slot, Player = player, Ability = ability, Carry = carry };
                        Evaluate(r, entry, t, data);
                        if (carry)
                            Carried(r, entry, t);
                        else if (entry.Status is MitStatus.Missing or MitStatus.UsedNotActive or MitStatus.OnCooldown)
                            ResolveConflict(r, plan, times, m, item.Slot, token, entry);
                        check.Entries.Add(entry);
                    }
                }
            }

            result.Add(check);
        }

        return result;
    }

    private static IEnumerable<Actor> Players(string sheetSlot, Dictionary<Actor, string> slots)
    {
        if (sheetSlot.Equals("Extras", StringComparison.OrdinalIgnoreCase))
            return slots.Keys;
        var p = PartySlots.Resolve(sheetSlot, slots);
        return p != null ? [p] : [];
    }

    private static void Evaluate(PullReplay r, MitEntry e, int t, IGameData? data)
    {
        var a = e.Ability;
        if (IsActive(r, e.Player, a, t))
        {
            e.Status = MitStatus.Active;
            return;
        }

        if (!ShapeValidator.IsAlive(r, e.Player, t - LeadMs))
        {
            e.Status = MitStatus.Dead;
            e.Detail = "dead";
            return;
        }

        var uses = Uses(r, e.Player, a);
        var late = uses.FirstOrDefault(u => u > t && u - t <= 2000, int.MinValue);
        if (late != int.MinValue)
        {
            e.Status = MitStatus.UsedNotActive;
            e.Detail = $"pressed {(late - t) / 1000f:0.0}s after the hit (too late)";
            return;
        }

        var recent = uses.LastOrDefault(u => u <= t && t - u <= 12000);
        if (uses.Any(u => u <= t && t - u <= 12000) && a.RecastS > 0)
        {
            e.Status = MitStatus.UsedNotActive;
            e.Detail = $"used at {Fmt(recent)} but not active at the hit ({(t - recent) / 1000f:0.0}s before)";
            return;
        }

        if (a.Requires != null)
        {
            var parent = MitigationCatalog.Resolve(a.Requires, Jobs.Abbrev(e.Player.Job));
            if (parent != null && !Uses(r, e.Player, parent).Any(u => u <= t && t - u <= 30000))
            {
                e.Status = MitStatus.NeedsPrerequisite;
                e.Detail = $"needs {a.Requires} first";
                return;
            }
        }

        var recastMs = (int)(Recast(a, r, e.Player, data) * 1000);
        var last = uses.LastOrDefault(u => u < t - LeadMs, int.MinValue);
        if (last != int.MinValue && recastMs > 0 && last + recastMs > t - LeadMs)
        {
            e.Status = MitStatus.OnCooldown;
            e.Detail = $"on cooldown: used at {Fmt(last)}, back at {Fmt(last + recastMs)}";
            return;
        }

        e.Status = MitStatus.Missing;
        e.Detail = last == int.MinValue ? "available, not used" : $"available (last used {Fmt(last)}), not used";
    }

    private static void Carried(PullReplay r, MitEntry e, int t)
    {
        if (e.Status is MitStatus.Active or MitStatus.Dead)
            return;
        var pressed = Uses(r, e.Player, e.Ability).LastOrDefault(u => u < t && t - u <= 40000, int.MinValue);
        if (pressed != int.MinValue)
        {
            e.Status = MitStatus.Expired;
            e.Detail = $"pressed at {Fmt(pressed)}, worn off before this hit ({(t - pressed) / 1000f:0.0}s later)";
        }
        else
        {
            e.Detail = "carry-over: not pressed for the earlier mechanic";
        }
    }

    /// <summary>The same slot is asked for the same cooldown at another mechanic within its recast: satisfied there?</summary>
    private static void ResolveConflict(
        PullReplay r, MitigationPlanDef plan, Dictionary<MitMechanicDef, (int T, bool Found)> times, MitMechanicDef current,
        string slot, string token, MitEntry e)
    {
        var recastMs = (int)(e.Ability.RecastS * 1000);
        if (recastMs <= 0)
            return;
        var t = times[current].T;
        foreach (var other in plan.Mechanics)
        {
            if (other == current)
                continue;
            var ot = times[other].T;
            if (Math.Abs(ot - t) >= recastMs || ot > r.EndMs)
                continue;
            var listed = other.Plan.Any(i => i.Slot == slot && i.Use.Contains(token, StringComparer.OrdinalIgnoreCase));
            if (listed && IsActive(r, e.Player, e.Ability, ot))
            {
                e.Status = MitStatus.SheetConflict;
                e.Detail = $"sheet also asks for it at {other.Name} ({Fmt(ot)}), {Math.Abs(ot - t) / 1000}s apart with a {e.Ability.RecastS:0}s recast; used there";
                return;
            }
        }
    }

    private static bool IsActive(PullReplay r, Actor player, MitAbility a, int t)
    {
        // Effects without a reliable status (e.g. Seraph) count as up for a fixed time after use.
        if (a.ActiveForS > 0 && Uses(r, player, a).Any(u => u <= t && t - u <= a.ActiveForS * 1000))
            return true;
        foreach (var s in r.Statuses)
        {
            if (s.StartMs > t + 100 || s.EndMs < t - 300)
                continue;
            var src = s.Source;
            if (src != player && !(src is { Kind: ActorKind.Pet } && src.OwnerId == player.Id))
                continue;
            if (!a.Statuses.Any(n => string.Equals(n, s.Name, StringComparison.OrdinalIgnoreCase)))
                continue;
            if (a.Target == MitTarget.Enemy ? !s.Target.IsPlayer : s.Target.IsPlayer)
                return true;
        }

        return false;
    }

    private static List<int> Uses(PullReplay r, Actor player, MitAbility a)
    {
        var list = new List<int>();
        foreach (var act in r.Actions)
        {
            var src = act.Source;
            var mine = src == player || (src.Kind == ActorKind.Pet && src.OwnerId == player.Id);
            if (mine && a.Actions.Any(n => string.Equals(n, act.Name, StringComparison.OrdinalIgnoreCase)))
            {
                if (list.Count == 0 || act.T - list[^1] > 1500)
                    list.Add(act.T);
            }
        }

        return list;
    }

    private static float Recast(MitAbility a, PullReplay r, Actor player, IGameData? data)
    {
        if (data != null)
        {
            var id = r.Actions.FirstOrDefault(x => x.Source == player && a.Actions.Contains(x.Name))?.ActionId;
            if (id is { } aid && data.GetAction(aid) is { RecastS: > 2.5f } info)
                return info.RecastS;
        }

        return a.RecastS;
    }

    private static string Fmt(int ms) => $"{ms / 60000}:{Math.Abs(ms) / 1000 % 60:00}";
}
