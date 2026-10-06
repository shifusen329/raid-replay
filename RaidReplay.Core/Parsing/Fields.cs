namespace RaidReplay.Core.Parsing;

// Field indices (0 = type, 1 = timestamp) for the line types we decode. Verified against
// FFXIV_ACT_Plugin 3.0.x / OverlayPlugin logs.

public static class F01 { public const int ZoneId = 2, ZoneName = 3; } // zone id hex

public static class F02 { public const int Id = 2, Name = 3; }

public static class F03
{
    // job/level/world are hex; npcNameId/npcBaseId/hp are decimal.
    public const int Id = 2, Name = 3, Job = 4, Level = 5, OwnerId = 6, WorldId = 7, World = 8, NpcNameId = 9,
                     NpcBaseId = 10, Hp = 11, MaxHp = 12, Mp = 13, MaxMp = 14, X = 17, Y = 18, Z = 19, Heading = 20;
}

public static class F11 { public const int Count = 2, FirstId = 3; }

public static class F20
{
    public const int SourceId = 2, SourceName = 3, ActionId = 4, ActionName = 5, TargetId = 6, TargetName = 7,
                     CastTime = 8, X = 9, Y = 10, Z = 11, Heading = 12;
}

public static class F21
{
    public const int SourceId = 2, SourceName = 3, ActionId = 4, ActionName = 5, TargetId = 6, TargetName = 7,
                     FirstEffect = 8, EffectPairs = 8,
                     TargetHp = 24, TargetMaxHp = 25, TargetX = 30, TargetY = 31, TargetZ = 32, TargetHeading = 33,
                     SourceHp = 34, SourceMaxHp = 35, SourceX = 40, SourceY = 41, SourceZ = 42, SourceHeading = 43,
                     Sequence = 44, TargetIndex = 45, TargetCount = 46,
                     // FFXIV_ACT_Plugin 3.x extras
                     OwnerId = 47, OwnerName = 48, EffectDisplayType = 49, ActionIdEx = 50, AnimationId = 51,
                     AnimationLock = 52, Rotation = 53;
}

public static class F23 { public const int SourceId = 2, SourceName = 3, ActionId = 4, ActionName = 5, Reason = 6; }

public static class F24
{
    // Actor at field 2 is the one ticking (target); source at 17.
    public const int TargetId = 2, TargetName = 3, Which = 4, EffectId = 5, Amount = 6, TargetHp = 7, TargetMaxHp = 8,
                     TargetX = 13, TargetY = 14, TargetZ = 15, TargetHeading = 16, SourceId = 17, SourceName = 18,
                     DamageType = 19, SourceHp = 20, SourceMaxHp = 21, SourceX = 26, SourceY = 27, SourceZ = 28,
                     SourceHeading = 29;
}

public static class F25 { public const int TargetId = 2, TargetName = 3, SourceId = 4, SourceName = 5; }

public static class F26
{
    // Same layout for 30 (StatusRemove).
    public const int StatusId = 2, StatusName = 3, Duration = 4, SourceId = 5, SourceName = 6, TargetId = 7,
                     TargetName = 8, Stacks = 9, TargetMaxHp = 10, SourceMaxHp = 11;
}

public static class F27 { public const int TargetId = 2, TargetName = 3, MarkerId = 6, Data0 = 7; }

public static class F28 { public const int Op = 2, Slot = 3, PlacerId = 4, PlacerName = 5, X = 6, Y = 7, Z = 8; }

public static class F29 { public const int Op = 2, Marker = 3, PlacerId = 4, PlacerName = 5, TargetId = 6, TargetName = 7; }

public static class F33 { public const int Instance = 2, Command = 3, P1 = 4, P2 = 5, P3 = 6, P4 = 7; }

public static class F34 { public const int Id = 2, Name = 3, TargetId = 4, TargetName = 5, Toggle = 6; }

public static class F35 { public const int SourceId = 2, SourceName = 3, TargetId = 4, TargetName = 5, TetherId = 8; }

public static class F37
{
    public const int Id = 2, Name = 3, Sequence = 4, Hp = 5, MaxHp = 6, Mp = 7, MaxMp = 8, Shield = 9, X = 11, Y = 12,
                     Z = 13, Heading = 14;
}

public static class F38 { public const int Id = 2, Name = 3, Hp = 5, MaxHp = 6, X = 11, Y = 12, Z = 13, Heading = 14; }

public static class F39 { public const int Id = 2, Name = 3, Hp = 4, MaxHp = 5, X = 10, Y = 11, Z = 12, Heading = 13; }

public static class F40 { public const int MapId = 2, Region = 3, Place = 4, SubPlace = 5; } // map id decimal

public static class F257 { public const int Instance = 2, Flags = 3, Location = 4; }

public static class F260 { public const int InActCombat = 2, InGameCombat = 3, ActChanged = 4, GameChanged = 5; }

public static class F261 { public const int Op = 2, Id = 3, FirstKey = 4; }

public static class F263 { public const int SourceId = 2, ActionId = 3, X = 4, Y = 5, Z = 6, Heading = 7; }

public static class F264
{
    public const int SourceId = 2, ActionId = 3, Sequence = 4, HasLocation = 5, X = 6, Y = 7, Z = 8, Heading = 9,
                     AnimationTarget = 10;
}

public static class F266 { public const int NpcId = 2, NameId = 3, YellId = 4; }

public static class F267 { public const int NpcId = 2, Instance = 3, NameId = 4, TextId = 5, Duration = 6; }

public static class F268 { public const int SourceId = 2, WorldId = 3, Seconds = 4, Result = 5, Name = 6; }

public static class F269 { public const int SourceId = 2, WorldId = 3, Name = 4; }

public static class F270 { public const int Id = 2, Heading = 3, Unk1 = 4, Unk2 = 5, X = 6, Y = 7, Z = 8; }

public static class F272 { public const int Id = 2, ParentId = 3, TetherId = 4, AnimationState = 5; }

public static class F273 { public const int Id = 2, Category = 3, P1 = 4, P2 = 5, P3 = 6, P4 = 7; }

/// <summary>Decodes the 8 effect (flags,value) pairs on 21/22 lines.</summary>
public static class Effects
{
    public const byte Miss = 0x01;
    public const byte FullResist = 0x02;
    public const byte Damage = 0x03;
    public const byte Heal = 0x04;
    public const byte Blocked = 0x05;
    public const byte Parried = 0x06;
    public const byte Invulnerable = 0x07;
    public const byte NoEffect = 0x08;
    public const byte MpLoss = 0x0A;
    public const byte MpGain = 0x0B;
    public const byte StatusToTarget = 0x0E;
    public const byte StatusToSource = 0x0F;
    public const byte StatusRemoved = 0x10;
    public const byte Knockback = 0x1F;
    public const byte Draw = 0x20;
    public const byte InstantDeath = 0x33;

    public static byte Type(uint flags) => (byte)(flags & 0xFF);

    /// <summary>Damage/heal amount; values over 65535 are encoded with the 0x4000 bit and a high byte in the low byte.</summary>
    public static int Amount(uint value) =>
        (value & 0x4000) != 0 ? (int)(((value & 0xFF) << 16) | (value >> 16)) : (int)(value >> 16);

    public static bool IsDamage(byte type) => type is Damage or Blocked or Parried;

    // Best-effort; not verified against a reference.
    public static bool IsCrit(uint flags) => (flags & 0x2000) != 0;

    public static bool IsDirectHit(uint flags) => (flags & 0x4000) != 0;
}

/// <summary>Well-known 33 (director) commands.</summary>
public static class DirectorCommand
{
    public const uint Commence = 0x40000001;
    public const uint Victory = 0x40000003;
    public const uint Wipe = 0x40000005;
    public const uint Recommence = 0x40000006;
    public const uint FadeOut = 0x4000000F;
    public const uint FadeIn = 0x40000010;
    public const uint BarrierUp = 0x40000012;
    public const uint FadeStart = 0x40000013;
    public const uint FadeEnd = 0x40000011;
}
