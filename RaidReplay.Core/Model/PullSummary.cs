using System.Text.Json.Serialization;

namespace RaidReplay.Core.Model;

public enum PullOutcome : byte
{
    Unknown,
    Wipe,
    Clear,
    ZoneOut,
    CombatEnd,
    InProgress,
}

public sealed class PartyMember
{
    public uint Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public byte Job { get; set; }
}

public sealed class PhaseMark
{
    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public long Ticks { get; set; }
}

/// <summary>Index entry for one pull: where it is in the file and what happened, at a glance.</summary>
public sealed class PullSummary
{
    public string FilePath { get; set; } = string.Empty;
    public int Ordinal { get; set; }

    public long CheckpointOffset { get; set; }
    public long CountdownOffset { get; set; } = -1;
    public long StartOffset { get; set; }
    public long EndOffset { get; set; }
    public long TailEndOffset { get; set; }

    public long CountdownTicks { get; set; }
    public int CountdownSeconds { get; set; }
    public long StartTicks { get; set; }
    public long EndTicks { get; set; }

    public uint ZoneId { get; set; }
    public string ZoneName { get; set; } = string.Empty;
    public int MapId { get; set; }
    public uint InstanceId { get; set; }
    public bool HasDirector { get; set; }

    public PullOutcome Outcome { get; set; }
    public bool StartTruncated { get; set; }
    public bool EndTruncated { get; set; }

    public List<PartyMember> Party { get; set; } = [];
    public int Deaths { get; set; }
    public long FirstDeathTicks { get; set; }
    public string? BossName { get; set; }
    public float BossHpPct { get; set; } = -1;

    public string? EncounterKey { get; set; }
    public List<PhaseMark> Phases { get; set; } = [];

    public WorldSnapshot Checkpoint { get; set; } = new();

    [JsonIgnore] public string Key => $"{Path.GetFileName(FilePath)}@{StartOffset}";
    [JsonIgnore] public int DurationMs => (int)((EndTicks - StartTicks) / TimeSpan.TicksPerMillisecond);
    [JsonIgnore] public DateTime StartLocal => new DateTime(StartTicks, DateTimeKind.Utc).ToLocalTime();
    [JsonIgnore] public string? FurthestPhase => Phases.Count > 0 ? Phases[^1].Name : null;
}
