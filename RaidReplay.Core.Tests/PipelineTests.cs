using RaidReplay.Core.Analysis;
using RaidReplay.Core.Encounters;
using RaidReplay.Core.Indexing;
using RaidReplay.Core.Loading;
using RaidReplay.Core.Model;
using RaidReplay.Core.Parsing;

namespace RaidReplay.Core.Tests;

public class IndexerTests
{
    [Fact]
    public void DetectsSinglePullWithCountdownAndWipe()
    {
        var p = TestEnv.OnlyPull("dmu_p1_cleave.log");
        Assert.Equal(PullOutcome.Wipe, p.Outcome);
        Assert.Equal(0x553u, p.ZoneId);
        Assert.Equal(79, p.MapId);
        Assert.True(p.HasDirector);
        Assert.NotEqual(0, p.CountdownTicks);
        Assert.InRange(p.DurationMs, 26000, 27500);
        Assert.Equal(8, p.Party.Count);
        Assert.Equal(7, p.Deaths);
        Assert.Equal("dmu", p.EncounterKey);
        Assert.Equal("P1 Kefka", p.FurthestPhase);
        Assert.InRange(p.BossHpPct, 82, 84.5f);
    }

    [Fact]
    public void ResumeMatchesFullIndex()
    {
        var src = TestEnv.Fixture("dmu_p1_puddles.log");
        var bytes = File.ReadAllBytes(src);
        var full = TestEnv.Index("dmu_p1_puddles.log");
        var dir = TestEnv.TempDir();
        var path = Path.Combine(dir, "growing.log");
        var obs = new EncounterObserverFactory(TestEnv.Registry);
        foreach (var cut in new[] { bytes.Length / 7, bytes.Length / 3, (bytes.Length / 2) + 13, bytes.Length - 5000 })
        {
            File.WriteAllBytes(path, bytes[..cut]);
            var partial = IndexStore.IndexFile(path, null, obs, TestEnv.Registry.HashFor);
            File.WriteAllBytes(path, bytes);
            var resumed = IndexStore.IndexFile(path, partial, obs, TestEnv.Registry.HashFor);
            var a = Assert.Single(full.Pulls);
            var b = Assert.Single(resumed.Pulls);
            Assert.Equal(a.StartOffset, b.StartOffset);
            Assert.Equal(a.EndOffset, b.EndOffset);
            Assert.Equal(a.Outcome, b.Outcome);
            Assert.Equal(a.Deaths, b.Deaths);
            Assert.Equal(a.Phases.Select(x => x.Id), b.Phases.Select(x => x.Id));
        }
    }

    [Fact]
    public void CachedIndexRoundTrips()
    {
        var index = TestEnv.Index("dmu_p1_cleave.log");
        var cache = TestEnv.TempDir();
        IndexStore.Save(cache, index);
        var loaded = IndexStore.Load(cache, index.Path);
        Assert.NotNull(loaded);
        Assert.Equal(index.Pulls[0].Key, loaded!.Pulls[0].Key);
        Assert.Equal(index.Pulls[0].Checkpoint.Combatants.Count, loaded.Pulls[0].Checkpoint.Combatants.Count);
        // Unchanged file: the cached index is returned as-is.
        Assert.Same(loaded, IndexStore.IndexFile(index.Path, loaded, new EncounterObserverFactory(TestEnv.Registry), TestEnv.Registry.HashFor));
    }
}

public class LoaderTests
{
    private static PullReplay Load(string fixture) =>
        PullLoader.Load(TestEnv.OnlyPull(fixture), null, TestEnv.Registry);

    [Fact]
    public void ReconstructsPartyBossAndDeaths()
    {
        var r = Load("dmu_p1_cleave.log");
        Assert.Equal(8, r.Party.Count);
        // Stationary players collapse to their first and last sample; most move.
        Assert.All(r.Party, p => Assert.True(p.Track.Count >= 2, $"{p.Name} has {p.Track.Count} samples"));
        Assert.True(r.Party.Count(p => p.Track.Count > 8) >= 6);
        var boss = Assert.Single(r.Actors, a => a.Kind == ActorKind.Boss);
        Assert.Equal(19504u, boss.BNpcBaseId);
        Assert.Equal("Kefka", boss.DisplayName);
        Assert.All(r.Actors.Where(a => a.BNpcBaseId == 9020), a => Assert.Equal(ActorKind.Helper, a.Kind));
        // All eight died; the last (a fall at the wipe) is logged after the pull-end line and only the replay tail has it.
        Assert.Equal(8, r.Deaths.Count(d => d.Victim.IsPlayer));
        Assert.Contains(r.Deaths, d => d.KillingBlow?.Action.ActionId is 0xC403 or 0xC4E1);
        Assert.Equal("P1 Kefka", r.PhaseAt(10000));
    }

    [Fact]
    public void LinksCastsAndIgnoresPreResolutionCancelLines()
    {
        var r = Load("dmu_p1_cleave.log");
        var rr3 = r.Casts.First(c => c.ActionId == 0xC403);
        Assert.NotNull(rr3.Resolution);
        Assert.Equal(CastOutcome.Completed, rr3.Outcome);
        Assert.True(rr3.HasExtra);
    }

    [Fact]
    public void BuildsMechanicAoesWithPackShapes()
    {
        var r = Load("dmu_p1_puddles.log");
        Assert.Contains(r.Aoes, a => a.Category == AoeCategory.Hazard && a.Label == "Gravitas puddle");
        Assert.Contains(r.Aoes, a => a.ActionId == 0xBA98 && a.Shape.Type == Geometry.ShapeType.Cone);
        Assert.Contains(r.Mechanics, m => m.Id == "p1_graven_image_2");
        Assert.Contains(r.Phases, p => p.IsSegment && p.Id == "p1_gi2");
    }

    [Fact]
    public void ShapeValidationIsAccurateOnFixture()
    {
        var stats = new Dictionary<uint, ShapeStats>();
        ShapeValidator.Accumulate(Load("dmu_p1_puddles.log"), stats);
        ShapeValidator.Accumulate(Load("dmu_p1_cleave.log"), stats);
        var tp = stats.Values.Sum(s => s.TruePositive);
        var fp = stats.Values.Sum(s => s.FalsePositive);
        var fn = stats.Values.Sum(s => s.FalseNegative);
        Assert.True(tp > 30, $"tp={tp}");
        Assert.True((double)tp / (tp + fp) > 0.9, $"precision {tp}/{tp + fp}");
        Assert.True((double)tp / (tp + fn) > 0.9, $"recall {tp}/{tp + fn}");
    }
}

public class AnalyzerTests
{
    [Fact]
    public void TankbusterCleaveIsTheRootCause()
    {
        var r = PullLoader.Load(TestEnv.OnlyPull("dmu_p1_cleave.log"), null, TestEnv.Registry);
        var report = WipeAnalyzer.Analyze(r);
        var root = Assert.IsType<Incident>(report.RootCause);
        Assert.Equal(IncidentKind.Death, root.Kind);
        Assert.Contains(root.Death!.KillingBlow!.Action.ActionId, new uint[] { 0xC403, 0xC4E1 });
        Assert.Contains("tankbuster cleave", root.Detail);
        // Non-tanks in the cleave get a safe spot outside it.
        Assert.Contains(root.Snapshot, s => s.ExpectedSource == ExpectedSource.SafeSpot && s.MissDistance > 1);
        Assert.Equal(8, root.Snapshot.Count);
    }

    [Fact]
    public void OverlappingPuddlesAreNamed()
    {
        var r = PullLoader.Load(TestEnv.OnlyPull("dmu_p1_puddles.log"), null, TestEnv.Registry);
        var report = WipeAnalyzer.Analyze(r);
        var root = Assert.IsType<Incident>(report.RootCause);
        Assert.Equal(IncidentKind.FailureAction, root.Kind);
        Assert.Contains("×4", root.Title);
        Assert.Contains("dropped overlapping", root.Detail);
        Assert.Equal(4, root.Players.Count);
        Assert.Equal("Mechanic failure", report.Verdict);
        Assert.StartsWith("[Raid Replay] Wipe", report.ChatLine);
    }

    [Fact]
    public void LearnedPositionsComeFromOtherPulls()
    {
        var a = PullLoader.Load(TestEnv.OnlyPull("dmu_p1_puddles.log"), null, TestEnv.Registry);
        var samples = PositionProfile.Extract(a);
        Assert.NotEmpty(samples.Samples);
        var profile = new PositionProfile("dmu");
        profile.Add(samples);
        // Same pull is excluded; a copy under another key is used.
        var s = samples.Samples.First(x => x.Ok);
        var parts = s.Anchor.Split('+');
        Assert.Null(profile.Query(parts[0], int.Parse(parts[1]), s.Variant, s.Player, s.Job, a.Summary.Key));
        profile.Add(new PullSamples { PullKey = "other1", Samples = samples.Samples });
        profile.Add(new PullSamples { PullKey = "other2", Samples = samples.Samples });
        var learned = profile.Query(parts[0], int.Parse(parts[1]), s.Variant, s.Player, s.Job, a.Summary.Key);
        Assert.NotNull(learned);
        Assert.Equal(s.X, learned!.Value.Pos.X, 0.01f);
    }
}

public class LiveTailerTests
{
    [Fact]
    public void RaisesPullEndedOnceWhileTheLogGrows()
    {
        var bytes = File.ReadAllBytes(TestEnv.Fixture("dmu_p1_cleave.log"));
        var dir = TestEnv.TempDir();
        var path = Path.Combine(dir, "Network_30301_20261005.log");
        var tailer = new LiveTailer(dir, Path.Combine(dir, "cache"), new EncounterObserverFactory(TestEnv.Registry));
        var started = new List<PullSummary>();
        var ended = new List<PullSummary>();
        tailer.PullStarted += started.Add;
        tailer.PullEnded += ended.Add;

        using (var fs = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.ReadWrite))
        {
            // Only the state header exists when we attach.
            var headerEnd = IndexOf(bytes, "\r\n261|"u8) + 2;
            fs.Write(bytes, 0, headerEnd);
            fs.Flush();
            tailer.Poll();
            Assert.Empty(ended);
            for (var pos = headerEnd; pos < bytes.Length; pos += 7919)
            {
                fs.Write(bytes, pos, Math.Min(7919, bytes.Length - pos));
                fs.Flush();
                tailer.Poll();
            }
        }

        tailer.Poll();
        Assert.Single(started);
        var p = Assert.Single(ended);
        Assert.Equal(PullOutcome.Wipe, p.Outcome);
        Assert.Equal("dmu", p.EncounterKey);
        var replay = PullLoader.Load(p, null, TestEnv.Registry, readToEof: true);
        Assert.NotNull(WipeAnalyzer.Analyze(replay).RootCause);
    }

    private static int IndexOf(byte[] haystack, ReadOnlySpan<byte> needle) => haystack.AsSpan().IndexOf(needle);
}

public class EncounterTests
{
    [Fact]
    public void BuiltInPacksLoadWithoutErrors()
    {
        Assert.Empty(TestEnv.Registry.Errors);
        var dmu = TestEnv.Registry.ForTerritory(0x553);
        Assert.NotNull(dmu);
        Assert.Equal("dmu", dmu!.Key);
        Assert.True(dmu.Abilities.Count > 40);
    }

    [Fact]
    public void PackRoundTripsThroughJson()
    {
        var def = TestEnv.Registry.ForTerritory(0x553)!.Def;
        var json = EncounterRegistry.Serialize(def);
        var again = EncounterRegistry.Parse(json);
        Assert.Equal(def.Abilities.Count, again.Abilities.Count);
        Assert.Equal(def.Phases.Select(p => p.Id), again.Phases.Select(p => p.Id));
    }

    [Fact]
    public void UnknownFieldsAreRejected()
    {
        Assert.ThrowsAny<Exception>(() => EncounterRegistry.Compile("""{ "key": "x", "match": { "territoryIds": ["0x1"] }, "typo": 1 }""", "t"));
        Assert.ThrowsAny<Exception>(() => EncounterRegistry.Compile("""{ "key": "x", "match": { "territoryIds": ["0x1"] }, "abilities": { "0x1": { "category": "nope" } } }""", "t"));
    }

    [Fact]
    public void TriggerMatching()
    {
        var t = new TriggerDef { AnyOf = [new TriggerDef { Director = new DirectorMatch { Command = new HexId(0x80000001), P1 = [new HexId(0x4F44)] } }] };
        Assert.True(EncounterRun.Matches(t, new TriggerEvent { Kind = TriggerKind.Director, Id = 0x80000001, P1 = 0x4F44 }));
        Assert.False(EncounterRun.Matches(t, new TriggerEvent { Kind = TriggerKind.Director, Id = 0x80000001, P1 = 0x4F43 }));
        var cast = new TriggerDef { CastStart = new IdMatch { Names = ["Kefka Says"] } };
        Assert.True(EncounterRun.Matches(cast, new TriggerEvent { Kind = TriggerKind.CastStart, Name = "kefka says" }));
    }
}

public class IntegrationTests
{
    private const string DmuLog = "Network_30301_20261005.log";

    [LogsFact(DmuLog)]
    public void IndexesTheDmuProgDay()
    {
        var path = Path.Combine(TestEnv.LogsPath!, DmuLog);
        var index = IndexStore.IndexFile(path, null, new EncounterObserverFactory(TestEnv.Registry), TestEnv.Registry.HashFor);
        var dmu = index.Pulls.Where(p => p.EncounterKey == "dmu").ToList();
        Assert.True(dmu.Count >= 116, $"{dmu.Count} DMU pulls");
        var first116 = dmu.Take(116).ToList();
        Assert.All(first116, p => Assert.Equal(PullOutcome.Wipe, p.Outcome));
        Assert.Equal(23, first116.Count(p => p.FurthestPhase == "P2 Forsaken Kefka"));
        Assert.True(first116[0].StartTruncated);
        var longest = first116.MaxBy(p => p.DurationMs)!;
        Assert.Equal(104, longest.Ordinal);
        // No overlapping pulls.
        for (var i = 1; i < index.Pulls.Count; i++)
            Assert.True(index.Pulls[i].StartOffset >= index.Pulls[i - 1].EndOffset);
    }

    [LogsFact(DmuLog)]
    public void WipeCountMatchesDirectorLines()
    {
        var path = Path.Combine(TestEnv.LogsPath!, DmuLog);
        var counter = new WipeCounter();
        LogLineReader.Read(path, 0, -1, new LineTypeSet(LineType.Director), counter);
        var index = IndexStore.IndexFile(path, null, null, null);
        Assert.Equal(counter.Wipes, index.Pulls.Count(p => p.Outcome == PullOutcome.Wipe));
    }

    private sealed class WipeCounter : ILineConsumer
    {
        public int Wipes { get; private set; }

        public bool OnLine(long offset, int type, ReadOnlySpan<byte> line)
        {
            Span<int> buf = stackalloc int[8];
            var f = new LineFields(line, buf);
            if (f.Hex(F33.Command) == DirectorCommand.Wipe)
                Wipes++;
            return true;
        }
    }
}

public class FixtureHygieneTests
{
    /// <summary>
    /// Fixtures come from real logs of real players. ACT's per-line checksum covers the original fields (names included),
    /// so it must be zeroed or names could be confirmed offline; player ids must be synthetic and dates neutral.
    /// </summary>
    [Theory]
    [InlineData("dmu_p1_cleave.log")]
    [InlineData("dmu_p1_puddles.log")]
    public void FixturesAreAnonymized(string fixture)
    {
        foreach (var line in File.ReadLines(TestEnv.Fixture(fixture)))
        {
            var f = line.Split('|');
            Assert.Equal("0000000000000000", f[^1]);
            Assert.True(f[1].StartsWith("2000-01-01T", StringComparison.Ordinal) || f[1].StartsWith("1999-12-31T", StringComparison.Ordinal), f[1]);
            Assert.EndsWith("+00:00", f[1]);
            // Actor-id fields per line type (other fields hold damage values etc. that can look like ids).
            int[] idFields = f[0] switch
            {
                "02" or "03" or "04" or "27" or "37" or "39" => [2],
                "11" => [.. Enumerable.Range(3, Math.Max(0, f.Length - 4))],
                "20" or "21" or "22" or "25" or "35" => [2, 4, 6],
                "26" or "30" => [5, 7],
                "261" => [3],
                _ => [],
            };
            foreach (var i in idFields.Where(i => i < f.Length - 1))
            {
                if (f[i].Length == 8 && f[i].StartsWith("10", StringComparison.Ordinal) &&
                    uint.TryParse(f[i], System.Globalization.NumberStyles.HexNumber, null, out var id))
                    Assert.True(id <= 0x100000FFu, $"unmapped player id {f[i]} in: {line}");
            }
        }
    }
}
