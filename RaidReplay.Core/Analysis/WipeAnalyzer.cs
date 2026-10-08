using System.Text;
using System.Numerics;
using RaidReplay.Core.Encounters;
using RaidReplay.Core.Geometry;
using RaidReplay.Core.Model;

namespace RaidReplay.Core.Analysis;

/// <summary>
/// Explains a wipe: finds incidents (deaths, failure actions, avoidable hits, under-soaked towers, missed stacks,
/// spread overlaps, enrage), picks the root cause (earliest severe incident), and for each incident snapshots every
/// raider with "where they should have been" (geometric safe spot / soak position / learned typical position).
/// </summary>
public static class WipeAnalyzer
{
    private const int GroupWindowMs = 400;

    public static WipeReport Analyze(PullReplay r, PositionProfile? profile = null, GameData.IGameData? data = null)
    {
        var report = new WipeReport { Pull = r, LearnedPulls = profile?.PullCount ?? 0 };
        var groups = PositionProfile.Groups(r);
        var endMs = r.EndMs;
        report.Slots = PartySlots.Assign(r);
        report.Mitigation = MitigationChecker.Check(r, report.Slots, data);

        FindDeaths(r, report, groups, profile);
        FindFailureActions(r, report);
        FindMechanicMisses(r, report, groups, profile);
        FindArrowPuzzle(r, report);
        FindBaitLandings(r, report);
        FindCleansePulses(r, report);
        AttachMitigation(r, report);

        report.Incidents.Sort((a, b) => a.T != b.T ? a.T.CompareTo(b.T) : b.Severity.CompareTo(a.Severity));
        Dedupe(report);

        foreach (var inc in report.Incidents)
        {
            inc.Mechanic ??= MechanicAt(r, inc.T);
            Snapshot(r, inc, groups, profile, report.Slots);
        }

        PickRootCause(r, report);
        report.Phase = r.PhaseAt(endMs);
        report.Segment = SegmentAt(r, endMs);
        Summarize(r, report);
        return report;
    }

    // ---- incident detection --------------------------------------------------------------------------------

    private static void FindDeaths(PullReplay r, WipeReport report, List<MechanicGroup> groups, PositionProfile? profile)
    {
        foreach (var d in r.Deaths)
        {
            if (!d.Victim.IsPlayer || d.T > r.EndMs + 500)
                continue;
            var fell = d.Cause.StartsWith("Fell", StringComparison.Ordinal);
            var inc = new Incident
            {
                Kind = fell ? IncidentKind.FellOff : IncidentKind.Death,
                T = d.T,
                Title = fell ? $"{d.Victim.Name} fell off the arena" : $"{d.Victim.Name} died",
                Death = d,
                Severity = 3,
            };
            inc.Players.Add(d.Victim);

            var kb = d.KillingBlow;
            if (kb != null)
            {
                var aoe = r.Aoes.FirstOrDefault(a => a.Action == kb.Action);
                if (aoe != null)
                    inc.Aoes.Add(aoe);
                // Simultaneous hits (e.g. four stacks landing together) share one HP snapshot: judge them as one burst.
                // A player can be the killer too (e.g. a Confused ally's auto-attack).
                var burst = r.Actions.Where(a => (!a.Source.IsPlayer || a.Source == kb.Action.Source) && a.T >= kb.T - 1000 && a.T <= kb.T + 100)
                             .SelectMany(a => a.Hits.Where(h => h.Target == d.Victim && h.Damage > 0)).ToList();
                var ticks = r.Ticks.Where(k => k.Target == d.Victim && !k.IsHeal && k.T >= kb.T - 1000 && k.T <= kb.T + 100).Sum(k => k.Amount);
                var burstDamage = burst.Sum(h => h.Damage) + ticks;
                var hpBefore = burst.Count > 0 ? burst.MinBy(h => h.T)!.HpBefore : kb.HpBefore;
                if (burst.Count > 1)
                    hpBefore = Math.Max(hpBefore, burst.Where(h => h.T == burst.Min(x => x.T)).Max(h => h.HpBefore));
                inc.BurstDamage = burstDamage;
                inc.HpBeforeBurst = hpBefore;
                var hpPct = kb.MaxHp > 0 ? 100.0 * hpBefore / kb.MaxHp : -1;
                var overkill = burstDamage - hpBefore;
                if (burst.Count > 1)
                {
                    var names = burst.GroupBy(h => h.Action.Name).Select(g => g.Count() > 1 ? $"{g.Count()}× {g.Key}" : g.Key);
                    inc.Detail = $"Killed by {string.Join(" + ", names)} ({burstDamage:N0} total within 1s, {hpPct:0}% HP before" +
                                 (overkill > 0 ? $", overkill {overkill:N0}" : "") + ")";
                }
                else
                {
                    inc.Detail = $"Killed by {kb.Action.Name} ({kb.Damage:N0} dmg, {hpPct:0}% HP before" +
                                 (overkill > 0 ? $", overkill {overkill:N0}" : "") + ")";
                }

                if (overkill < 0)
                    inc.Detail += $" — logged damage doesn't account for the death ({burstDamage:N0} against {hpBefore:N0} HP); cause unclear";
                var category = aoe?.Category;
                if (category is AoeCategory.Danger or AoeCategory.HiddenDanger or AoeCategory.Gaze)
                    inc.Detail += $" — stood in {aoe!.Label}";
                else if (category is AoeCategory.Raidwide)
                    inc.Detail += " — raidwide; check HP/mitigation before it";
                else if (category is AoeCategory.Tankbuster && !IsTank(d.Victim))
                    inc.Detail += " — tankbuster cleave on a non-tank (stand away from the tank/boss facing)";
                var mits = r.Statuses.Where(s => s.Target == d.Victim && s.Active(kb.T) && IsFriendly(s.Source))
                            .Select(s => s.Name).Distinct().Take(6).ToList();
                if (mits.Count > 0)
                    inc.Detail += $"; buffs: {string.Join(", ", mits)}";
                var debuffs = r.Statuses.Where(s => s.Target == d.Victim && s.Active(kb.T) && !IsFriendly(s.Source))
                               .Select(s => s.Name).Distinct().Take(4).ToList();
                if (debuffs.Count > 0)
                    inc.Detail += $"; debuffs: {string.Join(", ", debuffs)}";

                // A vulnerability debuff from an earlier mistake is usually what made the hit lethal.
                foreach (var vuln in r.Statuses.Where(s => s.Target == d.Victim && s.Active(kb.T) && !IsFriendly(s.Source) &&
                                                          s.Name.Contains("Vulnerability Up", StringComparison.OrdinalIgnoreCase)))
                {
                    var from = r.Actions.LastOrDefault(a => !a.Source.IsPlayer && Math.Abs(a.T - vuln.StartMs) <= 400 &&
                                                            a.Hits.Any(h => h.Target == d.Victim));
                    // Many mechanics apply a vulnerability by design (raidwides, soaks, baits). Only one that came from an
                    // avoidable hit is a mistake.
                    var fromAoe = from != null ? r.Aoes.FirstOrDefault(x => x.Action == from) : null;
                    var mistake = fromAoe?.Category is AoeCategory.Danger or AoeCategory.HiddenDanger ||
                                  (fromAoe?.Category == AoeCategory.Tankbuster && !IsTank(d.Victim)) ||
                                  (fromAoe?.Category == AoeCategory.Spread && SpreadOwner(r, fromAoe) is { } so && so != d.Victim);
                    if (!mistake)
                        break;
                    inc.Detail += $". Had {vuln.Name} from {from!.Name} at {FormatMs(from.T)} (avoidable) — that made this hit lethal";
                    inc.VerdictHint = "Vulnerability";
                    break;
                }

                // Killed by another player's targeted AoE (a tether rock, a bait line, a spread): whoever was off their
                // spot is at fault, which need not be the player who died.
                foreach (var a in burst.Select(h => r.Aoes.FirstOrDefault(x => x.Action == h.Action)).Distinct())
                {
                    if (a == null || OwnedBy(r, a) is not { } owner || owner == d.Victim ||
                        JudgeOwned(r, a, owner, d.Victim, groups, profile) is not { } fault)
                        continue;
                    ApplyOwnedFault(inc, fault, owner, d.Victim, a);
                    inc.Title = $"{d.Victim.Name} died to {owner.Name}'s {a.Label}";
                    break;
                }
            }
            else
            {
                inc.Detail = d.Cause;
            }

            if (fell)
            {
                var kbHit = r.Actions.Where(a => a.T <= d.T && a.T >= d.T - 4000)
                             .SelectMany(a => a.Hits).LastOrDefault(h => h.Target == d.Victim && h.Knockback);
                if (kbHit != null)
                {
                    var center = Center(r);
                    inc.Detail = $"Knocked back by {kbHit.Action.Name} from {Vector2.Distance(kbHit.TargetPos, center):0.0}y off center";
                    var aoe = r.Aoes.FirstOrDefault(a => a.Action == kbHit.Action);
                    if (aoe != null)
                        inc.Aoes.Add(aoe);
                }
                else
                {
                    inc.Detail = "Left the arena (no knockback recorded)";
                }
            }

            MarkDamageDownReset(r, inc, d, fell);
            report.Incidents.Add(inc);
        }
    }

    /// <summary>
    /// Damage Down (−90% damage dealt for 2 minutes) from an avoidable hit is often fixed by dying on purpose — jumping
    /// off the arena or eating a tankbuster/auto — and being raised with Weakness (−25% for 1 minute) instead. Such a
    /// death is a reset, not a wipe cause; the hit that applied the Damage Down is the mistake.
    /// </summary>
    private static void MarkDamageDownReset(PullReplay r, Incident inc, DeathEvent d, bool fell)
    {
        var dd = r.Statuses.LastOrDefault(s => s.Target == d.Victim && s.Source is not { IsPlayer: true } &&
                                               s.Name.Equals("Damage Down", StringComparison.OrdinalIgnoreCase) &&
                                               s.StartMs <= d.T - 1000 && d.T - s.StartMs <= 60000 && s.EndMs >= d.T - 3000);
        if (dd == null)
            return;

        var kb = d.KillingBlow;
        string? how = null;
        if (fell && !inc.Detail.StartsWith("Knocked back", StringComparison.Ordinal))
            how = "jumped off the arena";
        else if (kb != null && !IsTank(d.Victim))
        {
            var aoe = r.Aoes.FirstOrDefault(a => a.Action == kb.Action);
            if (aoe?.Category == AoeCategory.Tankbuster)
                how = $"stood in the tankbuster ({kb.Action.Name})";
            else if (aoe == null && kb.Action.Source.Kind == ActorKind.Boss && kb.Action.Hits.Count == 1)
                how = $"took the boss's {kb.Action.Name}";
        }

        if (how == null)
            return;

        // What applied it: an enemy action that hit them just before the debuff landed (not a heal from the party).
        // Among several enemy hits at once, a mistake (a danger AoE or a failure) is what gives Damage Down.
        var candidates = r.Actions.Where(a => !IsFriendly(a.Source) && a.T <= dd.StartMs + 200 && a.T >= dd.StartMs - 1500 &&
                                              a.Hits.Any(h => h.Target == d.Victim)).ToList();
        var cause = candidates.LastOrDefault(a => r.Aoes.Any(x => x.Action == a && x.Category is AoeCategory.Danger or AoeCategory.HiddenDanger or AoeCategory.Failure) ||
                                                  r.Encounter?.FailureActions.Contains(a.ActionId) == true)
                    ?? candidates.LastOrDefault();
        var from = cause != null ? $"{cause.Name} at {FormatMs(cause.T)}" : FormatMs(dd.StartMs);
        inc.Intentional = true;
        inc.Severity = 1;
        inc.VerdictHint = null;
        inc.DamageDownAt = cause?.T ?? dd.StartMs;

        // Most of the party got the same Damage Down and nobody raised them before the end: the pull was lost and they
        // ended it. That is no reset.
        var sameDd = r.Party.Count(p => r.Statuses.Any(s => s.Target == p && s.Source is not { IsPlayer: true } &&
                                                            s.Name.Equals("Damage Down", StringComparison.OrdinalIgnoreCase) &&
                                                            Math.Abs(s.StartMs - dd.StartMs) <= 2000));
        var raisedInPull = d.RaisedMs >= 0 && d.RaisedMs <= r.EndMs;
        if (!raisedInPull && sameDd >= 5)
        {
            inc.DeliberateWipe = true;
            inc.Title = $"{d.Victim.Name} wiped on purpose: {how}";
            inc.Detail = $"Damage Down on {sameDd} players from {from}; the pull was lost, so they ended it";
            return;
        }

        inc.Title = $"{d.Victim.Name} reset Damage Down: {how}";
        inc.Detail = $"Damage Down (−90% damage dealt for 2 min) from {from}; " +
                     $"died on purpose {(d.T - dd.StartMs) / 1000f:0.0}s later to trade it for Weakness (−25% for 1 min) — {RaiseInfo(r, d)}";
    }

    private static readonly HashSet<string> RaiseActions =
        new(StringComparer.OrdinalIgnoreCase) { "Raise", "Resurrection", "Ascend", "Egeiro", "Verraise", "Angel Whisper" };

    /// <summary>Who raised the player and whether it cost a hardcast (no Swiftcast/Dualcast).</summary>
    private static string RaiseInfo(PullReplay r, DeathEvent d)
    {
        var until = d.RaisedMs >= 0 ? d.RaisedMs : r.EndMs;
        var raise = r.Actions.FirstOrDefault(a => a.Source.IsPlayer && a.T >= d.T && a.T <= until && RaiseActions.Contains(a.Name) &&
                                                  a.Hits.Any(h => h.Target == d.Victim));
        if (raise == null)
            return d.RaisedMs >= 0 ? $"raised at {FormatMs(d.RaisedMs)}" : "not raised before the wipe";
        var how = raise.Cast is { DurationMs: > 1500 } c ? $"hardcast {c.DurationMs / 1000f:0.0}s" : "Swiftcast/instant";
        var accepted = d.RaisedMs >= 0 ? $", up at {FormatMs(d.RaisedMs)}" : ", not accepted before the wipe";
        return $"raised by {raise.Source.Name} at {FormatMs(raise.T)} ({how}){accepted}";
    }

    /// <summary>Time each player spent dead, under Damage Down, or with Weakness before <paramref name="untilMs"/>.</summary>
    private static string DpsLoss(PullReplay r, int untilMs)
    {
        var parts = new List<string>();
        foreach (var p in r.Party)
        {
            var dead = 0;
            foreach (var d in r.Deaths.Where(d => d.Victim == p && d.T < untilMs))
                dead += (d.RaisedMs >= 0 ? Math.Min(d.RaisedMs, untilMs) : untilMs) - d.T;
            int Uptime(string name) => r.Statuses.Where(s => s.Target == p && s.Source is not { IsPlayer: true } && s.StartMs < untilMs &&
                                                             s.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
                                         .Sum(s => Math.Min(s.EndMs, untilMs) - Math.Max(0, s.StartMs));
            var dd = Uptime("Damage Down");
            var weak = Uptime("Weakness") + Uptime("Brink of Death");
            var bits = new List<string>();
            if (dead > 1000)
                bits.Add($"dead {dead / 1000}s");
            if (dd > 1000)
                bits.Add($"Damage Down {dd / 1000}s");
            if (weak > 1000)
                bits.Add($"Weakness {weak / 1000}s");
            if (bits.Count > 0)
                parts.Add($"{p.Name} ({string.Join(", ", bits)})");
        }

        return parts.Count > 0 ? "Lost damage before the enrage: " + string.Join("; ", parts) : string.Empty;
    }

    private static void FindFailureActions(PullReplay r, WipeReport report)
    {
        var failureIds = r.Encounter?.FailureActions ?? [];
        var failures = r.Actions.Where(a => a.T <= r.EndMs + 500 && !a.Source.IsPlayer &&
                                            (failureIds.Contains(a.ActionId) ||
                                             r.Aoes.Any(x => x.Action == a && x.Category == AoeCategory.Failure)))
                        .ToList();

        // Simultaneous instances of the same failure (e.g. four puddles detonating) form one incident.
        var i = 0;
        while (i < failures.Count)
        {
            var a = failures[i];
            var j = i + 1;
            while (j < failures.Count && failures[j].ActionId == a.ActionId && failures[j].T - a.T <= 1000)
                j++;
            var batch = failures.GetRange(i, j - i);
            i = j;

            var def = r.Encounter?.AbilityFor(a.ActionId, a.Name);
            var enrage = def?.Category == "enrage";
            var label = def?.Label ?? a.Name;
            var inc = new Incident
            {
                Kind = enrage ? IncidentKind.Enrage : IncidentKind.FailureAction,
                T = a.T,
                Title = (enrage ? $"Enrage: {label}" : $"Mechanic failed: {label}") + (batch.Count > 1 ? $" ×{batch.Count}" : ""),
                Severity = enrage ? 4 : 3,
            };
            if (enrage)
            {
                inc.Detail = BossHpText(r, a.T);
            }
            else
            {
                var hit = batch.SelectMany(b => b.Hits).Where(h => h.Target.IsPlayer).Select(h => h.Target).Distinct().Count();
                var total = batch.SelectMany(b => b.Hits).Sum(h => (long)h.Damage);
                inc.Detail = hit > 0 ? $"Hit {hit} player(s) for {total:N0} total" : string.Empty;

                if (def?.FailureCause is { } cause)
                    BlameContact(r, inc, cause, label, a.T);
                else
                    BlameStandingInHazard(r, inc, a.T);

                // A failure that only hit one or two players went off on them (e.g. a bomb debuff set off by moving).
                var victims = batch.SelectMany(b => b.Hits).Where(h => h.Target.IsPlayer).Select(h => h.Target).Distinct().ToList();
                if (inc.Players.Count == 0 && victims.Count is 1 or 2)
                {
                    inc.Players.AddRange(victims);
                    inc.Detail += $" ({string.Join(", ", victims.Select(v => v.Name))})";
                }
            }

            report.Incidents.Add(inc);
        }

        // Enrage casts (cast start) are a clearer signal than the hit. One cut short (the bosses died) is no enrage.
        foreach (var c in r.Casts)
        {
            var def = r.Encounter?.AbilityFor(c.ActionId, c.Name);
            if (def?.Category != "enrage" || c.Outcome == CastOutcome.Cancelled ||
                report.Incidents.Any(i => i.Kind == IncidentKind.Enrage) || c.StartMs > r.EndMs)
                continue;
            report.Incidents.Add(new Incident
            {
                Kind = IncidentKind.Enrage,
                T = c.StartMs,
                Title = $"Enrage: {def.Label ?? c.Name}",
                Detail = BossHpText(r, c.StartMs),
                Severity = 4,
            });
        }

        // Prefer the cast start time for enrage incidents found via the hit.
        foreach (var inc in report.Incidents.Where(x => x.Kind == IncidentKind.Enrage).ToList())
        {
            var cast = r.Casts.LastOrDefault(c => c.StartMs <= inc.T && inc.T - c.StartMs <= 15000 &&
                                                  r.Encounter?.AbilityFor(c.ActionId, c.Name)?.Category == "enrage");
            if (cast != null && cast.StartMs != inc.T)
            {
                report.Incidents.Remove(inc);
                report.Incidents.Add(new Incident
                {
                    Kind = IncidentKind.Enrage, T = cast.StartMs, Title = inc.Title, Detail = BossHpText(r, cast.StartMs), Severity = 4,
                });
            }
        }
    }

    /// <summary>
    /// Links mitigation-plan checks to incidents: deaths at a planned mechanic list what was missing (only cooldowns that
    /// were actually available count), and each mechanic with available-but-unused mitigation gets its own incident.
    /// </summary>
    private static void AttachMitigation(PullReplay r, WipeReport report)
    {
        foreach (var check in report.Mitigation)
        {
            var missing = check.Missing.ToList();
            foreach (var inc in report.Incidents.Where(i => i.Kind is IncidentKind.Death && i.Death != null && !i.Intentional))
            {
                var hitT = inc.Death!.KillingBlow?.T ?? inc.T;
                var sameHit = inc.Death.KillingBlow != null &&
                              (check.Mechanic.Hits.Any(h => h.Value == inc.Death.KillingBlow.Action.ActionId) ||
                               check.Mechanic.HitNames.Any(n => string.Equals(n, inc.Death.KillingBlow.Action.Name, StringComparison.OrdinalIgnoreCase)));
                if (Math.Abs(hitT - check.T) > 2500 || (!sameHit && Math.Abs(hitT - check.T) > 1000))
                    continue;
                inc.Mitigation = check;
                if (missing.Count > 0)
                {
                    inc.Detail += $". Planned mitigation missing at {check.Mechanic.Name} (cooldown was available): {Describe(report, missing)}";
                    var kb = inc.Death.KillingBlow;
                    var lethal = inc.BurstDamage >= inc.HpBeforeBurst && inc.BurstDamage > 0;
                    if (kb != null && lethal &&
                        MitigationCatalog.WithMitigation(inc.BurstDamage, kb.MaxHp, missing.Select(e => e.Ability)) is { } est)
                    {
                        var survivable = est < inc.HpBeforeBurst;
                        inc.Detail += survivable
                                          ? $" — with it ≈{est:N0} damage against {inc.HpBeforeBurst:N0} HP: likely survivable"
                                          : $" — even with it ≈{est:N0} against {inc.HpBeforeBurst:N0} HP: still lethal";
                        if (survivable)
                            inc.VerdictHint ??= "Missing mitigation";
                    }
                }
                var conflicts = check.Entries.Where(e => e.Status is MitStatus.OnCooldown or MitStatus.SheetConflict).ToList();
                if (conflicts.Count > 0)
                    inc.Detail += $". Not available per sheet (cooldown): {Describe(report, conflicts)}";
            }

            if (missing.Count == 0 || !check.HitFound)
                continue;
            var mi = new Incident
            {
                Kind = IncidentKind.MissingMitigation,
                T = check.T,
                Title = $"{check.Mechanic.Name}: planned mitigation missing — {Describe(report, missing)}",
                Detail = string.Join("; ", missing.Select(e => $"{e.Ability.Name} ({SlotLabel(report, e)} {e.Player.Name}): {e.Detail}")),
                Severity = 1,
                Mitigation = check,
                Mechanic = check.Mechanic.Name,
            };
            mi.Players.AddRange(missing.Select(e => e.Player).Distinct());
            report.Incidents.Add(mi);
        }
    }

    private static string SlotLabel(WipeReport report, MitEntry e)
    {
        var slot = report.SlotOf(e.Player);
        return slot.Length > 0 ? slot : e.SheetSlot;
    }

    private static string Describe(WipeReport report, IEnumerable<MitEntry> entries) =>
        string.Join(", ", entries.Select(e => $"{e.Ability.Name} ({SlotLabel(report, e)})"));

    /// <summary>Generic fallback: who was standing in a hazard (e.g. a puddle that detonates on contact) just before.</summary>
    private static void BlameStandingInHazard(PullReplay r, Incident inc, int t0)
    {
        var hazards = r.Aoes.Where(x => x.Category == AoeCategory.Hazard && x.StartMs <= t0 && x.EndMs >= t0 - 1500).ToList();
        var culprits = new List<string>();
        foreach (var p in r.Party)
        {
            foreach (var hz in hazards)
            {
                var stepped = false;
                for (var t = t0 - 1500; t <= t0 && !stepped; t += 250)
                {
                    if (t < hz.StartMs || !p.Track.TrySample(t, out var pos, out _))
                        continue;
                    var (o, h) = hz.Placement(t);
                    stepped = ShapeMath.Contains(hz.Shape, o, h, pos);
                }

                if (stepped)
                {
                    inc.Players.Add(p);
                    inc.Aoes.Add(hz);
                    culprits.Add($"{p.Name} ({hz.Label})");
                    break;
                }
            }
        }

        if (culprits.Count > 0)
            inc.Detail += (inc.Detail.Length > 0 ? ". " : "") + "Standing in a hazard just before: " + string.Join(", ", culprits);
    }

    /// <summary>
    /// Pack-described contact failure (e.g. a "rock" knock-off AoE touching a puddle detonates it): find the trigger
    /// resolution, or the player, closest to a hazard when it happened, and say where they should have been instead.
    /// </summary>
    private static void BlameContact(PullReplay r, Incident inc, FailureCauseDef cause, string label, int t0)
    {
        var windowMs = (int)(cause.WindowS * 1000);
        var hazardBases = cause.HazardEobjs.Select(h => h.Value).ToHashSet();
        var triggerIds = cause.TriggerActions.Select(h => h.Value).ToHashSet();
        var hazardName = cause.HazardName ?? "hazard";
        var triggerName = cause.TriggerName ?? "trigger";

        // Hazards present just before the failure.
        var hazards = new List<(Actor Actor, Vector2 Pos)>();
        foreach (var a in r.Actors)
        {
            if (!hazardBases.Contains(a.BNpcBaseId) || a.SpawnMs > t0 || a.DespawnMs < t0 - 500)
                continue;
            if (a.Track.TrySample(t0, out var pos, out _))
                hazards.Add((a, pos));
        }

        if (hazards.Count == 0)
        {
            BlameStandingInHazard(r, inc, t0);
            return;
        }

        // Candidates: every trigger that resolved in the window (gap = how far inside contact range it was).
        var candidates = new List<(Actor Player, float Dist, float Gap, Vector2 At, Vector2 Hazard, string How, AoeInstance? Aoe)>();
        foreach (var act in r.Actions)
        {
            if (!triggerIds.Contains(act.ActionId) || act.T < t0 - windowMs || act.T > t0 + 300)
                continue;
            var holder = act.AnimationTarget ?? act.PrimaryTarget;
            if (holder is not { IsPlayer: true } || !holder.Track.TrySample(act.T, out var at, out _))
                continue;
            var nearest = hazards.MinBy(h => Vector2.Distance(h.Pos, at));
            var d = Vector2.Distance(nearest.Pos, at);
            candidates.Add((holder, d, d - cause.ContactDistance, at, nearest.Pos, triggerName, r.Aoes.FirstOrDefault(x => x.Action == act)));
        }

        if (cause.PlayerContact)
        {
            foreach (var p in r.Party)
            {
                for (var t = t0 - windowMs; t <= t0; t += 250)
                {
                    if (!ShapeValidator.IsAlive(r, p, t) || !p.Track.TrySample(t, out var at, out _))
                        continue;
                    var nearest = hazards.MinBy(h => Vector2.Distance(h.Pos, at));
                    var d = Vector2.Distance(nearest.Pos, at);
                    if (d - cause.HazardRadius < 0)
                    {
                        candidates.Add((p, d, d - cause.HazardRadius, at, nearest.Pos, "stepped into it", null));
                        break;
                    }
                }
            }
        }

        if (candidates.Count == 0)
        {
            inc.Detail += $". No {triggerName} resolved near the {hazardName}s in the {cause.WindowS:0.#}s before";
            return;
        }

        candidates.Sort((x, y) => x.Gap.CompareTo(y.Gap));
        var best = candidates[0];
        inc.Players.Add(best.Player);
        inc.Aoes.AddRange(r.Aoes.Where(x => x.Category == AoeCategory.Hazard && hazards.Any(h => h.Actor == x.Source)));
        if (best.Aoe != null)
            inc.Aoes.Add(best.Aoe);

        var wipe = r.EndMs - t0 <= 10000;
        var touched = best.Gap < 0.5f;
        var who = best.How == "stepped into it"
                      ? $"{best.Player.Name} stepped into a {hazardName}"
                      : $"{best.Player.Name}'s {triggerName} {(touched ? "touched" : "came closest to")} the {hazardName}s";
        inc.Title = $"{label}: {who}{(wipe ? " — this caused the wipe" : "")}";
        var reach = best.How == "stepped into it" ? cause.HazardRadius : cause.ContactDistance;
        var detail = new StringBuilder();
        detail.Append($"{best.Player.Name} was {best.Dist:0.0}y from the {hazardName} at ({best.Hazard.X:0.0},{best.Hazard.Y:0.0}) " +
                      $"when the {best.How} resolved; anything within {reach:0.#}y sets it off");
        if (touched)
            detail.Append($" ({-best.Gap:0.0}y too close)");
        else if (best.Gap < 1f)
            detail.Append(" — at the edge of contact range; ACT position samples are only accurate to about ±0.5y");
        detail.Append('.');
        var others = candidates.Skip(1).Where(c => c.How != "stepped into it").Select(c => $"{c.Player.Name} {c.Dist:0.0}y").ToList();
        if (others.Count > 0)
            detail.Append($" Other {triggerName}s: {string.Join(", ", others)}.");
        if (wipe)
            detail.Append($" The detonation ({inc.Detail.ToLowerInvariant()}) wiped the party.");
        inc.Detail = detail.ToString();

        // Where they should have been: the nearest point clear of every hazard by the contact distance.
        var clearRadius = reach + 1f;
        var zones = hazards.Select(h => new AoeInstance
        {
            Shape = Geometry.AoeShape.Circle(clearRadius), Origin = h.Pos, ResolveMs = t0, EndMs = t0, Category = AoeCategory.Hazard,
        }).ToList();
        if (SafeSpot.Nearest(r, zones, best.At, t0) is { } spot)
        {
            inc.Expected[best.Player] = (spot, ExpectedSource.SafeSpot,
                                         $"keep the {triggerName} {reach:0.#}y+ from the {hazardName}s");
        }
    }

    private static string BossHpText(PullReplay r, int t)
    {
        // The bosses the party was hitting (e.g. both Chaos and Exdeath, not the untargetable giant Kefka behind them).
        var present = r.Actors.Where(x => x.Kind == ActorKind.Boss && x.IsPresent(t) && x.MaxHp > 0).ToList();
        var attacked = r.Actions.Where(a => a.Source.IsPlayer && a.T <= t && a.T >= t - 30000)
                        .SelectMany(a => a.Hits).Where(h => h.Damage > 0).Select(h => h.Target).Where(present.Contains).Distinct().ToList();
        var bosses = attacked.Count > 0 ? attacked : present.OrderByDescending(x => x.MaxHp).Take(1).ToList();
        if (bosses.Count == 0)
            return string.Empty;
        var hp = string.Join(", ", bosses.OrderByDescending(b => b.MaxHp).Select(b => $"{b.DisplayName} at {100.0 * b.Hp.At(t) / b.MaxHp:0.0}%"));
        var deadDps = r.Deaths.Count(d => d.Victim.IsPlayer && d.T < t && (d.RaisedMs < 0 || d.RaisedMs > t));
        return $"DPS check failed: {hp} HP when the enrage began" +
               (deadDps > 0 ? $"; {deadDps} player(s) dead at the time" : "") +
               (DpsLoss(r, t) is { Length: > 0 } loss ? $". {loss}" : "");
    }

    internal static IEnumerable<AoeInstance> UnderSoaked(MechanicGroup g)
    {
        foreach (var a in g.Aoes)
        {
            if (a.Category != AoeCategory.Tower || a.Soakers <= 0 || a.Action == null)
                continue;
            var soakers = a.Action.Hits.Count(h => h.Target.IsPlayer);
            if (soakers < a.Soakers)
                yield return a;
        }
    }

    private static void FindMechanicMisses(PullReplay r, WipeReport report, List<MechanicGroup> groups, PositionProfile? profile)
    {
        foreach (var g in groups)
        {
            if (g.T > r.EndMs + 500)
                continue;
            var alive = r.Party.Where(p => ShapeValidator.IsAlive(r, p, g.T)).ToList();

            // Avoidable damage (incl. tankbuster cleaves on non-tanks, and someone else's spread). Players with a spread of
            // their own at this moment are judged by the spread-overlap check instead.
            var spreadOwners = g.Aoes.Where(a => a.Category == AoeCategory.Spread && a.Action != null).Select(a => SpreadOwner(r, a))
                                .Where(o => o != null).ToHashSet();
            foreach (var a in g.Aoes.Where(a => (a.Category is AoeCategory.Danger or AoeCategory.HiddenDanger or AoeCategory.Tankbuster or AoeCategory.Spread ||
                                                 (a.Category == AoeCategory.Bait && SoakDef(r, a)?.OnlyTarget == true)) && a.Action != null))
            {
                // Proximity damage reaches everyone; only a big hit means standing too close.
                var proximity = SoakDef(r, a)?.Proximity == true;
                var owner = a.Category is AoeCategory.Spread or AoeCategory.Bait ? OwnedBy(r, a) : null;
                if (a.Category == AoeCategory.Bait && owner == null)
                    continue;
                foreach (var h in a.Action!.Hits)
                {
                    // A hit a shield absorbed still counts when it left a debuff behind (e.g. Damage Down).
                    var debuff = h.Damage <= 0 && !h.InstantDeath && !h.Knockback
                                     ? r.Statuses.FirstOrDefault(s => s.Target == h.Target && !IsFriendly(s.Source) && s.StartMs >= h.T - 200 && s.StartMs <= h.T + 1000)
                                     : null;
                    if (!h.Target.IsPlayer || (h.Damage <= 0 && !h.InstantDeath && !h.Knockback && debuff == null))
                        continue;
                    if (proximity && !h.InstantDeath && (h.MaxHp <= 0 || h.Damage < h.MaxHp * 0.4f))
                        continue;
                    if (a.Category == AoeCategory.Tankbuster && (IsTank(h.Target) || h.Target == a.Action.PrimaryTarget || h.Target == a.Follow))
                        continue;
                    if (a.Category == AoeCategory.Spread && (owner == null || h.Target == owner || spreadOwners.Contains(h.Target)))
                        continue;
                    if (a.Category == AoeCategory.Bait && h.Target == owner)
                        continue;
                    var inc = new Incident
                    {
                        Kind = IncidentKind.AvoidableHit,
                        T = a.ResolveMs,
                        Title = a.Category == AoeCategory.Tankbuster ? $"{h.Target.Name} cleaved by {a.Label}" :
                                a.Category is AoeCategory.Spread or AoeCategory.Bait ? $"{h.Target.Name} hit by {owner!.Name}'s {a.Label}" : $"{h.Target.Name} hit by {a.Label}",
                        Detail = (debuff != null ? $"No damage (absorbed by a shield), but it gave {debuff.Name}" :
                                     $"{h.Damage:N0} damage" + (h.MaxHp > 0 ? $" ({100.0 * h.Damage / h.MaxHp:0}% max HP)" : "")) +
                                 (a.Category == AoeCategory.HiddenDanger ? " — the hidden (real) one" : ""),
                        Severity = 2,
                        Victim = h.Target,
                    };
                    inc.Players.Add(h.Target);
                    inc.Aoes.Add(a);

                    // Another player's AoE: whoever was off their spot is at fault.
                    if (owner != null && JudgeOwned(r, a, owner, h.Target, groups, profile) is { } fault)
                        ApplyOwnedFault(inc, fault, owner, h.Target, a);
                    report.Incidents.Add(inc);
                }
            }

            // Soaks set off before their intended time (e.g. puddles touched before the knockback that sets up the soak).
            var early = g.Aoes.Where(a => a.Category == AoeCategory.Tower && a.Action != null &&
                                          SoakDef(r, a)?.NotBeforeS is { } nb && a.ResolveMs < (nb * 1000) - 1000).ToList();
            if (early.Count > 0)
                report.Incidents.Add(EarlySoak(r, early));

            // Soaks short of players. Overlapping soaks at one spot (stacked puddles) form one incident.
            foreach (var cluster in ClusterByOrigin(UnderSoaked(g).Except(early).ToList(), 6f))
            {
                var a = cluster[0];
                var soakers = cluster.SelectMany(x => x.Action!.Hits).Where(h => h.Target.IsPlayer).Select(h => h.Target).Distinct().ToList();
                var (origin, _) = a.Placement(a.ResolveMs);
                var stacked = cluster.Count > 1 ? $" ({cluster.Count} overlapping)" : string.Empty;
                var inc = new Incident
                {
                    Kind = IncidentKind.TowerUnderSoaked,
                    T = a.ResolveMs,
                    Title = $"{a.Label}{stacked}: {soakers.Count}/{a.Soakers} players at ({origin.X:0.0},{origin.Y:0.0})",
                    Severity = 3,
                };
                inc.Aoes.AddRange(cluster);
                var maxHit = cluster.SelectMany(x => x.Action!.Hits).Where(h => h.Target.IsPlayer).Select(h => h.Damage).DefaultIfEmpty(0).Max();
                var detail = new StringBuilder();
                if (soakers.Count > 0)
                    detail.Append($"Inside: {string.Join(", ", soakers.Select(p => p.Name))} (up to {maxHit:N0} damage each). ");

                // The players who were inside did their job: the blame goes to whoever belonged in it and wasn't. Prefer the
                // players who usually stand in this soak at this moment in good pulls; otherwise the nearest free players.
                var needed = Math.Max(1, a.Soakers - soakers.Count);
                var missing = new List<(Actor P, float D, string Why)>();
                var deadMates = new List<Actor>();

                // Towers dropped on players (e.g. by Wave Cannon lines) are soaked by the dropper's role-mates who dropped
                // none (pack soakGroup "role"). A role-mate who was already dead is reported, never replaced by a bystander.
                // Towers two groups soak in a fixed rotation (pack soakOrder, e.g. AAABBBBA): the set's group members who
                // weren't in any of its towers are the ones missing.
                var rotation = SoakRotation(r, report, a);
                var roleSoak = rotation == null && SoakDef(r, a)?.SoakGroup == "role" && Dropper(r, a) is not null;
                if (rotation is var (group, set, members))
                {
                    var setSoakers = g.Aoes.Where(x => x.Category == AoeCategory.Tower && x.ActionId == a.ActionId && x.Action != null)
                                      .SelectMany(x => x.Action!.Hits).Where(h => h.Target.IsPlayer).Select(h => h.Target).ToHashSet();
                    deadMates = members.Where(p => !alive.Contains(p)).ToList();
                    needed = Math.Max(1, cluster.Sum(x => Math.Max(1, x.Soakers)) - soakers.Count);
                    missing = members.Where(p => alive.Contains(p) && !setSoakers.Contains(p))
                                     .Select(p => (p, d: Dist(p, a.ResolveMs, origin), why: $"group {group} soaks set {set}, wasn't in a tower"))
                                     .OrderBy(x => x.d).Take(needed).ToList();

                    // Nobody from the group was absent: one of them doubled up in the set's other tower instead.
                    if (missing.Count < needed)
                    {
                        var doubled = g.Aoes.Where(x => x.Category == AoeCategory.Tower && x.ActionId == a.ActionId && x.Action != null && !cluster.Contains(x))
                                       .Select(x => x.Action!.Hits.Where(h => h.Target.IsPlayer).Select(h => h.Target).Distinct().ToList())
                                       .Where(s => s.Count > Math.Max(1, a.Soakers)).SelectMany(s => s)
                                       .Where(p => members.Contains(p) && missing.All(m => m.P != p));
                        missing.AddRange(doubled.Select(p => (p, d: Dist(p, a.ResolveMs, origin), why: $"group {group} soaks set {set}, doubled up in the other tower"))
                                                .OrderBy(x => x.d).Take(needed - missing.Count));
                    }
                }
                else if (roleSoak)
                {
                    var dropper = Dropper(r, a)!;
                    var support = StackPositions.IsSupport(dropper);
                    var towers = g.Aoes.Where(x => x.Category == AoeCategory.Tower && x.ActionId == a.ActionId).ToList();
                    var droppers = towers.Select(x => Dropper(r, x)).Where(p => p != null).ToHashSet();

                    // Soakers of another tower that needed them are busy; a tower with extra soakers had one to spare.
                    var busy = towers.Where(x => !cluster.Contains(x) && x.Action != null)
                                     .Select(x => x.Action!.Hits.Where(h => h.Target.IsPlayer).Select(h => h.Target).Distinct().ToList())
                                     .Where(s => s.Count <= Math.Max(1, a.Soakers)).SelectMany(s => s).ToHashSet();
                    var mates = r.Party.Where(p => !droppers.Contains(p) && StackPositions.IsSupport(p) == support).ToList();
                    deadMates = mates.Where(p => !alive.Contains(p)).ToList();
                    needed = Math.Max(1, cluster.Sum(x => Math.Max(1, x.Soakers)) - soakers.Count);
                    missing = mates.Where(p => alive.Contains(p) && !busy.Contains(p) && !soakers.Contains(p))
                                   .Select(p => (p, d: Dist(p, a.ResolveMs, origin), why: $"{(support ? "supports" : "DPS")} soak {dropper.Name}'s"))
                                   .OrderBy(x => x.d).Take(needed).ToList();
                }
                else if (profile != null)
                {
                    foreach (var p in alive.Where(p => !soakers.Contains(p)))
                    {
                        var learned = profile.Query(g.Phase, g.PhaseSecond, g.Variant, p.Name, p.Job, r.Summary.Key);
                        if (learned is { } l && Vector2.Distance(l.Pos, origin) <= a.Shape.Radius + 2.5f)
                            missing.Add((p, Dist(p, a.ResolveMs, origin), $"usually soaks it, {l.Count} good pulls"));
                    }

                    missing = missing.OrderBy(x => x.D).Take(needed).ToList();
                }

                if (missing.Count == 0 && !roleSoak && rotation == null)
                {
                    var busy = BusySoakers(g);
                    missing = alive.Where(p => !busy.Contains(p) && !soakers.Contains(p)).Select(p => (p, d: Dist(p, a.ResolveMs, origin), why: "nearest free player"))
                                   .Where(x => x.d >= 0).OrderBy(x => x.d).Take(needed).ToList();
                }

                if (deadMates.Count > 0)
                {
                    var how = deadMates.Select(p => r.Deaths.LastOrDefault(d => d.Victim == p && d.T <= a.ResolveMs) is { } d
                                                        ? $"{p.Name} ({(d.Cause.StartsWith("Fell", StringComparison.Ordinal) ? "fell off" : "died")} at {FormatMs(d.T)})"
                                                        : p.Name);
                    detail.Append($"Its soakers were already dead: {string.Join(", ", how)}. ");

                    // The tower failed because of those deaths: the root cause is traced through them.
                    inc.Causes.AddRange(DeathsLeavingDead(report, deadMates, a.ResolveMs));
                }

                if (missing.Count > 0)
                {
                    detail.Append($"Missing from it: {string.Join(", ", missing.Select(x => $"{x.P.Name} ({x.D:0.0}y away, {x.Why})"))}");
                    foreach (var (p, _, _) in missing)
                    {
                        inc.Players.Add(p);
                        inc.Expected[p] = (origin, ExpectedSource.Soak, $"needed in {(a.Label.StartsWith("the ", StringComparison.OrdinalIgnoreCase) ? "" : "the ")}{a.Label}");
                    }
                }

                inc.Detail = detail.ToString().TrimEnd();
                report.Incidents.Add(inc);
            }

            // Stacks that need a number of soakers (e.g. confetti knockbacks taken by the holder's 3 role-mates): short
            // stacks kill whoever did take them, so the blame goes to whoever was out of place: the holder, or the group
            // members who stayed out. Players inside two stacks at once take both.
            var stacks = g.Aoes.Where(a => a.Category == AoeCategory.Stack && a.Action != null).ToList();
            var shortBlamed = FindShortStacks(r, report, g, stacks, alive, profile);

            // Missed stacks: alive players not hit by any party stack of the moment. Stacks with a soaker count (taken by a
            // few players, e.g. role-group knockbacks or wind duos) are checked by FindShortStacks instead.
            stacks = stacks.Where(a => a.Soakers <= 0).ToList();
            if (stacks.Count > 0)
            {
                // Anyone involved in another part of this moment (hit by any AoE, holding/baiting one) had a different
                // job; only uninvolved players count as having missed the stack.
                var stacked = new HashSet<Actor>();
                foreach (var a in g.Aoes)
                {
                    foreach (var h in a.Action?.Hits ?? [])
                    {
                        if (h.Target.IsPlayer)
                            stacked.Add(h.Target);
                    }

                    if (a.ExcludeActor != null)
                        stacked.Add(a.ExcludeActor);
                    if (a.Follow != null)
                        stacked.Add(a.Follow);
                    var origin = a.Placement(a.ResolveMs).Origin;
                    foreach (var p in alive)
                    {
                        if (Dist(p, a.ResolveMs, origin) is >= 0 and <= 1.5f && a.Category != AoeCategory.Stack)
                            stacked.Add(p);
                    }
                }

                foreach (var p in alive.Where(p => !stacked.Contains(p) && !shortBlamed.Contains(p)))
                {
                    var nearest = stacks.Select(a => (a, d: Dist(p, a.ResolveMs, a.Placement(a.ResolveMs).Origin)))
                                        .OrderBy(x => x.d).First();
                    var inc = new Incident
                    {
                        Kind = IncidentKind.MissedStack,
                        T = nearest.a.ResolveMs,
                        Title = $"{p.Name} missed {nearest.a.Label}",
                        Detail = $"{nearest.d:0.0}y from the nearest stack",
                        Severity = 2,
                    };
                    inc.Players.Add(p);
                    inc.Aoes.Add(nearest.a);
                    report.Incidents.Add(inc);
                }
            }

            // Spread overlap: a player hit by more than one spread of the moment.
            var spreads = g.Aoes.Where(a => a.Category == AoeCategory.Spread && a.Action != null).ToList();
            if (spreads.Count > 1)
            {
                var counts = spreads.SelectMany(a => a.Action!.Hits.Where(h => h.Target.IsPlayer).Select(h => (h.Target, a)))
                                    .GroupBy(x => x.Target).Where(x => x.Count() > 1);
                foreach (var c in counts)
                {
                    var owners = c.Select(x => x.a.Follow?.Name ?? x.a.Label).ToList();
                    var inc = new Incident
                    {
                        Kind = IncidentKind.SpreadOverlap,
                        T = g.T,
                        Title = $"{c.Key.Name} clipped by {c.Count()} spreads",
                        Detail = "Overlapping: " + string.Join(", ", owners),
                        Severity = 2,
                    };
                    inc.Players.Add(c.Key);
                    inc.Aoes.AddRange(c.Select(x => x.a));
                    report.Incidents.Add(inc);
                }
            }
        }
    }

    /// <summary>
    /// Arrow-teleporter puzzles: a Confused player kills whoever they reach once their teleport chain stops, so those
    /// deaths go to whatever broke the chain (a misplaced, stacked or early-used arrow, or the Confused player never
    /// reaching an arrow). Every arrow mistake is also listed on its own; ones that killed nobody are informational —
    /// the puzzle still failed.
    /// </summary>
    private static void FindArrowPuzzle(PullReplay r, WipeReport report)
    {
        var ap = ArrowSquare.Evaluate(r);
        report.Arrows = ap;
        if (ap == null)
            return;
        BlameMisplacedPenalty(r, report, ap);
        if (ap.Solved)
            return;
        var label = ap.Def.Label;
        var lethal = new HashSet<ArrowFinding>();

        foreach (var inc in report.Incidents.Where(i => i.Kind == IncidentKind.Death && i.Death != null).ToList())
        {
            var d = inc.Death!;
            var hit = d.KillingBlow is { } kb && kb.Action.Source.IsPlayer && kb.Action.Source != d.Victim
                          ? kb
                          : r.Actions.Where(a => a.Source.IsPlayer && a.Source != d.Victim && a.T >= d.T - 4000 && a.T <= d.T + 100)
                             .SelectMany(a => a.Hits).LastOrDefault(h => h.Target == d.Victim && h.Damage > 0 && h.Damage >= h.HpBefore);
            if (hit == null)
                continue;
            var killer = hit.Action.Source;
            var route = ap.Routes.FirstOrDefault(x => x.Player == killer && hit.T >= x.StartMs - 500 && hit.T <= x.EndMs + 1500);
            if (route == null)
                continue;

            string why;
            if (route.Break != null)
            {
                why = route.Break.Text;
                lethal.Add(route.Break.Cause ?? route.Break);
                foreach (var c in route.Break.Culprits.Where(c => !inc.Players.Contains(c)))
                    inc.Players.Add(c);
                inc.VerdictHint = route.Break.Fault switch
                {
                    ArrowFault.NoArrowReached => "Confused positioning",
                    ArrowFault.SetOffEarly => "Arrows used early",
                    _ => "Arrow placement",
                };
            }
            else if (ap.Def.MaxChain > 0 && route.Used.Count < ap.Def.MaxChain && ShortChainBy(ap, route) is { } other)
            {
                // Two Confused players on one stretch: the chain ran into arrows the other had already used.
                var (late, why2) = other;
                why = why2;
                if (!inc.Players.Contains(late))
                    inc.Players.Add(late);
                inc.VerdictHint = "Confused positioning";
            }
            else
            {
                var gap = d.Victim.Track.TrySample(hit.T, out var vp, out _) ? Vector2.Distance(vp, route.EndPos) : -1;
                why = $"{killer.Name}'s chain went through {route.Used.Count} arrows as intended and ended at ({route.EndPos.X:0.0}, {route.EndPos.Y:0.0})" +
                      (gap >= 0 ? $", {gap:0.0}y from {d.Victim.Name}" : "") + " — no arrow was at fault";
                inc.VerdictHint = "Confused positioning";
            }

            if (!inc.Players.Contains(killer))
                inc.Players.Add(killer);
            inc.Title = $"{d.Victim.Name} killed by confused {killer.Name}";
            inc.Detail = $"{killer.Name} was Confused and attacked them ({hit.Damage:N0}). {why}";
        }

        // Each arrow mistake on its own, timed to when it was made, with the spot the culprit should have used.
        // A Confused player who never reached an arrow and killed someone is already explained by that death.
        var findings = ap.Findings.Where(f => f.Fault != ArrowFault.Unused)
                         .Concat(ap.Routes.Where(x => x.Break is { Cause: null }).Select(x => x.Break!))
                         .Where(f => f.Culprits.Count > 0 || f.Fault == ArrowFault.Missing)
                         .Where(f => !(f.Fault == ArrowFault.NoArrowReached && lethal.Contains(f)))
                         .ToList();
        foreach (var f in findings)
        {
            var killed = lethal.Contains(f);
            var title = f.Fault switch
            {
                ArrowFault.Stacked => "arrows dropped on top of each other",
                ArrowFault.SetOffEarly => "arrow used before the confusion",
                ArrowFault.Misplaced => "arrow out of place",
                ArrowFault.WrongArrow => "arrow on the wrong spot",
                ArrowFault.NoArrowReached => "Confused player never reached an arrow",
                _ => "arrow missing",
            };
            var inc = new Incident
            {
                Kind = IncidentKind.ArrowPuzzle,
                T = f.Expected.Count > 0 ? f.Expected.Values.Min(e => e.T) : f.T,
                Title = $"{label}: {title}{(f.Culprits.Count > 0 ? $" ({string.Join(", ", f.Culprits.Select(c => c.Name))})" : "")}",
                Detail = f.Text + (killed ? "" : " — nobody died to it, but the puzzle failed"),
                Severity = killed ? 2 : 1,
                VerdictHint = f.Fault switch
                {
                    ArrowFault.SetOffEarly => "Arrows used early",
                    ArrowFault.NoArrowReached => "Confused positioning",
                    _ => "Arrow placement",
                },
                Mechanic = label,
            };
            inc.Players.AddRange(f.Culprits);
            foreach (var (p, e) in f.Expected)
                inc.Expected[p] = (e.Pos, ExpectedSource.Assigned, e.Note);
            report.Incidents.Add(inc);
        }

        var unused = ap.Findings.FirstOrDefault(f => f.Fault == ArrowFault.Unused);
        if (unused != null)
        {
            report.Incidents.Add(new Incident
            {
                Kind = IncidentKind.ArrowPuzzle,
                T = unused.T,
                Title = $"{label} failed: {ap.Unused.Count()} of {ap.Drops.Count} arrows unused",
                Detail = unused.Text,
                Severity = 1,
                Mechanic = label,
            });
        }
    }

    /// <summary>
    /// Abilities that hit harder for each arrow left on the ground off its spot (pack <c>misplacedPenalty</c>, e.g. Indulgent
    /// Will): a death they were part of goes to whoever placed the arrows that were off their spots when it hit. The arrows
    /// get one incident per cast, timed to the first drop, and the deaths trace to it.
    /// </summary>
    private static void BlameMisplacedPenalty(PullReplay r, WipeReport report, ArrowSquareResult ap)
    {
        if (ap.Def.MisplacedPenalty.Count == 0)
            return;
        var ids = ap.Def.MisplacedPenalty.Select(h => h.Value).ToHashSet();
        var casts = new Dictionary<int, Incident>();
        foreach (var inc in report.Incidents.Where(i => i.Kind == IncidentKind.Death && i.Death?.KillingBlow != null).ToList())
        {
            var d = inc.Death!;
            var kb = d.KillingBlow!;
            var penalty = r.Actions.Where(a => ids.Contains(a.ActionId) && a.T >= kb.T - 1000 && a.T <= kb.T + 100).ToList();
            // Only when it was lethal on its own: a player it didn't kill by itself died to whatever else hit them.
            var hit = penalty.SelectMany(a => a.Hits).FirstOrDefault(h => h.Target == d.Victim && h.Damage > 0);
            if (hit == null || hit.Damage < hit.HpBefore)
                continue;
            var off = ArrowSquare.OffSpotAt(ap, hit.T).Where(a => a.Owner != null).OrderBy(a => a.T).ToList();
            if (off.Count == 0)
                continue;
            var owners = off.Select(a => a.Owner!).Distinct().ToList();
            var name = hit.Action.Name;

            var castT = penalty.Min(a => a.T);
            if (!casts.TryGetValue(castT, out var arrows))
            {
                arrows = new Incident
                {
                    Kind = IncidentKind.ArrowPuzzle,
                    T = off[0].T,
                    Title = $"{ap.Def.Label}: {(off.Count == 1 ? "1 arrow off its spot" : $"{off.Count} arrows off their spots")} made {name} hit harder " +
                            $"({string.Join(", ", owners.Select(o => o.Name))})",
                    Detail = $"{name} hits harder for each arrow left on the ground off its spot. When it hit at {FormatMs(castT)}: " +
                             string.Join("; ", off.Select(a => ArrowSquare.Describe(ap, a))),
                    Severity = 2,
                    VerdictHint = "Arrow placement",
                    Mechanic = ap.Def.Label,
                };
                arrows.Players.AddRange(owners);
                foreach (var a in off.Where(a => a.Meant != null))
                    arrows.Expected.TryAdd(a.Owner!, (a.Meant!.Pos, ExpectedSource.Assigned, $"{a.Meant.Name} in the arrow layout"));
                casts[castT] = arrows;
                report.Incidents.Add(arrows);
            }

            // Killed together with another player's AoE: whoever was at fault for that stays, and the arrows' owners join.
            if (inc.Victim == null)
            {
                inc.Victim = d.Victim;
                inc.Players.Clear();
            }

            inc.Players.AddRange(owners.Where(o => !inc.Players.Contains(o)));
            inc.Title = $"{d.Victim.Name} died to {name}";
            inc.Detail += $". {name} hits harder for each arrow left off its spot, and {off.Count} {(off.Count == 1 ? "was" : "were")} when it hit " +
                          $"({string.Join(", ", off.Select(a => $"{a.Owner!.Name}'s"))})";
            inc.VerdictHint = "Arrow placement";
            inc.Causes.Add(arrows);
        }
    }

    /// <summary>
    /// Abilities that go to the farthest (or closest) player from the caster, e.g. a boss jump: when one lands on the party,
    /// say who it went to and why — the intended baiter dead, or nobody far enough out.
    /// </summary>
    private static void FindBaitLandings(PullReplay r, WipeReport report)
    {
        foreach (var a in r.Aoes.Where(a => a.Action != null && a.Cast != null))
        {
            var def = SoakDef(r, a);
            if (def?.Bait is not { } rule)
                continue;
            var heavy = a.Action!.Hits.Where(h => h.Target.IsPlayer && (h.InstantDeath || h.MaxHp > 0 && h.Damage >= h.MaxHp * 0.4f)).ToList();
            if (heavy.Count < 2)
                continue;

            var t0 = a.Cast!.StartMs;
            var caster = a.Source;
            if (caster == null || !caster.Track.TrySample(t0, out var from, out _))
                continue;
            var alive = r.Party.Where(p => ShapeValidator.IsAlive(r, p, t0))
                         .Select(p => (p, d: p.Track.TrySample(t0, out var pp, out _) ? Vector2.Distance(pp, from) : -1f))
                         .Where(x => x.d >= 0).OrderByDescending(x => x.d).ToList();
            if (alive.Count == 0)
                continue;
            var baiter = rule == "farthest" ? alive[0] : alive[^1];
            var second = alive.Count > 1 ? (rule == "farthest" ? alive[1] : alive[^2]) : default;
            var landing = a.Placement(a.ResolveMs).Origin;
            var died = heavy.Where(h => r.Deaths.Any(d => d.Victim == h.Target && d.T >= a.ResolveMs - 200 && d.T <= a.ResolveMs + 5000)).Select(h => h.Target).ToList();
            var dead = r.Party.Where(p => !ShapeValidator.IsAlive(r, p, t0)).ToList();

            var detail = new StringBuilder();
            detail.Append($"It goes to the {rule} player from {caster.Name} when the cast starts ({FormatMs(t0)}): that was {baiter.p.Name} at {baiter.d:0.0}y");
            if (second.p != null)
                detail.Append($" (next: {second.p.Name} at {second.d:0.0}y)");
            detail.Append($". It landed at ({landing.X:0.0}, {landing.Y:0.0}) and hit {heavy.Count} players hard " +
                          $"(up to {heavy.Max(h => h.Damage):N0})");
            if (rule == "farthest")
                detail.Append(" — someone has to run far out to bait it, away from the group");
            if (dead.Count > 0)
                detail.Append($". Dead at the time: {string.Join(", ", dead.Select(p => $"{p.Name} ({Core.GameData.Jobs.Abbrev(p.Job)})"))}");

            var inc = new Incident
            {
                Kind = IncidentKind.MissedStack,
                T = a.ResolveMs,
                Title = $"{a.Label} landed on the party" + (died.Count > 0 ? $" — {died.Count} died" : ""),
                Detail = detail.ToString(),
                Severity = died.Count > 0 ? 3 : 2,
                VerdictHint = "Bait",
            };
            inc.Players.Add(baiter.p);
            inc.Players.AddRange(heavy.Select(h => h.Target).Where(p => p != baiter.p).Distinct());
            inc.Aoes.Add(a);

            // Where the baiter should have been: the arena edge straight away from the caster.
            var center = Center(r);
            var arenaR = r.Encounter?.Def.Arena?.Radius ?? 20;
            var dir = baiter.p.Track.TrySample(t0, out var bp, out _) && Vector2.DistanceSquared(bp, from) > 0.01f
                          ? Vector2.Normalize(bp - from)
                          : Vector2.Normalize(center - from);
            inc.Expected[baiter.p] = (center + (dir * (arenaR - 1.5f)), ExpectedSource.Assigned, $"far out to bait {a.Label}");
            report.Incidents.Add(inc);
        }
    }

    /// <summary>
    /// Pulses fired by cleansing a debuff (e.g. the earth crystal): two cleanses inside the vulnerability window stack two
    /// pulses and wipe the party. Names the cleanses that came too close together and what caused each.
    /// </summary>
    private static void FindCleansePulses(PullReplay r, WipeReport report)
    {
        var defs = r.Encounter?.Def.CleansePulses;
        if (defs is not { Count: > 0 })
            return;
        foreach (var cp in defs)
        {
            var ids = cp.PulseActions.Select(x => x.Value).ToHashSet();
            var pulses = r.Actions.Where(a => ids.Contains(a.ActionId) && a.Hits.Any(h => h.Target.IsPlayer)).OrderBy(a => a.T).ToList();
            if (pulses.Count < 2)
                continue;
            var windowMs = (int)(cp.WindowS * 1000);

            // Cleanses: a listed status coming off early (not running out), with what removed it.
            var cleanses = new List<(int T, Actor Who, string Status, string How)>();
            foreach (var c in cp.Cleanses)
            {
                foreach (var s in r.Statuses.Where(s => s.Target.IsPlayer && s.Name.StartsWith(c.Status, StringComparison.OrdinalIgnoreCase) &&
                                                        s.EndMs < r.EndMs && s.AppliedAt(s.EndMs - 1) is var applied &&
                                                        (applied.Duration <= 0 || s.EndMs < applied.T + (applied.Duration * 1000) - 1500)))
                {
                    string how;
                    if (c.By == "heal")
                    {
                        var heal = r.Actions.Where(a => a.T >= s.EndMs - 1500 && a.T <= s.EndMs + 200)
                                    .SelectMany(a => a.Hits.Where(h => h.Target == s.Target && h.Heal > 0).Select(h => (a, h)))
                                    .OrderBy(x => x.a.T).FirstOrDefault();
                        how = heal.a != null ? $"healed to full by {heal.a.Source.Name}'s {heal.a.Name} at {FormatMs(heal.a.T)}" : "healed to full";
                    }
                    else
                    {
                        var hit = r.Actions.Where(a => a.T >= s.EndMs - 1500 && a.T <= s.EndMs + 200)
                                   .SelectMany(a => a.Hits.Where(h => h.Target == s.Target && h.Damage > 0).Select(h => (a, h)))
                                   .OrderByDescending(x => x.h.Damage).FirstOrDefault();
                        how = hit.a != null ? $"took {hit.a.Name} ({hit.h.Damage:N0}) at {FormatMs(hit.a.T)}" : "took a lethal hit";
                    }

                    cleanses.Add((s.EndMs, s.Target, s.Name, how));
                }
            }

            cleanses.Sort((x, y) => x.T.CompareTo(y.T));

            // The first pulse that lands while an earlier one's vulnerability is still up.
            for (var i = 1; i < pulses.Count; i++)
            {
                var prev = pulses.Take(i).Last(p => p.Source != pulses[i].Source || p.T < pulses[i].T);
                if (pulses[i].T - prev.T > windowMs)
                    continue;
                var t1 = prev.T;
                var t2 = pulses[i].T;
                var involved = cleanses.Where(c => c.T >= t1 - 3000 && c.T <= t2 + 200).ToList();
                var heavy = pulses[i].Hits.Where(h => h.Target.IsPlayer && h.MaxHp > 0 && h.Damage >= h.MaxHp * 0.5f).ToList();
                var gap = (t2 - t1) / 1000f;
                var inc = new Incident
                {
                    Kind = IncidentKind.FailureAction,
                    T = t2,
                    Title = $"{cp.Label}: two pulses {gap:0.0}s apart" + (heavy.Count > 0 ? $" — {heavy.Count} hit for {heavy.Max(h => h.Damage):N0}" : ""),
                    Severity = 3,
                    VerdictHint = "Double cleanse",
                };
                var detail = new StringBuilder();
                detail.Append($"Each pulse leaves {cp.VulnStatus} for {cp.WindowS:0.#}s, so a second cleanse inside it is lethal. ");
                if (involved.Count > 0)
                {
                    detail.Append("Cleansed together: " + string.Join("; ", involved.Take(3).Select(c => $"{c.Who.Name}'s {c.Status} at {FormatMs(c.T)} ({c.How})")) + ".");
                    foreach (var c in involved.Take(3))
                        inc.Players.Add(c.Who);
                    var healer = involved.Select(c => c.How).FirstOrDefault(h => h.StartsWith("healed to full by ", StringComparison.Ordinal));
                    var healerName = healer?["healed to full by ".Length..].Split("'s")[0];
                    var healerActor = r.Party.FirstOrDefault(p => p.Name == healerName);
                    if (healerActor != null && !inc.Players.Contains(healerActor))
                        inc.Players.Insert(0, healerActor);
                }
                else
                {
                    detail.Append("No early cleanse found for the second pulse.");
                }

                if (cp.Note != null)
                    detail.Append(' ').Append(cp.Note);
                inc.Detail = detail.ToString();
                report.Incidents.Add(inc);
                break;
            }
        }
    }

    /// <summary>
    /// A chain cut short because it landed on an arrow another Confused player had already used: each Confused player
    /// should step in at a side's middle arrow and own the stretch after it, so whoever stepped in elsewhere is off.
    /// </summary>
    private static (Actor Culprit, string Why)? ShortChainBy(ArrowSquareResult ap, ConfusedRoute route)
    {
        var landed = ap.Teleporters.Where(t => t.UsedBy != null && t.UsedBy != route)
                       .MinBy(t => Vector2.Distance(t.Pos, route.EndPos));
        if (landed?.UsedBy is not { } other || Vector2.Distance(landed.Pos, route.EndPos) > ap.Def.Step * 0.75f)
            return null;
        static string? EntrySpot(ConfusedRoute x) => x.Entry?.Arrow?.Meant?.Name;
        static bool AtMiddle(ConfusedRoute x) => EntrySpot(x) is "N" or "E" or "S" or "W";
        var culprit = !AtMiddle(route) || AtMiddle(other) ? route : other;
        var who = culprit.Player;
        var where = EntrySpot(culprit) is { } s ? $"at {s}" : $"at ({culprit.Entry?.Pos.X:0.0}, {culprit.Entry?.Pos.Y:0.0})";
        return (who, $"{route.Player.Name}'s chain stopped after {route.Used.Count} of {ap.Def.MaxChain} arrows on one {other.Player.Name} had already used — " +
                     $"{who.Name} stepped onto the arrows {where} (started at ({culprit.StartPos.X:0.0}, {culprit.StartPos.Y:0.0})) instead of at a side's middle arrow");
    }

    /// <summary>Stacks with fewer soakers than they need, and players caught in two stacks at once. Returns who was blamed.</summary>
    private static HashSet<Actor> FindShortStacks(PullReplay r, WipeReport report, MechanicGroup g, List<AoeInstance> stacks, List<Actor> alive,
                                                  PositionProfile? profile)
    {
        var blamed = new HashSet<Actor>();
        var deaths = r.Deaths.Where(d => d.Victim.IsPlayer).ToList();
        bool DiedTo(Actor p, AoeInstance a) => deaths.Any(d => d.Victim == p && d.T >= a.ResolveMs - 200 && d.T <= a.ResolveMs + 3000);

        foreach (var a in stacks.Where(a => a.Soakers > 0))
        {
            var holder = a.ExcludeActor ?? a.Follow;
            var soakers = a.Action!.Hits.Where(h => h.Target.IsPlayer && h.Target != holder).Select(h => h.Target).Distinct().ToList();
            if (soakers.Count >= a.Soakers)
                continue;
            var origin = a.Placement(a.ResolveMs).Origin;
            var def = SoakDef(r, a);
            var roleStack = def?.SoakGroup == "role" && holder != null;
            var others = stacks.Where(x => x != a).SelectMany(x => x.Action!.Hits.Select(h => h.Target)).ToHashSet();
            var group = roleStack
                            ? alive.Where(p => p != holder && StackPositions.IsSupport(p) == StackPositions.IsSupport(holder!)).ToList()
                            : alive.Where(p => p != holder && !others.Contains(p) && stacks.All(x => (x.ExcludeActor ?? x.Follow) != p)).ToList();
            var missing = group.Where(p => !soakers.Contains(p)).Select(p => (p, d: Dist(p, a.ResolveMs, origin)))
                               .OrderBy(x => x.d).Take(a.Soakers - soakers.Count).ToList();

            var dead = soakers.Where(p => DiedTo(p, a)).ToList();
            var spots = StackPositions.For(r, a);
            var maxHit = a.Action.Hits.Where(h => h.Target.IsPlayer && h.Target != holder).Select(h => h.Damage).DefaultIfEmpty(0).Max();
            var inc = new Incident
            {
                Kind = IncidentKind.MissedStack,
                T = a.ResolveMs,
                Title = $"{a.Label}{(holder != null ? $" on {holder.Name}" : "")}: {soakers.Count}/{a.Soakers} soakers" +
                        (dead.Count > 0 ? $" — {string.Join(", ", dead.Select(p => p.Name))} died" : ""),
                Severity = dead.Count > 0 ? 3 : 2,
            };
            inc.Aoes.Add(a);
            var detail = new StringBuilder();
            if (soakers.Count > 0)
                detail.Append($"Taken by {string.Join(", ", soakers.Select(p => p.Name))} (up to {maxHit:N0} each). ");

            List<ShortStackFault> faults;
            if (roleStack)
            {
                // Role-mates who were alive and stayed out of it: the stack only went short because the holder took it
                // away from them or they stood out of its reach. With none, the ones alive all took it and it was short
                // because the others were already dead.
                var mates = StackPositions.IsSupport(holder!) ? "supports" : "DPS";
                faults = missing.Count > 0 ? JudgeShortStack(r, a, g, holder!, missing.Select(x => x.p).ToList(), soakers, others, spots, profile, mates) : [];
                var deadMates = r.Party.Where(p => p != holder && StackPositions.IsSupport(p) == StackPositions.IsSupport(holder!) && !alive.Contains(p)).ToList();
                if (deadMates.Count > 0)
                {
                    var how = deadMates.Select(p => r.Deaths.LastOrDefault(d => d.Victim == p && d.T <= a.ResolveMs) is { } d
                                                        ? $"{p.Name} ({(d.Cause.StartsWith("Fell", StringComparison.Ordinal) ? "fell off" : "died")} at {FormatMs(d.T)})"
                                                        : p.Name);
                    detail.Append(group.Count < a.Soakers
                                      ? $"Only {group.Count} of the other {mates} were alive to take it: {string.Join(", ", how)} already dead. "
                                      : $"Already dead: {string.Join(", ", how)}. ");

                    // Too few left to fill it: the stack failed because of those deaths, so the root cause is traced through them.
                    if (group.Count < a.Soakers)
                        inc.Causes.AddRange(DeathsLeavingDead(report, deadMates, a.ResolveMs));
                }
            }
            else
            {
                faults = missing.Select(x => new ShortStackFault(x.p, x.d, origin, ExpectedSource.Soak, $"needed in {holder?.Name ?? "the"}'s {a.Label}",
                                                                 $"{x.p.Name} ({x.d:0.0}y away)"))
                                .ToList();
                if (faults.Count > 0)
                    detail.Append($"Missing from the stack: {string.Join(", ", faults.Select(f => f.Why))}. ");
            }

            if (roleStack && faults.Count > 0)
                detail.Append(string.Join(" ", faults.Select(f => f.Why + ".")));

            inc.Detail = detail.ToString().TrimEnd();
            foreach (var f in faults)
            {
                inc.Players.Add(f.P);
                if (f.Spot is { } spot)
                    inc.Expected[f.P] = (spot, f.Source, f.Note);
                blamed.Add(f.P);
            }

            // Those who took it short died for the players who were out of place: their deaths trace back to this.
            foreach (var death in report.Incidents.Where(i => i.Kind == IncidentKind.Death && i.Death != null && dead.Contains(i.Death.Victim) &&
                                                              i.Death.T >= a.ResolveMs - 200 && i.Death.T <= a.ResolveMs + 3000))
                death.Causes.Add(inc);

            report.Incidents.Add(inc);
        }

        // Players inside two stacks at once take both (e.g. both confetti knockbacks). Only for stacks assigned to a role
        // group: others may be meant to overlap (e.g. everyone soaking all wind stacks together).
        var doubled = stacks.Where(a => SoakDef(r, a)?.SoakGroup != null)
                            .SelectMany(a => a.Action!.Hits.Where(h => h.Target.IsPlayer && h.Target != (a.ExcludeActor ?? a.Follow)).Select(h => (h.Target, a)))
                            .GroupBy(x => x.Target).Where(x => x.Select(y => y.a).Distinct().Count() > 1).ToList();
        if (doubled.Count > 0)
        {
            var pair = doubled.SelectMany(x => x.Select(y => y.a)).Distinct().ToList();
            var holders = pair.Select(a => a.ExcludeActor ?? a.Follow).Where(h => h != null).Select(h => h!).Distinct().ToList();
            var gap = pair.Count >= 2 ? Vector2.Distance(pair[0].Placement(pair[0].ResolveMs).Origin, pair[1].Placement(pair[1].ResolveMs).Origin) : 0;
            var victims = doubled.Select(x => x.Key).ToList();
            var dead = victims.Where(p => pair.Any(a => DiedTo(p, a))).ToList();
            var inc = new Incident
            {
                Kind = IncidentKind.MissedStack,
                T = pair[0].ResolveMs,
                Title = $"{pair[0].Label}: {string.Join(", ", victims.Select(p => p.Name))} inside {pair.Count} stacks at once" +
                        (dead.Count > 0 ? $" — {string.Join(", ", dead.Select(p => p.Name))} died" : ""),
                Detail = $"The stacks on {string.Join(" and ", holders.Select(h => h.Name))} were {gap:0.0}y apart; each group needs its own",
                Severity = dead.Count > 0 ? 3 : 2,
            };
            inc.Aoes.AddRange(pair);
            inc.Players.AddRange(victims);
            report.Incidents.Add(inc);
        }

        return blamed;
    }

    /// <summary>
    /// The deaths that left these players dead at <paramref name="t"/>: each one's last death before it. An earlier death
    /// they were raised from didn't take them out of the mechanic.
    /// </summary>
    private static IEnumerable<Incident> DeathsLeavingDead(WipeReport report, IEnumerable<Actor> players, int t) =>
        players.Select(p => report.Incidents.Where(i => i.Kind is IncidentKind.Death or IncidentKind.FellOff && i.Death?.Victim == p && i.T <= t)
                                     .MaxBy(i => i.T))
               .Where(i => i != null).Select(i => i!);

    /// <summary>A player at fault for a short stack, where they should have been, and why.</summary>
    private readonly record struct ShortStackFault(Actor P, float Off, Vector2? Spot, ExpectedSource Source, string Note, string Why);

    /// <summary>
    /// A role stack went short while role-mates who could have taken it stayed out of it (<paramref name="out"/>, nearest
    /// first): either the holder took it away from them or they stood out of its reach. Whoever was off their spot
    /// (assigned in the pack, else learned) is at fault. With nobody off, the side judged on its spot was where it
    /// belonged, so the other side is; with no spots at all, the one standing apart from the group is.
    /// </summary>
    private static List<ShortStackFault> JudgeShortStack(PullReplay r, AoeInstance a, MechanicGroup g, Actor holder, List<Actor> @out,
                                                         List<Actor> soakers, HashSet<Actor> inOther, StackSpots? spots, PositionProfile? profile, string mates)
    {
        var t = a.ResolveMs;
        var hasHolderPos = holder.Track.TrySample(t, out var holderPos, out _);
        float FromHolder(Actor p) => hasHolderPos && p.Track.TrySample(t, out var pos, out _) ? Vector2.Distance(pos, holderPos) : -1;
        string Reach(Actor p) => FromHolder(p) is var d and >= 0 ? $"{d:0.0}y from {holder.Name}" : "not hit";

        (bool Known, bool Off, float Dist, Vector2 Spot, ExpectedSource Source, string Note, string Basis) Judge(Actor p)
        {
            if (!p.Track.TrySample(t, out var pos, out _))
                return default;
            if (spots != null)
            {
                var spot = p == holder ? spots.HolderSpot : spots.SoakerSpot;
                var d = Vector2.Distance(pos, spot);
                return (true, d > spots.Tolerance, d, spot, ExpectedSource.Assigned, spots.Note ?? $"{(p == holder ? "holder" : "soaker")} spot for {a.Label}",
                        "their assigned spot");
            }

            if (profile?.Query(g.Phase, g.PhaseSecond, g.Variant, p.Name, p.Job, r.Summary.Key) is { Count: >= 3 } l)
            {
                var d = Vector2.Distance(pos, l.Pos);
                return (true, d > Math.Max(2.5f, l.Spread * 2.5f), d, l.Pos, ExpectedSource.Learned, $"usual spot, {l.Count} good pulls",
                        $"their usual spot ({l.Count} good pulls)");
            }

            return default;
        }

        var h = Judge(holder);
        var outs = @out.Select(p => (P: p, J: Judge(p))).ToList();
        ShortStackFault Holder(string why) => new(holder, h.Dist, h.Known ? h.Spot : null, h.Source, h.Note, $"{holder.Name} (holder) {why}");
        ShortStackFault Out(Actor p, (bool Known, bool Off, float Dist, Vector2 Spot, ExpectedSource Source, string Note, string Basis) j, string why) =>
            j.Known
                ? new(p, j.Dist, j.Spot, j.Source, j.Note, $"{p.Name} {why}")
                : new(p, 0, a.Placement(t).Origin, ExpectedSource.Soak, $"needed in {holder.Name}'s {a.Label}", $"{p.Name} {why}");

        // Off their spot, or inside the other group's stack instead of this one: at fault, largest miss first. A player on
        // their spot who was hit by the other group's stack had it brought to them by its holder.
        var faults = new List<ShortStackFault>();
        if (h.Off)
            faults.Add(Holder($"was {h.Dist:0.0}y off {h.Basis}; {string.Join(", ", @out.Select(p => $"{p.Name} ({Reach(p)})"))} {(@out.Count > 1 ? "weren't" : "wasn't")} in it"));
        foreach (var (p, j) in outs)
        {
            if (j.Off)
                faults.Add(Out(p, j, $"was {j.Dist:0.0}y off {j.Basis} and {(inOther.Contains(p) ? "in the other group's stack" : "out of the stack")} ({Reach(p)})"));
            else if (!j.Known && inOther.Contains(p))
                faults.Add(Out(p, j, $"took the other group's {a.Label} instead of {holder.Name}'s ({Reach(p)})") with { Off = FromHolder(p) });
        }

        if (faults.Count > 0)
            return faults.OrderByDescending(f => f.Off).ToList();

        // Nobody off. Both sides judged: the one farther off was out of place, unless both were close to their spots.
        if (h.Known && outs.All(x => x.J.Known))
        {
            var far = outs.MaxBy(x => x.J.Dist);
            if (Math.Max(h.Dist, far.J.Dist) >= 2f)
            {
                return h.Dist >= far.J.Dist
                           ? [Holder($"was {h.Dist:0.0}y off {h.Basis}, farther than {far.P.Name} ({far.J.Dist:0.0}y); {far.P.Name} was {Reach(far.P)} and not in it")]
                           : [Out(far.P, far.J, $"was {far.J.Dist:0.0}y off {far.J.Basis}, farther than {holder.Name} ({h.Dist:0.0}y), and out of the stack ({Reach(far.P)})")];
            }
        }
        else if (h.Known)
        {
            // Only the holder's spot is known, and they were on it: the others stayed out.
            return outs.Select(x => Out(x.P, x.J, $"stayed out of the stack ({Reach(x.P)}) while {holder.Name} was on {h.Basis}")).ToList();
        }
        else if (outs.All(x => x.J.Known))
        {
            // Only the others' spots are known, and they were on them: the holder took it away from them.
            return [Holder($"took it away from the other {mates}: {string.Join(", ", outs.Select(x => $"{x.P.Name} was on {x.J.Basis} ({Reach(x.P)})"))}")];
        }

        // Spots don't decide: the ones who took it show where the group stood. When everyone who stayed out stood with
        // them, the holder was too far from the group; otherwise those standing apart stayed out.
        var at = soakers.Count > 0 ? soakers : @out;
        var pts = at.Select(p => p.Track.TrySample(t, out var pos, out _) ? pos : (Vector2?)null).Where(p => p != null).Select(p => p!.Value).ToList();
        if (pts.Count > 0 && (soakers.Count > 0 || pts.Count >= 2))
        {
            var mid = pts.Aggregate(Vector2.Zero, (s, p) => s + p) / pts.Count;
            float Apart(Actor p) => p.Track.TrySample(t, out var pos, out _) ? Vector2.Distance(pos, mid) : float.MaxValue;
            var apart = outs.Where(x => Apart(x.P) > (soakers.Count > 0 ? 3f : 4.5f)).ToList();
            if (apart.Count == 0)
            {
                var off = hasHolderPos ? Vector2.Distance(holderPos, mid) : 0;
                return [new ShortStackFault(holder, off, mid, ExpectedSource.Soak, $"with the other {mates} for {a.Label}",
                                            $"{holder.Name} (holder) took it {off:0.0}y away from the other {mates}, who stood together; " +
                                            string.Join(", ", @out.Select(p => $"{p.Name} was {Reach(p)}")))];
            }

            return apart.Select(x => new ShortStackFault(x.P, Apart(x.P), mid, ExpectedSource.Soak, $"with the other {mates} for {a.Label}",
                                                         $"{x.P.Name} stood {Apart(x.P):0.0}y apart from the {mates} who took it ({Reach(x.P)})"))
                        .ToList();
        }

        return outs.Select(x => Out(x.P, x.J, $"stayed out of the stack ({Reach(x.P)})")).ToList();
    }

    /// <summary>Whose spread this is: the player it follows, or the player standing on its centre when it went off.</summary>
    private static Actor? SpreadOwner(PullReplay r, AoeInstance a)
    {
        if (a.Follow is { IsPlayer: true } f)
            return f;
        if (a.ExcludeActor is { IsPlayer: true } x)
            return x;
        var origin = a.Placement(a.ResolveMs).Origin;
        var nearest = r.Party.Select(p => (p, d: Dist(p, a.ResolveMs, origin))).Where(x => x.d >= 0).OrderBy(x => x.d).FirstOrDefault();
        return nearest.p != null && nearest.d <= 2f ? nearest.p : null;
    }

    /// <summary>
    /// The player an AoE was aimed at or carried by (a tether rock, a bait line, a spread), if any. Shared AoEs (stacks,
    /// towers), tankbusters and raidwides have no owner in this sense.
    /// </summary>
    private static Actor? OwnedBy(PullReplay r, AoeInstance a)
    {
        if (a.Action == null || a.Category is AoeCategory.Stack or AoeCategory.Tower or AoeCategory.Tankbuster or AoeCategory.Raidwide)
            return null;
        if (a.Category == AoeCategory.Spread)
            return SpreadOwner(r, a);
        if (a.Follow is { IsPlayer: true } f)
            return f;
        if (a.Category != AoeCategory.Bait)
            return null;
        if (a.Action.AnimationTarget is { IsPlayer: true } at)
            return at;

        // A cone or line fired at whoever stands nearest its holder (pack aim, e.g. a Spell's Trouble cone) belongs to the
        // holder: where they stood decided who it went to.
        var hit = a.Action.Hits.Where(h => h.Target.IsPlayer).Select(h => h.Target).Distinct().ToList();
        if (SoakDef(r, a)?.Aim == "nearestToHolder" && Holder(r, a, hit) is { } holder)
            return holder;

        // A line or cone fired from its caster at a player: the player hit nearest its axis is the one it was aimed at.
        if (hit.Count <= 1 || a.Shape.Type is not (ShapeType.Rect or ShapeType.Cone))
            return hit.FirstOrDefault() ?? a.Action.PrimaryTarget;
        var (o, heading) = a.Placement(a.ResolveMs);
        var dir = new Vector2(MathF.Sin(heading), MathF.Cos(heading));
        return hit.MinBy(p => p.Track.TrySample(a.ResolveMs, out var pos, out _)
                                  ? MathF.Abs((dir.X * (pos.Y - o.Y)) - (dir.Y * (pos.X - o.X)))
                                  : float.MaxValue);
    }

    /// <summary>The player an AoE fired at whoever stands nearest its holder went to: its logged target, else the hit nearest the holder.</summary>
    private static Actor? AimedAt(PullReplay r, AoeInstance a, Actor holder)
    {
        var hit = a.Action?.Hits.Where(h => h.Target.IsPlayer && h.Target != holder).Select(h => h.Target).Distinct().ToList() ?? [];
        if (a.Action?.PrimaryTarget is { } primary && hit.Contains(primary))
            return primary;
        if (!holder.Track.TrySample(a.ResolveMs, out var at, out _))
            return null;
        return hit.MinBy(p => p.Track.TrySample(a.ResolveMs, out var pos, out _) ? Vector2.Distance(pos, at) : float.MaxValue);
    }

    /// <summary>The party member standing at an AoE's origin when it resolved (not one it hit), if any.</summary>
    private static Actor? Holder(PullReplay r, AoeInstance a, List<Actor> hit)
    {
        var (origin, _) = a.Placement(a.ResolveMs);
        return r.Party.Where(p => !hit.Contains(p) && p.Track.TrySample(a.ResolveMs, out var pos, out _) && Vector2.Distance(pos, origin) <= 1.5f)
                .MinBy(p => p.Track.TrySample(a.ResolveMs, out var pos, out _) ? Vector2.Distance(pos, origin) : float.MaxValue);
    }

    /// <summary>
    /// For towers with a soak rotation (pack soakOrder): which group soaks the set this tower belongs to, the set's
    /// number, and that group's players. Group A is the first holders of the group marker plus their partners.
    /// </summary>
    private static (char Group, int Set, List<Actor> Members)? SoakRotation(PullReplay r, WipeReport report, AoeInstance tower)
    {
        if (SoakDef(r, tower)?.SoakOrder is not { Order.Length: > 0 } so)
            return null;

        // Sets: this ability's towers, split wherever more than SetGapS passes between two of them.
        var set = 0;
        int? last = null;
        foreach (var x in r.Aoes.Where(x => x.ActionId == tower.ActionId && x.Category == AoeCategory.Tower && x.Action != null)
                              .OrderBy(x => x.ResolveMs))
        {
            if (last != null && x.ResolveMs - last > so.SetGapS * 1000)
                set++;
            last = x.ResolveMs;
            if (x == tower)
                break;
        }

        if (set >= so.Order.Length)
            return null;
        var marks = r.HeadMarkers.Where(m => m.MarkerId == so.GroupMarker.Value && m.Target.IsPlayer && m.T <= tower.ResolveMs).OrderBy(m => m.T).ToList();
        if (marks.Count == 0)
            return null;
        var groupA = marks.Where(m => m.T - marks[0].T <= 1000).Select(m => m.Target).ToHashSet();
        foreach (var holder in groupA.ToList())
        {
            if (!report.Slots.TryGetValue(holder, out var slot))
                continue;
            foreach (var partner in so.Partners.Where(p => p.Contains(slot)).SelectMany(p => p))
            {
                if (report.Slots.FirstOrDefault(kv => kv.Value == partner).Key is { } p)
                    groupA.Add(p);
            }
        }

        var group = so.Order[set];
        var members = r.Party.Where(p => groupA.Contains(p) == (group == 'A')).ToList();
        return (group, set + 1, members);
    }

    /// <summary>The player standing on a tower's spot when it appeared (e.g. the target of the line that dropped it).</summary>
    private static Actor? Dropper(PullReplay r, AoeInstance tower)
    {
        var (origin, _) = tower.Placement(tower.ResolveMs);
        var at = tower.StartMs < tower.ResolveMs ? tower.StartMs : tower.ResolveMs - 3000;
        return r.Party.Select(p => (p, d: Dist(p, at, origin))).Where(x => x.d is >= 0 and <= 2.5f).OrderBy(x => x.d)
                .Select(x => x.p).FirstOrDefault();
    }

    private static void ApplyOwnedFault(Incident inc, OwnedFault fault, Actor owner, Actor victim, AoeInstance a)
    {
        inc.Victim = victim;
        inc.Players.Clear();
        inc.Players.Add(fault.Culprit);
        inc.Detail += (inc.Detail.Length > 0 ? ". " : "") + $"{owner.Name}'s {a.Label} hit {victim.Name}: {fault.Why}";
        if (fault.Spot is { } spot)
            inc.Expected[fault.Culprit] = (spot, fault.Source,
                                           fault.Source == ExpectedSource.Learned ? $"usual spot, {fault.Pulls} good pulls" : $"max melee for {a.Label}");
        inc.VerdictHint = fault.Hint;
    }

    /// <summary>Who is at fault when one player's AoE hits another, and where they should have been (if known).</summary>
    private readonly record struct OwnedFault(Actor Culprit, Vector2? Spot, float Off, int Pulls, string Why,
                                              string Hint = "Out of position", ExpectedSource Source = ExpectedSource.Learned);

    /// <summary>
    /// One player's AoE hit another: the owner is at fault if they were off their usual spot; otherwise the player hit,
    /// if they were. A player hit while on their spot means the owner brought it to them. Two players both on their spots
    /// are only hit together by design (stacks and absorbs, judged elsewhere), so that gives no verdict.
    /// </summary>
    private static OwnedFault? JudgeOwned(PullReplay r, AoeInstance a, Actor owner, Actor victim, List<MechanicGroup> groups,
                                          PositionProfile? profile, bool viaHolder = true)
    {
        // Fired at whoever stands nearest its holder (pack aim): a player it passed through on its way is judged against
        // the player it went to, like any aimed AoE; the player it went to, when they were the wrong one, against the
        // holder (below).
        var aimed = viaHolder && SoakDef(r, a)?.Aim == "nearestToHolder";
        if (aimed && AimedAt(r, a, owner) is { } target && target != victim && target != owner)
            return JudgeOwned(r, a, target, victim, groups, profile, viaHolder: false);

        var g = groups.FirstOrDefault(x => x.Aoes.Contains(a)) ?? groups.Where(x => Math.Abs(x.T - a.ResolveMs) <= 1500)
                                                                         .MinBy(x => Math.Abs(x.T - a.ResolveMs));

        // Usual spots come from the position profile; without one, only the group check below can decide.
        (bool Known, bool Off, Vector2 Spot, float Dist, int Pulls) Judge(Actor p)
        {
            if (profile == null || g == null)
                return (false, false, default, 0, 0);
            var learned = profile.Query(g.Phase, g.PhaseSecond, g.Variant, p.Name, p.Job, r.Summary.Key);
            if (learned is not { Count: >= 3 } l || !p.Track.TrySample(a.ResolveMs, out var pos, out _))
                return (false, false, default, 0, 0);
            var d = Vector2.Distance(pos, l.Pos);
            return (true, d > Math.Max(2.5f, l.Spread * 2.5f), l.Pos, d, l.Count);
        }

        var o = Judge(owner);
        var v = Judge(victim);
        OwnedFault Owner() => new(owner, o.Known ? o.Spot : null, o.Dist, o.Pulls,
                                  o.Known ? $"{owner.Name} was {o.Dist:0.0}y off their usual spot ({o.Pulls} good pulls)" : $"{owner.Name} brought it to them");
        OwnedFault Victim() => new(victim, v.Known ? v.Spot : null, v.Dist, v.Pulls,
                                   v.Known ? $"{victim.Name} was {v.Dist:0.0}y off their usual spot ({v.Pulls} good pulls)" : $"{victim.Name} walked into it");

        if (o.Known && o.Off)
            return Owner();
        if (v.Known && v.Off)
            return Victim();

        // It went to the wrong player: the holder aimed it by where they stood.
        if (aimed && Misaimed(r, a, owner, victim) is { } misaimed)
            return misaimed;

        // Both near their spots: it still only hits someone else when somebody was out of place, so the one farther off is.
        if (o.Known && v.Known)
            return Math.Max(o.Dist, v.Dist) < 2f ? null : o.Dist >= v.Dist ? Owner() : Victim();

        // Only one side has a learned spot (or neither): a player on their spot, or standing with a group (a stack spot),
        // was where they belonged, so the other side brought the AoE there.
        if (!o.Known && (v.Known || InGroup(r, victim, owner, a.ResolveMs)))
        {
            var why = v.Known ? $"{victim.Name} was on their usual spot ({v.Pulls} good pulls)" : $"{victim.Name} was standing with the group";
            return Owner() with { Why = $"{why}, so {owner.Name} brought it to them" };
        }

        if (!v.Known && o.Known)
            return Victim() with { Why = $"{owner.Name} was on their usual spot ({o.Pulls} good pulls), so {victim.Name} walked into it" };

        // Baited at max melee (e.g. Past's/Future's End): positions alone didn't decide, so whoever was farther off the
        // max-melee ring baited it incorrectly.
        if (SoakDef(r, a)?.BaitAt == "maxMelee" && MaxMeleeRing(r, a) is var (center, ring))
        {
            (float Off, float Dist, Vector2 Spot)? Ring(Actor p)
            {
                if (!p.Track.TrySample(a.ResolveMs, out var pos, out _))
                    return null;
                var d = Vector2.Distance(pos, center);
                var dir = d > 0.01f ? (pos - center) / d : new Vector2(0, -1);
                return (MathF.Abs(d - ring), d, center + (dir * ring));
            }

            if (Ring(owner) is { } ro && Ring(victim) is { } rv && Math.Max(ro.Off, rv.Off) >= 1f)
            {
                var (who, x) = ro.Off >= rv.Off ? (owner, ro) : (victim, rv);
                var side = x.Dist < ring ? "inside" : "outside";
                return new OwnedFault(who, x.Spot, x.Off, 0,
                                      $"{who.Name} baited it incorrectly: {x.Dist:0.0}y from the boss's centre, {x.Off:0.0}y {side} max melee ({ring:0.0}y)",
                                      "Incorrect baiting", ExpectedSource.Assigned);
            }
        }

        return null;
    }

    /// <summary>
    /// A cone or line fired at the player nearest its holder went to someone who was taking another player-targeted AoE
    /// (their own spread, a stack, another bait) at the same moment, so it went to the wrong player: the holder aimed it.
    /// Names who was nearest and who came next. Null when the player hit had nothing else to take.
    /// </summary>
    private static OwnedFault? Misaimed(PullReplay r, AoeInstance a, Actor holder, Actor victim)
    {
        var others = r.Aoes.Where(x => x != a && x.Action != null && x.Action != a.Action && Math.Abs(x.ResolveMs - a.ResolveMs) <= 500 &&
                                       x.Category is AoeCategory.Spread or AoeCategory.Stack or AoeCategory.Bait &&
                                       x.Action.Hits.Any(h => h.Target == victim)).ToList();
        var also = others.FirstOrDefault(x => OwnedBy(r, x) == victim) ?? others.FirstOrDefault();
        if (also == null || !holder.Track.TrySample(a.ResolveMs, out var at, out _) || !victim.Track.TrySample(a.ResolveMs, out var vp, out _))
            return null;
        var next = r.Party.Where(p => p != holder && p != victim && ShapeValidator.IsAlive(r, p, a.ResolveMs))
                    .Select(p => (Player: p, Dist: p.Track.TrySample(a.ResolveMs, out var pp, out _) ? Vector2.Distance(pp, at) : float.MaxValue))
                    .Where(x => x.Dist < float.MaxValue).OrderBy(x => x.Dist).FirstOrDefault();
        var taking = OwnedBy(r, also) == victim ? $"their own {also.Label}" : also.Label;
        var why = $"it goes to whoever stands nearest its holder, and {victim.Name} was nearest ({Vector2.Distance(vp, at):0.0}y) " +
                  $"while taking {taking}" + (next.Player != null ? $"; {next.Player.Name} was next ({next.Dist:0.0}y)" : "");
        return new OwnedFault(holder, null, 0, 0, why);
    }

    /// <summary>The boss an AoE was baited from, and its max-melee ring (hitbox + 3y).</summary>
    private static (Vector2 Center, float Radius)? MaxMeleeRing(PullReplay r, AoeInstance a)
    {
        var (origin, _) = a.Placement(a.ResolveMs);
        var boss = a.Source is { Kind: ActorKind.Boss } source
                       ? source
                       : r.Actors.Where(x => x.Kind == ActorKind.Boss && x.IsPresent(a.ResolveMs) && !x.IsHidden(a.ResolveMs))
                          .MinBy(x => x.Track.TrySample(a.ResolveMs, out var p, out _) ? Vector2.Distance(p, origin) : float.MaxValue);
        if (boss == null || !boss.Track.TrySample(a.ResolveMs, out var center, out _))
            return null;
        return (center, boss.Radius + 3f);
    }

    /// <summary>Whether a player stood with at least two others (not counting <paramref name="except"/>) within 3.5y.</summary>
    private static bool InGroup(PullReplay r, Actor p, Actor except, int t)
    {
        if (!p.Track.TrySample(t, out var pos, out _))
            return false;
        return r.Party.Count(q => q != p && q != except && ShapeValidator.IsAlive(r, q, t - 100) &&
                                  q.Track.TrySample(t, out var qp, out _) && Vector2.Distance(pos, qp) <= 3.5f) >= 2;
    }

    private static AbilityDef? SoakDef(PullReplay r, AoeInstance a) =>
        r.Encounter?.AbilityFor(a.Action?.ActionId ?? a.ActionId, a.Action?.Name);

    private static List<List<AoeInstance>> ClusterByOrigin(List<AoeInstance> aoes, float within)
    {
        var clusters = new List<List<AoeInstance>>();
        foreach (var a in aoes)
        {
            var o = a.Placement(a.ResolveMs).Origin;
            var c = clusters.FirstOrDefault(c => Vector2.Distance(c[0].Placement(c[0].ResolveMs).Origin, o) <= within);
            if (c != null)
                c.Add(a);
            else
                clusters.Add([a]);
        }

        return clusters;
    }

    /// <summary>
    /// A soak that resolved before its intended time: someone touched it early. The first player to step into it set it
    /// off; everyone inside shares the damage.
    /// </summary>
    private static Incident EarlySoak(PullReplay r, List<AoeInstance> early)
    {
        var a = early[0];
        var def = SoakDef(r, a)!;
        var t = early.Min(x => x.ResolveMs);
        var circles = early.Select(x => (Origin: x.Placement(x.ResolveMs).Origin, R: x.Shape.Radius)).ToList();
        var inside = early.SelectMany(x => x.Action!.Hits).Where(h => h.Target.IsPlayer).Select(h => h.Target).Distinct().ToList();
        bool In(Vector2 p, float pad) => circles.Any(c => Vector2.Distance(c.Origin, p) <= c.R + pad);

        // Entry time: the last moment (within 10 s) each player was still outside, plus one step.
        Actor? toucher = null;
        var firstEntry = int.MaxValue;
        foreach (var p in inside)
        {
            var entry = t;
            for (var tt = t; tt >= t - 10000; tt -= 100)
            {
                if (!p.Track.TrySample(tt, out var pos, out _) || !In(pos, 0.2f))
                    break;
                entry = tt;
            }

            if (entry < firstEntry)
            {
                firstEntry = entry;
                toucher = p;
            }
        }

        var maxHit = early.SelectMany(x => x.Action!.Hits).Where(h => h.Target.IsPlayer).Select(h => h.Damage).DefaultIfEmpty(0).Max();
        var where = circles[0].Origin;
        var inc = new Incident
        {
            Kind = IncidentKind.FailureAction,
            T = t,
            Title = $"{a.Label} set off early" + (toucher != null ? $" by {toucher.Name}" : string.Empty) +
                    (def.NotBeforeLabel != null ? $" — soak it {def.NotBeforeLabel}" : string.Empty),
            Severity = 3,
        };
        inc.Aoes.AddRange(early);
        if (toucher != null)
            inc.Players.Add(toucher);
        var detail = new StringBuilder();
        if (toucher != null)
            detail.Append($"{toucher.Name} stepped into the puddles at ({where.X:0.0},{where.Y:0.0}) at {FormatMs(firstEntry)}, which set them off. ");
        detail.Append($"{inside.Count} player(s) inside ({string.Join(", ", inside.Select(p => p.Name))}), up to {maxHit:N0} damage each; " +
                      $"intended: {a.Soakers} players {def.NotBeforeLabel ?? $"after {FormatMs((int)(def.NotBeforeS!.Value * 1000))}"}.");
        inc.Detail = detail.ToString();

        if (toucher != null && toucher.Track.TrySample(firstEntry - 100, out var before, out _))
        {
            var zones = circles.Select(c => new AoeInstance
            {
                Shape = AoeShape.Circle(c.R + 1f), Origin = c.Origin, ResolveMs = t, EndMs = t, Category = AoeCategory.Hazard,
            }).ToList();
            if (SafeSpot.Nearest(r, zones, before, t) is { } spot)
                inc.Expected[toucher] = (spot, ExpectedSource.SafeSpot, "stay out of the puddles until the soak");
        }

        return inc;
    }

    private static HashSet<Actor> BusySoakers(MechanicGroup g)
    {
        var busy = new HashSet<Actor>();
        foreach (var a in g.Aoes.Where(a => a.Category is AoeCategory.Tower or AoeCategory.Stack && a.Action != null))
        {
            foreach (var h in a.Action!.Hits)
            {
                if (h.Target.IsPlayer)
                    busy.Add(h.Target);
            }
        }

        return busy;
    }

    private static void Dedupe(WipeReport report)
    {
        // A death whose killing blow is an avoidable hit already listed: merge the hit into the death.
        var hits = report.Incidents.Where(i => i.Kind == IncidentKind.AvoidableHit).ToList();
        foreach (var death in report.Incidents.Where(i => i.Kind == IncidentKind.Death && i.Death?.KillingBlow != null).ToList())
        {
            var dup = hits.FirstOrDefault(h => (h.Victim ?? h.Players.FirstOrDefault()) == death.Death!.Victim &&
                                               h.Aoes.Any(a => a.Action == death.Death.KillingBlow!.Action));
            if (dup != null)
                report.Incidents.Remove(dup);
        }
    }

    // ---- root cause / summary ------------------------------------------------------------------------------

    /// <summary>Severe incidents closer than this are treated as one cascade.</summary>
    private const int CascadeGapMs = 20000;

    private static void PickRootCause(PullReplay r, WipeReport report)
    {
        // A clear has nothing to explain; its incidents are still listed.
        if (r.Summary.Outcome == PullOutcome.Clear)
            return;
        var enrage = report.Incidents.FirstOrDefault(i => i.Kind == IncidentKind.Enrage);

        // Damage Down resets are deliberate, but they still cost raises (8s hardcasts without Swiftcast) and can remove
        // players a mechanic needs, so they are part of the cascade — never its root.
        // Deliberate deaths that ended a lost pull are not part of what lost it.
        var severe = report.Incidents.Where(i => (i.Severity >= 3 || i.Intentional) && i.Kind != IncidentKind.Enrage && !i.DeliberateWipe).ToList();

        // Walk back from the end of the pull through the final cascade of deaths/failures. Earlier incidents that
        // were recovered from (e.g. a death that was raised long before the wipe) are not the cause.
        Incident? start = null;
        var cursor = r.EndMs;
        var cascade = new List<Incident>();
        for (var i = severe.Count - 1; i >= 0; i--)
        {
            if (cursor - severe[i].T > CascadeGapMs)
                break;
            start = severe[i];
            cursor = severe[i].T;
            cascade.Add(severe[i]);
        }

        // Follow recorded causes back (e.g. a tower that failed because its soakers were already dead).
        var traced = new HashSet<Incident>();
        while (start is { Causes.Count: > 0 } && traced.Add(start))
            start = start.Causes.MinBy(c => c.T);

        // Who an incident is about: who is at fault, and who it happened to.
        static IEnumerable<Actor> About(Incident i) => i.Victim != null ? i.Players.Append(i.Victim) : i.Players;

        Incident? trigger = null;
        if (start is { Intentional: true })
        {
            // The cascade began with a reset: what applied its Damage Down is the real mistake. That is the player's own
            // avoidable hit, or a failure that gave it out (a missed tower).
            trigger = report.Incidents.LastOrDefault(i => i.Kind == IncidentKind.AvoidableHit && About(i).Intersect(About(start)).Any() &&
                                                          Math.Abs(i.T - start.DamageDownAt) <= 1500)
                      ?? report.Incidents.LastOrDefault(i => !i.Intentional && i.Kind is IncidentKind.FailureAction or IncidentKind.TowerUnderSoaked &&
                                                             i.T <= start.DamageDownAt + 500 && start.DamageDownAt - i.T <= 3000);
        }
        else if (start != null)
        {
            // An avoidable hit / missed mechanic shortly before the cascade start, on the same player, started it. A cause
            // comes before its effect: something at the same moment is part of it, not its trigger.
            trigger = report.Incidents.LastOrDefault(i => i.Severity == 2 && i.T <= start.T - 300 && start.T - i.T <= 6000 &&
                                                          About(i).Intersect(About(start)).Any());
        }

        // An enrage is always the verdict; earlier deaths are listed as contributing (lost DPS).
        var root = enrage ?? trigger ?? start ?? report.Incidents.LastOrDefault();

        // A reset is never a mistake: if it is still the root, what gave its Damage Down wasn't found.
        if (root is { Intentional: true } && root != enrage)
            root.VerdictHint = "Unexplained reset";

        // Several resets in the final cascade: a contributing note (each one needs a raise and healer GCDs), never the
        // verdict. Resets trace to whatever applied their Damage Down.
        var resets = cascade.Where(i => i.Intentional && !i.DeliberateWipe).OrderBy(i => i.T).ToList();
        if (enrage == null && root != null && resets.Count >= 2)
        {
            var span = (resets[^1].T - resets[0].T) / 1000f;
            root.Detail += $". {resets.Count} players reset Damage Down within {span:0.#}s during the collapse " +
                           $"({string.Join("; ", resets.Select(x => $"{x.Players.FirstOrDefault()?.Name}: {x.Detail[(x.Detail.LastIndexOf('—') + 1)..].Trim()}"))})" +
                           " — each reset needs a raise (8s hardcast without Swiftcast) and healer GCDs to recover";
        }

        if (root != null)
        {
            root.IsRootCause = true;
            root.InCollapse = true;
        }

        // Before an enrage, the deaths of the collapse cost the DPS: contributing too.
        foreach (var i in cascade.Concat(traced))
            i.InCollapse = true;

        report.RootCause = root;
    }

    private static void Summarize(PullReplay r, WipeReport report)
    {
        var s = r.Summary;
        var duration = FormatMs(r.EndMs);
        var where = report.Segment != null ? $"{report.Phase} · {report.Segment}" : report.Phase.Length > 0 ? report.Phase : s.ZoneName;
        var root = report.RootCause;
        var deaths = report.Incidents.Count(i => i.Kind is IncidentKind.Death or IncidentKind.FellOff);
        report.Verdict = root?.VerdictHint switch
        {
            "Missing mitigation" => "Missing mitigation",
            "Vulnerability" => "Death with a vulnerability debuff",

            "Arrow placement" => "Arrow placement failure",
            "Arrows used early" => "Arrows used before the confusion",
            "Confused positioning" => "Confused player reached an ally",
            "Bait" => "Bait landed on the party",
            "Double cleanse" => "Two cleanses inside the vulnerability window",
            "Out of position" => "Out of position",
            "Incorrect baiting" => "Incorrect baiting",
            "Unexplained reset" => "Damage Down reset (cause not found)",
            _ => null,
        } ?? root?.Kind switch
        {
            IncidentKind.Enrage => "DPS check",
            IncidentKind.FellOff => "Fell off the arena",
            IncidentKind.TowerUnderSoaked or IncidentKind.FailureAction => "Mechanic failure",
            IncidentKind.AvoidableHit => "Avoidable damage",
            IncidentKind.MissedStack or IncidentKind.SpreadOverlap => "Stack/spread error",
            IncidentKind.Death => "Death",
            IncidentKind.MissingMitigation => "Missing mitigation",
            IncidentKind.ArrowPuzzle => "Arrow placement failure",
            _ => s.Outcome == PullOutcome.Clear ? "Clear" : "Unknown",
        };
        var context = root?.Mechanic ?? where;
        var contextText = context.Length > 0 ? $" ({context})" : string.Empty;
        report.Headline = root != null
                              ? $"{report.Verdict} at {FormatMs(root.T)}{contextText}: {root.Title}"
                              : $"{s.Outcome} at {duration} ({where})";
        var boss = s.BossHpPct >= 0 ? $", boss {s.BossHpPct:0.0}%" : "";
        report.ChatLine = $"[Raid Replay] {s.Outcome} #{s.Ordinal} {duration} ({where}){boss}, {deaths} death(s). " +
                          (root != null ? $"Root cause {FormatMs(root.T)}: {root.Title}" + (root.Detail.Length > 0 ? $" — {root.Detail}" : "") : "");
        // Positioning notes belong to the root cause only (other incidents carry their own snapshots).
        var misses = (root?.Snapshot ?? []).Where(p => p.Involved && p.MissDistance > 1.5f).OrderByDescending(p => p.MissDistance).ToList();
        foreach (var m in misses.Take(4))
            report.Notes.Add($"{m.Player.Name}: {m.MissDistance:0.0}y from {Describe(m.ExpectedSource)}{(m.ExpectedNote != null ? $" ({m.ExpectedNote})" : "")}");
    }

    private static string Describe(ExpectedSource s) => s switch
    {
        ExpectedSource.SafeSpot => "the nearest safe spot",
        ExpectedSource.Soak => "the soak position",
        ExpectedSource.Learned => "their usual position",
        ExpectedSource.Assigned => "their assigned spot",
        _ => "expected position",
    };

    // ---- snapshots & expected positions --------------------------------------------------------------------

    private static void Snapshot(PullReplay r, Incident inc, List<MechanicGroup> groups, PositionProfile? profile, Dictionary<Actor, string> slots)
    {
        var t = inc.T;
        var group = groups.Where(g => Math.Abs(g.T - t) <= 1500).OrderBy(g => Math.Abs(g.T - t)).FirstOrDefault();
        var hpT = (inc.Death?.KillingBlow?.T ?? (inc.Aoes.Count > 0 ? Math.Min(t, inc.Aoes[0].ResolveMs) : t)) - 200;
        var phaseSpan = r.Phases.LastOrDefault(p => !p.IsSegment && p.StartMs <= t);
        var qPhase = group?.Phase ?? phaseSpan?.Id ?? "pull";
        var qSecond = group?.PhaseSecond ?? (int)Math.Round((t - Math.Max(0, phaseSpan?.StartMs ?? 0)) / 1000.0);
        var qVariant = group?.Variant ?? string.Empty;
        var dangers = (group?.Aoes ?? []).Concat(inc.Aoes)
                                         .Where(a => a.Category is AoeCategory.Danger or AoeCategory.HiddenDanger or AoeCategory.Tankbuster && a.Shape.IsArea)
                                         .Distinct().ToList();
        foreach (var p in r.Party)
        {
            if (!p.Track.TrySample(t, out var pos, out var heading))
                continue;
            var hp = p.Hp.At(hpT);
            var snap = new PlayerSnapshot
            {
                Player = p, Slot = slots.TryGetValue(p, out var slot) ? slot : string.Empty, Pos = pos, Heading = heading,
                HpPct = p.MaxHp > 0 && hp >= 0 ? 100f * hp / p.MaxHp : -1,
                Alive = ShapeValidator.IsAlive(r, p, t - 1), Involved = inc.Players.Contains(p),
            };

            // 0) Incident-specific expectation (e.g. keep your rock clear of the puddles).
            if (inc.Expected.TryGetValue(p, out var forced))
            {
                snap.Expected = forced.Pos;
                snap.ExpectedSource = forced.Source;
                snap.ExpectedNote = forced.Note;
                snap.Involved = true;
            }

            // 1) Learned position for this mechanic moment.
            if (snap.Expected == null && profile != null && snap.Alive)
            {
                var learned = profile.Query(qPhase, qSecond, qVariant, p.Name, p.Job, r.Summary.Key);
                if (learned is { } l)
                {
                    snap.Expected = l.Pos;
                    snap.ExpectedSource = ExpectedSource.Learned;
                    snap.ExpectedNote = $"{l.Count} good pulls{(l.SameVariant ? ", same pattern" : "")}, ±{l.Spread:0.0}y";
                }
            }

            // 2) Soak position for an under-soaked tower / missed stack.
            if (snap.Expected == null && inc.Kind is IncidentKind.TowerUnderSoaked or IncidentKind.MissedStack && inc.Aoes.Count > 0 &&
                (snap.Involved || inc.Detail.Contains(p.Name, StringComparison.Ordinal)))
            {
                snap.Expected = inc.Aoes[0].Placement(inc.Aoes[0].ResolveMs).Origin;
                snap.ExpectedSource = ExpectedSource.Soak;
                if (inc.Kind == IncidentKind.TowerUnderSoaked)
                    snap.Involved = true;
            }

            // 3) Geometric safe spot when standing in danger.
            var mine = IsTank(p) ? dangers.Where(a => a.Category != AoeCategory.Tankbuster).ToList() : dangers;
            if (snap.Expected == null && mine.Count > 0 && InDanger(mine, pos, t))
            {
                var safe = SafeSpot.Nearest(r, mine, pos, t);
                if (safe is { } s)
                {
                    snap.Expected = s;
                    snap.ExpectedSource = ExpectedSource.SafeSpot;
                }
            }

            inc.Snapshot.Add(snap);
        }
    }

    private static bool InDanger(List<AoeInstance> dangers, Vector2 pos, int t)
    {
        foreach (var a in dangers)
        {
            var (o, h) = a.Placement(Math.Min(t, a.ResolveMs));
            if (ShapeMath.Contains(a.Shape, o, h, pos))
                return true;
        }

        return false;
    }

    private static bool IsTank(Actor a) => GameData.Jobs.Get(a.Job)?.Role == 1;

    /// <summary>A status from a player or their pet (fairy, seraph, carbuncle) is friendly; anything else is a debuff.</summary>
    private static bool IsFriendly(Actor? source) => source is { IsPlayer: true } or { Kind: ActorKind.Pet };

    private static float Dist(Actor p, int t, Vector2 to) =>
        p.Track.TrySample(t, out var pos, out _) ? Vector2.Distance(pos, to) : -1;

    internal static Vector2 Center(PullReplay r) =>
        r.Encounter?.Def.Arena is { } a ? new Vector2(a.Center[0], a.Center[1]) : new Vector2(100, 100);

    private static string? MechanicAt(PullReplay r, int t)
    {
        var m = r.Mechanics.LastOrDefault(m => m.T <= t + 500 && t - m.T <= Math.Max(15000, m.DurationMs));
        if (m != null)
            return m.Label;
        return SegmentAt(r, t);
    }

    // A segment ends with its phase: a later phase without segments of its own has none.
    private static string? SegmentAt(PullReplay r, int t) =>
        r.Phases.LastOrDefault(p => p.IsSegment && p.StartMs <= t && (p.EndMs <= p.StartMs || t <= p.EndMs))?.Name;

    public static string FormatMs(int ms) => $"{ms / 60000}:{Math.Abs(ms) / 1000 % 60:00}";
}

/// <summary>Grid search for the nearest point outside all given AoEs and inside the arena.</summary>
public static class SafeSpot
{
    public static Vector2? Nearest(PullReplay r, IReadOnlyList<AoeInstance> dangers, Vector2 from, int t, float step = 0.5f)
    {
        var arena = r.Encounter?.Def.Arena;
        var center = WipeAnalyzer.Center(r);
        var radius = arena?.Radius ?? 20f;
        var square = arena?.Shape is "square" or "rect";
        var placements = dangers.Select(a => (a, p: a.Placement(Math.Min(t, a.ResolveMs)))).ToList();
        Vector2? best = null;
        var bestD = float.MaxValue;
        for (var ring = 0f; ring <= radius * 2; ring += step)
        {
            if (ring > bestD)
                break;
            var n = Math.Max(1, (int)(2 * MathF.PI * ring / step));
            for (var k = 0; k < n; k++)
            {
                var ang = 2 * MathF.PI * k / n;
                var c = from + new Vector2(MathF.Sin(ang) * ring, MathF.Cos(ang) * ring);
                var inArena = square
                                  ? MathF.Abs(c.X - center.X) <= (arena!.Width > 0 ? arena.Width / 2 : radius) - 1 &&
                                    MathF.Abs(c.Y - center.Y) <= (arena.Height > 0 ? arena.Height / 2 : radius) - 1
                                  : Vector2.Distance(c, center) <= radius - 1;
                if (!inArena)
                    continue;
                var safe = true;
                foreach (var (a, p) in placements)
                {
                    if (ShapeMath.Contains(a.Shape, p.Origin, p.Heading, c, 0.75f))
                    {
                        safe = false;
                        break;
                    }
                }

                if (safe)
                {
                    bestD = ring;
                    best = c;
                    break;
                }
            }

            if (best != null)
                break;
        }

        return best;
    }
}
