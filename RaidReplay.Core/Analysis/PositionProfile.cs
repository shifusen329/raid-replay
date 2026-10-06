using System.IO.Compression;
using System.Numerics;
using System.Text.Json;
using System.Text.Json.Serialization;
using RaidReplay.Core.Model;

namespace RaidReplay.Core.Analysis;

/// <summary>
/// A "mechanic moment": AoEs resolving together. Anchored by phase-relative time (scripted fight timelines make
/// this stable across pulls) and keyed by a variant string (which actions, orientations and tower locations).
/// </summary>
public sealed class MechanicGroup
{
    public required string Phase { get; init; }
    public int PhaseSecond { get; init; }
    public int T { get; init; }
    public required string Variant { get; init; }
    public required List<AoeInstance> Aoes { get; init; }

    public string Anchor => $"{Phase}+{PhaseSecond}";
}

/// <summary>One player's position at one mechanic moment of one pull.</summary>
public sealed class PositionSample
{
    public string Anchor { get; set; } = string.Empty;
    public string Variant { get; set; } = string.Empty;
    public string Player { get; set; } = string.Empty;
    public byte Job { get; set; }
    public float X { get; set; }
    public float Y { get; set; }
    public bool Ok { get; set; }
}

public sealed class PullSamples
{
    public string PullKey { get; set; } = string.Empty;
    public long StartTicks { get; set; }
    public List<PositionSample> Samples { get; set; } = [];
}

public sealed class ProfileFile
{
    public int Format { get; set; } = PositionProfile.Format;
    public string Encounter { get; set; } = string.Empty;
    public List<PullSamples> Pulls { get; set; } = [];
}

[JsonSerializable(typeof(ProfileFile))]
internal sealed partial class ProfileJsonContext : JsonSerializerContext;

public readonly record struct LearnedPosition(Vector2 Pos, float Spread, int Count, bool SameVariant);

/// <summary>Where each player usually stands at each mechanic moment, learned from pulls where it went fine.</summary>
public sealed class PositionProfile
{
    public const int Format = 2;
    public const float ConsistentSpread = 2.5f;

    private static readonly HashSet<AoeCategory> MechanicCategories =
    [
        AoeCategory.Danger, AoeCategory.HiddenDanger, AoeCategory.Fake, AoeCategory.Tower, AoeCategory.Stack,
        AoeCategory.Spread, AoeCategory.Gaze, AoeCategory.Knockback, AoeCategory.Bait, AoeCategory.Tankbuster,
    ];

    private readonly object gate = new();
    private readonly Dictionary<string, PullSamples> pulls = new();

    public PositionProfile(string encounter) => Encounter = encounter;

    public string Encounter { get; }
    public int PullCount
    {
        get
        {
            lock (gate)
                return pulls.Count;
        }
    }

    public bool Contains(string pullKey)
    {
        lock (gate)
            return pulls.ContainsKey(pullKey);
    }

    /// <summary>Groups resolving mechanic AoEs into moments (within 400 ms).</summary>
    public static List<MechanicGroup> Groups(PullReplay r)
    {
        var aoes = r.Aoes.Where(a => MechanicCategories.Contains(a.Category) && a.ResolveMs <= r.EndMs && a.ResolveMs >= 0)
                    .OrderBy(a => a.ResolveMs).ToList();
        var result = new List<MechanicGroup>();
        var i = 0;
        while (i < aoes.Count)
        {
            var j = i + 1;
            while (j < aoes.Count && aoes[j].ResolveMs - aoes[i].ResolveMs <= 400)
                j++;
            var group = aoes.GetRange(i, j - i);
            var t = group[0].ResolveMs;
            var phase = r.Phases.LastOrDefault(p => !p.IsSegment && p.StartMs <= t);
            result.Add(new MechanicGroup
            {
                Phase = phase?.Id ?? "pull",
                PhaseSecond = (int)Math.Round((t - Math.Max(0, phase?.StartMs ?? 0)) / 1000.0),
                T = t,
                Variant = VariantOf(group),
                Aoes = group,
            });
            i = j;
        }

        return result;
    }

    public static string VariantOf(IEnumerable<AoeInstance> group)
    {
        var parts = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var a in group)
        {
            var (origin, heading) = a.Placement(a.ResolveMs);
            var part = $"{a.ActionId:X}";
            if (a.Shape.Type is Geometry.ShapeType.Cone or Geometry.ShapeType.Rect or Geometry.ShapeType.HalfRoom or Geometry.ShapeType.Cross)
                part += $"@h{(int)Math.Round(heading * 180 / MathF.PI / 45) * 45}";
            if (a.Category == AoeCategory.Tower || a.Follow == null)
                part += $"@{Math.Round(origin.X / 2) * 2:0},{Math.Round(origin.Y / 2) * 2:0}";
            parts.Add(part);
        }

        return string.Join(";", parts);
    }

    /// <summary>Extracts per-player positions at each mechanic moment and whether the player handled it.</summary>
    public static PullSamples Extract(PullReplay r)
    {
        var result = new PullSamples { PullKey = r.Summary.Key, StartTicks = r.Summary.StartTicks };
        var failures = r.Actions.Where(a => r.Encounter?.FailureActions.Contains(a.ActionId) == true).Select(a => a.T).ToList();
        foreach (var g in Groups(r))
        {
            if (g.T > r.EndMs - 1500)
                continue; // the pull ended right here; nothing to learn
            var failed = failures.Any(f => Math.Abs(f - g.T) <= 1500) || WipeAnalyzer.UnderSoaked(g).Any();
            foreach (var p in r.Party)
            {
                if (!ShapeValidator.IsAlive(r, p, g.T) || !p.Track.TrySample(g.T, out var pos, out _))
                    continue;
                var hitByDanger = g.Aoes.Any(a => a.Category is AoeCategory.Danger or AoeCategory.HiddenDanger &&
                                                  a.Action?.Hits.Any(h => h.Target == p && h.Damage > 0) == true);
                var ok = !failed && !hitByDanger && ShapeValidator.IsAlive(r, p, g.T + 2500);
                result.Samples.Add(new PositionSample
                {
                    Anchor = g.Anchor, Variant = g.Variant, Player = p.Name, Job = p.Job, X = pos.X, Y = pos.Y, Ok = ok,
                });
            }
        }

        return result;
    }

    public void Add(PullSamples samples)
    {
        lock (gate)
            pulls[samples.PullKey] = samples;
    }

    /// <summary>Typical position of a player at an anchor (±1 s), excluding the given pull.</summary>
    public LearnedPosition? Query(string phase, int phaseSecond, string variant, string player, byte job, string excludePull)
    {
        var anchors = new[] { $"{phase}+{phaseSecond}", $"{phase}+{phaseSecond - 1}", $"{phase}+{phaseSecond + 1}" };
        var same = new List<Vector2>();
        var any = new List<Vector2>();
        lock (gate)
        {
            foreach (var (key, pull) in pulls)
            {
                if (key == excludePull)
                    continue;
                foreach (var s in pull.Samples)
                {
                    if (!s.Ok || s.Player != player || Array.IndexOf(anchors, s.Anchor) < 0)
                        continue;
                    var v = new Vector2(s.X, s.Y);
                    any.Add(v);
                    if (s.Variant == variant)
                        same.Add(v);
                }
            }
        }

        if (same.Count >= 2 && Median(same, out var m1, out var s1) && s1 <= ConsistentSpread)
            return new LearnedPosition(m1, s1, same.Count, true);
        if (any.Count >= 3 && Median(any, out var m2, out var s2) && s2 <= ConsistentSpread)
            return new LearnedPosition(m2, s2, any.Count, false);
        return null;
    }

    private static bool Median(List<Vector2> pts, out Vector2 median, out float spread)
    {
        var xs = pts.Select(p => p.X).Order().ToArray();
        var ys = pts.Select(p => p.Y).Order().ToArray();
        median = new Vector2(xs[xs.Length / 2], ys[ys.Length / 2]);
        var m = median;
        var d = pts.Select(p => Vector2.Distance(p, m)).Order().ToArray();
        spread = d[d.Length / 2];
        return true;
    }

    public static string FileFor(string cacheDir, string encounter) =>
        Path.Combine(cacheDir, "profiles", $"{encounter}.json.gz");

    public static PositionProfile Load(string cacheDir, string encounter)
    {
        var profile = new PositionProfile(encounter);
        var file = FileFor(cacheDir, encounter);
        if (!File.Exists(file))
            return profile;
        try
        {
            using var fs = File.OpenRead(file);
            using var gz = new GZipStream(fs, CompressionMode.Decompress);
            var data = JsonSerializer.Deserialize(gz, ProfileJsonContext.Default.ProfileFile);
            if (data is { Format: Format })
            {
                foreach (var p in data.Pulls)
                    profile.pulls[p.PullKey] = p;
            }
        }
        catch (Exception)
        {
            // Corrupt cache: rebuild.
        }

        return profile;
    }

    public void Save(string cacheDir)
    {
        ProfileFile data;
        lock (gate)
            data = new ProfileFile { Encounter = Encounter, Pulls = pulls.Values.ToList() };
        var file = FileFor(cacheDir, Encounter);
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        var tmp = file + ".tmp";
        using (var fs = File.Create(tmp))
        using (var gz = new GZipStream(fs, CompressionLevel.Fastest))
            JsonSerializer.Serialize(gz, data, ProfileJsonContext.Default.ProfileFile);
        File.Move(tmp, file, true);
    }
}
