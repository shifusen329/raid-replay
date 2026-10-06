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
