namespace RaidReplay.Core.GameData;

/// <summary>Subset of the Lumina Action sheet relevant to AoE inference.</summary>
public sealed record ActionInfo(
    uint Id, string Name, byte CastType, float EffectRange, float XAxisModifier, string? OmenPath, bool TargetArea,
    int Cast100Ms, uint Icon, bool IsPlayerAction);

public sealed record StatusInfo(uint Id, string Name, uint Icon, byte MaxStacks, byte Category);

/// <summary>Role: 1 tank, 2 melee, 3 ranged, 4 healer (ClassJob.Role); RangedCaster distinguishes casters.</summary>
public sealed record JobInfo(uint Id, string Abbreviation, byte Role, bool Caster, uint Icon);

/// <summary>Map texture info. Texture pixel = (world + Offset) * SizeFactor / 100 + 1024 on a 2048 texture.</summary>
public sealed record MapInfo(uint Id, string MapKey, ushort SizeFactor, short OffsetX, short OffsetY, string Name)
{
    public string TexturePath => $"ui/map/{MapKey}/{MapKey.Replace("/", string.Empty)}_m.tex";
}

/// <summary>Game data lookups; implemented with Lumina in the plugin (and optionally the CLI).</summary>
public interface IGameData
{
    ActionInfo? GetAction(uint id);
    StatusInfo? GetStatus(uint id);
    JobInfo? GetJob(uint id);
    MapInfo? GetMap(uint id);

    /// <summary>Icon for waymark slot 0..7 (A, B, C, D, 1, 2, 3, 4).</summary>
    uint WaymarkIcon(int slot);

    /// <summary>Icon for a 29-line sign marker index.</summary>
    uint SignIcon(int marker);
}

/// <summary>Fallback with no game files: only a static job table.</summary>
public class NullGameData : IGameData
{
    public static readonly NullGameData Instance = new();

    public virtual ActionInfo? GetAction(uint id) => null;
    public virtual StatusInfo? GetStatus(uint id) => null;
    public virtual JobInfo? GetJob(uint id) => Jobs.Get(id);
    public virtual MapInfo? GetMap(uint id) => null;
    public virtual uint WaymarkIcon(int slot) => 0;
    public virtual uint SignIcon(int marker) => 0;
}

public static class Jobs
{
    private static readonly Dictionary<uint, JobInfo> Table = new()
    {
        [1] = new(1, "GLA", 1, false, 62101), [2] = new(2, "PGL", 2, false, 62102), [3] = new(3, "MRD", 1, false, 62103),
        [4] = new(4, "LNC", 2, false, 62104), [5] = new(5, "ARC", 3, false, 62105), [6] = new(6, "CNJ", 4, false, 62106),
        [7] = new(7, "THM", 3, true, 62107), [19] = new(19, "PLD", 1, false, 62119), [20] = new(20, "MNK", 2, false, 62120),
        [21] = new(21, "WAR", 1, false, 62121), [22] = new(22, "DRG", 2, false, 62122), [23] = new(23, "BRD", 3, false, 62123),
        [24] = new(24, "WHM", 4, false, 62124), [25] = new(25, "BLM", 3, true, 62125), [26] = new(26, "ACN", 3, true, 62126),
        [27] = new(27, "SMN", 3, true, 62127), [28] = new(28, "SCH", 4, false, 62128), [29] = new(29, "ROG", 2, false, 62129),
        [30] = new(30, "NIN", 2, false, 62130), [31] = new(31, "MCH", 3, false, 62131), [32] = new(32, "DRK", 1, false, 62132),
        [33] = new(33, "AST", 4, false, 62133), [34] = new(34, "SAM", 2, false, 62134), [35] = new(35, "RDM", 3, true, 62135),
        [36] = new(36, "BLU", 3, true, 62136), [37] = new(37, "GNB", 1, false, 62137), [38] = new(38, "DNC", 3, false, 62138),
        [39] = new(39, "RPR", 2, false, 62139), [40] = new(40, "SGE", 4, false, 62140), [41] = new(41, "VPR", 2, false, 62141),
        [42] = new(42, "PCT", 3, true, 62142),
    };

    public static JobInfo? Get(uint id) => Table.GetValueOrDefault(id);

    public static string Abbrev(uint id) => Table.TryGetValue(id, out var j) ? j.Abbreviation : id == 0 ? "?" : $"J{id}";

    /// <summary>Sort key: tanks, healers, melee, ranged, casters.</summary>
    public static int RoleOrder(uint id) => Table.TryGetValue(id, out var j)
                                                ? j.Role switch { 1 => 0, 4 => 1, 2 => 2, 3 when !j.Caster => 3, _ => 4 }
                                                : 9;
}
