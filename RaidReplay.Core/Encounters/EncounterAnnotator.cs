using RaidReplay.Core.Model;

namespace RaidReplay.Core.Encounters;

/// <summary>Applies phases, mechanics, head-marker and tether labels to a loaded pull.</summary>
public static class EncounterAnnotator
{
    public static void Annotate(PullReplay r, CompiledEncounter? enc, IReadOnlyList<EncounterMark> marks)
    {
        BuildPhases(r, marks);
        foreach (var m in marks)
        {
            if (m.Kind != MarkKind.Mechanic || m.Def is not MechanicDef def)
                continue;
            r.Mechanics.Add(new MechanicMarker
            {
                Id = def.Id, Label = def.Label, T = r.ToMs(m.Ticks), DurationMs = (int)(def.DurationS * 1000),
                Phase = def.Phase,
            });
        }

        r.Mechanics.Sort((a, b) => a.T.CompareTo(b.T));

        foreach (var hm in r.HeadMarkers)
        {
            if (enc != null && enc.HeadMarkers.TryGetValue(hm.MarkerId, out var def))
            {
                hm.Label = def.Label;
                hm.DurationMs = (int)(def.DurationS * 1000);
            }
        }

        AnnotateTethers(r, enc);
    }

    private static void BuildPhases(PullReplay r, IReadOnlyList<EncounterMark> marks)
    {
        var phases = marks.Where(m => m.Kind == MarkKind.Phase).OrderBy(m => m.Ticks).ToList();
        if (phases.Count == 0)
        {
            AutoPhases(r);
            return;
        }

        for (var i = 0; i < phases.Count; i++)
        {
            var start = Math.Max(r.ToMs(phases[i].Ticks), i == 0 ? Math.Min(0, r.ToMs(phases[i].Ticks)) : int.MinValue);
            r.Phases.Add(new PhaseSpan
            {
                Id = phases[i].Id, Name = phases[i].Name, StartMs = i == 0 ? Math.Min(0, start) : start,
                EndMs = i + 1 < phases.Count ? r.ToMs(phases[i + 1].Ticks) : r.EndMs,
            });
        }

        var segs = marks.Where(m => m.Kind is MarkKind.Segment or MarkKind.Phase).OrderBy(m => m.Ticks).ToList();
        for (var i = 0; i < segs.Count; i++)
        {
            if (segs[i].Kind != MarkKind.Segment)
                continue;
            r.Phases.Add(new PhaseSpan
            {
                Id = segs[i].Id, Name = segs[i].Name, StartMs = r.ToMs(segs[i].Ticks),
                EndMs = i + 1 < segs.Count ? r.ToMs(segs[i + 1].Ticks) : r.EndMs, IsSegment = true,
            });
        }
    }

    /// <summary>Without a pack: a new phase whenever a different boss-class enemy is first damaged.</summary>
    private static void AutoPhases(PullReplay r)
    {
        var firstHit = new Dictionary<(uint, string), int>();
        foreach (var a in r.Actions)
        {
            if (!a.Source.IsPlayer)
                continue;
            foreach (var h in a.Hits)
            {
                if (h.Target.Kind is ActorKind.Boss && h.Damage > 0)
                    firstHit.TryAdd((h.Target.BNpcBaseId, h.Target.Name), a.T);
            }
        }

        var ordered = firstHit.OrderBy(kv => kv.Value).ToList();
        if (ordered.Count < 2)
            return;
        for (var i = 0; i < ordered.Count; i++)
        {
            r.Phases.Add(new PhaseSpan
            {
                Id = $"p{i + 1}", Name = $"P{i + 1} {ordered[i].Key.Item2}", StartMs = i == 0 ? 0 : ordered[i].Value,
                EndMs = i + 1 < ordered.Count ? ordered[i + 1].Value : r.EndMs,
            });
        }
    }

    private static void AnnotateTethers(PullReplay r, CompiledEncounter? enc)
    {
        var tethers = r.Tethers;
        for (var i = 0; i < tethers.Count; i++)
        {
            var t = tethers[i];
            TetherDef? def = null;
            enc?.Tethers.TryGetValue(t.TetherId, out def);
            var maxMs = (int)((def?.MaxS ?? 10) * 1000);
            var end = t.StartMs + maxMs;

            // A later tether from the same source or onto the same target replaces this one.
            for (var j = i + 1; j < tethers.Count; j++)
            {
                if (tethers[j].StartMs > end)
                    break;
                if (tethers[j].Source == t.Source || tethers[j].Target == t.Target)
                {
                    end = Math.Min(end, tethers[j].StartMs);
                    break;
                }
            }

            if (def != null)
            {
                t.Label = def.Label;
                if (def.LabelsByNextAbility != null)
                {
                    var window = t.StartMs + (int)(def.ResolveWindowS * 1000);
                    foreach (var a in r.Actions)
                    {
                        if (a.T < t.StartMs)
                            continue;
                        if (a.T > window)
                            break;
                        if (a.Source != t.Source)
                            continue;
                        var label = def.LabelsByNextAbility
                                       .FirstOrDefault(kv => HexId.TryParse(kv.Key, out var id) && id == a.ActionId).Value;
                        if (label != null)
                        {
                            t.Label = label;
                            end = Math.Min(end, a.T);
                            break;
                        }
                    }
                }
            }

            if (t.Source.DespawnMs < end)
                end = t.Source.DespawnMs;
            t.EndMs = Math.Max(t.StartMs + 100, end);
        }
    }
}
