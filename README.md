# Raid Replay

A Dalamud plugin for FINAL FANTASY XIV that rebuilds every pull from your ACT network logs. You can scrub through each pull on a map, and every wipe is explained the moment it happens.

- **Replay any pull.** Each pull is drawn on the arena map with:
  - waymarks and target signs;
  - every player's position, facing, HP and movement trail;
  - boss casts and AoE telegraphs (circles, cones, lines, donuts, half-room cleaves);
  - towers, puddles, tethers and head markers;
  - deaths, and a death recap for each one.

  Controls: play/pause, 0.25–8× speed, step, scrub, zoom and pan. A timeline shows phases, mechanics, boss casts, deaths and the incidents from the wipe report.
- **Live wipe analysis.** The plugin follows the log ACT is currently writing. When a pull ends, about two seconds later it:
  - prints a one-line summary to chat;
  - opens a **Wipe Report** with:
    - the verdict (DPS check, mechanic failure, avoidable damage, stack/spread error, death, fell off);
    - the root cause and the incidents that led to it;
    - for each incident, where every raider was, which way they faced, their HP, and **where they should have been**.
- **"Should have been"** comes from three sources:
  - **Learned positions:** where that player usually stood at that moment in your pulls where the mechanic went fine. Learned in the background from your log history.
  - **Safe spot:** the nearest point outside every damaging AoE resolving at that moment.
  - **Soak position:** the tower or stack the player should have been in.
  - **Assigned spot:** a fixed spot from the encounter pack, e.g. the spot a player's arrow belonged on, or the corner a knockback holder or its soakers stand on.

## Install (dev plugin)

1. Build: `dotnet build RaidReplay.slnx -c Release`. This needs the .NET 10 SDK and XIVLauncher's Dalamud dev install in `%AppData%\XIVLauncher\addon\Hooks\dev`.
2. In game, open `/xlsettings` → Experimental → Dev Plugin Locations, and add `…\RaidReplay\bin\x64\Release\RaidReplay.dll`.
3. Open `/xlplugins` → Dev Tools and enable **Raid Replay**.

**Commands:**
- `/raidreplay` (or `/rreplay`): the replay window.
- `/aar` (or `/raidreplay report`): the after-action report of the last wipe; again to close.
- `/raidreplay config`: settings.

ACT must write network logs. By default the plugin reads `%USERPROFILE%\AppData\Roaming\Advanced Combat Tracker\FFXIVLogs`; you can change this in settings (environment variables are expanded). Indexing is incremental and cached; the first pass over about 23 GB of logs takes under a minute.

## Encounter packs

The engine works on any fight. It reads AoE shapes from the game's Action sheet and infers bosses, helpers, phases and raidwides from what happened in the log. For fights you progress, an **encounter pack** (JSON) makes the replay and analysis precise. A pack can describe:
- phases and sub-phases (triggered by casts, director lines, spawns, map effects, …);
- mechanic labels;
- actor roles (boss, helper, clone, and so on, keyed by BNpcBase);
- event objects such as puddles and teleporters;
- per-ability AoE shape, origin, heading and category (danger, fake, tower, stack, spread, tankbuster, bait, …), including telegraphs drawn before the hit;
- towers spawned by map effects;
- head-marker and tether labels;
- soak counts and who soaks (e.g. the holder's role group), and fixed holder/soaker spots relative to waymarks;
- arrow-teleporter puzzles: the intended layout and how far a teleport carries and chains;
- failure abilities.

**Built-in pack: Dancing Mad (Ultimate)** ([`dmu.json`](RaidReplay.Core/Encounters/Packs/dmu.json)).
- P1 and P2 are verified against real logs. Every shape was tuned with `rr validate-shapes`: most score 0.98–1.00 precision against actual hits.
- P3–P5 are placeholders taken from the guides until logs reach those phases.
- Tele-trouncing (Graven Image III) is checked arrow by arrow:
  - every arrow is matched to the spot it belonged on (the plain clockwise square, or the corner variant some groups use);
  - arrows dropped on top of each other, or used up before the confusion (including by an out-of-place confetti knockback), are blamed on whoever was out of place;
  - each Confused player is followed through the arrows actually on the ground. This agrees with the game's own puzzle verdict on every logged pull;
  - a Confused player's kill goes to whatever broke their chain.

**Your own packs:** put them in the plugin's `encounters` folder (Settings → Encounters → Open folder). A pack whose `key` matches a built-in pack replaces it. "Export built-in packs" writes copies you can start from.

## Command line (`RaidReplay.Cli`, `rr.exe`)

The parser, indexer, loader and analyzer live in `RaidReplay.Core`, which does not depend on Dalamud. The CLI runs them outside the game. It reads `LOGS_PATH` from the environment or from `.env`. If the game is installed, it reads action data from the game files with Lumina, using the Lumina copy in your Dalamud dev install.

```
rr pulls latest                        # list pulls of the newest log
rr dump-pull <file> <n> [--events]     # casts, deaths, phases, markers of pull n
rr dump-frame <file> <n> --at 42.5     # everyone's position / active AoEs at a time
rr analyze <file> <n>                  # wipe report
rr learn [file|all]                    # build position profiles from past pulls
rr watch                               # live: analyze each pull as it ends
rr validate-shapes <file>              # AoE shape precision/recall vs actual hits
rr validate-contact <file> <trigger> <eobj> <failure>  # calibrate "X touching Y detonates" blame distances
rr arrows <file> <n>                   # arrow puzzle: who dropped what where, chains, findings
rr arrows-calibrate <file>             # arrow drops/stacking and simulated chains vs the game's verdict, all pulls
rr stack-survey <file> <action> [--geo] # every resolution of a stack: soakers, damage, deaths (positions vs waymarks)
rr phases <file> · rr index · rr stats <file> · rr sanitize <file> <n> <out> · rr map-png <mapId> <out.png>
```

## Layout

| Project | |
|---|---|
| `RaidReplay.Core` | Log parsing (span-based, ~800 MB/s), pull detection, cached and resumable per-file index, live tailer, pull reconstruction (actor tracks, casts/actions/hits, statuses, deaths), AoE inference, encounter packs, wipe analyzer, position profiles |
| `RaidReplay` | Dalamud plugin: background service, ImGui replay canvas, timeline, report windows, Lumina game data |
| `RaidReplay.Cli` | `rr` tool for inspecting and validating outside the game |
| `RaidReplay.Core.Tests` | xUnit tests on sanitized log fixtures. Integration tests run against your real logs when `LOGS_PATH` is set |

## Known limitations

- **Position sampling:** ACT logs positions about 1–3 times per second per player, so fast movement such as knockbacks and teleports is interpolated between samples.
- **Waymarks** placed before ACT started logging are not visible.
- **Tether end times** are inferred.
- **Without a pack,** classifying abilities as "avoidable" is heuristic.
