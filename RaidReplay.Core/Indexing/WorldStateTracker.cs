using RaidReplay.Core.Model;
using RaidReplay.Core.Parsing;

namespace RaidReplay.Core.Indexing;

/// <summary>
/// Maintains the "current world" while streaming a log: zone, map, party, waymarks, signs and the live
/// combatant roster with last-known position/HP. Shared by the indexer and the pull loader so a pull
/// can be loaded by seeking to a checkpoint and restoring a <see cref="WorldSnapshot"/>.
/// </summary>
public sealed class WorldStateTracker
{
    /// <summary>Line types the tracker consumes.</summary>
    public static readonly int[] Types =
    [
        LineType.ChangeZone, LineType.ChangePrimaryPlayer, LineType.AddCombatant, LineType.RemoveCombatant,
        LineType.PartyList, LineType.Waymark, LineType.SignMarker, LineType.Director, LineType.Map,
        LineType.CombatantMemory, LineType.ActorMove, LineType.ActorSetPos, LineType.Ability, LineType.AoeAbility,
        LineType.UpdateHp,
    ];

    public const float MaxCoord = 3000f;

    private readonly WaymarkState?[] waymarks = new WaymarkState?[8];

    public Dictionary<uint, CombatantState> Combatants { get; } = new();
    public Dictionary<int, uint> Signs { get; } = new();
    public uint ZoneId { get; private set; }
    public string ZoneName { get; private set; } = string.Empty;
    public int MapId { get; private set; }
    public string MapName { get; private set; } = string.Empty;
    public uint InstanceId { get; private set; }
    public uint PrimaryPlayerId { get; private set; }
    public List<uint> Party { get; private set; } = [];
    public long LastTicks { get; private set; }

    /// <summary>When the current zone was entered (its ChangeZone line).</summary>
    public long EnteredTicks { get; private set; }

    /// <summary>When the duty commenced (director 40000001), 0 if it hasn't in this zone.</summary>
    public long DutyStartTicks { get; private set; }

    /// <summary>The duty's time limit in seconds (e.g. 7200 for an Ultimate), 0 if unknown.</summary>
    public int DutyLimitS { get; private set; }

    public ReadOnlySpan<WaymarkState?> Waymarks => waymarks;

    public StringPool Strings { get; } = new();

    public static bool ValidCoord(float v) => !float.IsNaN(v) && Math.Abs(v) < MaxCoord;

    public void Apply(int type, scoped in LineFields f, long ticks)
    {
        if (ticks > LastTicks)
            LastTicks = ticks;

        switch (type)
        {
            case LineType.ChangeZone:
                ZoneId = f.Hex(F01.ZoneId);
                ZoneName = f.Str(F01.ZoneName, Strings);
                Combatants.Clear();
                Signs.Clear();
                Array.Clear(waymarks);
                InstanceId = 0;
                EnteredTicks = ticks;
                DutyStartTicks = 0;
                DutyLimitS = 0;
                break;
            case LineType.ChangePrimaryPlayer:
                PrimaryPlayerId = f.Hex(F02.Id);
                break;
            case LineType.AddCombatant:
                ApplyAdd(f, ticks);
                break;
            case LineType.RemoveCombatant:
                Combatants.Remove(f.Hex(F03.Id));
                break;
            case LineType.PartyList:
            {
                var count = f.Int(F11.Count);
                var party = new List<uint>(count);
                for (var i = 0; i < count && f.Has(F11.FirstId + i); i++)
                {
                    var id = f.Hex(F11.FirstId + i);
                    if (id != 0)
                        party.Add(id);
                }

                Party = party;
                break;
            }
            case LineType.Waymark:
            {
                var slot = f.Int(F28.Slot);
                if ((uint)slot >= 8)
                    break;
                if (f.Is(F28.Op, "Add"u8))
                {
                    waymarks[slot] = new WaymarkState
                    {
                        Slot = slot, X = f.Float(F28.X), Y = f.Float(F28.Y), Z = f.Float(F28.Z),
                    };
                }
                else
                {
                    waymarks[slot] = null;
                }

                break;
            }
            case LineType.SignMarker:
            {
                var marker = f.Int(F29.Marker);
                if (f.Is(F29.Op, "Add"u8))
                    Signs[marker] = f.Hex(F29.TargetId);
                else
                    Signs.Remove(marker);
                break;
            }
            case LineType.Director:
                InstanceId = f.Hex(F33.Instance);
                // Duty commence: its first parameter is the time limit in seconds.
                if (f.Hex(F33.Command) == 0x40000001)
                {
                    DutyStartTicks = ticks;
                    DutyLimitS = (int)f.Hex(F33.P1);
                }

                break;
            case LineType.Map:
                MapId = f.Int(F40.MapId);
                MapName = f.Str(F40.Place, Strings);
                break;
            case LineType.CombatantMemory:
                ApplyMemory(f, ticks);
                break;
            case LineType.ActorMove:
            case LineType.ActorSetPos:
                SetPos(f.Hex(F270.Id), f.Float(F270.X), f.Float(F270.Y), f.Float(F270.Z), f.Float(F270.Heading), ticks);
                break;
            case LineType.Ability:
            case LineType.AoeAbility:
            {
                var tgt = f.Hex(F21.TargetId);
                if (tgt != 0 && tgt != 0xE0000000 && Combatants.TryGetValue(tgt, out var t))
                {
                    if (!f.IsEmpty(F21.TargetMaxHp))
                    {
                        t.Hp = f.Int(F21.TargetHp);
                        var max = f.Int(F21.TargetMaxHp);
                        if (max > 0)
                            t.MaxHp = max;
                    }

                    SetPos(t, f.Float(F21.TargetX), f.Float(F21.TargetY), f.Float(F21.TargetZ),
                           f.Float(F21.TargetHeading), ticks);
                }

                if (Combatants.TryGetValue(f.Hex(F21.SourceId), out var s))
                {
                    SetPos(s, f.Float(F21.SourceX), f.Float(F21.SourceY), f.Float(F21.SourceZ),
                           f.Float(F21.SourceHeading), ticks);
                }

                break;
            }
            case LineType.UpdateHp:
                if (Combatants.TryGetValue(f.Hex(F39.Id), out var c))
                {
                    c.Hp = f.Int(F39.Hp);
                    var max = f.Int(F39.MaxHp);
                    if (max > 0)
                        c.MaxHp = max;
                    SetPos(c, f.Float(F39.X), f.Float(F39.Y), f.Float(F39.Z), f.Float(F39.Heading), ticks);
                }

                break;
        }
    }

    private void ApplyAdd(scoped in LineFields f, long ticks)
    {
        var id = f.Hex(F03.Id);
        if (!Combatants.TryGetValue(id, out var c))
        {
            c = new CombatantState { Id = id };
            Combatants[id] = c;
        }

        c.Name = f.Str(F03.Name, Strings);
        c.Job = (byte)f.Hex(F03.Job);
        c.Level = (byte)f.Hex(F03.Level);
        c.OwnerId = f.Hex(F03.OwnerId);
        c.WorldId = (ushort)f.Hex(F03.WorldId);
        c.World = f.IsEmpty(F03.World) ? null : f.Str(F03.World, Strings);
        c.BNpcNameId = (uint)f.Long(F03.NpcNameId);
        c.BNpcBaseId = (uint)f.Long(F03.NpcBaseId);
        c.Hp = f.Int(F03.Hp);
        c.MaxHp = f.Int(F03.MaxHp);
        if (c.ObjectType == 0)
            c.ObjectType = c.IsPlayer ? (byte)1 : (byte)2;
        SetPos(c, f.Float(F03.X), f.Float(F03.Y), f.Float(F03.Z), f.Float(F03.Heading), ticks);
    }

    private void ApplyMemory(scoped in LineFields f, long ticks)
    {
        var op = f[F261.Op];
        var id = f.Hex(F261.Id);
        if (op.SequenceEqual("Remove"u8))
        {
            Combatants.Remove(id);
            return;
        }

        var isAdd = op.SequenceEqual("Add"u8);
        if (!Combatants.TryGetValue(id, out var c))
        {
            if (!isAdd)
                return;
            c = new CombatantState { Id = id };
            Combatants[id] = c;
        }

        var mem = new MemoryFields();
        mem.Read(f, Strings);
        mem.ApplyTo(c, isAdd, ticks);
    }

    private void SetPos(uint id, float x, float y, float z, float h, long ticks)
    {
        if (Combatants.TryGetValue(id, out var c))
            SetPos(c, x, y, z, h, ticks);
    }

    private static void SetPos(CombatantState c, float x, float y, float z, float h, long ticks)
    {
        if (!ValidCoord(x) || !ValidCoord(y))
            return;
        c.X = x;
        c.Y = y;
        if (ValidCoord(z))
            c.Z = z;
        if (!float.IsNaN(h) && Math.Abs(h) <= 4f)
            c.Heading = h;
        c.PosTicks = ticks;
    }

    public WorldSnapshot Snapshot()
    {
        var snap = new WorldSnapshot
        {
            Ticks = LastTicks,
            ZoneId = ZoneId,
            ZoneName = ZoneName,
            MapId = MapId,
            MapName = MapName,
            InstanceId = InstanceId,
            PrimaryPlayerId = PrimaryPlayerId,
            Party = [..Party],
            EnteredTicks = EnteredTicks,
            DutyStartTicks = DutyStartTicks,
            DutyLimitS = DutyLimitS,
        };
        foreach (var w in waymarks)
        {
            if (w != null)
                snap.Waymarks.Add(new WaymarkState { Slot = w.Slot, X = w.X, Y = w.Y, Z = w.Z });
        }

        foreach (var (marker, target) in Signs)
            snap.Signs.Add(new SignState { Marker = marker, TargetId = target });
        foreach (var c in Combatants.Values)
            snap.Combatants.Add(c.Clone());
        return snap;
    }

    public void Restore(WorldSnapshot s)
    {
        LastTicks = s.Ticks;
        ZoneId = s.ZoneId;
        ZoneName = s.ZoneName;
        MapId = s.MapId;
        MapName = s.MapName;
        InstanceId = s.InstanceId;
        PrimaryPlayerId = s.PrimaryPlayerId;
        Party = [..s.Party];
        EnteredTicks = s.EnteredTicks;
        DutyStartTicks = s.DutyStartTicks;
        DutyLimitS = s.DutyLimitS;
        Array.Clear(waymarks);
        foreach (var w in s.Waymarks)
        {
            if ((uint)w.Slot < 8)
                waymarks[w.Slot] = new WaymarkState { Slot = w.Slot, X = w.X, Y = w.Y, Z = w.Z };
        }

        Signs.Clear();
        foreach (var sign in s.Signs)
            Signs[sign.Marker] = sign.TargetId;
        Combatants.Clear();
        foreach (var c in s.Combatants)
            Combatants[c.Id] = c.Clone();
    }
}

/// <summary>Decoded key/value pairs of a 261 CombatantMemory line (only the keys we use).</summary>
public struct MemoryFields
{
    public string? Name;
    public int? Job, Level, Type, MaxHp, WorldId;
    public uint? BNpcId, BNpcNameId, OwnerId, ModelStatus;
    public float? X, Y, Z, Heading, Radius;
    public float? GroundX, GroundY, GroundZ;

    public void Read(scoped in LineFields f, StringPool pool)
    {
        // Pairs run from FirstKey up to (excluding) the trailing checksum field.
        for (var i = F261.FirstKey; i + 1 < f.Count - 1; i += 2)
        {
            var key = f[i];
            var val = f[i + 1];
            if (key.IsEmpty)
                continue;
            switch (key[0])
            {
                case (byte)'P':
                    if (key.SequenceEqual("PosX"u8))
                        X = Coord(val);
                    else if (key.SequenceEqual("PosY"u8))
                        Y = Coord(val);
                    else if (key.SequenceEqual("PosZ"u8))
                        Z = Coord(val);
                    break;
                case (byte)'H':
                    if (key.SequenceEqual("Heading"u8))
                    {
                        var h = Utf8Num.Float(val);
                        if (!float.IsNaN(h) && Math.Abs(h) <= 4f)
                            Heading = h;
                    }

                    break;
                case (byte)'N':
                    if (key.SequenceEqual("Name"u8))
                        Name = pool.Get(val);
                    break;
                case (byte)'J':
                    if (key.SequenceEqual("Job"u8))
                        Job = Utf8Num.Int(val);
                    break;
                case (byte)'L':
                    if (key.SequenceEqual("Level"u8))
                        Level = Utf8Num.Int(val);
                    break;
                case (byte)'T':
                    if (key.SequenceEqual("Type"u8))
                        Type = Utf8Num.Int(val);
                    break;
                case (byte)'M':
                    if (key.SequenceEqual("MaxHP"u8))
                        MaxHp = Utf8Num.Int(val);
                    else if (key.SequenceEqual("ModelStatus"u8))
                        ModelStatus = (uint)Utf8Num.Long(val);
                    break;
                case (byte)'B':
                    if (key.SequenceEqual("BNpcID"u8))
                        BNpcId = Utf8Num.Hex(val);
                    else if (key.SequenceEqual("BNpcNameID"u8))
                        BNpcNameId = Utf8Num.Hex(val);
                    break;
                case (byte)'O':
                    if (key.SequenceEqual("OwnerID"u8))
                        OwnerId = Utf8Num.Hex(val);
                    break;
                case (byte)'R':
                    if (key.SequenceEqual("Radius"u8))
                    {
                        var r = Utf8Num.Float(val);
                        if (r is >= 0 and < 100)
                            Radius = r;
                    }

                    break;
                case (byte)'W':
                    if (key.SequenceEqual("WorldID"u8))
                        WorldId = Utf8Num.Int(val);
                    break;
                case (byte)'C':
                    if (key.SequenceEqual("CastGroundTargetX"u8))
                        GroundX = Coord(val);
                    else if (key.SequenceEqual("CastGroundTargetY"u8))
                        GroundY = Coord(val);
                    else if (key.SequenceEqual("CastGroundTargetZ"u8))
                        GroundZ = Coord(val);
                    break;
            }
        }
    }

    private static float? Coord(ReadOnlySpan<byte> s)
    {
        var v = Utf8Num.Float(s);
        return WorldStateTracker.ValidCoord(v) ? v : null;
    }

    public readonly bool HasPosition => X.HasValue || Y.HasValue || Z.HasValue || Heading.HasValue;

    /// <summary>Applies to a combatant. For Add lines missing position keys mean zero.</summary>
    public readonly void ApplyTo(CombatantState c, bool isAdd, long ticks)
    {
        if (Name != null)
            c.Name = Name;
        if (Job.HasValue)
            c.Job = (byte)Job.Value;
        if (Level.HasValue && Level.Value is > 0 and <= 200)
            c.Level = (byte)Level.Value;
        if (Type.HasValue)
            c.ObjectType = (byte)Type.Value;
        if (MaxHp.HasValue && MaxHp.Value > 0)
            c.MaxHp = MaxHp.Value;
        if (WorldId.HasValue)
            c.WorldId = (ushort)WorldId.Value;
        if (BNpcId.HasValue && c.BNpcBaseId == 0)
            c.BNpcBaseId = BNpcId.Value;
        if (OwnerId.HasValue && OwnerId.Value != 0xE0000000)
            c.OwnerId = OwnerId.Value;
        if (Radius.HasValue)
            c.Radius = Radius.Value;
        if (ModelStatus.HasValue)
            c.ModelStatus = ModelStatus.Value;
        else if (isAdd)
            c.ModelStatus = 0;

        if (isAdd)
        {
            c.X = X ?? 0;
            c.Y = Y ?? 0;
            c.Z = Z ?? 0;
            c.Heading = Heading ?? 0;
            c.PosTicks = ticks;
        }
        else if (HasPosition)
        {
            if (X.HasValue)
                c.X = X.Value;
            if (Y.HasValue)
                c.Y = Y.Value;
            if (Z.HasValue)
                c.Z = Z.Value;
            if (Heading.HasValue)
                c.Heading = Heading.Value;
            c.PosTicks = ticks;
        }
    }
}
