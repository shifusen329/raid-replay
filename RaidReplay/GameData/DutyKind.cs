using System;
using Dalamud.Game;
using Lumina.Excel.Sheets;

namespace RaidReplay.GameData;

/// <summary>Kinds of duty, for choosing after which pulls the wipe report opens by itself.</summary>
[Flags]
public enum DutyKind
{
    None = 0,
    NormalRaid = 1 << 0,
    SavageRaid = 1 << 1,
    ExtremeTrial = 1 << 2,
    UltimateRaid = 1 << 3,
    NormalTrial = 1 << 4,
    AllianceRaid = 1 << 5,
    Dungeon = 1 << 6,
    Other = 1 << 7,
}

public static class DutyKinds
{
    /// <summary>The fights people prog: on by default.</summary>
    public const DutyKind Default = DutyKind.NormalRaid | DutyKind.SavageRaid | DutyKind.ExtremeTrial | DutyKind.UltimateRaid;

    /// <summary>Every kind with its settings label and tooltip, in display order.</summary>
    public static readonly (DutyKind Kind, string Label, string Tip)[] All =
    [
        (DutyKind.NormalRaid, "Normal raids", "8-player raids that aren't Savage."),
        (DutyKind.SavageRaid, "Savage raids", ""),
        (DutyKind.ExtremeTrial, "Extreme and unreal trials", "Includes the Minstrel's Ballad trials."),
        (DutyKind.UltimateRaid, "Ultimate raids", ""),
        (DutyKind.NormalTrial, "Normal trials", "Includes the old (Hard) trials."),
        (DutyKind.AllianceRaid, "Alliance raids", "24-player raids, including Chaotic."),
        (DutyKind.Dungeon, "Dungeons", ""),
        (DutyKind.Other, "Other duties", "Variant and criterion dungeons, deep dungeons, field operations, guildhests, quest battles..."),
    ];

    /// <summary>
    /// The kind of duty a zone is. The game data has no flag for Savage, Extreme or Unreal, so those are told apart by
    /// the duty's English name, which works whatever the client's language.
    /// </summary>
    public static DutyKind Of(uint territory)
    {
        if (Plugin.DataManager.GetExcelSheet<TerritoryType>().GetRowOrDefault(territory)?.ContentFinderCondition.ValueNullable
            is not { RowId: > 0 } duty)
            return DutyKind.Other;
        var name = Plugin.DataManager.GetExcelSheet<ContentFinderCondition>(ClientLanguage.English).GetRowOrDefault(duty.RowId)?.Name
                         .ToString() ?? string.Empty;
        var alliance = duty.ContentMemberType.ValueNullable?.PartyCount > 1;
        return duty.ContentType.RowId switch
        {
            28 => DutyKind.UltimateRaid,
            5 or 37 when alliance => DutyKind.AllianceRaid,
            5 => name.Contains("(Savage)", StringComparison.Ordinal) ? DutyKind.SavageRaid : DutyKind.NormalRaid,
            4 => name.Contains("(Extreme)", StringComparison.Ordinal) || name.Contains("(Unreal)", StringComparison.Ordinal) ||
                 name.StartsWith("the Minstrel's Ballad", StringComparison.OrdinalIgnoreCase)
                     ? DutyKind.ExtremeTrial
                     : DutyKind.NormalTrial,
            2 => DutyKind.Dungeon,
            _ => DutyKind.Other,
        };
    }
}
