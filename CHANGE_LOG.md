# Changelog

Notable changes to Raid Replay. Versions match the plugin version shown in `/xlplugins`.

## [0.0.2] - 2026-10-06

### Added

- **Phase 3 (Chaos & Exdeath) in Dancing Mad (Ultimate), from the transition through Earthquake.** Checked against a pull log that reaches P3.
  - The timeline splits P3 into Transition, The Decisive Battle, Bowels of Agony, Implosion, Umbra Smash + Ultima Blaster, Limit cut, The Decisive Battle II, Earthquake and Giant Kefka.
  - Chaos and Exdeath are drawn as bosses. The fire, water, wind and earth crystals appear on the map.
  - Every P3 attack has a name, a shape and a type. That includes:
    - Thunder III's big AoE and the two-hit buster;
    - the Implosion cleaves;
    - Ultima Blaster's dashes and the limit-cut lines;
    - Umbra Smash and Vacuum Wave;
    - the crystal attacks and the wind stacks.
  - Limit-cut markers are labelled 1–8.
  - The mitigation sheet is checked from Bowels of Agony to Earthquake.
- **New P3 wipe explanations:**
  - **Earth crystal double cleanse.** The report says when two pulses landed inside the Earth Resistance Down window, whose Accretion or Primordial Crust was cleansed, and by which heal or hit.
  - **Bait jump landing on the party (Umbra Smash).** The report says who was farthest when the cast started and who took the heavy hits. It also lists who was dead at the time, such as the usual baiter.
- **Late Phase 2 sections:** Light of Judgment II, Trine + Wings of Destruction, and Ultimate Embrace II.
- **`/rr`** opens the replay window, the same as `/raidreplay`.
- **Tele-trouncing teleporters look like they do in game:** amber pads with a chevron showing where they send you, drawn at their real size. Hovering one shows its direction, and the legend explains them.
- **Command line:** `rr dump-pull … --actors` lists every non-player actor with its hitbox and when it was visible. `rr statuses` and `rr status-detail` list debuffs, map effects and director lines.

### Changed

- P3 now starts at the transition (about 6:24, Aero III Assault), as the mitigation sheet does. It used to start when Chaos and Exdeath appear (about 7:00).
- Standing in another player's spread now counts as an avoidable hit, and the vulnerability it leaves is named when it makes a later hit lethal.
- Stacks taken by only a few players, such as the P3 wind stacks or the confetti, are judged on whether enough people took them. They no longer produce "missed stack" errors for everyone else.
- "Inside two stacks at once" is only flagged for role-assigned stacks such as the confetti, so a deliberate group stack is not an error.
- Boss attacks whose damage falls off with distance are only flagged as avoidable when they hit hard.

### Fixed

- P1 Kefka and his hitbox were never drawn during P1. He is now visible until the P2 transition.
- Mechanic markers could fire in the wrong phase; for example, P1's "Gaze" showed up in P3.
- Auto-attacks and other attacks the log leaves unnamed showed as "unknown_c252". They now use the encounter's name, e.g. "attack".
- Shields and buffs from pets (fairy, seraph, carbuncle) were listed as debuffs in death recaps.

## [0.0.1] - 2026-10-06

First alpha test build.

### Added

- **Replay.** Any pull from your ACT network logs is drawn on the arena map with:
  - waymarks;
  - each player's position, facing, HP and trail;
  - boss casts and AoE telegraphs;
  - towers, tethers and head markers;
  - deaths, each with a death recap.

  You can play, scrub, zoom and pan. The timeline shows phases, mechanics, casts, deaths and incidents.
- **After-action report between pulls.**
  - The plugin follows the log ACT is writing and opens a wipe card a few seconds after each wipe.
  - The card shows the verdict and the root cause, and who was at fault, with job icon and party slot.
  - A mini-map shows where everyone was and where they should have been.
  - Contributing incidents and the mitigation-vs-plan check are on the card.
  - A one-line summary goes to chat.
  - `/aar` reopens the last report.
- **Dancing Mad (Ultimate) Phases 1 and 2,** checked against real pulls:
  - Mitigation checked against the Ikuya Mitty sheet. It is cooldown-aware, so cooldowns the sheet asks for too early aren't blamed.
  - Damage Down resets (deliberate deaths) are recognised, and the report says when too many resets at once overwhelmed recovery.
  - Gravitas puddles: blames the rock that touched them, or a puddle soaked early or by too few players.
  - Double-trouble Trap (confetti): blames role-mates who skipped the stack, players caught in both stacks, and holders off their marker corner.
  - Tele-trouncing arrows: blames out-of-place arrows, arrows dropped on top of each other, arrows set off early, and Confused players who reached an ally.
- **Look and feel.**
  - A dark theme applied only to Raid Replay's own windows.
  - Panels can be resized, collapsed or popped out.
  - The pull list has labelled columns.
  - The timeline has zoom and a legend, and every keyboard shortcut is listed under "(?)".
- **Install from the custom repository** (`repo.json`). The default log folder is `%USERPROFILE%\AppData\Roaming\Advanced Combat Tracker\FFXIVLogs`.
