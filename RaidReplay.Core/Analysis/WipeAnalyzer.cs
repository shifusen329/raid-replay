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

        FindDeaths(r, report);
        FindFailureActions(r, report);
        FindMechanicMisses(r, report, groups);
        FindArrowPuzzle(r, report);
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
        report.Segment = r.Phases.LastOrDefault(p => p.IsSegment && p.StartMs <= endMs)?.Name;
        Summarize(r, report);
        return report;
    }

    // ---- incident detection --------------------------------------------------------------------------------

    private static void FindDeaths(PullReplay r, WipeReport report)
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
                var mits = r.Statuses.Where(s => s.Target == d.Victim && s.Active(kb.T) && s.Source is { IsPlayer: true })
                            .Select(s => s.Name).Distinct().Take(6).ToList();
                if (mits.Count > 0)
                    inc.Detail += $"; buffs: {string.Join(", ", mits)}";
                var debuffs = r.Statuses.Where(s => s.Target == d.Victim && s.Active(kb.T) && s.Source is { IsPlayer: false })
                               .Select(s => s.Name).Distinct().Take(4).ToList();
                if (debuffs.Count > 0)
                    inc.Detail += $"; debuffs: {string.Join(", ", debuffs)}";

                // A vulnerability debuff from an earlier mistake is usually what made the hit lethal.
                foreach (var vuln in r.Statuses.Where(s => s.Target == d.Victim && s.Active(kb.T) && s.Source is { IsPlayer: false } &&
                                                          s.Name.Contains("Vulnerability Up", StringComparison.OrdinalIgnoreCase)))
                {
                    var from = r.Actions.LastOrDefault(a => !a.Source.IsPlayer && Math.Abs(a.T - vuln.StartMs) <= 400 &&
                                                            a.Hits.Any(h => h.Target == d.Victim));
                    // Many mechanics apply a vulnerability by design (raidwides, soaks, baits). Only one that came from an
                    // avoidable hit is a mistake.
                    var fromAoe = from != null ? r.Aoes.FirstOrDefault(x => x.Action == from) : null;
                    var mistake = fromAoe?.Category is AoeCategory.Danger or AoeCategory.HiddenDanger ||
                                  (fromAoe?.Category == AoeCategory.Tankbuster && !IsTank(d.Victim));
                    if (!mistake)
                        break;
                    inc.Detail += $". Had {vuln.Name} from {from!.Name} at {FormatMs(from.T)} (avoidable) — that made this hit lethal";
                    inc.VerdictHint = "Vulnerability";
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

        var cause = r.Actions.LastOrDefault(a => !a.Source.IsPlayer && Math.Abs(a.T - dd.StartMs) <= 500 && a.Hits.Any(h => h.Target == d.Victim));
        inc.Intentional = true;
        inc.Severity = 1;
        inc.VerdictHint = null;
        inc.DamageDownAt = cause?.T ?? dd.StartMs;
        inc.Title = $"{d.Victim.Name} reset Damage Down: {how}";
        inc.Detail = $"Damage Down (−90% damage dealt for 2 min) from {(cause != null ? $"{cause.Name} at {FormatMs(cause.T)}" : $"{FormatMs(dd.StartMs)}")}; " +
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
            }

            report.Incidents.Add(inc);
        }

        // Enrage casts (cast start) are a clearer signal than the hit.
        foreach (var c in r.Casts)
        {
            var def = r.Encounter?.AbilityFor(c.ActionId, c.Name);
            if (def?.Category != "enrage" || report.Incidents.Any(i => i.Kind == IncidentKind.Enrage) || c.StartMs > r.EndMs)
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
        var boss = r.Actors.Where(x => x.Kind == ActorKind.Boss && x.IsPresent(t)).OrderByDescending(x => x.MaxHp).FirstOrDefault();
        if (boss is not { MaxHp: > 0 })
            return string.Empty;
        var pct = 100.0 * boss.Hp.At(t) / boss.MaxHp;
        var deadDps = r.Deaths.Count(d => d.Victim.IsPlayer && d.T < t && (d.RaisedMs < 0 || d.RaisedMs > t));
        return $"DPS check failed: {boss.DisplayName} at {pct:0.0}% HP when the enrage began" +
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

    private static void FindMechanicMisses(PullReplay r, WipeReport report, List<MechanicGroup> groups)
    {
        foreach (var g in groups)
        {
            if (g.T > r.EndMs + 500)
                continue;
            var alive = r.Party.Where(p => ShapeValidator.IsAlive(r, p, g.T)).ToList();

            // Avoidable damage (incl. tankbuster cleaves on non-tanks).
            foreach (var a in g.Aoes.Where(a => a.Category is AoeCategory.Danger or AoeCategory.HiddenDanger or AoeCategory.Tankbuster &&
                                                a.Action != null))
            {
                foreach (var h in a.Action!.Hits)
                {
                    if (!h.Target.IsPlayer || (h.Damage <= 0 && !h.InstantDeath && !h.Knockback))
                        continue;
                    if (a.Category == AoeCategory.Tankbuster && (IsTank(h.Target) || h.Target == a.Action.PrimaryTarget || h.Target == a.Follow))
                        continue;
                    var inc = new Incident
                    {
                        Kind = IncidentKind.AvoidableHit,
                        T = a.ResolveMs,
                        Title = a.Category == AoeCategory.Tankbuster ? $"{h.Target.Name} cleaved by {a.Label}" : $"{h.Target.Name} hit by {a.Label}",
                        Detail = $"{h.Damage:N0} damage" + (h.MaxHp > 0 ? $" ({100.0 * h.Damage / h.MaxHp:0}% max HP)" : "") +
                                 (a.Category == AoeCategory.HiddenDanger ? " — the hidden (real) one" : ""),
                        Severity = 2,
                    };
                    inc.Players.Add(h.Target);
                    inc.Aoes.Add(a);
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
                inc.Players.AddRange(soakers);
                var maxHit = cluster.SelectMany(x => x.Action!.Hits).Where(h => h.Target.IsPlayer).Select(h => h.Damage).DefaultIfEmpty(0).Max();
                var detail = new StringBuilder();
                if (soakers.Count > 0)
                    detail.Append($"Inside: {string.Join(", ", soakers.Select(p => p.Name))} (up to {maxHit:N0} damage each). ");
                var busy = BusySoakers(g);
                var free = alive.Where(p => !busy.Contains(p)).Select(p => (p, d: Dist(p, a.ResolveMs, origin)))
                                .Where(x => x.d >= 0).OrderBy(x => x.d).Take(Math.Max(1, a.Soakers - soakers.Count)).ToList();
                if (free.Count > 0)
                    detail.Append("Nearest free players: " + string.Join(", ", free.Select(x => $"{x.p.Name} ({x.d:0.0}y)")));
                inc.Detail = detail.ToString().TrimEnd();
                foreach (var (p, _) in free)
                    inc.Expected[p] = (origin, ExpectedSource.Soak, $"needed in the {a.Label}");
                report.Incidents.Add(inc);
            }

            // Stacks that need a number of soakers (e.g. confetti knockbacks taken by the holder's 3 role-mates): short
            // stacks kill whoever did take them, so the blame goes to the group members who stayed out. Players inside two
            // stacks at once take both.
            var stacks = g.Aoes.Where(a => a.Category == AoeCategory.Stack && a.Action != null).ToList();
            var shortBlamed = FindShortStacks(r, report, stacks, alive);

            // Missed stacks: alive players not hit by any stack of the moment.
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
        if (ap == null || ap.Solved)
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
    private static HashSet<Actor> FindShortStacks(PullReplay r, WipeReport report, List<AoeInstance> stacks, List<Actor> alive)
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
            var others = stacks.Where(x => x != a).SelectMany(x => x.Action!.Hits.Select(h => h.Target)).ToHashSet();
            var group = def?.SoakGroup == "role" && holder != null
                            ? alive.Where(p => p != holder && StackPositions.IsSupport(p) == StackPositions.IsSupport(holder)).ToList()
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
            if (missing.Count > 0)
            {
                detail.Append($"Missing from the stack: {string.Join(", ", missing.Select(x => $"{x.p.Name} ({x.d:0.0}y away)"))}" +
                              (def?.SoakGroup == "role" && holder != null ? $" — {(StackPositions.IsSupport(holder) ? "supports" : "DPS")} take {holder.Name}'s. " : ". "));
            }

            if (def?.SoakGroup == "role" && holder != null)
            {
                var deadMates = r.Party.Where(p => p != holder && StackPositions.IsSupport(p) == StackPositions.IsSupport(holder) && !alive.Contains(p)).ToList();
                if (deadMates.Count > 0)
                    detail.Append($"Already dead: {string.Join(", ", deadMates.Select(p => p.Name))}.");
            }

            inc.Detail = detail.ToString().TrimEnd();
            foreach (var (p, _) in missing)
            {
                inc.Players.Add(p);
                inc.Expected[p] = spots != null
                                      ? (spots.SoakerSpot, ExpectedSource.Assigned, spots.Note ?? $"soaker spot for {a.Label}")
                                      : (origin, ExpectedSource.Soak, $"needed in {holder?.Name ?? "the"}'s {a.Label}");
                blamed.Add(p);
            }

            inc.Players.AddRange(dead);
            report.Incidents.Add(inc);
        }

        // Players inside two stacks at once take both (e.g. both confetti knockbacks).
        var doubled = stacks.SelectMany(a => a.Action!.Hits.Where(h => h.Target.IsPlayer && h.Target != (a.ExcludeActor ?? a.Follow)).Select(h => (h.Target, a)))
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
            var dup = hits.FirstOrDefault(h => h.Players.Contains(death.Death!.Victim) &&
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
        var enrage = report.Incidents.FirstOrDefault(i => i.Kind == IncidentKind.Enrage);

        // Damage Down resets are deliberate, but they still cost raises (8s hardcasts without Swiftcast) and can remove
        // players a mechanic needs, so they are part of the cascade — never its root.
        var severe = report.Incidents.Where(i => (i.Severity >= 3 || i.Intentional) && i.Kind != IncidentKind.Enrage).ToList();

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

        Incident? trigger = null;
        if (start is { Intentional: true })
        {
            // The cascade began with a reset: the hit that applied its Damage Down is the real mistake.
            trigger = report.Incidents.LastOrDefault(i => i.Kind == IncidentKind.AvoidableHit && i.Players.Intersect(start.Players).Any() &&
                                                          Math.Abs(i.T - start.DamageDownAt) <= 1500);
        }
        else if (start != null)
        {
            // An avoidable hit / missed mechanic shortly before the cascade start, on the same player, started it.
            trigger = report.Incidents.LastOrDefault(i => i.Severity == 2 && i.T <= start.T && start.T - i.T <= 6000 &&
                                                          (i.Players.Intersect(start.Players).Any() || start.Players.Count == 0));
        }

        // An enrage is always the verdict; earlier deaths are listed as contributing (lost DPS).
        var root = enrage ?? trigger ?? start ?? report.Incidents.LastOrDefault();

        // Several resets in the final cascade: they overwhelmed recovery (raises, healer GCDs).
        var resets = cascade.Where(i => i.Intentional).OrderBy(i => i.T).ToList();
        if (enrage == null && root != null && resets.Count >= 2)
        {
            root.VerdictHint = "Resets";
            var span = (resets[^1].T - resets[0].T) / 1000f;
            root.Detail += $". {resets.Count} players reset Damage Down within {span:0.#}s during the collapse " +
                           $"({string.Join("; ", resets.Select(x => $"{x.Players.FirstOrDefault()?.Name}: {x.Detail[(x.Detail.LastIndexOf('—') + 1)..].Trim()}"))})" +
                           " — each reset needs a raise (8s hardcast without Swiftcast) and healer GCDs to recover";
        }
        else if (start is { Intentional: true } && trigger == null && root == start)
        {
            root.VerdictHint = "Resets";
        }
        if (root != null)
            root.IsRootCause = true;
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
            "Resets" => "Damage Down resets overwhelmed recovery",
            "Arrow placement" => "Arrow placement failure",
            "Arrows used early" => "Arrows used before the confusion",
            "Confused positioning" => "Confused player reached an ally",
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
        var misses = report.Incidents.SelectMany(i => i.Snapshot).Where(p => p.Involved && p.MissDistance > 1.5f).ToList();
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

    private static float Dist(Actor p, int t, Vector2 to) =>
        p.Track.TrySample(t, out var pos, out _) ? Vector2.Distance(pos, to) : -1;

    internal static Vector2 Center(PullReplay r) =>
        r.Encounter?.Def.Arena is { } a ? new Vector2(a.Center[0], a.Center[1]) : new Vector2(100, 100);

    private static string? MechanicAt(PullReplay r, int t)
    {
        var m = r.Mechanics.LastOrDefault(m => m.T <= t + 500 && t - m.T <= Math.Max(15000, m.DurationMs));
        if (m != null)
            return m.Label;
        return r.Phases.LastOrDefault(p => p.IsSegment && p.StartMs <= t)?.Name;
    }

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
