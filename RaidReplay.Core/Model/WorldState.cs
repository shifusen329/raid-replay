namespace RaidReplay.Core.Model;

/// <summary>Last-known state of one combatant (players, NPCs, event objects).</summary>
public sealed class CombatantState
{
    public uint Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public byte Job { get; set; }
    public byte Level { get; set; }
    public uint OwnerId { get; set; }
    public ushort WorldId { get; set; }
    public string? World { get; set; }
    public uint BNpcNameId { get; set; }
    public uint BNpcBaseId { get; set; }

    /// <summary>Dalamud ObjectKind (1 player, 2 battle npc, 3 event npc, 7 event object, ...). 0 = unknown.</summary>
    public byte ObjectType { get; set; }

    public int Hp { get; set; }
    public int MaxHp { get; set; }
    public float X { get; set; }
    public float Y { get; set; }
    public float Z { get; set; }
    public float Heading { get; set; }
    public float Radius { get; set; }
    public uint ModelStatus { get; set; }

    /// <summary>Ticks of the last position update (0 = unknown).</summary>
    public long PosTicks { get; set; }

    [System.Text.Json.Serialization.JsonIgnore]
    public bool IsPlayer => (Id >> 28) == 1;

    public CombatantState Clone() => (CombatantState)MemberwiseClone();
}

public sealed class WaymarkState
{
    public int Slot { get; set; }
    public float X { get; set; }
    public float Y { get; set; }
    public float Z { get; set; }
}

public sealed class SignState
{
    public int Marker { get; set; }
    public uint TargetId { get; set; }
}

/// <summary>Everything needed to resume parsing mid-file without rescanning from the zone-in.</summary>
public sealed class WorldSnapshot
{
    public long Ticks { get; set; }
    public uint ZoneId { get; set; }
    public string ZoneName { get; set; } = string.Empty;
    public int MapId { get; set; }
    public string MapName { get; set; } = string.Empty;
    public uint InstanceId { get; set; }
    public uint PrimaryPlayerId { get; set; }
    public List<uint> Party { get; set; } = [];
    public long EnteredTicks { get; set; }
    public long DutyStartTicks { get; set; }
    public int DutyLimitS { get; set; }
    public List<WaymarkState> Waymarks { get; set; } = [];
    public List<SignState> Signs { get; set; } = [];
    public List<CombatantState> Combatants { get; set; } = [];
}
