using RaidReplay.Core.Indexing;
using RaidReplay.Core.Model;
using RaidReplay.Core.Parsing;

namespace RaidReplay.Core.Encounters;

public enum TriggerKind : byte
{
    CastStart,
    Ability,
    Spawn,
    Despawn,
    Director,
    MapEffect,
    HeadMarker,
    StatusGain,
    StatusLose,
    Tether,
    NameToggle,
    Hp,
    ActorControl,
}

/// <summary>A normalized, encounter-relevant occurrence derived from one log line.</summary>
public struct TriggerEvent
{
    public TriggerKind Kind;
    public long Ticks;
    public uint Id;
    public string? Name;
    public uint ActorId;
    public uint ActorBase;
    public uint TargetId;
    public uint P1, P2, P3, P4;
    public bool Flag;
    public float Pct;
    public bool IsEobj;
}

/// <summary>Converts log lines to trigger events (shared by the indexer and the pull loader).</summary>
public sealed class TriggerEventSource
{
    public static readonly int[] Types =
    [
        LineType.StartsCasting, LineType.Ability, LineType.AoeAbility, LineType.AddCombatant, LineType.RemoveCombatant,
        LineType.CombatantMemory, LineType.Director, LineType.MapEffect, LineType.HeadMarker, LineType.StatusAdd,
        LineType.StatusRemove, LineType.Tether, LineType.NameToggle, LineType.UpdateHp, LineType.ActorControlExtra,
    ];

    private readonly HashSet<uint> spawned = [];
    private uint lastSeq;
    private uint lastSeqSource;

    /// <summary>Returns true and fills <paramref name="e"/> if the line yields an event. Call before the world tracker applies a Remove.</summary>
    public bool TryConvert(int type, scoped in LineFields f, long ticks, WorldStateTracker world, out TriggerEvent e)
    {
        e = new TriggerEvent { Ticks = ticks };
        switch (type)
        {
            case LineType.StartsCasting:
                e.Kind = TriggerKind.CastStart;
                e.Id = f.Hex(F20.ActionId);
                e.Name = f.Str(F20.ActionName, world.Strings);
                e.ActorId = f.Hex(F20.SourceId);
                e.ActorBase = BaseOf(world, e.ActorId);
                e.TargetId = f.Hex(F20.TargetId);
                return true;
            case LineType.Ability:
            case LineType.AoeAbility:
            {
                var src = f.Hex(F21.SourceId);
                var seq = f.Hex(F21.Sequence);
                if (type == LineType.AoeAbility && seq == lastSeq && src == lastSeqSource)
                    return false;
                lastSeq = seq;
                lastSeqSource = src;
                e.Kind = TriggerKind.Ability;
                e.Id = f.Hex(F21.ActionId);
                e.Name = f.Str(F21.ActionName, world.Strings);
                e.ActorId = src;
                e.ActorBase = BaseOf(world, src);
                e.TargetId = f.Hex(F21.TargetId);
                return true;
            }
            case LineType.AddCombatant:
            {
                var id = f.Hex(F03.Id);
                if ((id >> 28) != 4 || !spawned.Add(id))
                    return false;
                e.Kind = TriggerKind.Spawn;
                e.ActorId = id;
                e.ActorBase = (uint)f.Long(F03.NpcBaseId);
                e.Name = f.Str(F03.Name, world.Strings);
                return true;
            }
            case LineType.RemoveCombatant:
            {
                var id = f.Hex(F03.Id);
                if (!spawned.Remove(id))
                    return false;
                e.Kind = TriggerKind.Despawn;
                e.ActorId = id;
                e.ActorBase = (uint)f.Long(F03.NpcBaseId);
                e.Name = f.Str(F03.Name, world.Strings);
                return true;
            }
            case LineType.CombatantMemory:
            {
                var op = f[F261.Op];
                var id = f.Hex(F261.Id);
                if ((id >> 28) != 4)
                    return false;
                if (op.SequenceEqual("Add"u8))
                {
                    if (!spawned.Add(id))
                        return false;
                    var mem = new MemoryFields();
                    mem.Read(f, world.Strings);
                    e.Kind = TriggerKind.Spawn;
                    e.ActorId = id;
                    e.ActorBase = mem.BNpcId ?? 0;
                    e.Name = mem.Name;
                    e.IsEobj = mem.Type == 7;
                    return true;
                }

                if (op.SequenceEqual("Remove"u8) && spawned.Remove(id))
                {
                    e.Kind = TriggerKind.Despawn;
                    e.ActorId = id;
                    e.ActorBase = BaseOf(world, id);
                    return true;
                }

                return false;
            }
            case LineType.Director:
                e.Kind = TriggerKind.Director;
                e.Id = f.Hex(F33.Command);
                e.P1 = f.Hex(F33.P1);
                e.P2 = f.Hex(F33.P2);
                e.P3 = f.Hex(F33.P3);
                e.P4 = f.Hex(F33.P4);
                return true;
            case LineType.MapEffect:
                e.Kind = TriggerKind.MapEffect;
                e.Id = f.Hex(F257.Flags);
                e.P1 = f.Hex(F257.Location);
                return true;
            case LineType.HeadMarker:
                e.Kind = TriggerKind.HeadMarker;
                e.Id = f.Hex(F27.MarkerId);
                e.TargetId = f.Hex(F27.TargetId);
                e.ActorBase = BaseOf(world, e.TargetId);
                return true;
            case LineType.StatusAdd:
            case LineType.StatusRemove:
                e.Kind = type == LineType.StatusAdd ? TriggerKind.StatusGain : TriggerKind.StatusLose;
                e.Id = f.Hex(F26.StatusId);
                e.Name = f.Str(F26.StatusName, world.Strings);
                e.ActorId = f.Hex(F26.SourceId);
                e.ActorBase = BaseOf(world, e.ActorId);
                e.TargetId = f.Hex(F26.TargetId);
                return true;
            case LineType.Tether:
                e.Kind = TriggerKind.Tether;
                e.Id = f.Hex(F35.TetherId);
                e.ActorId = f.Hex(F35.SourceId);
                e.ActorBase = BaseOf(world, e.ActorId);
                e.TargetId = f.Hex(F35.TargetId);
                return true;
            case LineType.NameToggle:
                e.Kind = TriggerKind.NameToggle;
                e.ActorId = f.Hex(F34.Id);
                e.ActorBase = BaseOf(world, e.ActorId);
                e.Flag = f.Hex(F34.Toggle) != 0;
                return true;
            case LineType.UpdateHp:
            {
                var id = f.Hex(F39.Id);
                if ((id >> 28) != 4)
                    return false;
                var max = f.Long(F39.MaxHp);
                if (max <= 0)
                    return false;
                e.Kind = TriggerKind.Hp;
                e.ActorId = id;
                e.ActorBase = BaseOf(world, id);
                e.Pct = (float)(100.0 * f.Long(F39.Hp) / max);
                return true;
            }
            case LineType.ActorControlExtra:
                e.Kind = TriggerKind.ActorControl;
                e.ActorId = f.Hex(F273.Id);
                e.ActorBase = BaseOf(world, e.ActorId);
                e.IsEobj = world.Combatants.TryGetValue(e.ActorId, out var c) && c.ObjectType == 7;
                e.Id = f.Hex(F273.Category);
                e.P1 = f.Hex(F273.P1);
                e.P2 = f.Hex(F273.P2);
                e.P3 = f.Hex(F273.P3);
                e.P4 = f.Hex(F273.P4);
                return true;
        }

        return false;
    }

    private static uint BaseOf(WorldStateTracker world, uint id) =>
        world.Combatants.TryGetValue(id, out var c) ? c.BNpcBaseId : 0;
}

public enum MarkKind : byte
{
    Phase,
    Segment,
    Mechanic,
    Draw,
    ProgPoint,
}

public sealed class EncounterMark
{
    public MarkKind Kind { get; init; }
    public required string Id { get; init; }
    public required string Name { get; init; }
    public long Ticks { get; init; }
    public TriggerEvent Event { get; init; }
    public object? Def { get; init; }
}

/// <summary>Evaluates an encounter's phase/segment/mechanic/draw triggers over a stream of events for one pull.</summary>
public sealed class EncounterRun
{
    private readonly CompiledEncounter enc;
    private readonly Dictionary<TriggerDef, int> counts = new(ReferenceEqualityComparer.Instance);
    private int phase = -1;
    private int segment = -1;
    private int progPoint = -1;

    public EncounterRun(CompiledEncounter enc, long pullStartTicks)
    {
        this.enc = enc;
        var phases = enc.Def.Phases;
        if (phases.Count > 0 && (phases[0].Start == null || phases[0].Start!.PullStart == true))
            EnterPhase(0, pullStartTicks, default);
        foreach (var m in enc.Def.Mechanics)
        {
            if (m.Trigger.PullStart == true)
                Marks.Add(new EncounterMark { Kind = MarkKind.Mechanic, Id = m.Id, Name = m.Label, Ticks = pullStartTicks, Def = m });
        }
    }

    public CompiledEncounter Encounter => enc;
    public List<EncounterMark> Marks { get; } = [];
    public string? CurrentPhase => phase >= 0 ? enc.Def.Phases[phase].Id : null;

    public void Feed(in TriggerEvent e)
    {
        var phases = enc.Def.Phases;
        for (var q = phase + 1; q < phases.Count; q++)
        {
            var start = phases[q].Start;
            if (start != null && Fires(start, e))
            {
                EnterPhase(q, e.Ticks + Delay(start), e);
                break;
            }
        }

        if (phase >= 0)
        {
            var segs = phases[phase].Segments;
            for (var s = segment + 1; s < segs.Count; s++)
            {
                if (Fires(segs[s].Start, e))
                {
                    segment = s;
                    Marks.Add(new EncounterMark
                    {
                        Kind = MarkKind.Segment, Id = segs[s].Id, Name = segs[s].Name,
                        Ticks = e.Ticks + Delay(segs[s].Start), Event = e, Def = segs[s],
                    });
                    break;
                }
            }
        }

        if (phase >= 0)
            FeedProgPoints(phases[phase].ProgPoints, e);

        foreach (var m in enc.Def.Mechanics)
        {
            // A mechanic tied to a phase only fires in it (the same ability can be reused later, e.g. P1's gaze setup in P3).
            if (m.Phase != null && (phase < 0 || !string.Equals(phases[phase].Id, m.Phase, StringComparison.Ordinal)))
                continue;
            if (m.Trigger.PullStart != true && Fires(m.Trigger, e))
            {
                Marks.Add(new EncounterMark
                {
                    Kind = MarkKind.Mechanic, Id = m.Id, Name = m.Label, Ticks = e.Ticks + Delay(m.Trigger), Event = e, Def = m,
                });
            }
        }

        foreach (var d in enc.Def.Triggers)
        {
            if (Fires(d.On, e))
            {
                Marks.Add(new EncounterMark
                {
                    Kind = MarkKind.Draw, Id = d.Id, Name = d.Label ?? d.Id, Ticks = e.Ticks + Delay(d.On), Event = e, Def = d,
                });
            }
        }
    }

    private void EnterPhase(int q, long ticks, in TriggerEvent e)
    {
        phase = q;
        segment = -1;
        progPoint = -1;
        var p = enc.Def.Phases[q];
        Marks.Add(new EncounterMark { Kind = MarkKind.Phase, Id = p.Id, Name = p.Name, Ticks = ticks, Event = e, Def = p });
        if (p.ProgPoints is [{ Start: null } first, ..])
        {
            progPoint = 0;
            Marks.Add(new EncounterMark { Kind = MarkKind.ProgPoint, Id = first.Id, Name = first.Name, Ticks = ticks, Event = e, Def = first });
        }
    }

    /// <summary>
    /// Moves to the first later prog point whose start fires. Every later prog point sees the event, so occurrences count
    /// even when an earlier one fires on it (e.g. the 2nd and 3rd Mystery Magic starting two different prog points).
    /// </summary>
    private void FeedProgPoints(List<ProgPointDef> pps, in TriggerEvent e)
    {
        var next = -1;
        for (var i = progPoint + 1; i < pps.Count; i++)
        {
            if (pps[i].Start is { } start && Fires(start, e) && next < 0)
                next = i;
        }

        if (next < 0)
            return;
        progPoint = next;
        var pp = pps[next];
        Marks.Add(new EncounterMark { Kind = MarkKind.ProgPoint, Id = pp.Id, Name = pp.Name, Ticks = e.Ticks + Delay(pp.Start!), Event = e, Def = pp });
    }

    private static long Delay(TriggerDef t) => (long)((t.DelayS ?? 0) * TimeSpan.TicksPerSecond);

    private bool Fires(TriggerDef t, in TriggerEvent e)
    {
        if (!Matches(t, e))
            return false;
        if (t.Occurrence is not { } n)
            return true;
        var c = counts.GetValueOrDefault(t) + 1;
        counts[t] = c;
        return c == n;
    }

    public static bool Matches(TriggerDef t, TriggerEvent e)
    {
        if (t.AnyOf != null)
        {
            foreach (var child in t.AnyOf)
            {
                if (Matches(child, e))
                    return true;
            }

            return false;
        }

        if (t.CastStart != null)
            return e.Kind == TriggerKind.CastStart && Ids(t.CastStart, e);
        if (t.Ability != null)
            return e.Kind == TriggerKind.Ability && Ids(t.Ability, e);
        if (t.Spawn != null)
            return e.Kind == TriggerKind.Spawn && Bases(t.Spawn, e);
        if (t.Despawn != null)
            return e.Kind == TriggerKind.Despawn && Bases(t.Despawn, e);
        if (t.Director != null)
        {
            return e.Kind == TriggerKind.Director && e.Id == t.Director.Command &&
                   (t.Director.P1 == null || t.Director.P1.Any(p => p.Value == e.P1)) &&
                   (t.Director.P2 == null || t.Director.P2.Any(p => p.Value == e.P2));
        }

        if (t.MapEffect != null)
        {
            return e.Kind == TriggerKind.MapEffect && (t.MapEffect.Flags == null || t.MapEffect.Flags.Value.Value == e.Id) &&
                   (t.MapEffect.Location == null || t.MapEffect.Location.Any(l => l.Value == e.P1));
        }

        if (t.HeadMarker != null)
            return e.Kind == TriggerKind.HeadMarker && Ids(t.HeadMarker, e);
        if (t.StatusGain != null)
            return e.Kind == TriggerKind.StatusGain && Ids(t.StatusGain, e);
        if (t.StatusLose != null)
            return e.Kind == TriggerKind.StatusLose && Ids(t.StatusLose, e);
        if (t.Tether != null)
            return e.Kind == TriggerKind.Tether && Ids(t.Tether, e);
        if (t.NameToggle != null)
        {
            return e.Kind == TriggerKind.NameToggle &&
                   (t.NameToggle.BnpcBase == null || t.NameToggle.BnpcBase.Contains(e.ActorBase)) &&
                   (t.NameToggle.Targetable == null || t.NameToggle.Targetable == e.Flag);
        }

        if (t.HpBelow != null)
        {
            return e.Kind == TriggerKind.Hp && e.Pct < t.HpBelow.Pct &&
                   (t.HpBelow.BnpcBase == null || t.HpBelow.BnpcBase.Contains(e.ActorBase));
        }

        if (t.ActorControl != null)
        {
            var ac = t.ActorControl;
            return e.Kind == TriggerKind.ActorControl && e.Id == ac.Category &&
                   (ac.P1 == null || ac.P1.Any(p => p.Value == e.P1)) &&
                   (ac.P2 == null || ac.P2.Any(p => p.Value == e.P2)) &&
                   (ac.BnpcBase == null || ac.BnpcBase.Contains(e.ActorBase)) &&
                   (ac.EobjBase == null || ac.EobjBase.Any(b => b.Value == e.ActorBase));
        }

        return false;
    }

    private static bool Ids(IdMatch m, TriggerEvent e)
    {
        if (m.SourceBnpcBase != null && !m.SourceBnpcBase.Contains(e.ActorBase))
            return false;
        if (m.Ids != null)
        {
            foreach (var id in m.Ids)
            {
                if (id.Value == e.Id)
                    return true;
            }
        }

        if (m.Names != null && e.Name != null)
        {
            foreach (var n in m.Names)
            {
                if (string.Equals(n, e.Name, StringComparison.OrdinalIgnoreCase))
                    return true;
            }
        }

        return m.Ids == null && m.Names == null;
    }

    private static bool Bases(BaseMatch m, TriggerEvent e)
    {
        if (m.BnpcBase != null && m.BnpcBase.Contains(e.ActorBase) && !e.IsEobj)
            return true;
        if (m.EobjBase != null && m.EobjBase.Any(b => b.Value == e.ActorBase))
            return true;
        if (m.Names != null && e.Name != null && m.Names.Any(n => string.Equals(n, e.Name, StringComparison.OrdinalIgnoreCase)))
            return true;
        return false;
    }
}

/// <summary>Runs encounter phase detection during indexing.</summary>
public sealed class EncounterObserverFactory(EncounterRegistry registry) : IPullObserverFactory
{
    public LineTypeSet Types { get; } = new(TriggerEventSource.Types);

    public IPullObserver? Create(uint zoneId, PullSummary pull, WorldStateTracker world)
    {
        var enc = registry.ForTerritory(zoneId);
        if (enc == null)
            return null;
        pull.EncounterKey = enc.Key;
        return new Observer(new EncounterRun(enc, pull.StartTicks));
    }

    private sealed class Observer(EncounterRun run) : IPullObserver
    {
        private readonly TriggerEventSource source = new();

        public void OnLine(int type, scoped in LineFields f, long ticks, WorldStateTracker world)
        {
            if (source.TryConvert(type, f, ticks, world, out var e))
                run.Feed(e);
        }

        public void Complete(PullSummary pull)
        {
            foreach (var m in run.Marks)
            {
                if (m.Kind == MarkKind.Phase && m.Ticks <= pull.EndTicks)
                    pull.Phases.Add(new PhaseMark { Id = m.Id, Name = m.Name, Ticks = m.Ticks });
            }
        }
    }
}
