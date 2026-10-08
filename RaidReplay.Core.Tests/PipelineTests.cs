using RaidReplay.Core.Analysis;
using RaidReplay.Core.Encounters;
using RaidReplay.Core.Export;
using RaidReplay.Core.Feedback;
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
        // The zone was entered at the excerpt's first line (its synthesized ChangeZone); pulls of one entry share it.
        Assert.Equal(new DateTime(2000, 1, 1, 0, 0, 0, DateTimeKind.Utc).Ticks, p.EnteredTicks);
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
            Assert.Equal(a.EnteredTicks, b.EnteredTicks);
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
    public void RockThatTouchedThePuddlesIsBlamed()
    {
        // Combining the purple puddles is the strat; a yellow-tether rock (Vitrophyre) resolving within ~10y of a
        // puddle detonates them all. The rock holder closest to the puddles is to blame.
        var r = PullLoader.Load(TestEnv.OnlyPull("dmu_p1_puddles.log"), null, TestEnv.Registry);
        var report = WipeAnalyzer.Analyze(r);
        var root = Assert.IsType<Incident>(report.RootCause);
        Assert.Equal(IncidentKind.FailureAction, root.Kind);
        Assert.Contains("Vitrophyre rock touched the Gravitas puddles", root.Title);
        Assert.Contains("caused the wipe", root.Title);
        var culprit = Assert.Single(root.Players);
        var rocks = r.Actions.Where(a => a.ActionId == 0xBAB0 && Math.Abs(a.T - root.T) < 2000).ToList();
        Assert.Equal(4, rocks.Count);
        Assert.Contains(rocks, a => a.AnimationTarget == culprit);
        Assert.Contains("wiped the party", root.Detail);
        var snap = Assert.Single(root.Snapshot, s => s.Player == culprit);
        Assert.Equal(ExpectedSource.SafeSpot, snap.ExpectedSource);
        Assert.InRange(snap.MissDistance!.Value, 1, 8);
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

    [LogsFact(DmuLog)]
    public void ArrowChainsMatchTheGamesPuzzleVerdict()
    {
        // The game confirms a solved arrow puzzle with director 80000027 0C (~3:10). Following the Confused players
        // through the arrows actually on the ground must agree with it on every pull that got that far.
        var path = Path.Combine(TestEnv.LogsPath!, DmuLog);
        var index = IndexStore.IndexFile(path, null, new EncounterObserverFactory(TestEnv.Registry), TestEnv.Registry.HashFor);
        var checkedPulls = 0;
        foreach (var p in index.Pulls.Where(p => p.EncounterKey == "dmu" && p.DurationMs > 185000))
        {
            var r = PullLoader.Load(p, null, TestEnv.Registry);
            var sq = ArrowSquare.Evaluate(r);
            if (sq == null || sq.ConfusedStartMs < 0 || r.EndMs < sq.ConfusedEndMs + 10000)
                continue;
            var game = r.Directors.Any(d => d.Command == 0x80000027 && d.P1 == 0x0C);
            Assert.True(game == sq.Solved, $"pull #{p.Ordinal}: game {(game ? "solved" : "failed")}, simulation {(sq.Solved ? "solved" : "failed")}");
            checkedPulls++;
        }

        Assert.True(checkedPulls >= 40, $"{checkedPulls} pulls checked");
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
    [InlineData("dmu_p1_resets.log")]
    [InlineData("dmu_p1_undersoak.log")]
    [InlineData("dmu_p1_puddles.log")]
    [InlineData("dmu_p1_arrows.log")]
    [InlineData("dmu_p1_knockback_arrows.log")]
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

public class AfterActionTests
{
    private static (PullReplay Replay, WipeReport Report) Analyze(string fixture)
    {
        var r = PullLoader.Load(TestEnv.OnlyPull(fixture), null, TestEnv.Registry);
        return (r, WipeAnalyzer.Analyze(r));
    }

    [Fact]
    public void PartySlotsCoverTheStandardEight()
    {
        var (_, report) = Analyze("dmu_p1_puddles.log");
        Assert.Equal(PartySlots.Order.Order(), report.Slots.Values.Order());
        var h1 = report.Slots.First(kv => kv.Value == "H1").Key;
        Assert.Contains(h1.Job, new byte[] { 24, 33 }); // pure healer
        var r2 = report.Slots.First(kv => kv.Value == "R2").Key;
        Assert.Contains(r2.Job, new byte[] { 25, 27, 35, 42 }); // caster
    }

    [Fact]
    public void MitigationIsCheckedAgainstThePlanWithCooldownAwareness()
    {
        var (_, report) = Analyze("dmu_p1_puddles.log");
        var loj = Assert.Single(report.Mitigation, c => c.Mechanic.Id == "loj_1");
        Assert.True(loj.HitFound);
        Assert.NotEmpty(loj.Entries);
        // Nothing is blamed unless its cooldown was available and it isn't a carry-over.
        Assert.All(report.Mitigation.SelectMany(c => c.Entries).Where(e => e.Blamable),
                   e => Assert.True(e.Status is MitStatus.Missing or MitStatus.UsedNotActive && !e.Carry));
        Assert.All(report.Mitigation.SelectMany(c => c.Entries).Where(e => e.Status == MitStatus.OnCooldown),
                   e => Assert.False(e.Blamable));
    }

    [Fact]
    public void DamageDownResetsAreDeliberateAndTheirCascadeIsExplained()
    {
        // Pull #11: a real Blizzard cone gives Damage Down -> the player jumps off (reset) -> their tower goes unsoaked
        // -> the Unmitigated Explosion gives most of the party Damage Down -> two players jump off to end the pull.
        var (_, report) = Analyze("dmu_p1_resets.log");
        var deliberate = report.Incidents.Where(i => i.Intentional).ToList();
        Assert.All(deliberate, i => Assert.Equal(1, i.Severity));

        var reset = Assert.Single(deliberate, i => !i.DeliberateWipe);
        Assert.Contains("reset Damage Down", reset.Title);
        Assert.Contains("Blizzard III", reset.Detail);

        // Jumping off once the whole party has Damage Down ends a lost pull: not a reset.
        var wipes = deliberate.Where(i => i.DeliberateWipe).ToList();
        Assert.True(wipes.Count >= 2, $"{wipes.Count} deliberate wipes");
        Assert.All(wipes, i => Assert.Contains("wiped on purpose", i.Title));

        var root = Assert.IsType<Incident>(report.RootCause);
        Assert.False(root.Intentional);
        Assert.Equal(IncidentKind.AvoidableHit, root.Kind);

        // Resets are never the verdict (ATTRIBUTION.md): the root's own verdict stands.
        Assert.Equal("Avoidable damage", report.Verdict);
    }

    [Fact]
    public void UnderSoakedPuddlesNameTheMissingSoakers()
    {
        // Pull #75: one player soaks four overlapping Gravitas puddles alone at 2:01.
        var (_, report) = Analyze("dmu_p1_undersoak.log");
        var soak = Assert.Single(report.Incidents, i => i.Kind == IncidentKind.TowerUnderSoaked && i.Title.Contains("1/4"));
        Assert.Contains("4 overlapping", soak.Title);
        Assert.Contains("Missing from it", soak.Detail);
        // The players who were inside are not the culprits; the ones who should have been are.
        Assert.DoesNotContain(soak.Players, p => soak.Detail.StartsWith($"Inside: {p.Name}", StringComparison.Ordinal));
        Assert.Contains(soak.Snapshot, s => s.ExpectedSource == ExpectedSource.Soak);
    }

    [Fact]
    public void MitigationWouldSaveEstimate()
    {
        var kerachole = MitigationCatalog.Resolve("Kerachole", "SGE")!;
        var reprisal = MitigationCatalog.Resolve("Reprisal", "WAR")!;
        Assert.Equal(90000, MitigationCatalog.WithMitigation(100000, 200000, [kerachole]));
        Assert.Equal(81000, MitigationCatalog.WithMitigation(100000, 200000, [kerachole, reprisal]));
        Assert.Null(MitigationCatalog.WithMitigation(100000, 200000, [MitigationCatalog.Resolve("Macrocosmos", "AST")!]));
        Assert.Equal("Tactician", MitigationCatalog.Resolve("@partyMit", "MCH")!.Name);
        Assert.Null(MitigationCatalog.Resolve("@partyMit", "SAM"));
    }
}

public class ArrowPuzzleTests
{
    private static (PullReplay Replay, WipeReport Report) Analyze(string fixture)
    {
        var r = PullLoader.Load(TestEnv.OnlyPull(fixture), null, TestEnv.Registry);
        return (r, WipeAnalyzer.Analyze(r));
    }

    private static Actor Player(PullReplay r, string name) => r.Party.Single(p => p.Name == name);

    [Fact]
    public void ConfusedKillsGoToWhoeverMisplacedTheArrow()
    {
        // Pull #39: Player8's E arrow sat 3.9y inside N3, so Player7 (Confused) was thrown onto empty ground after one
        // teleport and killed Player5 — the wipe's root cause. Player1's S arrow for E3 was on SE: Player2 killed Player3.
        var (r, report) = Analyze("dmu_p1_arrows.log");
        Assert.Equal("Arrow placement failure", report.Verdict);
        var root = report.RootCause!;
        Assert.Equal("Player5 killed by confused Player7", root.Title);
        Assert.Contains(Player(r, "Player8"), root.Players);

        var second = Assert.Single(report.Incidents, i => i.Title == "Player3 killed by confused Player2");
        Assert.Contains(Player(r, "Player1"), second.Players);

        Assert.NotNull(report.Arrows);
        Assert.False(report.Arrows!.Solved);
        Assert.Equal(16, report.Arrows.Drops.Count);
    }

    [Fact]
    public void StackedArrowsBlameTheArrowsThatWereOutOfPlace()
    {
        // Three W arrows overlapped near the south side and vanished: Player6's second arrow and Player1's arrow were
        // off their spots, Player6's first one was where it belonged — so it is not counted against the drop that hit it.
        var (r, report) = Analyze("dmu_p1_arrows.log");
        var stack = Assert.Single(report.Arrows!.Findings, f => f.Fault == ArrowFault.Stacked);
        Assert.Equal(["Player1", "Player6"], stack.Culprits.Select(c => c.Name).Order());
        var inc = Assert.Single(report.Incidents, i => i.Kind == IncidentKind.ArrowPuzzle && i.Title.Contains("on top of each other"));
        Assert.Equal(ExpectedSource.Assigned, inc.Snapshot.Single(s => s.Player == Player(r, "Player1")).ExpectedSource);
    }

    [Fact]
    public void ArrowsKnockedIntoGoToTheHolderWhoWasOffTheirCorner()
    {
        // Pull #61: the DPS confetti holder (Player4) stood 7.2y from the bottom-right corner of marker 3, so the third
        // knockback sent Player5 and Player7 south into the arrows before the confusion. The soakers were on their spot.
        var (r, report) = Analyze("dmu_p1_knockback_arrows.log");
        var early = report.Arrows!.Findings.Where(f => f.Fault == ArrowFault.SetOffEarly).ToList();
        Assert.Equal(2, early.Count);
        Assert.All(early, f => Assert.Equal([Player(r, "Player4")], f.Culprits));
        Assert.All(early, f => Assert.Contains("holding it", f.Text));
    }

    [Fact]
    public void ConfusedPlayersOutOfPositionAreCalledOut()
    {
        var (r, report) = Analyze("dmu_p1_knockback_arrows.log");
        // Never reached an arrow at all.
        var walk = Assert.Single(report.Incidents, i => i.Title == "Player8 killed by confused Player5");
        Assert.Equal("Confused positioning", walk.VerdictHint);
        Assert.Contains("never stepped on an arrow", walk.Detail);
        // Stepped in at a corner and ran into arrows another Confused player had already used.
        var shortChain = Assert.Single(report.Incidents, i => i.Title == "Player2 killed by confused Player7");
        Assert.Contains("stopped after 2 of 4 arrows", shortChain.Detail);
        Assert.Equal("Confused player reached an ally", report.Verdict);
        Assert.Equal(walk, report.RootCause);
    }

    [Fact]
    public void DeathsToIndulgentWillGoToWhoeverMisplacedArrows()
    {
        // Pull #70 (09-22): Player8's E arrow was 10.5y from its spot, outside the square, and still on the ground when
        // Indulgent Will hit; it hits harder for each misplaced arrow, and killed two players outright.
        var (r, report) = Analyze("dmu_p1_indulgent_will.log");
        var owner = Player(r, "Player8");
        var deaths = report.Incidents.Where(i => i.Kind == IncidentKind.Death && i.Title.EndsWith("died to Indulgent Will")).ToList();
        Assert.Equal(["Player1", "Player5"], deaths.Select(d => d.Victim!.Name).Order());
        Assert.All(deaths, d => Assert.Equal([owner], d.Players));

        var arrows = Assert.Single(report.Incidents, i => i.Kind == IncidentKind.ArrowPuzzle && i.Title.Contains("made Indulgent Will hit harder"));
        Assert.Equal([owner], arrows.Players);
        Assert.All(deaths, d => Assert.Contains(arrows, d.Causes));
        Assert.Equal(arrows, report.RootCause);
        Assert.Equal("Arrow placement failure", report.Verdict);
    }

    [Fact]
    public void ShortConfettiStackBlamesTheRoleMatesWhoStayedOut()
    {
        // Pull #75: the second confetti knockback on a support was taken by one player (1.5M damage, dead); the last
        // living support stood apart from them (the third was already dead). The one who took it is a victim, not a culprit.
        var (r, report) = Analyze("dmu_p1_undersoak.log");
        var inc = Assert.Single(report.Incidents, i => i.Kind == IncidentKind.MissedStack && i.Title.Contains("1/3 soakers"));
        Assert.Equal(3, inc.Severity);
        var culprit = Assert.Single(inc.Players);
        Assert.DoesNotContain($"{culprit.Name} died", inc.Title);
        var holder = inc.Aoes[0].ExcludeActor!;
        Assert.Equal(StackPositions.IsSupport(holder), StackPositions.IsSupport(culprit));
        Assert.Contains("already dead", inc.Detail);
        Assert.Equal(ExpectedSource.Soak, inc.Snapshot.Single(s => s.Player == culprit).ExpectedSource);

        // Too few supports were left to fill it, so it traces back to the earlier death; the death of the one who took
        // it traces to the stack.
        Assert.Contains(inc.Causes, c => c.Kind == IncidentKind.Death && c.T < inc.T);
        Assert.Contains(inc, report.Incidents.Single(i => i.Kind == IncidentKind.Death && i.Death!.Victim.Name == "Player7").Causes);
    }

    [Fact]
    public void ShortConfettiStackBlamesAHolderOffTheirCorner()
    {
        // Pull #63: the third DPS confetti holder (Player4) stood 10.1y off the bottom-right corner of marker 3, so it went
        // to two supports on their own corner (both died) and missed a DPS on theirs. Neither the supports who died nor
        // the DPS who wasn't in it are at fault.
        var (r, report) = Analyze("dmu_p1_confetti_holder.log");
        var holder = Player(r, "Player4");
        var inc = Assert.Single(report.Incidents, i => i.Kind == IncidentKind.MissedStack && i.Aoes[0].ExcludeActor == holder);
        Assert.Contains("2/3 soakers", inc.Title);
        Assert.Equal([holder], inc.Players);
        Assert.Contains("Player4 (holder) was 10.1y off their assigned spot", inc.Detail);
        Assert.Equal(ExpectedSource.Assigned, inc.Snapshot.Single(s => s.Player == holder).ExpectedSource);
        Assert.Equal(inc, report.RootCause);
    }
}

/// <summary>Some players log without OverlayPlugin: no 26x lines (no combat flag, combatant state, cast positions or animation targets).</summary>
public class NoOverlayPluginTests
{
    private static string Strip(string fixture)
    {
        var lines = File.ReadAllLines(TestEnv.Fixture(fixture))
                        .Where(l => !(int.TryParse(l.AsSpan(0, Math.Max(0, l.IndexOf('|'))), out var type) && type >= 253))
                        .ToList();
        var path = Path.Combine(TestEnv.TempDir(), fixture);
        File.WriteAllText(path, string.Join("\r\n", lines) + "\r\n");
        return path;
    }

    private static FileIndex Index(string path) =>
        IndexStore.IndexFile(path, null, new EncounterObserverFactory(TestEnv.Registry), TestEnv.Registry.HashFor);

    [Fact]
    public void PullIsFoundWithoutTheCombatFlag()
    {
        var full = TestEnv.OnlyPull("dmu_p1_cleave.log");
        var plain = Assert.Single(Index(Strip("dmu_p1_cleave.log")).Pulls);
        Assert.Equal(PullOutcome.Wipe, plain.Outcome);
        Assert.Equal("dmu", plain.EncounterKey);
        Assert.Equal(full.Deaths, plain.Deaths);
        // The start falls back to the first hit on an enemy, shortly after the combat flag would have been.
        Assert.InRange((plain.StartTicks - full.StartTicks) / TimeSpan.TicksPerMillisecond, -500, 6000);
    }

    [Fact]
    public void ConfettiHolderIsTheUnhitPlayerTheyStoodOn()
    {
        // Holders per confetti resolution (the two simultaneous ones can come out in either order).
        static List<HashSet<string?>> Holders(PullReplay r) =>
            r.Aoes.Where(a => a.ActionId == 0xBAA7 && a.Action != null).GroupBy(a => a.ResolveMs / 2000).OrderBy(g => g.Key)
             .Select(g => g.Select(a => a.Follow?.Name).ToHashSet()).ToList();

        var full = PullLoader.Load(TestEnv.OnlyPull("dmu_p1_knockback_arrows.log"), null, TestEnv.Registry);
        var path = Strip("dmu_p1_knockback_arrows.log");
        var plain = PullLoader.Load(Assert.Single(Index(path).Pulls), null, TestEnv.Registry);
        Assert.All(plain.Aoes.Where(a => a.ActionId == 0xBAA7 && a.Action != null),
                   a => Assert.DoesNotContain(a.Action!.Hits, h => h.Target == a.Follow));

        // Where positions allow, the holder is the same as the one the AbilityExtra line names. Here the third holder's
        // position is ~8y stale without OverlayPlugin's lines, so it stays unknown rather than becoming a soaker.
        var expected = Holders(full);
        var found = Holders(plain);
        Assert.Equal(expected.Count, found.Count);
        Assert.True(found.Sum(s => s.Count(h => h != null)) >= 5);
        for (var i = 0; i < found.Count; i++)
            Assert.Subset(expected[i], found[i].Where(h => h != null).ToHashSet());
    }

    [Fact]
    public void AnalyzerDoesNotJudgeArrowsItCannotSee()
    {
        var path = Strip("dmu_p1_arrows.log");
        var r = PullLoader.Load(Assert.Single(Index(path).Pulls), null, TestEnv.Registry);
        var report = WipeAnalyzer.Analyze(r);
        Assert.Null(report.Arrows);
        Assert.DoesNotContain(report.Incidents, i => i.Kind == IncidentKind.ArrowPuzzle);
    }
}

public class PackSchemaTests
{
    [Fact]
    public void MitigationPhaseAndOnlyTargetAreValidated()
    {
        Assert.ThrowsAny<Exception>(() => EncounterRegistry.Compile("""
            { "key": "x", "match": { "territoryIds": ["0x1"] },
              "mitigation": { "mechanics": [ { "id": "m", "name": "m", "phase": "nope", "atS": 1, "hits": ["0x1"] } ] } }
            """, "t"));
        Assert.ThrowsAny<Exception>(() => EncounterRegistry.Compile("""
            { "key": "x", "match": { "territoryIds": ["0x1"] }, "abilities": { "0x1": { "category": "danger", "onlyTarget": true } } }
            """, "t"));
        var dmu = TestEnv.Registry.ForTerritory(0x553)!.Def;
        Assert.Contains(dmu.Mitigation!.Mechanics, m => m.Phase == "p5");
    }

    [Fact]
    public void HeadMarkerIconsAreValidated()
    {
        Assert.ThrowsAny<Exception>(() => EncounterRegistry.Compile("""
            { "key": "x", "match": { "territoryIds": ["0x1"] }, "headMarkers": { "0x1": { "label": "m", "icon": "bogus" } } }
            """, "t"));
        var ok = EncounterRegistry.Compile("""
            { "key": "x", "match": { "territoryIds": ["0x1"] }, "headMarkers": { "0x1": { "label": "m", "icon": "stackGround" } } }
            """, "t");
        Assert.Equal("stackGround", ok.HeadMarkers[1].Icon);
        Assert.Equal("stack", TestEnv.Registry.ForTerritory(0x553)!.HeadMarkers[0x02CB].Icon);
    }

    [Fact]
    public void AimAndShownAsAreValidatedAndApplied()
    {
        Assert.ThrowsAny<Exception>(() => EncounterRegistry.Compile("""
            { "key": "x", "match": { "territoryIds": ["0x1"] }, "abilities": { "0x1": { "category": "bait", "aim": "farthest", "shape": { "type": "cone" } } } }
            """, "t"));
        Assert.ThrowsAny<Exception>(() => EncounterRegistry.Compile("""
            { "key": "x", "match": { "territoryIds": ["0x1"] }, "abilities": { "0x1": { "category": "bait", "aim": "nearestToHolder", "shape": { "type": "circle" } } } }
            """, "t"));
        var dmu = TestEnv.Registry.ForTerritory(0x553)!;
        Assert.Equal("nearestToHolder", dmu.Abilities[0xBAC2].Aim);
        Assert.Equal("cone (Spell's Trouble)", dmu.Abilities[0xBAC2].ShownAs);
    }
}

public class DamageMeterTests
{
    private static bool IsEnemy(Actor a) => !a.IsPlayer && a.Kind != ActorKind.Pet;

    [Theory]
    [InlineData("dmu_p1_puddles.log")]
    [InlineData("dmu_p1_cleave.log")]
    public void CreditsHitsTicksAndPetsToEachPlayerAtAnyMoment(string fixture)
    {
        var r = PullLoader.Load(TestEnv.OnlyPull(fixture), null, TestEnv.Registry);
        var meter = DamageMeter.For(r);
        foreach (var p in r.Party)
        {
            bool Mine(Actor? a) => a == p || (a is { Kind: ActorKind.Pet } && a.OwnerId == p.Id);
            var expected = r.Actions.Where(a => Mine(a.Source)).SelectMany(a => a.Hits).Where(h => h.Damage > 0 && IsEnemy(h.Target))
                            .Sum(h => (long)h.Damage) +
                           r.Ticks.Where(k => !k.IsHeal && Mine(k.Source) && IsEnemy(k.Target)).Sum(k => (long)k.Amount);
            Assert.Equal(expected, meter.DamageUntil(p, int.MaxValue));

            // Windows add up, and the total only grows.
            var mid = r.EndMs / 2;
            Assert.Equal(expected, meter.Damage(p, int.MinValue, mid) + meter.Damage(p, mid, int.MaxValue));
            Assert.True(meter.DamageUntil(p, mid) <= meter.DamageUntil(p, mid + 1000));
        }

        Assert.True(r.Party.Count(p => meter.DamageUntil(p, r.EndMs) > 0) >= 7);
    }

    [Fact]
    public void PetDamageCountsForItsOwner()
    {
        var r = PullLoader.Load(TestEnv.OnlyPull("dmu_p1_cleave.log"), null, TestEnv.Registry);
        var meter = DamageMeter.For(r);
        var pet = r.Actions.First(a => a.Source.Kind == ActorKind.Pet && a.Hits.Any(h => h.Damage > 0 && IsEnemy(h.Target)));
        var owner = Assert.Single(r.Party, p => p.Id == pet.Source.OwnerId);
        Assert.True(meter.Damage(owner, pet.T - 1, pet.T) >= pet.Hits.Where(h => IsEnemy(h.Target)).Sum(h => (long)h.Damage));
    }
}

public class FeedbackTests
{
    [Fact]
    public void ExcerptOfAPullIndexesBackToTheSamePull()
    {
        var pull = TestEnv.OnlyPull("dmu_p1_cleave.log");
        var path = Path.Combine(TestEnv.TempDir(), "excerpt.log");
        ExcerptInfo info;
        using (var writer = new StreamWriter(path, false, new System.Text.UTF8Encoding(false)))
            info = PullExcerpt.Write(pull, writer);

        Assert.True(info.Lines > 1000);
        Assert.Equal(pull.Party.Select((p, i) => $"Player{i + 1}"), pull.Party.Select(p => info.LogNames[p.Id]));
        Assert.All(File.ReadLines(path), l => Assert.EndsWith("|0000000000000000", l));
        Assert.StartsWith("01|2000-01-01T00:00:00", File.ReadLines(path).First());

        var again = Assert.Single(IndexStore.IndexFile(path, null, new EncounterObserverFactory(TestEnv.Registry), TestEnv.Registry.HashFor).Pulls);
        Assert.Equal(pull.Outcome, again.Outcome);
        Assert.Equal(pull.Deaths, again.Deaths);
        Assert.Equal(pull.Party.Count, again.Party.Count);
        Assert.InRange(again.DurationMs, pull.DurationMs - 50, pull.DurationMs + 50);
    }

    [Fact]
    public void PlayersWhoAppearMidPullAreAnonymizedToo()
    {
        // A player who isn't in the pull-start snapshot shows up halfway through and uses an ability.
        var lines = File.ReadAllLines(TestEnv.Fixture("dmu_p1_cleave.log")).ToList();
        var at = lines.FindIndex(400, l => l.StartsWith("21|"));
        var ts = lines[at].Split('|')[1];
        lines.InsertRange(at + 1,
        [
            $"03|{ts}|10ABCDEF|Stranger Person|26|64|0000|4A|Faraway|0|0|71622|226488|10000|10000|||100.00|100.00|0.00|0.00|0000000000000000",
            $"21|{ts}|10ABCDEF|Stranger Person|3E7D|Standard Step|10ABCDEF|Stranger Person|E|71A0000|0|0|0|0|0|0|0|0|0|0|0|0|0|0|226488|226488|10000|10000|||100.00|100.00|0.00|0.00|226488|226488|10000|10000|||100.00|100.00|0.00|0.00|0000BEEF|0|1|00||01|3E7D|3E7D|0.100|0000|0000000000000000",
        ]);
        var dir = TestEnv.TempDir();
        var source = Path.Combine(dir, "stranger.log");
        File.WriteAllLines(source, lines);
        var pull = Assert.Single(IndexStore.IndexFile(source, null, null, null).Pulls);

        var excerpt = new StringWriter();
        var info = PullExcerpt.Write(pull, excerpt);
        Assert.DoesNotContain("Stranger Person", excerpt.ToString());
        Assert.DoesNotContain("10ABCDEF", excerpt.ToString());
        Assert.DoesNotContain("Faraway", excerpt.ToString());
        Assert.Equal($"Player{pull.Party.Count + 1}", info.LogNames[0x10ABCDEF]);
    }

    [Fact]
    public void ReportCarriesSlotsNotNames()
    {
        var r = PullLoader.Load(TestEnv.OnlyPull("dmu_p1_cleave.log"), null, TestEnv.Registry);
        var report = WipeAnalyzer.Analyze(r);
        var root = report.RootCause!;
        var excerpt = new ExcerptInfo(1, r.Party.Select((p, i) => (p.Id, $"Player{i + 1}")).ToDictionary(x => x.Id, x => x.Item2));
        var body = FeedbackReport.Build(report, root, FeedbackCategory.WrongCulprit, "  " + new string('x', 5000) + "  ", "0.0.6.0",
                                        Guid.NewGuid(), "dmu 1234abcd", excerpt, "H4sI");

        Assert.Equal("wrong_culprit", (string?)body["category"]);
        Assert.Equal(FeedbackReport.MaxNote, ((string?)body["note"])!.Length);
        Assert.Equal(report.Incidents.IndexOf(root), (int?)body["report"]!["rootCause"]);
        Assert.Equal(root.Title.Length > 0, ((string?)body["incident"]!["title"])!.Length > 0);
        Assert.Equal(8, body["report"]!["players"]!.AsArray().Count);

        // Names appear only as the log's PlayerN labels in report.players, never in any text.
        var texts = body.DeepClone().AsObject();
        texts["report"]!.AsObject().Remove("players");
        var json = texts.ToJsonString();
        Assert.All(r.Party, p => Assert.DoesNotContain(p.Name, json));
        Assert.Contains("\"atFault\":[\"", json);
    }
}
