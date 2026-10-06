using System.Numerics;
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

    public static WipeReport Analyze(PullReplay r, PositionProfile? profile = null)
    {
        var report = new WipeReport { Pull = r, LearnedPulls = profile?.PullCount ?? 0 };
        var groups = PositionProfile.Groups(r);
        var endMs = r.EndMs;

        FindDeaths(r, report);
        FindFailureActions(r, report);
        FindMechanicMisses(r, report, groups);

        report.Incidents.Sort((a, b) => a.T != b.T ? a.T.CompareTo(b.T) : b.Severity.CompareTo(a.Severity));
        Dedupe(report);

        foreach (var inc in report.Incidents)
        {
            inc.Mechanic ??= MechanicAt(r, inc.T);
            Snapshot(r, inc, groups, profile);
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
                var hpPct = kb.MaxHp > 0 ? 100.0 * kb.HpBefore / kb.MaxHp : -1;
                var overkill = kb.Damage - kb.HpBefore;
                inc.Detail = $"Killed by {kb.Action.Name} ({kb.Damage:N0} dmg, {hpPct:0}% HP before" +
                             (overkill > 0 ? $", overkill {overkill:N0}" : "") + ")";
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

            report.Incidents.Add(inc);
        }
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

                // Who was standing in a hazard (e.g. a puddle that detonates on contact) just before?
                var hazards = r.Aoes.Where(x => x.Category == AoeCategory.Hazard && x.StartMs <= a.T && x.EndMs >= a.T - 1500).ToList();
                var culprits = new List<string>();
                foreach (var p in r.Party)
                {
                    foreach (var hz in hazards)
                    {
                        var stepped = false;
                        for (var t = a.T - 1500; t <= a.T; t += 250)
                        {
                            if (t < hz.StartMs || !p.Track.TrySample(t, out var pos, out _))
                                continue;
                            var (o, h) = hz.Placement(t);
                            if (ShapeMath.Contains(hz.Shape, o, h, pos))
                            {
                                stepped = true;
                                break;
                            }
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

                // Hazards dropped on top of each other (e.g. puddles placed together) — name who dropped them.
                if (culprits.Count == 0)
                {
                    var overlapping = hazards.Where(h1 => hazards.Any(h2 => h2 != h1 && h2.Label == h1.Label &&
                                                                            Vector2.Distance(h1.Origin, h2.Origin) < Math.Max(h1.Shape.Radius, 1))).ToList();
                    if (overlapping.Count > 1)
                    {
                        var droppers = new List<string>();
                        foreach (var hz in overlapping)
                        {
                            var dropper = r.Party.Select(p => (p, d: Dist(p, hz.StartMs, hz.Origin))).Where(x => x.d >= 0)
                                           .OrderBy(x => x.d).FirstOrDefault();
                            if (dropper.p != null && !inc.Players.Contains(dropper.p))
                            {
                                inc.Players.Add(dropper.p);
                                droppers.Add(dropper.p.Name);
                            }

                            inc.Aoes.Add(hz);
                        }

                        var c = overlapping[0].Origin;
                        inc.Detail += (inc.Detail.Length > 0 ? ". " : "") +
                                      $"{overlapping.Count} {overlapping[0].Label}s were dropped overlapping near ({c.X:0.0},{c.Y:0.0})" +
                                      (droppers.Count > 0 ? $" by {string.Join(", ", droppers)}" : "");
                    }
                }
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

    private static string BossHpText(PullReplay r, int t)
    {
        var boss = r.Actors.Where(x => x.Kind == ActorKind.Boss && x.IsPresent(t)).OrderByDescending(x => x.MaxHp).FirstOrDefault();
        if (boss is not { MaxHp: > 0 })
            return string.Empty;
        var pct = 100.0 * boss.Hp.At(t) / boss.MaxHp;
        var deadDps = r.Deaths.Count(d => d.Victim.IsPlayer && d.T < t && (d.RaisedMs < 0 || d.RaisedMs > t));
        return $"DPS check failed: {boss.DisplayName} at {pct:0.0}% HP when the enrage began" +
               (deadDps > 0 ? $"; {deadDps} player(s) dead at the time" : "");
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

            // Towers short of soakers.
            foreach (var a in UnderSoaked(g))
            {
                var soakers = a.Action!.Hits.Where(h => h.Target.IsPlayer).Select(h => h.Target).ToList();
                var (origin, _) = a.Placement(a.ResolveMs);
                var inc = new Incident
                {
                    Kind = IncidentKind.TowerUnderSoaked,
                    T = a.ResolveMs,
                    Title = $"{a.Label}: {soakers.Count}/{a.Soakers} soakers at ({origin.X:0.0},{origin.Y:0.0})",
                    Severity = 3,
                };
                inc.Aoes.Add(a);
                inc.Players.AddRange(soakers);
                var busy = BusySoakers(g);
                var free = alive.Where(p => !busy.Contains(p)).Select(p => (p, d: Dist(p, a.ResolveMs, origin)))
                                .Where(x => x.d >= 0).OrderBy(x => x.d).Take(a.Soakers - soakers.Count + 1).ToList();
                if (free.Count > 0)
                    inc.Detail = "Nearest free players: " + string.Join(", ", free.Select(x => $"{x.p.Name} ({x.d:0.0}y)"));
                report.Incidents.Add(inc);
            }

            // Missed stacks: alive players not hit by any stack of the moment.
            var stacks = g.Aoes.Where(a => a.Category == AoeCategory.Stack && a.Action != null).ToList();
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

                foreach (var p in alive.Where(p => !stacked.Contains(p)))
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
        var severe = report.Incidents.Where(i => i.Severity >= 3 && i.Kind != IncidentKind.Enrage).ToList();

        // Walk back from the end of the pull through the final cascade of deaths/failures. Earlier incidents that
        // were recovered from (e.g. a death that was raised long before the wipe) are not the cause.
        Incident? start = null;
        var cursor = r.EndMs;
        for (var i = severe.Count - 1; i >= 0; i--)
        {
            if (cursor - severe[i].T > CascadeGapMs)
                break;
            start = severe[i];
            cursor = severe[i].T;
        }

        // An avoidable hit / missed mechanic shortly before the cascade start, on the same player, started it.
        Incident? trigger = null;
        if (start != null)
        {
            trigger = report.Incidents.LastOrDefault(i => i.Severity == 2 && i.T <= start.T && start.T - i.T <= 6000 &&
                                                          (i.Players.Intersect(start.Players).Any() || start.Players.Count == 0));
        }

        // An enrage is always the verdict; earlier deaths are listed as contributing (lost DPS).
        var root = enrage ?? trigger ?? start ?? report.Incidents.LastOrDefault();
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
        report.Verdict = root?.Kind switch
        {
            IncidentKind.Enrage => "DPS check",
            IncidentKind.FellOff => "Fell off the arena",
            IncidentKind.TowerUnderSoaked or IncidentKind.FailureAction => "Mechanic failure",
            IncidentKind.AvoidableHit => "Avoidable damage",
            IncidentKind.MissedStack or IncidentKind.SpreadOverlap => "Stack/spread error",
            IncidentKind.Death => "Death",
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
        _ => "expected position",
    };

    // ---- snapshots & expected positions --------------------------------------------------------------------

    private static void Snapshot(PullReplay r, Incident inc, List<MechanicGroup> groups, PositionProfile? profile)
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
                Player = p, Pos = pos, Heading = heading, HpPct = p.MaxHp > 0 && hp >= 0 ? 100f * hp / p.MaxHp : -1,
                Alive = ShapeValidator.IsAlive(r, p, t - 1), Involved = inc.Players.Contains(p),
            };

            // 1) Learned position for this mechanic moment.
            if (profile != null && snap.Alive)
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
