# Changelog

Notable changes to Raid Replay. Versions match the plugin version shown in `/xlplugins`.

## [0.0.8] - 2026-10-08

### Added

- **The pull list is grouped by entry:** each time you entered a duty, with its time limit (an Ultimate's two hours).
  - An entry's row shows when you entered, how much of the time limit was used, its pulls (or "Clear"), the zone and the best boss HP reached. Hover for the details.
  - Click an entry to show or hide its pulls. The newest entry, and the one holding the pull you're viewing, are open.
  - A roulette dungeon is now one row instead of a row per trash pack.
  - The button next to Zone switches back to one flat list.

- **Encounter packs** have new fields:
  - `shownAs` on an ability: the name players know an attack by, replacing the log's name everywhere;
  - `aim: "nearestToHolder"` on an ability: a cone or line fired at the player standing nearest its holder;
  - `misplacedPenalty` on an arrow puzzle: abilities that hit harder for each arrow left off its spot.

### Changed

- **Spell's Trouble attacks are called what players see:** "cone (Spell's Trouble)", "spread (Spell's Trouble)" and "stack (Spell's Trouble)". Their log names (Spellwave, Spellscatter, Spelldriver) have no cast bar and only appear in the battle log.
- **`ATTRIBUTION.md`** has the rules for cones fired at the player nearest their holder, short role stacks (the confetti) and Indulgent Will, with worked examples.
- **Log index:** the index format changed (it now records when you entered each duty), so the first launch re-indexes your log folder once.

### Fixed

- **The pull list only scrolled through the newest few dozen pulls,** e.g. pulls 49–90 of today's log, though its count said thousands. All pulls are listed again.
- **A Spell's Trouble cone that went to the wrong player blamed the player it killed.** The cone goes to whoever stands nearest its holder, so the holder aims it by where they stand.
  - When it goes to a player who was taking their own spread, stack or bait at that moment, the holder is at fault ("Out of position"). The report says who was nearest and who was next, e.g. 6.0 y against 6.3 y.
  - A player caught on the cone's way to its target is still judged against that target. Their death now names the cone's holder, e.g. "died to the off-tank's cone (Spell's Trouble)".
  - This changed the root cause of 7 of 185 Dancing Mad pulls in the test logs.
- **When two players died to a short confetti (Double-trouble Trap), the report blamed them.** It listed the players who took it as culprits, and only blamed the holder when nobody took it.
  - Now the holder or the role-mate who stayed out is at fault, whichever was off their spot: the marker corners for the third confetti, otherwise their usual spot. If both were, both are named. The players who took it are victims.
  - When a role-mate was already dead, too few were left to fill it, so the confetti traces back to that death and nobody is blamed for the confetti itself.
  - A support on their own corner, whom the DPS confetti was brought to, isn't blamed for missing their own.
  - This changed who is blamed in 69 of 1,258 Dancing Mad pulls in the test logs, and the root cause in 3.
- **Deaths to Indulgent Will blamed the players who died.** Indulgent Will hits harder for each arrow left on the ground off its spot (in 312 pulls, a median of 92k with none and 151k with four or more). A death it causes by itself now goes to whoever placed those arrows, as "Arrow placement failure", and the arrows get their own incident, timed to when they were dropped.
  - A player it didn't kill by itself, e.g. one at full HP who also stood in an Idyllic Will sleep circle, still died to whatever else hit them. So does anyone it killed with every arrow on its spot.
- **A mechanic left short because a player was dead traced back to their first death of the pull,** even when they had been raised since. It now traces to the death they were still dead from.

## [0.0.7] - 2026-10-07

### Added

- **Boss tab in the replay window** (after Party). It shows each boss on the field at the playhead with its HP and whether it can be targeted. Below that are the statuses on it, in two groups: the party's debuffs and DoTs, with who applied them, then the boss's own effects. Each status has its icon, stacks and time left.
- **Stack, spread and cone head markers have icons on the replay map,** in place of the plain diamond, and the legend shows them. In Dancing Mad these are Spell's Trouble's stack, spread and cone and the role-stack marker. Other markers keep the diamond.
- **Encounter packs:** a head marker can name its `icon` (`stack`, `stackGround`, `spread`, `fireSpread` or `cone`).

### Fixed

- **No report for some Tele-trouncing pulls.** The analysis crashed, so the pull got no after-action report, when either:
  - nobody stood near an arrow teleporter as it appeared;
  - every other player was dead when someone was Confused.

  This affected one pull out of 171 in the test logs.
- **Statuses that were re-applied while still on** (a refreshed DoT, Kardia, Dance Partner, Closed Position):
  - hovering a player showed the time until the status finally fell off, e.g. two minutes for Dia;
  - if it was still on when the pull ended, it disappeared one duration after it was first applied, so death recaps left these buffs out.

  Time left and stacks now follow the latest application.

## [0.0.6] - 2026-10-07

### Added

- **Feedback button in the after-action report,** for reporting what the analysis got wrong.
  - The form asks for the incident (the selected one by default), what's wrong (wrong player blamed, wrong root cause, a missed mistake, a wrong "should have been" spot, or something else) and a note.
  - The pull's log can be attached, anonymized: names become Player1–8, the date is shifted and chat is removed.
  - Every player name in the report becomes a party slot. "What will be sent" shows the exact report, and nothing is sent until you press Send.
  - A report that can't be delivered is kept and retried later.
  - See "Feedback" in the README for exactly what a report contains.
- **DPS tab in the replay window** (between Report and Deaths): each player's DPS, share of the party's damage and total damage at the playhead, plus the party's DPS.
  - Three windows: since the pull started (as ACT shows it), since the current phase started (what a DPS check measures), and the last 15 seconds.
  - Counts hits and DoT ticks on enemies, with pets' and summons' damage credited to their owner. Raid buffs aren't credited to whoever gave them.

### Changed

- **The after-action report opens by itself only after pulls in normal raids, Savage raids, Extreme and unreal trials, and Ultimate raids.** It used to open after every pull, including trash pulls in roulette dungeons.
  - Settings › Live has a checkbox for each kind of duty: the four above, plus normal trials, alliance raids (including Chaotic), dungeons, and other duties such as criterion and deep dungeons.
  - The report is still made after every pull, and `/aar` opens it.

### Fixed

- **`rr sanitize` kept some real names.** A player who first appeared during the pull, not at its start, kept their real name and id in the excerpt. They now get the next PlayerN like everyone else. The same anonymizer prepares the log attached to a feedback report.

## [0.0.5] - 2026-10-07

### Added

- **Plugin icon** in the plugin installer.

### Changed

- **Fewer copy buttons.** The after-action report has one copy icon at the top, for the summary, and a "Copy" link in each expanded incident. The "Copy summary" button at the bottom and the root cause's "Copy recap" link are gone.

### Fixed

- **Copied text pasted into the game's chat kept only its first line.** Both copies now give one line that fits the chat box (at most 500 bytes): what happened, who is at fault and how far off their spot they were. Right-click either one for the full multi-line text, for Discord.

## [0.0.4] - 2026-10-07

### Added

- **`ATTRIBUTION.md`:** the rules the report follows to pick the root cause and decide who is at fault, as two flowcharts.
- **"Out of position" verdict.** When another player's targeted AoE hits or kills someone (a tether rock, a Wave Cannon line, a spread), the report judges both players against their usual spots:
  - if the AoE's owner was off their spot, the owner is at fault;
  - otherwise, the player hit is at fault if they were off theirs;
  - a player standing with a group counts as on their spot.

  For example, a rock carried into the stacked supports now blames the rock's holder, not the tank who died.
- **Wave Cannon clips are reported.** A Wave Cannon line or a tether rock that hits anyone besides its own target is now an incident.
- **"Incorrect baiting" verdict for Past's/Future's End.** The strategy has everyone bait at max melee of Kefka. When a bait hits someone besides its target and positions don't settle who erred, whichever of the two was farther from max melee is at fault. A bait that clips another player is reported even when nobody dies.
- **Esc closes the after-action report,** even while the game has the focus. That press doesn't also open the game's system menu.
- **"Copy recap" on each incident in the after-action report** (the root cause and every expanded incident). It copies the incident as text: what happened, who is at fault, where they should have been, and for a death, the damage and healing taken in the 10 seconds before it.
- **Encounter packs** have new fields:
  - `soakOrder`: towers two groups soak in a fixed rotation;
  - `soakGroup: "role"` on towers that drop on a player;
  - `baitAt: "maxMelee"`: baits taken at max melee.

### Changed

- **Forsaken towers (P2) follow the soak order.** Group A is the first two Spell's Trouble stacks plus their melee/ranged partners. It soaks sets 1, 2, 3 and 8; group B soaks sets 4–7.
  - A tower short of players blames the set's group member who wasn't in a tower, or who doubled up in the other tower.
  - It used to blame whoever stood nearest, often a player from the other group.
  - Group members who were already dead are reported, and the wipe is traced to their deaths.
- **Wave Cannon towers follow the strategy.** Each tower is soaked by the role-mates of the player it dropped on who weren't targeted by a line. If those players were already dead, the report says so and traces the wipe to their deaths, instead of blaming whoever stood nearest. A player who doubled up in another tower counts as free to take the empty one.

### Fixed

- **Jumping off to end a lost pull was counted as a reset.** After a missed tower gives the whole party Damage Down, players who kill themselves to wipe are now "wiped on purpose": informational, and no part of what lost the pull.
- **"Damage Down resets overwhelmed recovery" is no longer a verdict.** The report shows the real root: the missed tower, or the avoidable hit that gave the Damage Down. Several resets are still noted on the root.
- **Shielded hits that gave Damage Down were ignored.** A hit a shield absorbed now counts as avoidable damage when it left a debuff, so a reset traces back to it.
- **Wrong Damage Down source:** the source of a Damage Down could be credited to a heal that landed at the same moment (e.g. Liturgy of the Bell). It is now the enemy hit that gave it.
- **Root cause too late:** a hit at the same moment as the collapse could be picked as its root cause; a cause now has to come before what it explains.
- **Unrelated root cause:** a failure that named nobody could adopt any nearby hit as its root cause; it no longer does.
- **AAR tables:** text in the expanded incident tables ("Where everyone was", mitigation) was cut off at the column edge. It now wraps.
- **Wave Cannon lines** could be drawn pointing the wrong way. In some logs the line's logged target is the statue itself; the line now uses its real direction when it fires.

## [0.0.3] - 2026-10-06

### Added

- **The rest of Dancing Mad (Ultimate): late Phase 3, Phase 4 and Phase 5,** checked against a full clear.
  - New timeline sections:
    - **Late P3:** hand slams and black holes, Damning Edict, Hot Tail, White Hole + Implosion II, Stomp-a-Mole + Knock Down, and the Meteor enrage.
    - **P4:** Mystery Magic + Grand Cross, Flood of Naught, Mana Charge, Blizzard III Blowout, Mana Release, and Ultima Upsurge.
    - **P5:** Ultima Repeater, Chaotic Flood, Maddening Orchestra, Celestriad, Stray Apocalypse, Forsaken, and Forsaken Null.
  - Every attack in these phases has a name, a shape and a type. Black holes, P4's Chaos and Neo Exdeath, and P5's Kefka are drawn. The laser tethers and stack markers are labelled.
  - The mitigation sheet is checked through P5. P4 and P5 entries are timed from the start of their phase, because P4 begins whenever the P3 bosses die.
- **New wipe explanations:**
  - **Black-hole laser:** a laser that passes through anyone other than its tethered player.
  - **Acceleration Bomb:** a bomb that goes off (Death Bomb) names its holder.
- **Logs written without OverlayPlugin are read.** Some players' ACT setups don't write OverlayPlugin's extra lines.
  - Pulls in these logs are found and replayed.
  - Bosses the log never removes are hidden once they leave the fight, so only one Kefka is drawn at a time (P1 Kefka after P2 starts, P4 Kefka in P5).
  - Analysis that needs those lines is skipped instead of guessed, such as the arrow puzzle and the confetti holder when it can't be placed.
  - Expect less precise positions in these logs.
- **Encounter packs** have two new fields:
  - `onlyTarget` marks a bait aimed at one player, so anyone else it hits took an avoidable hit;
  - `phase` on a mitigation entry times it from that phase's start instead of the pull start.

### Changed

- **Role stack nobody took:** when the rest of the role stood together out of its reach, the holder who left the group is blamed, not the players who stayed. One example is a confetti holder 9 y away from the other supports.
- **Enrage message:** it now gives the HP of the bosses you were attacking, such as Chaos and Exdeath in P3, not the largest boss on the field.
- **Log index:** the index format changed, so the first launch re-indexes your log folder once.

### Fixed

- **Tower blame:** an under-soaked tower blamed the player who **was** in it. It now blames whoever belonged in it and wasn't:
  - by default, the player who usually stands in that tower at that moment in your good pulls;
  - otherwise, the nearest free player.

  The players inside are still listed in the detail.
- **Positioning notes:** the notes under the root cause mixed in notes from other incidents, for example arrow spots from an earlier mechanic under a later tower. They now come from the root cause only.
- **Clears:** a clear showed a root cause, for example "DPS check" from P3's Meteor, a cast the kill cut short. Clears now read "Clear", and a cancelled enrage cast doesn't count as an enrage.
- **Section labels:** incidents in a phase without sections of its own were labelled with the previous phase's last section.
- **Giant Kefka:** P3's "Giant Kefka" section started at the second hand slam instead of the first.

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
