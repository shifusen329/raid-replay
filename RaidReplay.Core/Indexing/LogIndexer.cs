using RaidReplay.Core.Model;
using RaidReplay.Core.Parsing;

namespace RaidReplay.Core.Indexing;

/// <summary>Observes the lines of one pull while indexing (e.g. encounter phase detection).</summary>
public interface IPullObserver
{
    void OnLine(int type, scoped in LineFields f, long ticks, WorldStateTracker world);
    void Complete(PullSummary pull);
}

/// <summary>Creates a pull observer for a zone, or null if nothing to observe.</summary>
public interface IPullObserverFactory
{
    LineTypeSet Types { get; }
    IPullObserver? Create(uint zoneId, PullSummary pull, WorldStateTracker world);
}

public sealed class Checkpoint
{
    public long Offset { get; set; }
    public WorldSnapshot Snapshot { get; set; } = new();
}

public sealed class PendingCountdown
{
    public long Ticks { get; set; }
    public long Offset { get; set; }
    public int Seconds { get; set; }
}

/// <summary>State needed to continue indexing a growing file from a safe point.</summary>
public sealed class ResumeState
{
    public long Offset { get; set; }
    public WorldSnapshot Snapshot { get; set; } = new();
    public int PullOrdinal { get; set; }
    public bool Seen260 { get; set; }
    public long FileFirstTicks { get; set; }
    public PendingCountdown? Countdown { get; set; }
}

/// <summary>
/// Single-pass pull detector. Pull start = 260 game-combat-on (gameChanged=1). Pull end = director
/// wipe/victory, combat-off without a director verdict (5 s grace), zone change, or EOF. Combat flapping
/// (combat back on within 15 s with no verdict) is merged into the same pull. Idle checkpoints allow the
/// loader to seek close to a pull instead of rescanning from the zone-in.
/// </summary>
public sealed class LogIndexer : ILineConsumer
{
    private const long Ms = TimeSpan.TicksPerMillisecond;
    private const long Sec = TimeSpan.TicksPerSecond;
    private const long CheckpointInterval = 15 * Sec;
    private const long EndingGrace = 5 * Sec;
    private const long FlapMerge = 15 * Sec;
    private const long TailLength = 8 * Sec;
    private const long MaxLeadIn = 40 * Sec;
    private const long DefaultLeadIn = 10 * Sec;
    private const int MaxCheckpoints = 12;

    private readonly string path;
    private readonly IPullObserverFactory? observers;
    private readonly LineTypeSet observerTypes;
    private readonly List<Checkpoint> checkpoints = [];
    private readonly Dictionary<uint, CombatantState> engaged = new();

    private State state = State.Idle;
    private PullSummary? pull;
    private IPullObserver? observer;
    private PendingCountdown? countdown;
    private long combatOffTicks;
    private long combatOffOffset;
    private long tailUntil;
    private long lastCheckpointTicks;
    private bool seen260;
    private long fileFirstTicks;
    private int ordinal;
    private ResumeState? safe;

    public LogIndexer(string path, IPullObserverFactory? observers = null)
    {
        this.path = path;
        this.observers = observers;
        observerTypes = observers?.Types ?? new LineTypeSet();
        Filter = new LineTypeSet(WorldStateTracker.Types)
            .Union(new LineTypeSet(LineType.InCombat, LineType.Countdown, LineType.CountdownCancel, LineType.Death))
            .Union(observerTypes);
    }

    private enum State
    {
        Idle,
        Active,
        Ending,
        Tail,
    }

    public LineTypeSet Filter { get; }
    public WorldStateTracker World { get; } = new();
    public List<PullSummary> Pulls { get; } = [];
    public long LinesProcessed { get; private set; }
    public long Malformed { get; private set; }

    public void Resume(ResumeState resume, IEnumerable<PullSummary> previousPulls)
    {
        World.Restore(resume.Snapshot);
        ordinal = resume.PullOrdinal;
        seen260 = resume.Seen260;
        fileFirstTicks = resume.FileFirstTicks;
        countdown = resume.Countdown;
        lastCheckpointTicks = resume.Snapshot.Ticks;
        checkpoints.Clear();
        checkpoints.Add(new Checkpoint { Offset = resume.Offset, Snapshot = resume.Snapshot });
        safe = resume;
        Pulls.AddRange(previousPulls);
    }

    public bool OnLine(long offset, int type, ReadOnlySpan<byte> line)
    {
        Span<int> buf = stackalloc int[type == LineType.CombatantMemory ? 192 : 64];
        var f = new LineFields(line, buf);
        var ticks = f.Ticks;
        if (ticks == 0)
        {
            Malformed++;
            return true;
        }

        LinesProcessed++;
        if (fileFirstTicks == 0)
            fileFirstTicks = ticks;

        // Time-based transitions (use the max seen time; lines can be slightly out of order).
        var now = Math.Max(ticks, World.LastTicks);
        if (state == State.Ending && now - combatOffTicks > EndingGrace)
            EndPull(PullOutcome.CombatEnd, combatOffTicks, combatOffOffset);
        if (state == State.Tail && now > tailUntil)
            FinishTail(offset);

        // Checkpoint before applying the line so a restore + replay from `offset` is exact.
        var isReset = type == LineType.ChangeZone ||
                      (type == LineType.Director && f.Hex(F33.Command) is DirectorCommand.Recommence or DirectorCommand.FadeEnd);
        if (type == LineType.ChangeZone)
        {
            if (state is State.Active or State.Ending)
                EndPull(PullOutcome.ZoneOut, now, offset);
            if (state == State.Tail)
                FinishTail(offset);
            checkpoints.Clear();
            countdown = null;
        }

        if (state == State.Idle && (isReset || now - lastCheckpointTicks > CheckpointInterval))
            TakeCheckpoint(offset, now);

        // Observers see the line before the tracker applies it (so removed actors can still be resolved).
        if (observer != null && state is State.Active or State.Ending && observerTypes.Contains(type))
            observer.OnLine(type, f, ticks, World);

        World.Apply(type, f, ticks);

        switch (type)
        {
            case LineType.Countdown:
                if (state is State.Idle or State.Tail)
                    countdown = new PendingCountdown { Ticks = ticks, Offset = offset, Seconds = f.Int(F268.Seconds) };
                break;
            case LineType.CountdownCancel:
                countdown = null;
                break;
            case LineType.InCombat:
                OnInCombat(offset, f, ticks);
                break;
            case LineType.Director:
                OnDirector(offset, f, ticks);
                break;
            case LineType.Death:
                if (state is State.Active or State.Ending && pull != null && (f.Hex(F25.TargetId) >> 28) == 1)
                {
                    pull.Deaths++;
                    if (pull.FirstDeathTicks == 0)
                        pull.FirstDeathTicks = ticks;
                }

                break;
            case LineType.Ability:
            case LineType.AoeAbility:
                if (state is State.Active or State.Ending)
                {
                    var src = f.Hex(F21.SourceId);
                    var tgt = f.Hex(F21.TargetId);
                    if ((src >> 28) == 1 && (tgt >> 28) == 4 && World.Combatants.TryGetValue(tgt, out var enemy))
                        engaged[tgt] = enemy;
                }

                break;
        }

        return true;
    }

    private void OnInCombat(long offset, scoped in LineFields f, long ticks)
    {
        var inGame = f.Int(F260.InGameCombat) == 1;
        var gameChanged = f.Int(F260.GameChanged) == 1;
        var actChanged = f.Int(F260.ActChanged) == 1;
        var first = !seen260;
        seen260 = true;
        if (!gameChanged)
            return;

        if (inGame)
        {
            if (state == State.Ending && ticks - combatOffTicks < FlapMerge)
            {
                state = State.Active;
                return;
            }

            if (state == State.Tail)
                FinishTail(offset);
            if (state == State.Idle)
            {
                var truncated = (first && actChanged) || ticks - fileFirstTicks < 2 * Sec;
                StartPull(offset, ticks, truncated);
            }
        }
        else if (state == State.Active)
        {
            state = State.Ending;
            combatOffTicks = ticks;
            combatOffOffset = offset;
        }
    }

    private void OnDirector(long offset, scoped in LineFields f, long ticks)
    {
        var cmd = f.Hex(F33.Command);
        switch (cmd)
        {
            case DirectorCommand.Wipe:
            case DirectorCommand.Victory:
            {
                var outcome = cmd == DirectorCommand.Wipe ? PullOutcome.Wipe : PullOutcome.Clear;
                if (state == State.Active)
                    EndPull(outcome, ticks, offset);
                else if (state == State.Ending)
                    EndPull(outcome, Math.Min(combatOffTicks, ticks), combatOffOffset);
                else if (state == State.Tail && Pulls.Count > 0 && Pulls[^1].Outcome == PullOutcome.CombatEnd &&
                         ticks - Pulls[^1].EndTicks < 2 * EndingGrace)
                    Pulls[^1].Outcome = outcome;
                break;
            }
            case DirectorCommand.Recommence:
            case DirectorCommand.FadeEnd:
                if (state == State.Tail)
                    FinishTail(offset);
                break;
        }
    }

    private void TakeCheckpoint(long offset, long now)
    {
        lastCheckpointTicks = now;
        var cp = new Checkpoint { Offset = offset, Snapshot = World.Snapshot() };
        checkpoints.Add(cp);
        if (checkpoints.Count > MaxCheckpoints)
            checkpoints.RemoveAt(0);
        safe = new ResumeState
        {
            Offset = offset, Snapshot = cp.Snapshot, PullOrdinal = ordinal, Seen260 = seen260,
            FileFirstTicks = fileFirstTicks, Countdown = countdown,
        };
    }

    private void StartPull(long offset, long ticks, bool truncated)
    {
        var cd = countdown;
        if (cd != null && ticks - cd.Ticks > (cd.Seconds + 10) * Sec)
            cd = null;

        var leadIn = cd != null ? Math.Min(MaxLeadIn, ticks - cd.Ticks + (2 * Sec)) : DefaultLeadIn;
        Checkpoint? cp = null;
        foreach (var c in checkpoints)
        {
            if (c.Snapshot.Ticks <= ticks - leadIn || cp == null)
                cp = c;
        }

        pull = new PullSummary
        {
            FilePath = path,
            Ordinal = ++ordinal,
            CheckpointOffset = cp?.Offset ?? offset,
            Checkpoint = cp?.Snapshot ?? World.Snapshot(),
            CountdownOffset = cd?.Offset ?? -1,
            CountdownTicks = cd?.Ticks ?? 0,
            CountdownSeconds = cd?.Seconds ?? 0,
            StartOffset = offset,
            StartTicks = ticks,
            ZoneId = World.ZoneId,
            ZoneName = World.ZoneName,
            MapId = World.MapId,
            StartTruncated = truncated,
        };

        var partyIds = World.Party.Count > 0 ? World.Party : [World.PrimaryPlayerId];
        foreach (var id in partyIds)
        {
            if (id == 0)
                continue;
            World.Combatants.TryGetValue(id, out var c);
            pull.Party.Add(new PartyMember { Id = id, Name = c?.Name ?? $"{id:X8}", Job = c?.Job ?? 0 });
        }

        countdown = null;
        engaged.Clear();
        state = State.Active;
        observer = observers?.Create(World.ZoneId, pull, World);
    }

    private void EndPull(PullOutcome outcome, long endTicks, long endOffset)
    {
        if (pull == null)
        {
            state = State.Idle;
            return;
        }

        pull.Outcome = outcome;
        pull.EndTicks = Math.Max(endTicks, pull.StartTicks);
        pull.EndOffset = Math.Max(endOffset, pull.StartOffset);
        pull.InstanceId = World.InstanceId;
        pull.HasDirector = World.InstanceId != 0;
        pull.MapId = World.MapId != 0 ? World.MapId : pull.MapId;

        // Boss = engaged enemy with the largest max HP still present; otherwise the largest overall.
        CombatantState? boss = null;
        foreach (var e in engaged.Values)
        {
            var present = World.Combatants.ContainsKey(e.Id);
            if (boss == null || (present && !World.Combatants.ContainsKey(boss.Id)) ||
                (present == World.Combatants.ContainsKey(boss.Id) && e.MaxHp > boss.MaxHp))
                boss = e;
        }

        if (boss is { MaxHp: > 0 })
        {
            pull.BossName = boss.Name;
            pull.BossHpPct = Math.Clamp(100f * boss.Hp / boss.MaxHp, 0, 100);
        }

        observer?.Complete(pull);
        observer = null;
        Pulls.Add(pull);
        state = State.Tail;
        tailUntil = pull.EndTicks + TailLength;
        pull.TailEndOffset = pull.EndOffset;
    }

    private void FinishTail(long offset)
    {
        if (Pulls.Count > 0)
            Pulls[^1].TailEndOffset = Math.Max(Pulls[^1].TailEndOffset, offset);
        pull = null;
        state = State.Idle;
    }

    /// <summary>Call at end of input. Closes any open pull as in-progress / truncated.</summary>
    public void Finish(long endOffset)
    {
        if (state is State.Active or State.Ending && pull != null)
        {
            var end = state == State.Ending ? combatOffTicks : World.LastTicks;
            EndPull(state == State.Ending ? PullOutcome.CombatEnd : PullOutcome.InProgress, end, endOffset);
            Pulls[^1].EndTruncated = true;
        }

        if (state == State.Tail)
            FinishTail(endOffset);
    }

    /// <summary>The pull currently in progress (combat on, or within the combat-off grace period).</summary>
    public PullSummary? ActivePull => state is State.Active or State.Ending ? pull : null;

    /// <summary>The latest safe resume point (an idle checkpoint with no open pull), if any.</summary>
    public ResumeState? SafeResume => safe;
}
