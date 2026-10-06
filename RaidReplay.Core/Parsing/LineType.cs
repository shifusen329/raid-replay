namespace RaidReplay.Core.Parsing;

/// <summary>ACT / OverlayPlugin network log line type ids (first field of each line).</summary>
public static class LineType
{
    public const int Chat = 0;
    public const int ChangeZone = 1;
    public const int ChangePrimaryPlayer = 2;
    public const int AddCombatant = 3;
    public const int RemoveCombatant = 4;
    public const int PartyList = 11;
    public const int PlayerStats = 12;
    public const int StartsCasting = 20;
    public const int Ability = 21;
    public const int AoeAbility = 22;
    public const int CancelAbility = 23;
    public const int DoTHoT = 24;
    public const int Death = 25;
    public const int StatusAdd = 26;
    public const int HeadMarker = 27;
    public const int Waymark = 28;
    public const int SignMarker = 29;
    public const int StatusRemove = 30;
    public const int Gauge = 31;
    public const int Director = 33;
    public const int NameToggle = 34;
    public const int Tether = 35;
    public const int LimitBreak = 36;
    public const int EffectResult = 37;
    public const int StatusList = 38;
    public const int UpdateHp = 39;
    public const int Map = 40;
    public const int SystemLogMessage = 41;
    public const int StatusList3 = 42;
    public const int Version = 253;
    public const int MapEffect = 257;
    public const int FateDirector = 258;
    public const int CeDirector = 259;
    public const int InCombat = 260;
    public const int CombatantMemory = 261;
    public const int RsvData = 262;
    public const int StartsUsingExtra = 263;
    public const int AbilityExtra = 264;
    public const int ContentFinderSettings = 265;
    public const int NpcYell = 266;
    public const int BattleTalk2 = 267;
    public const int Countdown = 268;
    public const int CountdownCancel = 269;
    public const int ActorMove = 270;
    public const int ActorSetPos = 271;
    public const int SpawnNpcExtra = 272;
    public const int ActorControlExtra = 273;
    public const int ActorControlSelfExtra = 274;

    public const int MaxType = 300;

    /// <summary>Parses the leading decimal type id up to the first '|'. Returns -1 if malformed.</summary>
    public static int Parse(ReadOnlySpan<byte> line)
    {
        var value = 0;
        for (var i = 0; i < line.Length && i < 4; i++)
        {
            var c = line[i];
            if (c == (byte)'|')
                return i == 0 ? -1 : value;
            var d = c - (byte)'0';
            if ((uint)d > 9)
                return -1;
            value = (value * 10) + d;
        }

        return -1;
    }
}

/// <summary>Fixed-size membership set of line types used to gate parsing cheaply.</summary>
public sealed class LineTypeSet
{
    private readonly bool[] flags = new bool[LineType.MaxType];

    public LineTypeSet(params int[] types)
    {
        foreach (var t in types)
            Add(t);
    }

    public static LineTypeSet All
    {
        get
        {
            var set = new LineTypeSet();
            for (var i = 0; i < LineType.MaxType; i++)
                set.flags[i] = true;
            return set;
        }
    }

    public void Add(int type)
    {
        if ((uint)type < LineType.MaxType)
            flags[type] = true;
    }

    public void Remove(int type)
    {
        if ((uint)type < LineType.MaxType)
            flags[type] = false;
    }

    public void AddRange(IEnumerable<int> types)
    {
        foreach (var t in types)
            Add(t);
    }

    public bool Contains(int type) => (uint)type < LineType.MaxType && flags[type];

    public LineTypeSet Union(LineTypeSet other)
    {
        var set = new LineTypeSet();
        for (var i = 0; i < LineType.MaxType; i++)
            set.flags[i] = flags[i] || other.flags[i];
        return set;
    }
}
