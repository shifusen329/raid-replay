# Test fixtures

Sanitized excerpts of real ACT network logs (FFXIV_ACT_Plugin 3.0.3.1), produced with:

    rr sanitize Network_30301_20261005.log <pull> RaidReplay.Core.Tests/Fixtures/<name>.log

Each file starts with a synthesized state header (zone, map, primary player, combatants, party, waymarks) built from
the pull's index checkpoint, followed by the pull's lines, anonymized:

- chat (00), status lists (38/42) and plugin debug lines are removed;
- player names become Player1..8 and player entity ids 10000001..; world names/ids are cleared;
- every line's ACT checksum is zeroed (it covers the original fields, so it would let a guessed name be confirmed);
- timestamps are shifted to start on 2000-01-01 UTC (relative timing preserved).

`FixtureHygieneTests` enforces this. Line endings are CRLF and must
stay byte-exact (see `.gitattributes`).

| File | Pull | What it exercises |
|---|---|---|
| `dmu_p1_cleave.log` | DMU #13, 0:26 wipe | countdown → pull → wipe; Revolting Ruin III tankbuster cleaving six non-tanks |
| `dmu_p1_puddles.log` | DMU #50, 1:33 wipe | Gravitas puddles dropped on top of each other → four Gravitational Explosions |
| `dmu_p1_resets.log` | DMU #11, 0:53 wipe | Damage Down from a real Blizzard cone → deliberate jump-off reset → unsoaked tower → more Damage Down → reset cascade |
| `dmu_p1_undersoak.log` | DMU #75, 2:08 wipe | Gravity III puddle soak at 2:01 taken by one player instead of four |
| `dmu_p1_arrows.log` | DMU #39, 3:15 wipe | Tele-trouncing: an arrow 3.9y inside its spot breaks a Confused chain → ally killed (root cause); three W arrows dropped on top of each other, two of them out of place |
| `dmu_p1_knockback_arrows.log` | DMU #61, 3:12 wipe | third confetti held 7.2y off its marker corner knocks two players into the arrows; Confused players who never reach an arrow or step in at a corner |
| `dmu_p1_indulgent_will.log` | DMU #70 (09-22), 3:07 wipe | one arrow left 10.5y off its spot makes Indulgent Will hit harder; it kills two players outright, which goes to the arrow's owner (root cause) |
| `dmu_p1_confetti_holder.log` | DMU #63 (10-01), 3:06 wipe | third DPS confetti held 10.1y off its marker corner goes to two supports on their own corner (both die) instead of the DPS: the holder is at fault (root cause) |
