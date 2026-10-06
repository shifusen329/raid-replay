using System.Numerics;
using RaidReplay.Core.Aoe;
using RaidReplay.Core.Encounters;
using RaidReplay.Core.GameData;
using RaidReplay.Core.Indexing;
using RaidReplay.Core.Model;
using RaidReplay.Core.Parsing;

namespace RaidReplay.Core.Loading;

public static class PullLoader
{
    /// <summary>Reconstructs a pull by seeking to its checkpoint and streaming to the end of its tail.</summary>
    /// <param name="readToEof">Read to the current end of file (live: the tail is still being written).</param>
    public static PullReplay Load(
        PullSummary summary, IGameData? data = null, EncounterRegistry? encounters = null, CancellationToken ct = default,
        bool readToEof = false)
    {
        var enc = encounters?.ForTerritory(summary.ZoneId);
        var builder = new PullBuilder(summary, enc, data ?? NullGameData.Instance);
        var end = !readToEof && summary.TailEndOffset > summary.StartOffset ? summary.TailEndOffset : -1;
        LogLineReader.Read(summary.FilePath, summary.CheckpointOffset, end, builder.Filter, builder, ct);
        ct.ThrowIfCancellationRequested();
        return builder.Build();
    }
}

internal sealed class PullBuilder : ILineConsumer
{
    private const long Ms = TimeSpan.TicksPerMillisecond;
    private const long Sec = TimeSpan.TicksPerSecond;

    private readonly PullSummary summary;
    private readonly CompiledEncounter? enc;
    private readonly IGameData data;
    private readonly WorldStateTracker world = new();
    private readonly TriggerEventSource triggerSource = new();
    private readonly EncounterRun? run;
    private readonly long startTicks;
    private readonly long recordFrom;
    private readonly long stopTicks;
    private readonly HashSet<uint> partyIds;

    private readonly Dictionary<uint, ActorBuild> current = new();
    private readonly List<ActorBuild> actors = [];
    private readonly List<CastEvent> casts = [];
    private readonly List<CastExtra> castExtras = [];
    private readonly List<ActionEvent> actions = [];
    private readonly Dictionary<uint, ActionEvent> actionsBySeq = new();
    private readonly List<AbilityExtra> abilityExtras = [];
    private readonly List<EffectResult> effectResults = [];
    private readonly List<TickEvent> ticks = [];
    private readonly List<StatusLine> statusLines = [];
    private readonly List<DeathEvent> deaths = [];
    private readonly List<HeadMarkerEvent> headMarkers = [];
    private readonly List<TetherEvent> tethers = [];
    private readonly List<WaymarkEvent> waymarks = [];
    private readonly List<SignEvent> signs = [];
    private readonly List<MapEffectEvent> mapEffects = [];
    private readonly List<DirectorEvent> directors = [];
    private readonly List<ActorControlEvent> actorControls = [];
    private readonly List<NameToggleEvent> nameToggles = [];
    private readonly List<MapChangeEvent> mapChanges = [];
    private readonly List<(uint Src, uint Action, int T)> cancels = [];
    private readonly List<WaymarkState> initialWaymarks = [];
    private readonly List<string> diagnostics = [];

    private bool recording;
    private int order;
    private int initialMapId;
    private long lastTicks;

    public PullBuilder(PullSummary summary, CompiledEncounter? enc, IGameData data)
    {
        this.summary = summary;
        this.enc = enc;
        this.data = data;
        startTicks = summary.StartTicks;
        var leadIn = summary.CountdownTicks != 0
                         ? Math.Min(40 * Sec, startTicks - summary.CountdownTicks + Sec)
                         : 10 * Sec;
        recordFrom = Math.Max(startTicks - leadIn, summary.Checkpoint.Ticks);
        stopTicks = summary.EndTicks + (8 * Sec);
        world.Restore(summary.Checkpoint);
        partyIds = [..summary.Party.Select(p => p.Id)];
        if (enc != null)
            run = new EncounterRun(enc, startTicks);

        Filter = LineTypeSet.All;
        foreach (var t in new[]
                 {
                     LineType.Chat, LineType.PlayerStats, LineType.Gauge, LineType.LimitBreak, LineType.SystemLogMessage,
                     LineType.StatusList3, 249, 250, 251, 252, 253, 254, 255, 256, LineType.FateDirector,
                     LineType.CeDirector, LineType.RsvData, LineType.ContentFinderSettings,
                 })
            Filter.Remove(t);
    }

    public LineTypeSet Filter { get; }

    private int T(long t) => (int)((t - startTicks) / Ms);

    public bool OnLine(long offset, int type, ReadOnlySpan<byte> line)
    {
        Span<int> buf = stackalloc int[type switch
        {
            LineType.CombatantMemory => 192,
            LineType.StatusList => 16,
            _ => 64,
        }];
        var f = new LineFields(line, buf);
        var ticks = f.Ticks;
        if (ticks == 0)
            return true;
        if (ticks > lastTicks)
            lastTicks = ticks;
        if (lastTicks > stopTicks + (2 * Sec))
            return false;

        if (!recording && ticks >= recordFrom)
            StartRecording();

        // Encounter triggers see the line before the tracker applies it.
        if (run != null && ticks >= startTicks && ticks <= stopTicks && triggerSource.TryConvert(type, f, ticks, world, out var ev))
            run.Feed(ev);

        if (recording && ticks <= stopTicks)
            RecordBeforeApply(type, f, ticks);

        world.Apply(type, f, ticks);

        if (recording && ticks <= stopTicks)
        {
            order++;
            Record(type, f, ticks);
        }

        return true;
    }

    private void StartRecording()
    {
        recording = true;
        initialMapId = world.MapId;
        foreach (var w in world.Waymarks)
        {
            if (w != null)
                initialWaymarks.Add(new WaymarkState { Slot = w.Slot, X = w.X, Y = w.Y, Z = w.Z });
        }

        var t0 = T(recordFrom);
        foreach (var c in world.Combatants.Values)
        {
            var a = Create(c.Id, int.MinValue);
            if (c.PosTicks != 0 && TrackerState.ValidPos(c.X, c.Y))
                a.AddPos(t0, c.X, c.Y, c.Z, c.Heading, 0, PosFlags.None, order);
            if (c.MaxHp > 0)
                a.Hp.Add((t0, c.Hp));
        }

        foreach (var (marker, target) in world.Signs)
            signs.Add(new SignEvent { T = t0, Marker = marker, Add = true, Target = Get(target)?.Actor });
    }

    // ---- actors ---------------------------------------------------------------------------------------

    private ActorBuild Create(uint id, int spawnMs)
    {
        var gen = 0;
        if (current.TryGetValue(id, out var prev))
            gen = prev.Actor.Generation + 1;
        var a = new ActorBuild(new Actor { Id = id, Generation = gen, SpawnMs = spawnMs });
        current[id] = a;
        actors.Add(a);
        if (world.Combatants.TryGetValue(id, out var c))
            a.UpdateFrom(c);
        return a;
    }

    private ActorBuild? Get(uint id)
    {
        if (id == 0 || id == 0xE0000000)
            return null;
        return current.TryGetValue(id, out var a) ? a : Create(id, int.MinValue);
    }

    private ActorBuild Spawn(uint id, int t)
    {
        if (current.TryGetValue(id, out var a) && !a.Despawned)
            return a;
        return Create(id, t);
    }

    private void Despawn(uint id, int t)
    {
        if (current.TryGetValue(id, out var a) && !a.Despawned)
        {
            a.Despawned = true;
            a.Actor.DespawnMs = t;
        }
    }

    // ---- recording -------------------------------------------------------------------------------------

    private void RecordBeforeApply(int type, scoped in LineFields f, long ticks)
    {
        // Removals: capture before the tracker drops the combatant.
        if (type == LineType.RemoveCombatant)
            Despawn(f.Hex(F03.Id), T(ticks));
        else if (type == LineType.CombatantMemory && f[F261.Op].SequenceEqual("Remove"u8))
            Despawn(f.Hex(F261.Id), T(ticks));
    }

    private void Record(int type, scoped in LineFields f, long ticks)
    {
        var t = T(ticks);
        switch (type)
        {
            case LineType.AddCombatant:
            {
                var id = f.Hex(F03.Id);
                var a = Spawn(id, t);
                if (world.Combatants.TryGetValue(id, out var c))
                    a.UpdateFrom(c);
                a.AddPos(t, f.Float(F03.X), f.Float(F03.Y), f.Float(F03.Z), f.Float(F03.Heading), 1, PosFlags.None, order);
                a.Hp.Add((t, f.Int(F03.Hp)));
                break;
            }
            case LineType.CombatantMemory:
                RecordMemory(f, t);
                break;
            case LineType.ActorMove:
            case LineType.ActorSetPos:
            {
                var a = Get(f.Hex(F270.Id));
                a?.AddPos(t, f.Float(F270.X), f.Float(F270.Y), f.Float(F270.Z), f.Float(F270.Heading), 3,
                          type == LineType.ActorSetPos ? PosFlags.Teleport : PosFlags.None, order);
                break;
            }
            case LineType.StartsCasting:
                RecordCast(f, t);
                break;
            case LineType.StartsUsingExtra:
                castExtras.Add(new CastExtra(f.Hex(F263.SourceId), f.Hex(F263.ActionId), t,
                                             new Vector2(f.Float(F263.X), f.Float(F263.Y)), f.Float(F263.Heading)));
                break;
            case LineType.Ability:
            case LineType.AoeAbility:
                RecordAbility(type, f, t);
                break;
            case LineType.AbilityExtra:
            {
                Vector2? loc = null;
                if (f.Int(F264.HasLocation) == 1)
                {
                    var x = f.Float(F264.X);
                    var y = f.Float(F264.Y);
                    if (TrackerState.ValidPos(x, y) && (Math.Abs(x) > 1 || Math.Abs(y) > 1))
                        loc = new Vector2(x, y);
                }

                abilityExtras.Add(new AbilityExtra(f.Hex(F264.Sequence), f.Hex(F264.SourceId), loc, f.Float(F264.Heading),
                                                   f.Hex(F264.AnimationTarget)));
                break;
            }
            case LineType.EffectResult:
            {
                var a = Get(f.Hex(F37.Id));
                if (a == null)
                    break;
                var hp = f.Int(F37.Hp);
                a.Hp.Add((t, hp));
                a.AddPos(t, f.Float(F37.X), f.Float(F37.Y), f.Float(F37.Z), f.Float(F37.Heading), 1, PosFlags.None, order);
                effectResults.Add(new EffectResult(f.Hex(F37.Sequence), a.Actor.Id, hp));
                break;
            }
            case LineType.StatusList:
            {
                var a = Get(f.Hex(F38.Id));
                if (a == null)
                    break;
                if (!f.IsEmpty(F38.MaxHp))
                    a.Hp.Add((t, f.Int(F38.Hp)));
                a.AddPos(t, f.Float(F38.X), f.Float(F38.Y), f.Float(F38.Z), f.Float(F38.Heading), 1, PosFlags.None, order);
                break;
            }
            case LineType.UpdateHp:
            {
                var a = Get(f.Hex(F39.Id));
                if (a == null)
                    break;
                a.Hp.Add((t, f.Int(F39.Hp)));
                a.AddPos(t, f.Float(F39.X), f.Float(F39.Y), f.Float(F39.Z), f.Float(F39.Heading), 1, PosFlags.None, order);
                break;
            }
            case LineType.DoTHoT:
            {
                var target = Get(f.Hex(F24.TargetId));
                if (target == null)
                    break;
                var source = Get(f.Hex(F24.SourceId));
                var heal = f.Is(F24.Which, "HoT"u8);
                ticks_Add(new TickEvent
                {
                    Target = target.Actor, Source = source?.Actor, T = t, Amount = (int)f.Hex(F24.Amount), IsHeal = heal,
                    EffectId = f.Hex(F24.EffectId),
                });
                target.Hp.Add((t, f.Int(F24.TargetHp)));
                target.AddPos(t, f.Float(F24.TargetX), f.Float(F24.TargetY), f.Float(F24.TargetZ), f.Float(F24.TargetHeading), 1,
                              PosFlags.None, order);
                source?.AddPos(t, f.Float(F24.SourceX), f.Float(F24.SourceY), f.Float(F24.SourceZ),
                               f.Float(F24.SourceHeading), 1, PosFlags.None, order);
                break;
            }
            case LineType.CancelAbility:
                cancels.Add((f.Hex(F23.SourceId), f.Hex(F23.ActionId), t));
                break;
            case LineType.Death:
            {
                var victim = Get(f.Hex(F25.TargetId));
                if (victim != null)
                    deaths.Add(new DeathEvent { Victim = victim.Actor, Source = Get(f.Hex(F25.SourceId))?.Actor, T = t });
                break;
            }
            case LineType.StatusAdd:
            case LineType.StatusRemove:
            {
                var target = Get(f.Hex(F26.TargetId));
                if (target == null)
                    break;
                statusLines.Add(new StatusLine(
                    t, order, type == LineType.StatusAdd, target.Actor, Get(f.Hex(F26.SourceId))?.Actor, f.Hex(F26.StatusId),
                    f.Str(F26.StatusName, world.Strings), f.Float(F26.Duration), (int)f.Hex(F26.Stacks)));
                break;
            }
            case LineType.HeadMarker:
            {
                var target = Get(f.Hex(F27.TargetId));
                if (target != null)
                    headMarkers.Add(new HeadMarkerEvent { Target = target.Actor, MarkerId = f.Hex(F27.MarkerId), T = t });
                break;
            }
            case LineType.Tether:
            {
                var src = Get(f.Hex(F35.SourceId));
                var tgt = Get(f.Hex(F35.TargetId));
                if (src != null && tgt != null)
                    tethers.Add(new TetherEvent { Source = src.Actor, Target = tgt.Actor, TetherId = f.Hex(F35.TetherId), StartMs = t });
                break;
            }
            case LineType.Waymark:
            {
                var slot = f.Int(F28.Slot);
                if ((uint)slot < 8)
                {
                    waymarks.Add(new WaymarkEvent
                    {
                        T = t, Slot = slot, Add = f.Is(F28.Op, "Add"u8),
                        Pos = new Vector3(f.Float(F28.X), f.Float(F28.Y), f.Float(F28.Z)),
                    });
                }

                break;
            }
            case LineType.SignMarker:
                signs.Add(new SignEvent
                {
                    T = t, Marker = f.Int(F29.Marker), Add = f.Is(F29.Op, "Add"u8), Target = Get(f.Hex(F29.TargetId))?.Actor,
                });
                break;
            case LineType.Director:
                directors.Add(new DirectorEvent
                {
                    T = t, Instance = f.Hex(F33.Instance), Command = f.Hex(F33.Command), P1 = f.Hex(F33.P1),
                    P2 = f.Hex(F33.P2), P3 = f.Hex(F33.P3), P4 = f.Hex(F33.P4),
                });
                break;
            case LineType.NameToggle:
            {
                var a = Get(f.Hex(F34.Id));
                if (a != null)
                {
                    var on = f.Hex(F34.Toggle) != 0;
                    nameToggles.Add(new NameToggleEvent { Actor = a.Actor, T = t, Targetable = on });
                    a.Toggles.Add((t, on));
                }

                break;
            }
            case LineType.Map:
                mapChanges.Add(new MapChangeEvent { T = t, MapId = f.Int(F40.MapId), Name = f.Str(F40.Place, world.Strings) });
                break;
            case LineType.MapEffect:
                mapEffects.Add(new MapEffectEvent
                {
                    T = t, Instance = f.Hex(F257.Instance), Flags = f.Hex(F257.Flags), Location = f.Hex(F257.Location),
                });
                break;
            case LineType.ActorControlExtra:
            {
                var a = Get(f.Hex(F273.Id));
                if (a != null)
                {
                    actorControls.Add(new ActorControlEvent
                    {
                        Actor = a.Actor, T = t, Category = f.Hex(F273.Category), P1 = f.Hex(F273.P1), P2 = f.Hex(F273.P2),
                        P3 = f.Hex(F273.P3), P4 = f.Hex(F273.P4),
                    });
                }

                break;
            }
        }
    }

    private void ticks_Add(TickEvent e) => ticks.Add(e);

    private void RecordMemory(scoped in LineFields f, int t)
    {
        var op = f[F261.Op];
        var id = f.Hex(F261.Id);
        if (op.SequenceEqual("Remove"u8))
            return; // handled before apply

        var isAdd = op.SequenceEqual("Add"u8);
        var a = isAdd ? Spawn(id, t) : Get(id);
        if (a == null || !world.Combatants.TryGetValue(id, out var c))
            return;
        a.UpdateFrom(c);

        var mem = new MemoryFields();
        mem.Read(f, world.Strings);
        if (isAdd || mem.HasPosition)
            a.AddPos(t, c.X, c.Y, c.Z, c.Heading, 2, PosFlags.None, order);
        if (isAdd || mem.ModelStatus.HasValue)
            a.Model.Add((t, c.ModelStatus));
    }

    private void RecordCast(scoped in LineFields f, int t)
    {
        var src = Get(f.Hex(F20.SourceId));
        if (src == null)
            return;
        var castMs = (int)(f.Float(F20.CastTime) * 1000);
        var x = f.Float(F20.X);
        var y = f.Float(F20.Y);
        casts.Add(new CastEvent
        {
            Source = src.Actor,
            ActionId = f.Hex(F20.ActionId),
            Name = f.Str(F20.ActionName, world.Strings),
            Target = Get(f.Hex(F20.TargetId))?.Actor,
            StartMs = t,
            DurationMs = castMs > 0 ? castMs : 0,
            Pos = new Vector2(x, y),
            Heading = f.Float(F20.Heading),
        });
        src.AddPos(t, x, y, f.Float(F20.Z), f.Float(F20.Heading), 1, PosFlags.None, order);
    }

    private void RecordAbility(int type, scoped in LineFields f, int t)
    {
        var src = Get(f.Hex(F21.SourceId));
        if (src == null)
            return;
        var seq = f.Hex(F21.Sequence);
        var actionId = f.Hex(F21.ActionId);
        if (!actionsBySeq.TryGetValue(seq, out var action) || action.Source != src.Actor || action.ActionId != actionId)
        {
            float? rot = null;
            if (f.Has(F21.Rotation) && Utf8Num.TryHex(f[F21.Rotation], out var r) && f[F21.Rotation].Length == 4)
                rot = Utf8Num.RotationToHeading(r);
            action = new ActionEvent
            {
                Source = src.Actor,
                ActionId = actionId,
                Name = f.Str(F21.ActionName, world.Strings),
                Sequence = seq,
                T = t,
                IsAoeLine = type == LineType.AoeAbility,
                SourcePos = new Vector2(f.Float(F21.SourceX), f.Float(F21.SourceY)),
                SourceHeading = f.Float(F21.SourceHeading),
                RotationHeading = rot,
                TargetCount = f.Int(F21.TargetCount),
            };
            actionsBySeq[seq] = action;
            actions.Add(action);
            src.AddPos(t, f.Float(F21.SourceX), f.Float(F21.SourceY), f.Float(F21.SourceZ), f.Float(F21.SourceHeading), 1,
                       PosFlags.None, order);
        }

        var tgt = Get(f.Hex(F21.TargetId));
        if (tgt == null)
            return;
        action.PrimaryTarget ??= tgt.Actor;
        var hit = new HitEvent
        {
            Action = action,
            Target = tgt.Actor,
            T = t,
            HpBefore = f.Int(F21.TargetHp),
            MaxHp = f.Int(F21.TargetMaxHp),
            TargetPos = new Vector2(f.Float(F21.TargetX), f.Float(F21.TargetY)),
        };
        for (var i = 0; i < 8; i++)
        {
            var flags = f.Hex(F21.FirstEffect + (i * 2));
            if (flags == 0)
                continue;
            var value = f.Hex(F21.FirstEffect + (i * 2) + 1);
            switch (Effects.Type(flags))
            {
                case Effects.Damage:
                case Effects.Blocked:
                case Effects.Parried:
                    hit.Damage += Effects.Amount(value);
                    hit.Kind = Effects.Type(flags) switch
                    {
                        Effects.Blocked => HitKind.Blocked,
                        Effects.Parried => HitKind.Parried,
                        _ => HitKind.Damage,
                    };
                    hit.Crit |= Effects.IsCrit(flags);
                    hit.DirectHit |= Effects.IsDirectHit(flags);
                    action.AnyDamage = true;
                    break;
                case Effects.Heal:
                    hit.Heal += Effects.Amount(value);
                    if (hit.Kind == HitKind.Other)
                        hit.Kind = HitKind.Heal;
                    break;
                case Effects.Miss:
                    if (hit.Kind == HitKind.Other)
                        hit.Kind = HitKind.Miss;
                    break;
                case Effects.Invulnerable:
                    if (hit.Kind == HitKind.Other)
                        hit.Kind = HitKind.Invulnerable;
                    break;
                case Effects.NoEffect:
                    if (hit.Kind == HitKind.Other)
                        hit.Kind = HitKind.NoEffect;
                    break;
                case Effects.Knockback:
                case Effects.Draw:
                    hit.Knockback = true;
                    break;
                case Effects.InstantDeath:
                    hit.InstantDeath = true;
                    break;
            }
        }

        action.Hits.Add(hit);
        if (hit.MaxHp > 0)
            tgt.Hp.Add((t, hit.HpBefore));
        tgt.AddPos(t, f.Float(F21.TargetX), f.Float(F21.TargetY), f.Float(F21.TargetZ), f.Float(F21.TargetHeading), 1,
                   PosFlags.None, order);
    }

    // ---- build -----------------------------------------------------------------------------------------

    public PullReplay Build()
    {
        if (!recording)
            StartRecording();

        var replay = new PullReplay
        {
            Summary = summary,
            StartTicks = startTicks,
            FirstMs = T(recordFrom),
            EndMs = summary.DurationMs,
            LastMs = Math.Min(T(lastTicks), T(stopTicks)),
            Encounter = enc,
            InitialMapId = initialMapId != 0 ? initialMapId : summary.MapId,
        };

        foreach (var a in actors)
        {
            a.Build();
            a.Actor.Index = replay.Actors.Count;
            replay.Actors.Add(a.Actor);
        }

        BuildCasts(replay);
        BuildActions(replay);
        BuildStatuses(replay);
        BuildDeaths(replay);

        replay.Ticks.AddRange(ticks.OrderBy(e => e.T));
        replay.HeadMarkers.AddRange(headMarkers.OrderBy(e => e.T));
        replay.Tethers.AddRange(tethers.OrderBy(e => e.StartMs));
        replay.Waymarks.AddRange(waymarks.OrderBy(e => e.T));
        replay.InitialWaymarks.AddRange(initialWaymarks);
        replay.Signs.AddRange(signs.OrderBy(e => e.T));
        replay.MapEffects.AddRange(mapEffects.OrderBy(e => e.T));
        replay.Directors.AddRange(directors.OrderBy(e => e.T));
        replay.ActorControls.AddRange(actorControls.OrderBy(e => e.T));
        replay.NameToggles.AddRange(nameToggles.OrderBy(e => e.T));
        replay.MapChanges.AddRange(mapChanges.OrderBy(e => e.T));

        ActorClassifier.Classify(replay, enc, partyIds);
        foreach (var id in summary.Party.Select(p => p.Id))
        {
            var actor = replay.Actors.LastOrDefault(a => a.Id == id);
            if (actor != null)
                replay.Party.Add(actor);
        }

        if (replay.Party.Count == 0)
            replay.Party.AddRange(replay.Actors.Where(a => a.Kind == ActorKind.Player));
        replay.Party.Sort((x, y) => Jobs.RoleOrder(x.Job).CompareTo(Jobs.RoleOrder(y.Job)));

        var marks = run?.Marks ?? [];
        EncounterAnnotator.Annotate(replay, enc, marks);
        AoeInference.Run(replay, enc, data, marks);
        replay.Diagnostics.AddRange(diagnostics);
        return replay;
    }

    private void BuildCasts(PullReplay replay)
    {
        casts.Sort((a, b) => a.StartMs.CompareTo(b.StartMs));
        // Attach 263 StartsUsingExtra: same source/action, closest in time (≤ 300 ms).
        var bySource = casts.GroupBy(c => (c.Source.Id, c.ActionId)).ToDictionary(g => g.Key, g => g.ToList());
        foreach (var x in castExtras)
        {
            if (!bySource.TryGetValue((x.Source, x.Action), out var list))
                continue;
            CastEvent? best = null;
            var bestDt = 301;
            foreach (var c in list)
            {
                var dt = Math.Abs(c.StartMs - x.T);
                if (dt < bestDt && !c.HasExtra)
                {
                    best = c;
                    bestDt = dt;
                }
            }

            if (best != null && TrackerState.ValidPos(x.Pos.X, x.Pos.Y))
            {
                best.Pos = x.Pos;
                if (!float.IsNaN(x.Heading))
                    best.Heading = x.Heading;
                best.HasExtra = true;
            }
        }

        foreach (var c in casts)
        {
            c.EndMs = c.StartMs + c.DurationMs;
            c.Index = replay.Casts.Count;
            replay.Casts.Add(c);
        }

    }

    private void BuildActions(PullReplay replay)
    {
        actions.Sort((a, b) => a.T.CompareTo(b.T));
        foreach (var x in abilityExtras)
        {
            if (!actionsBySeq.TryGetValue(x.Sequence, out var action) || action.Source.Id != x.Source)
                continue;
            action.Location ??= x.Location;
            if (!float.IsNaN(x.Heading))
                action.ExtraHeading ??= x.Heading;
            if (x.AnimTarget != 0 && x.AnimTarget != 0xE0000000 && current.TryGetValue(x.AnimTarget, out var at))
                action.AnimationTarget ??= at.Actor;
        }

        foreach (var r in effectResults)
        {
            if (r.Sequence == 0 || !actionsBySeq.TryGetValue(r.Sequence, out var action))
                continue;
            foreach (var hit in action.Hits)
            {
                if (hit.Target.Id == r.Target && hit.HpAfter < 0)
                {
                    hit.HpAfter = r.Hp;
                    break;
                }
            }
        }

        foreach (var a in actions)
        {
            a.Index = replay.Actions.Count;
            replay.Actions.Add(a);
        }

        // Link casts to their resolving action: same source & action id, near the end of the cast.
        var index = new Dictionary<(uint, uint), List<ActionEvent>>();
        foreach (var a in replay.Actions)
        {
            var key = (a.Source.Id, a.ActionId);
            if (!index.TryGetValue(key, out var list))
                index[key] = list = [];
            list.Add(a);
        }

        foreach (var c in replay.Casts)
        {
            if (c.Outcome == CastOutcome.Cancelled)
                continue;
            var resolved = false;
            if (index.TryGetValue((c.Source.Id, c.ActionId), out var list))
            {
                foreach (var a in list)
                {
                    if (a.Cast != null || a.T < c.StartMs + c.DurationMs - 600)
                        continue;
                    if (a.T > c.StartMs + c.DurationMs + 3000)
                        break;
                    a.Cast = c;
                    c.Resolution = a;
                    resolved = true;
                    break;
                }
            }

            c.Outcome = resolved || c.EndMs <= replay.LastMs ? CastOutcome.Completed : CastOutcome.Unknown;
        }

        // ACT logs "23 … Cancelled" shortly before many successful resolutions; only casts that never resolved
        // are treated as cancelled/interrupted.
        foreach (var (src, action, t) in cancels)
        {
            var cast = replay.Casts.LastOrDefault(c => c.Source.Id == src && c.ActionId == action && c.StartMs <= t &&
                                                       c.Resolution == null && c.Outcome != CastOutcome.Cancelled &&
                                                       t <= c.StartMs + c.DurationMs + 500);
            if (cast == null)
                continue;
            cast.Outcome = CastOutcome.Cancelled;
            cast.EndMs = t;
        }
    }

    private void BuildStatuses(PullReplay replay)
    {
        statusLines.Sort((a, b) => a.T != b.T ? a.T.CompareTo(b.T) : a.Order.CompareTo(b.Order));
        var open = new Dictionary<(Actor, uint, Actor?), StatusInterval>();
        foreach (var s in statusLines)
        {
            var key = (s.Target, s.StatusId, s.Source);
            if (s.Add)
            {
                if (open.TryGetValue(key, out var existing))
                {
                    existing.Duration = s.Duration;
                    existing.Stacks = s.Stacks;
                    continue;
                }

                var interval = new StatusInterval
                {
                    Target = s.Target, Source = s.Source, StatusId = s.StatusId, Name = s.Name, StartMs = s.T,
                    Duration = s.Duration, Stacks = s.Stacks,
                };
                open[key] = interval;
                replay.Statuses.Add(interval);
            }
            else if (open.Remove(key, out var interval) ||
                     open.Remove(open.Keys.FirstOrDefault(k => k.Item1 == s.Target && k.Item2 == s.StatusId), out interval))
            {
                interval.EndMs = s.T;
                interval.Removed = true;
            }
        }

        foreach (var interval in open.Values)
        {
            var end = replay.LastMs;
            if (interval.Duration is > 0 and < 9000)
                end = Math.Min(end, interval.StartMs + (int)(interval.Duration * 1000));
            if (interval.Target.DespawnMs < end)
                end = interval.Target.DespawnMs;
            interval.EndMs = Math.Max(interval.StartMs, end);
        }

        foreach (var s in replay.Statuses)
        {
            if (s.Name.Length == 0)
                s.Name = data.GetStatus(s.StatusId)?.Name ?? $"Status {s.StatusId:X}";
        }
    }

    private void BuildDeaths(PullReplay replay)
    {
        deaths.Sort((a, b) => a.T.CompareTo(b.T));
        var arena = enc?.Def.Arena;
        foreach (var d in deaths)
        {
            if (d.Victim.Track.TrySample(d.T, out var pos, out _))
                d.Pos = pos;

            // Killing blow: last damaging hit on the victim at or shortly before death.
            HitEvent? last = null;
            foreach (var a in replay.Actions)
            {
                if (a.T > d.T + 100)
                    break;
                if (a.T < d.T - 10000)
                    continue;
                foreach (var h in a.Hits)
                {
                    if (h.Target == d.Victim && (h.Damage > 0 || h.InstantDeath))
                        last = h;
                }
            }

            d.KillingBlow = last;
            var hp = d.Victim.Hp;
            for (var i = 0; i < hp.T.Length; i++)
            {
                if (hp.T[i] > d.T + 1500 && hp.Values[i] > 0)
                {
                    d.RaisedMs = hp.T[i];
                    break;
                }
            }

            var noKiller = d.Source == null || d.Source == d.Victim;
            if (noKiller && arena is { FallRadius: > 0 })
            {
                var center = new Vector2(arena.Center[0], arena.Center[1]);
                var maxDist = 0f;
                for (var t = d.T - 3000; t <= d.T; t += 250)
                {
                    if (d.Victim.Track.TrySample(t, out var p, out _))
                        maxDist = Math.Max(maxDist, Vector2.Distance(p, center));
                }

                if (maxDist > arena.FallRadius)
                {
                    d.Cause = "Fell off the arena";
                    continue;
                }
            }

            d.Cause = last != null
                          ? $"{last.Action.Name} ({last.Action.Source.DisplayName}){(last.Damage > 0 ? $" {last.Damage:N0}" : "")}"
                          : d.Source != null && d.Source != d.Victim
                              ? d.Source.DisplayName
                              : "Unknown";
        }

        replay.Deaths.AddRange(deaths);
    }

    private sealed record CastExtra(uint Source, uint Action, int T, Vector2 Pos, float Heading);

    private sealed record AbilityExtra(uint Sequence, uint Source, Vector2? Location, float Heading, uint AnimTarget);

    private sealed record EffectResult(uint Sequence, uint Target, int Hp);

    private sealed record StatusLine(
        int T, int Order, bool Add, Actor Target, Actor? Source, uint StatusId, string Name, float Duration, int Stacks);
}

internal static class TrackerState
{
    public static bool ValidPos(float x, float y) =>
        WorldStateTracker.ValidCoord(x) && WorldStateTracker.ValidCoord(y) && !(x == 0 && y == 0);
}

/// <summary>Mutable per-actor accumulation during loading.</summary>
internal sealed class ActorBuild(Actor actor)
{
    public Actor Actor { get; } = actor;
    public List<Sample> Pos { get; } = [];
    public List<(int T, int Hp)> Hp { get; } = [];
    public List<(int T, uint Status)> Model { get; } = [];
    public List<(int T, bool On)> Toggles { get; } = [];
    public bool Despawned { get; set; }

    public void UpdateFrom(CombatantState c)
    {
        if (c.Name.Length > 0)
            Actor.Name = c.Name;
        if (c.Job != 0)
            Actor.Job = c.Job;
        if (c.Level != 0)
            Actor.Level = c.Level;
        if (c.OwnerId != 0)
            Actor.OwnerId = c.OwnerId;
        if (c.BNpcNameId != 0)
            Actor.BNpcNameId = c.BNpcNameId;
        if (c.BNpcBaseId != 0)
            Actor.BNpcBaseId = c.BNpcBaseId;
        if (c.ObjectType != 0)
            Actor.ObjectType = c.ObjectType;
        if (c.MaxHp > 0)
            Actor.MaxHp = c.MaxHp;
        if (c.Radius > 0)
            Actor.Radius = c.Radius;
    }

    public void AddPos(int t, float x, float y, float z, float h, byte rank, PosFlags flags, int order)
    {
        if (!TrackerState.ValidPos(x, y))
            return;
        if (!WorldStateTracker.ValidCoord(z))
            z = float.NaN;
        if (float.IsNaN(h) || Math.Abs(h) > 4f)
            h = float.NaN;
        Pos.Add(new Sample(t, x, y, z, h, rank, flags, order));
    }

    public void Build()
    {
        Pos.Sort((a, b) => a.T != b.T ? a.T.CompareTo(b.T) : a.Order.CompareTo(b.Order));

        // Rank filtering. NPCs with network-level movement (270/271/261) ignore ACT-memory positions, which are
        // stale for invisible helpers. Players drop low-rank samples close to a better one.
        var hasGood = Pos.Exists(s => s.Rank >= 2);
        var filtered = new List<Sample>(Pos.Count);
        if (!Actor.IsPlayer && hasGood)
        {
            filtered.AddRange(Pos.Where(s => s.Rank != 1));
        }
        else
        {
            var good = Pos.Where(s => s.Rank >= 2).Select(s => s.T).ToArray();
            foreach (var s in Pos)
            {
                if (s.Rank == 1 && good.Length > 0)
                {
                    var i = Array.BinarySearch(good, s.T);
                    if (i < 0)
                        i = ~i;
                    var near = (i < good.Length && good[i] - s.T <= 250) || (i > 0 && s.T - good[i - 1] <= 250);
                    if (near)
                        continue;
                }

                filtered.Add(s);
            }
        }

        // Fill missing heading/z by carrying forward.
        var lastH = float.NaN;
        var lastZ = float.NaN;
        for (var i = 0; i < filtered.Count; i++)
        {
            var s = filtered[i];
            if (float.IsNaN(s.H))
                s.H = lastH;
            else
                lastH = s.H;
            if (float.IsNaN(s.Z))
                s.Z = lastZ;
            else
                lastZ = s.Z;
            filtered[i] = s;
        }

        var firstH = filtered.FirstOrDefault(s => !float.IsNaN(s.H)).H;
        var firstZ = filtered.FirstOrDefault(s => !float.IsNaN(s.Z)).Z;

        // Keep the first and last of runs of identical samples; collapse same-ms duplicates to the last one.
        var kept = new List<Sample>(filtered.Count);
        for (var i = 0; i < filtered.Count; i++)
        {
            var s = filtered[i];
            if (float.IsNaN(s.H))
                s.H = float.IsNaN(firstH) ? 0 : firstH;
            if (float.IsNaN(s.Z))
                s.Z = float.IsNaN(firstZ) ? 0 : firstZ;
            if (kept.Count > 0 && kept[^1].T == s.T)
            {
                s.Flags |= kept[^1].Flags;
                kept[^1] = s;
                continue;
            }

            if (kept.Count >= 2 && Same(kept[^1], s) && Same(kept[^2], kept[^1]) && s.Flags == PosFlags.None)
            {
                kept[^1] = s;
                continue;
            }

            kept.Add(s);
        }

        // A teleport (271) is often preceded by a 261 memory sample already at the new location (261 is logged a
        // few hundred ms earlier). Move the discontinuity to the first sample at the new spot.
        for (var k = 1; k < kept.Count; k++)
        {
            if ((kept[k].Flags & PosFlags.Teleport) == 0)
                continue;
            var first = k;
            for (var j = k - 1; j > 0 && kept[k].T - kept[j].T <= 1000; j--)
            {
                if (Math.Abs(kept[j].X - kept[k].X) < 0.05f && Math.Abs(kept[j].Y - kept[k].Y) < 0.05f)
                    first = j;
                else
                    break;
            }

            if (first != k)
            {
                var s = kept[first];
                s.Flags |= PosFlags.Teleport;
                kept[first] = s;
            }
        }

        // Teleport detection: implausible speed.
        for (var i = 1; i < kept.Count; i++)
        {
            var a = kept[i - 1];
            var b = kept[i];
            var dist = MathF.Sqrt(((b.X - a.X) * (b.X - a.X)) + ((b.Y - a.Y) * (b.Y - a.Y)));
            var dt = Math.Max(1, b.T - a.T) / 1000f;
            if (dist > 3 && dist / dt > 30)
            {
                b.Flags |= PosFlags.Teleport;
                kept[i] = b;
            }
        }

        var n = kept.Count;
        var t = new int[n];
        var x = new float[n];
        var y = new float[n];
        var z = new float[n];
        var h = new float[n];
        var fl = new PosFlags[n];
        for (var i = 0; i < n; i++)
        {
            t[i] = kept[i].T;
            x[i] = kept[i].X;
            y[i] = kept[i].Y;
            z[i] = kept[i].Z;
            h[i] = kept[i].H;
            fl[i] = kept[i].Flags;
        }

        Actor.Track = new PositionTrack(t, x, y, z, h, fl);

        Hp.Sort((a, b) => a.T.CompareTo(b.T));
        var ht = new List<int>(Hp.Count);
        var hv = new List<int>(Hp.Count);
        foreach (var (time, hp) in Hp)
        {
            if (hp < 0)
                continue;
            if (hv.Count > 0 && hv[^1] == hp)
                continue;
            if (ht.Count > 0 && ht[^1] == time)
            {
                hv[^1] = hp;
                continue;
            }

            ht.Add(time);
            hv.Add(hp);
        }

        Actor.Hp = new HpTrack(ht.ToArray(), hv.ToArray());

        Model.Sort((a, b) => a.T.CompareTo(b.T));
        int? hiddenSince = null;
        foreach (var (time, status) in Model)
        {
            var hidden = (status & 0x4000) != 0;
            if (hidden && hiddenSince == null)
                hiddenSince = time == Actor.SpawnMs || Model.Count > 0 && time == Model[0].T ? int.MinValue : time;
            else if (!hidden && hiddenSince != null)
            {
                Actor.HiddenSpans.Add(new MsSpan(hiddenSince.Value, time));
                hiddenSince = null;
            }
        }

        if (hiddenSince != null)
            Actor.HiddenSpans.Add(new MsSpan(hiddenSince.Value, int.MaxValue));

        Toggles.Sort((a, b) => a.T.CompareTo(b.T));
        int? offSince = null;
        foreach (var (time, on) in Toggles)
        {
            if (!on && offSince == null)
                offSince = time;
            else if (on && offSince != null)
            {
                Actor.UntargetableSpans.Add(new MsSpan(offSince.Value, time));
                offSince = null;
            }
            else if (on && offSince == null && Actor.UntargetableSpans.Count == 0)
            {
                // Became targetable without a prior "off": untargetable since spawn.
                Actor.UntargetableSpans.Add(new MsSpan(int.MinValue, time));
            }
        }

        if (offSince != null)
            Actor.UntargetableSpans.Add(new MsSpan(offSince.Value, int.MaxValue));
    }

    private static bool Same(in Sample a, in Sample b) =>
        Math.Abs(a.X - b.X) < 0.02f && Math.Abs(a.Y - b.Y) < 0.02f && Math.Abs(Angles.Wrap(a.H - b.H)) < 0.02f;

    internal struct Sample(int t, float x, float y, float z, float h, byte rank, PosFlags flags, int order)
    {
        public int T = t;
        public float X = x, Y = y, Z = z, H = h;
        public byte Rank = rank;
        public PosFlags Flags = flags;
        public int Order = order;
    }
}
