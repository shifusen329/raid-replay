using System.Numerics;

namespace RaidReplay.Core.Model;

public enum ActorKind : byte
{
    Player,
    Pet,
    Boss,
    Enemy,
    Helper,
    EventObject,
    Other,
}

public readonly record struct MsSpan(int Start, int End)
{
    public bool Contains(int t) => t >= Start && t < End;
}

/// <summary>One combatant instance during a pull (an id that despawns and respawns becomes a new actor).</summary>
public sealed class Actor
{
    public int Index { get; internal set; }
    public uint Id { get; init; }
    public int Generation { get; init; }
    public string Name { get; set; } = string.Empty;
    public ActorKind Kind { get; set; } = ActorKind.Other;
    public byte Job { get; set; }
    public byte Level { get; set; }
    public uint OwnerId { get; set; }
    public uint BNpcNameId { get; set; }
    public uint BNpcBaseId { get; set; }
    public byte ObjectType { get; set; }
    public int MaxHp { get; set; }
    public float Radius { get; set; }
    public int SpawnMs { get; set; } = int.MinValue;
    public int DespawnMs { get; set; } = int.MaxValue;

    public PositionTrack Track { get; set; } = PositionTrack.Empty;
    public HpTrack Hp { get; set; } = HpTrack.Empty;
    public List<MsSpan> HiddenSpans { get; } = [];
    public List<MsSpan> UntargetableSpans { get; } = [];

    /// <summary>Label from an encounter pack (e.g. "Forsaken Kefka").</summary>
    public string? Label { get; set; }

    /// <summary>Pack role string (boss, helper, clone, emitter, eobj, ...).</summary>
    public string? Role { get; set; }

    public bool Render { get; set; } = true;
    public string? Conf { get; set; }

    public bool IsPlayer => (Id >> 28) == 1;
    public string DisplayName => Label ?? Name;

    public bool IsPresent(int t) => t >= SpawnMs && t < DespawnMs;

    public bool IsHidden(int t)
    {
        foreach (var s in HiddenSpans)
        {
            if (s.Contains(t))
                return true;
        }

        return false;
    }

    public bool IsTargetable(int t)
    {
        foreach (var s in UntargetableSpans)
        {
            if (s.Contains(t))
                return false;
        }

        return true;
    }

    public override string ToString() => $"{DisplayName} ({Id:X8}{(Generation > 0 ? $"#{Generation}" : "")})";
}

[Flags]
public enum PosFlags : byte
{
    None = 0,

    /// <summary>Do not interpolate from the previous sample into this one.</summary>
    Teleport = 1,
}

/// <summary>Time-ordered position/heading samples with interpolation.</summary>
public sealed class PositionTrack
{
    public static readonly PositionTrack Empty = new([], [], [], [], [], []);

    /// <summary>Interpolation window used when consecutive samples are far apart in time.</summary>
    public const int MaxMoveMs = 1000;

    public PositionTrack(int[] t, float[] x, float[] y, float[] z, float[] h, PosFlags[] flags)
    {
        T = t;
        X = x;
        Y = y;
        Z = z;
        H = h;
        Flags = flags;
    }

    public int[] T { get; }
    public float[] X { get; }
    public float[] Y { get; }
    public float[] Z { get; }
    public float[] H { get; }
    public PosFlags[] Flags { get; }
    public int Count => T.Length;

    /// <summary>Index of the last sample at or before t (-1 if none).</summary>
    public int Floor(int t)
    {
        int lo = 0, hi = T.Length - 1, ans = -1;
        while (lo <= hi)
        {
            var mid = (lo + hi) >>> 1;
            if (T[mid] <= t)
            {
                ans = mid;
                lo = mid + 1;
            }
            else
            {
                hi = mid - 1;
            }
        }

        return ans;
    }

    public bool TrySample(int t, out Vector2 pos, out float heading) => TrySample(t, out pos, out _, out heading);

    public bool TrySample(int t, out Vector2 pos, out float z, out float heading)
    {
        if (T.Length == 0)
        {
            pos = default;
            z = 0;
            heading = 0;
            return false;
        }

        var i = Floor(t);
        if (i < 0)
            i = 0;
        var j = i + 1;
        if (j >= T.Length || t <= T[i] || (Flags[j] & PosFlags.Teleport) != 0)
        {
            pos = new Vector2(X[i], Y[i]);
            z = Z[i];
            heading = H[i];
            return true;
        }

        // Long gaps mean "no update while stationary": hold, then move over the last MaxMoveMs before the next sample.
        var t0 = T[i];
        if (T[j] - t0 > MaxMoveMs * 2)
        {
            t0 = T[j] - MaxMoveMs;
            if (t <= t0)
            {
                pos = new Vector2(X[i], Y[i]);
                z = Z[i];
                heading = H[i];
                return true;
            }
        }

        var u = (float)(t - t0) / (T[j] - t0);
        pos = new Vector2(X[i] + ((X[j] - X[i]) * u), Y[i] + ((Y[j] - Y[i]) * u));
        z = Z[i] + ((Z[j] - Z[i]) * u);
        heading = Angles.Lerp(H[i], H[j], u);
        return true;
    }
}

public static class Angles
{
    public static float Wrap(float a)
    {
        while (a > MathF.PI)
            a -= 2 * MathF.PI;
        while (a <= -MathF.PI)
            a += 2 * MathF.PI;
        return a;
    }

    public static float Lerp(float a, float b, float u) => Wrap(a + (Wrap(b - a) * u));

    /// <summary>Unit facing vector for a heading (0 = +Y/south, π/2 = +X/east).</summary>
    public static Vector2 Dir(float heading) => new(MathF.Sin(heading), MathF.Cos(heading));

    /// <summary>Heading pointing from a to b.</summary>
    public static float Toward(Vector2 a, Vector2 b) => MathF.Atan2(b.X - a.X, b.Y - a.Y);
}

public sealed class HpTrack
{
    public static readonly HpTrack Empty = new([], []);

    public HpTrack(int[] t, int[] hp)
    {
        T = t;
        Values = hp;
    }

    public int[] T { get; }
    public int[] Values { get; }

    public int At(int t, int fallback = -1)
    {
        int lo = 0, hi = T.Length - 1, ans = -1;
        while (lo <= hi)
        {
            var mid = (lo + hi) >>> 1;
            if (T[mid] <= t)
            {
                ans = mid;
                lo = mid + 1;
            }
            else
            {
                hi = mid - 1;
            }
        }

        return ans >= 0 ? Values[ans] : T.Length > 0 ? Values[0] : fallback;
    }
}

public enum CastOutcome : byte
{
    Unknown,
    Completed,
    Cancelled,
}

public sealed class CastEvent
{
    public int Index { get; internal set; }
    public required Actor Source { get; init; }
    public uint ActionId { get; init; }
    public string Name { get; set; } = string.Empty;
    public Actor? Target { get; init; }
    public int StartMs { get; init; }
    public int DurationMs { get; init; }

    /// <summary>Cast origin/location (263 StartsUsingExtra when present; else the 20 line's caster position).</summary>
    public Vector2 Pos { get; set; }

    public float Heading { get; set; }
    public bool HasExtra { get; set; }
    public int EndMs { get; set; }
    public CastOutcome Outcome { get; set; }
    public ActionEvent? Resolution { get; set; }
}

public enum HitKind : byte
{
    Other,
    Damage,
    Heal,
    Miss,
    Blocked,
    Parried,
    Invulnerable,
    NoEffect,
}

public sealed class HitEvent
{
    public required ActionEvent Action { get; init; }
    public required Actor Target { get; init; }
    public int T { get; init; }
    public HitKind Kind { get; set; }
    public int Damage { get; set; }
    public int Heal { get; set; }
    public bool Crit { get; set; }
    public bool DirectHit { get; set; }
    public bool Knockback { get; set; }
    public bool InstantDeath { get; set; }
    public int HpBefore { get; set; }
    public int MaxHp { get; set; }
    public int HpAfter { get; set; } = -1;
    public Vector2 TargetPos { get; set; }
}

public sealed class ActionEvent
{
    public int Index { get; internal set; }
    public required Actor Source { get; init; }
    public uint ActionId { get; init; }
    public string Name { get; set; } = string.Empty;
    public uint Sequence { get; init; }
    public int T { get; init; }
    public bool IsAoeLine { get; set; }
    public Vector2 SourcePos { get; set; }
    public float SourceHeading { get; set; }

    /// <summary>Precise facing at resolution (21/22 rotation field; plugin 3.x).</summary>
    public float? RotationHeading { get; set; }

    /// <summary>Valid ground location from 264 AbilityExtra.</summary>
    public Vector2? Location { get; set; }

    public float? ExtraHeading { get; set; }
    public Actor? AnimationTarget { get; set; }
    public Actor? PrimaryTarget { get; set; }
    public int TargetCount { get; set; }
    public List<HitEvent> Hits { get; } = [];
    public CastEvent? Cast { get; set; }
    public bool AnyDamage { get; set; }

    public float BestHeading => RotationHeading ?? ExtraHeading ?? SourceHeading;
}

public sealed class TickEvent
{
    public required Actor Target { get; init; }
    public Actor? Source { get; init; }
    public int T { get; init; }
    public int Amount { get; init; }
    public bool IsHeal { get; init; }
    public uint EffectId { get; init; }
}

/// <summary>One application of a status: when, with how many stacks, for how long (seconds; 0 or 9999 = no timer).</summary>
public readonly record struct StatusApplication(int T, int Stacks, float Duration);

public sealed class StatusInterval
{
    public required Actor Target { get; init; }
    public Actor? Source { get; init; }
    public uint StatusId { get; init; }
    public string Name { get; set; } = string.Empty;
    public int StartMs { get; init; }
    public int EndMs { get; set; } = int.MaxValue;

    /// <summary>Duration of the latest application (seconds).</summary>
    public float Duration { get; set; }

    /// <summary>Stacks of the latest application.</summary>
    public int Stacks { get; set; }

    public bool Removed { get; set; }

    /// <summary>
    /// Every application, oldest first, when the status was re-applied while active (a DoT refreshed, a stack added);
    /// null when it was applied once.
    /// </summary>
    public List<StatusApplication>? Applications { get; set; }

    public bool Active(int t) => t >= StartMs && t < EndMs;

    /// <summary>The application in force at <paramref name="t"/>: the latest one at or before t.</summary>
    public StatusApplication AppliedAt(int t)
    {
        if (Applications is not { Count: > 0 } all)
            return new StatusApplication(StartMs, Stacks, Duration);
        var current = all[0];
        foreach (var a in all)
        {
            if (a.T > t)
                break;
            current = a;
        }

        return current;
    }

    /// <summary>
    /// When the status runs out, as seen at <paramref name="t"/>: the application in force plus its duration, or its
    /// removal if that came sooner. Null when the log gives no timer (permanent effects) and it was never removed.
    /// </summary>
    public int? ExpiresAt(int t)
    {
        var a = AppliedAt(t);
        if (a.Duration is > 0 and < 9000)
            return Math.Min(a.T + (int)(a.Duration * 1000), EndMs);
        return Removed ? EndMs : null;
    }
}

public sealed class DeathEvent
{
    public required Actor Victim { get; init; }
    public Actor? Source { get; init; }
    public int T { get; init; }
    public Vector2 Pos { get; set; }
    public int RaisedMs { get; set; } = -1;
    public string Cause { get; set; } = string.Empty;
    public HitEvent? KillingBlow { get; set; }
}

public sealed class HeadMarkerEvent
{
    public required Actor Target { get; init; }
    public uint MarkerId { get; init; }
    public int T { get; init; }
    public string? Label { get; set; }
    public int DurationMs { get; set; } = 5000;
}

public sealed class TetherEvent
{
    public required Actor Source { get; init; }
    public required Actor Target { get; init; }
    public uint TetherId { get; init; }
    public int StartMs { get; init; }
    public int EndMs { get; set; }
    public string? Label { get; set; }
}

public sealed class WaymarkEvent
{
    public int T { get; init; }
    public int Slot { get; init; }
    public bool Add { get; init; }
    public Vector3 Pos { get; init; }
}

public sealed class SignEvent
{
    public int T { get; init; }
    public int Marker { get; init; }
    public bool Add { get; init; }
    public Actor? Target { get; init; }
}

public sealed class MapEffectEvent
{
    public int T { get; init; }
    public uint Instance { get; init; }
    public uint Flags { get; init; }
    public uint Location { get; init; }
}

public sealed class DirectorEvent
{
    public int T { get; init; }
    public uint Instance { get; init; }
    public uint Command { get; init; }
    public uint P1 { get; init; }
    public uint P2 { get; init; }
    public uint P3 { get; init; }
    public uint P4 { get; init; }
}

public sealed class ActorControlEvent
{
    public required Actor Actor { get; init; }
    public int T { get; init; }
    public uint Category { get; init; }
    public uint P1 { get; init; }
    public uint P2 { get; init; }
    public uint P3 { get; init; }
    public uint P4 { get; init; }
}

public sealed class NameToggleEvent
{
    public required Actor Actor { get; init; }
    public int T { get; init; }
    public bool Targetable { get; init; }
}

public sealed class MapChangeEvent
{
    public int T { get; init; }
    public int MapId { get; init; }
    public string Name { get; init; } = string.Empty;
}
