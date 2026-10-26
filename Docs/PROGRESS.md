# Tactica — Progress

> **Companion to [ARCHITECTURE.md](ARCHITECTURE.md), not a duplicate of it.**
> That document describes *what the system is today*. This one tracks *how far along we are* and
> *why things were decided the way they were*. Update it when a phase starts or finishes, or when a
> decision is made that a future reader would otherwise have to reverse-engineer.
>
> High-level by design. If you need the mechanism, the invariant, or the gotcha, it is in
> ARCHITECTURE.md. What the game is *meant* to become is in [GAME_DESIGN.md](GAME_DESIGN.md); the
> roadmap below is its build order.

Last updated 2026-10-25.

---

## Phases

- [x] **Phase 0** — Setup
- [x] **Phase 1** — Grid System
- [x] **Phase 2** — Camera
- [x] **Phase 3** — Job / Stat System
- [x] **Phase 4** — Combat Core
- [x] **Phase 5** — UI
- [x] **Phase 6** — Enemy AI
- [x] **Phase 7** — Recruitment / Mission Flow (prototype)
- [x] **Phase 8** — Ability & Job Data
- [x] **Phase 9** — JP, Progression & Save Format
- [x] **Phase 10** — Crew Screen
- [x] **Phase 11** — Combat Depth
- [x] **Phase 12** — First Vertical Slice: Classes & Enemies
- [x] **Phase 13** — Enemy AI II
- [x] **Phase 14** — World Map, Districts, Shop & Story
- [~] **Phase 15** — Polish (first pass done; real UI and audio pass remain) ← *currently here*

Parallel track, not a phase: **Art** — character models in progress (see Roadmap).

---

## Completed

### Phase 0 — Setup

Unity 6000.5.2f1 on URP (since upgraded to 6000.6.7f1), with Active Input Handling set to **Input System package only**. Five
namespaces (`Grid`, `Camera`, `Stats`, `Combat`, `UI`), each mapping 1:1 to a folder under
`Assets/Scripts/`. `.gitignore` audited so nothing outside the project itself is tracked.

*Decision:* input-backend `#if` plumbing is centralised in `GridInput` / `CameraInput` rather than
scattered, so the legacy-vs-new-input question is answered in exactly two files.

### Phase 1 — Grid System

Tile data, rendering, hover detection, and pathfinding. `GridTile` structs in a
`Dictionary<Vector2Int, GridTile>`; one cube GameObject per tile; raycast picking masked to a
`Tiles` layer; layered highlight states resolved to a material in one place.

*Decisions:* a dictionary rather than a 2D array, to keep sparse and irregular maps possible from
the start — which generated maps now use. *(Superseded in Phase 7: movement and range are both
Manhattan now.)* Movement allows **one diagonal step per path** — so `(2,2)` costs 3 — while ability
range stays pure Chebyshev, where it costs 2. The two answer different questions (a path pays for
ground covered; an arrow does not walk), and the divergence is deliberate rather than a bug to
reconcile.

### Phase 2 — Camera

Orbit/pan/zoom tactics camera driven by a pivot, yaw, pitch and distance, with a single method
writing the transform so the three controls cannot fight each other. Auto-frames the board from the
grid's own dimensions on start.

*Decision:* **FFT-style 90° snap rotation** on Q/E rather than free orbit — it keeps the board
readable from four fixed angles. Input is only read while idle, so mashing the key cannot queue
rotations; extra presses are discarded rather than buffered.

### Phase 3 — Job / Stat System

`JobDefinition` and `AbilityDefinition` ScriptableObjects, per-level stat growth, and ability
unlocks gated by level. Units derive their numbers from job + level rather than storing them.

*Decision:* stats are split in two. `CharacterStats` holds **capabilities** (recomputable at any
moment from job and level); `CurrentStats` holds **live pools** (HP/MP — authoritative history that
nothing can recompute). Split this way, "recalculating stats accidentally heals the unit" is not a
bug you can write. Levelling fills nothing; it only raises the cap.

### Phase 4 — Combat Core

Turn order, a combat state machine, action resolution, and end conditions. Initiative is a snapshot
built from Speed at the start of an encounter. *(Superseded in Phase 7 by the CT clock; the Speed
order survives as the tie-break.)*

*Decisions:* ties in initiative break on **original roster index**, explicitly, because a
non-deterministic turn order makes bugs reproduce only sometimes. `ActionResolver` is a plain C#
class, not a MonoBehaviour, so combat maths is constructible and testable without a scene. Damage is
floored at 1 — a negative result would otherwise *heal* the target.

### Phase 5 — UI

World-space HP/MP bars, a screen-space HUD with active-unit and target panels, and the action menu
that drives the whole player turn.

*Decisions:* unit bars are built **entirely in code** — a hand-authored hierarchy cannot be
pre-made for a unit that gets spawned at runtime for a recruitment roster. Turn economy is
**move once per turn, attack uncapped** *(superseded in Phase 7: one move and one action)*. **Wait grants a defensive stance** (+5 Defense until the
unit next acts) *only if it did not attack* — moving into cover and bracing still earns it, so Wait
is never strictly worse than standing still. The move range appears **only** on an explicit Move
press; showing it automatically at turn start lit tiles that ignored clicks, which read as a broken
grid.

*Deferred:* the defensive stance has no status icon yet — it is visible only in the console.

### Phase 6 — Enemy AI

Non-player units take their own turns end to end: pick a target, walk into range, attack, hand the
turn on. A full encounter now plays through with no manual intervention on the AI side.
`EnemyAIController` reuses the same primitives the player path does — the move-range flood fill,
`TryMoveUnitToTile`, `ActionResolver` — so both sides are bound by identical rules.

*Decisions:* the AI is a **sibling of `PlayerActionController`, not a branch inside it**. The two
share every action primitive and differ only in where the decision comes from — a button versus an
evaluation — so merging them would buy nothing and cost a class whose every method opens with
"if this is an AI turn". Both subscribe to the same turn-change event and ignore the turns that are
not theirs, so neither knows the other exists.

A turn runs as a **coroutine**, and that is structural rather than cosmetic: ending a turn inline
would call `EndCurrentTurn` from inside the event that dispatched it, so an all-AI round would
resolve as one nested call stack in a single frame with the state machine re-entering itself.
Yielding first gives each turn its own frame — and, incidentally, makes it watchable.

Ending an AI turn calls `EndCurrentTurn` **directly** rather than going through anything shaped
like the player's Wait. Wait grants the defensive stance, which is a player *choice*; an AI unit
that waited would be taking a decision nobody made.

*Deferred:* target selection is a placeholder heuristic — see below.

*Follow-up, since:* combat now ends **the moment the last unit on a side goes down**, not at the
next turn boundary. `CombatManager.TryEndCombatNow()` holds the transition, and both controllers
call it right after an attack lands — not `ActionResolver`, which stays a plain C# object with no
scene dependency so combat maths remains testable without a scene. Before this, knocking out the
final enemy left the fight running until the player pressed Wait, with corpses still targetable.

### Phase 7 — Recruitment / Mission Flow (prototype)

Closed 2026-10-25. Everything below shipped; what was still open (persistent HP, XP) moved to
Phases 9 and 14.

**Prototype loop in place.** Main menu → headquarters → roster / recruit / mission select → combat
→ result screen → headquarters, with a JSON save. Menus are deliberately bare — black background,
grey buttons, built in code — because their job right now is to make factions and recruitment
*testable*, not to be the UI. A new `Tactica.Campaign` namespace holds the data and rules
(`CampaignState`, plain C#) and the one scene-facing piece (`MissionRunner`, which spawns units and
reports results); `CampaignMenu` in `UI` drives both and boots itself, so no scene edit was needed.

*Decisions (first cut; the later entries below supersede the map, recruitment and Neutral
rules):* missions carry **no map data** — spawn tiles are chosen per side at runtime, so a
mission is only "who you fight". Roster units carry **no HP** between fights; persistent damage
needs a rest/heal system to mean anything. After a **victory** any opponent can be recruited, the
defeated included; after a defeat or retreat only those who **ended on the Player side** can — which
makes a mid-fight `SetFaction` a recruitment. A won mission gives deployed survivors **+1 level**,
labelled as a stand-in for XP. An in-combat **Units / Factions** panel cycles any unit's side, as the
test bench for all of this.

*Since:* KO'd units **no longer take turns** — with several units a side, waiting through every
corpse was most of a round. They still occupy their tile.

**Blocker cleared: factions are real now.** The `isPlayerControlled` bool became a
`Faction { Player, Enemy, Neutral }` enum owned by `GridUnit`, with `SetFaction` as the entry point
recruitment will call. `ActionResolver.IsValidTarget` compares factions instead of treating anyone
who is not the caster as an enemy, so allies can no longer be attacked and `TargetsAllies` —
previously able to hit nobody at all — works. `CheckCombatEndCondition` counts Player and Enemy
only. This was done ahead of recruitment on purpose: a recruited unit changes side by definition, so
the concept had to exist before the feature that needs it.

*Decisions:* `Neutral` is included but unused, because adding an enum member later is free and
migrating serialized data twice is not. Its hostility is **provisional** — the only rule is
"different faction is a valid target", so a neutral is currently attackable by both sides. That is
right for wild creatures and wrong for a recruitable villager, and the real answer is a
per-faction relationship table, since hostility is not symmetric. Flagged in
`ActionResolver.IsValidTarget` rather than silently decided.

**Edge case fixed with it.** Converting the *last* enemy used to leave combat `Ongoing` forever: the
empty-side guard in `CheckCombatEndCondition` could not tell "never had enemies" from "converted
them all". `StartCombat` now snapshots which sides were present, and the guard reads that.

**Open at the time:** a neutral hostility table (made unnecessary by guests), persistent HP and XP
(now Phases 9 and 14), and mission objectives (since added).

**Refactor pass (2026-10-24).** A full read of every script, then targeted fixes rather than
rewrites — most of the code's oddities are documented decisions, and those were left alone.

- *Bug fixed:* the HUD drove whichever unit held the turn. Move during an AI turn walked the enemy;
  Wait ended the AI's turn and gave it a stance. Player input now refuses non-Player units, and the
  buttons grey out on other turns.
- *Both namespace cycles broken,* each by turning a field into an event:
  `GridCursorHighlighter.TileClicked` (Grid ↔ Combat) and `GridUnit.Spawned` / `StatsChanged`
  with a self-attaching `UnitStatusBar` (Grid ↔ UI). `Grid` now depends only on `Stats`, so an
  asmdef split is possible.
- *Scaffolding removed:* MP spending goes through `GridUnit.SpendMP`, so `ActionResolver` no longer
  pokes the status bar. `GridUnit.OnDestroy` releases its tile.
- *Smaller:* targeting resets on every turn change, not only on Wait; logs and messages use unit
  names; the ability-range tooltip no longer claims movement uses the same metric.

**FFT systems, cyberpunk-flavoured (2026-10-24).** The first five roadmap items from the FFT notes:

- *CT clock* replaces fixed initiative: Speed fills CT each tick, 100 takes a turn, the turn costs
  100 / 80 / 60 by move-and-act / one / neither, carry-over capped at 60. A **Turn Queue** strip
  forecasts the next 8 turns.
- *One action per turn* through an **Act** menu listing every unlocked ability — this also retired
  the "first unlocked ability" placeholder on the player side.
- *Flatline* (FFT's death counter): a downed unit has three of its own turns; then it is gone, and a
  flatlined crew member is removed from the roster for good. **Reboot Protocol** (Lv2 Cyber Knight)
  brings one back before that.
- *System Override* (FFT's Invite) hacks a unit onto your side, `Power + Magic`% (≈41% at Lv2).
  Recruitment after a contract now requires it: the result screen no longer hands out every
  defeated enemy.
- *Targeting preview* — damage and resulting HP, or hack chance, before committing.
- Menus and contracts renamed to the setting: Safehouse, Contracts, Crew, Hire Runners, Jack Out.

*Decisions:* knocked-out units keep charging CT so the flatline deadline runs on the same clock as
everything else (fast units go down faster, as in FFT). `Heal` refuses the knocked out, so standing
someone up always goes through Revive and clears the counter. Wait keeps its defensive stance on top
of the cheaper CT cost for now — flagged in ARCHITECTURE.md as a double reward to revisit with facing.

**Second FFT pass (2026-10-24).**

- *Facing and evasion:* units face where they walked and what they hit; a neon visor shows it.
  Physical hits roll against the target's Evasion — full from the front, half from the side, none
  from behind, doubled while defending. Cyber Knights 10%, Gooners 20%.
- *Manhattan movement and range,* replacing 8-directional movement with a one-diagonal limit. Closes
  the known "diagonals are strictly better" problem; movement and range now share one metric. Each
  unit's Jump is the climb limit.
- *AI reboots* downed allies it can reach; Gooners learn Reboot at level 3.
- *Objectives:* Neutralize (win when the target drops or is hacked) and Protect (lose when the guest
  drops), with two new contracts — **Cut the Signal** and **Courier Run** — and the objective shown
  in combat.

*Decisions:* objectives only ever end a fight early; the side-is-down end condition still holds for
every mission. Facing is automatic for now — FFT's end-of-turn facing choice is the next UI step.

**Guests and generated maps (2026-10-25).**

- *Side and control split,* FFT's guest model: a unit's faction decides who it may hit and who
  wins; `IsPlayerControlled` (Player side, not a guest) decides who gives it orders. The Courier and
  the Civilian are now AI-driven guests; losing a guest is a Protect objective, and guests do not
  hold the Player side up. Block Watch became a Protect contract.
- *Neutral is now a deliberate third party* hostile to everyone, unused by current contracts. The
  relationship table it would have needed is unnecessary with guests.
- *Generated maps per contract:* wider boards (12x10 to 16x12) with terraces, ragged edges, holes and
  raised cover, from a seed. Tiles render as height-tinted columns; the camera reframes per map.
- *Movement retuned for the bigger boards:* Cyber Knight Move 3, Gooner Move 2 and Jump 2 (they can
  climb cover; knights cannot).
- *AI approach uses walking distance,* so it routes around holes and cover instead of stalling
  against them.

**FFT research.** [FFT_DESIGN_NOTES.md](FFT_DESIGN_NOTES.md) records how Final Fantasy Tactics'
systems work, with numbers from the Battle Mechanics Guide, why each works, and a prioritised list
of what fits Tactica — one action per turn and a CT turn system first.

**Game design document (2026-10-25).** [GAME_DESIGN.md](GAME_DESIGN.md) is the target design: 18
classes in three tiers with JP and hybrid slots, seven weapon types, districts on a city map, a
story, and the enemy roster. Copied into the repo with a few corrections where it disagreed with
the code (Override's Power is 35, the enemy table is planned, Wait's rewards). The roadmap below is
its build order.

---

## Roadmap

From GAME_DESIGN.md's "Yol haritası", with one change: **combat depth comes before class content.**
Most of the slice's abilities (Rend Plating, Lag Spike, Stim, Marked Shot, Shockwave) are status
effects, AoE or reactions, and the design document itself lists "abilities need infrastructure
first" as a risk. Building the classes first would mean shipping them with half their kits logging
"not implemented".

Each phase lists what it delivers and how we know it is done.

### Phase 8 — Ability & Job Data ✅ (2026-10-25)
Small data changes everything after builds on. **Done:** `WeaponType`, `AbilitySlot`, `JobTier` and
`JobRequirement` in `Stats/ProgressionTypes.cs`; abilities carry slot, JP cost and required weapon;
jobs carry tier, prerequisites, allowed weapons and an `isFixedKit` flag for enemy jobs. `Magic` is
`Tech` (authored values kept through `FormerlySerializedAs`). The level-unlock list stays as each
job's ability list - for crew it becomes what JP can buy (Phase 9), for fixed-kit enemies it still
unlocks by level. Cyber Knight re-authored (Start tier, Shield Gauntlet, JP 100/200/300), Gooner
marked fixed-kit; combat unchanged.
- `AbilityDefinition`: `jpCost`, slot type (Action / Reaction / Support / Movement), weapon
  requirement.
- `JobDefinition`: tier (start / mid / top), prerequisites (job + level pairs), allowed weapon types,
  ability list in place of level unlocks.
- Rename `Magic` to `Tech` (with `FormerlySerializedAs`), as the design calls it.
- *Done when:* the existing Cyber Knight and Gooner are re-authored in the new shape with no
  behaviour change in combat.

### Phase 9 — JP, Progression & Save Format ✅ (2026-10-25)
**Done:** `Stats/Progression.cs` holds the numbers (job-level table, JP and XP per action).
`ActionResolver.OnActionResolved` reports each carried-out action with an *effective* flag;
`MissionRunner` turns effective crew actions into JP and XP. `CampaignState.ApplyResult` grants JP
to the primary job, shares a quarter with every other surviving crew member in the fight, and turns
XP into character levels (100 each) - the +1-level-per-win placeholder is gone. `RosterUnit` holds
JP per job (lifetime and spendable), learned abilities, secondary job, passive slots and a Locked
flag (hacked recruits); crew fight with what they have learned (`GridUnit.SetActionLoadout`).
**Decision:** v1 saves are **migrated, not invalidated** - each unit becomes a fresh v2 unit with its
starter ability learned. Verified in the editor: two hits on a Gooner gave Aria 48 JP and 28 XP,
Bram 12 shared JP, and the v2 save round-trips.

- JP per effective action — `[(8 + 2 × job level + [char level / 4]) × M]`, M = 3 — with 1/4 shared
  to the rest of the crew; nothing for misses or waiting.
- Job levels 0–8 from lifetime JP (100 / 200 / 350 / 550 / 800 / 1150 / 1550 / 2100).
- XP and character level, replacing the +1-level-per-win placeholder.
- `RosterUnit`: JP per job, learned abilities, slot choices, weapon, locked flag (hacked units).
- New `campaign.json` format; older saves are migrated on load (see the decision above).
- *Done when:* a contract's result screen shows JP and XP earned, and a reload keeps them.

### Phase 10 — Crew Screen ✅ (2026-10-25)
**Done:** a **Manage** screen per crew member (replacing the Lv −/+ debug buttons): primary and
secondary job, the three passive slots, and buying abilities with any unlocked job's JP via job
tabs. The rules - prerequisites, the hybrid rule, JP cost, slot fit, locked units - are static
methods on `CampaignState`; a refused button shows its reason. `CampaignConfig.playerJobs` lists the
jobs offered. Verified: Aria bought System Override for 200 JP and fights with it. The "unlock Warden,
Blade secondary" check waits for Phase 12's jobs.

- Primary and secondary job, Reaction / Support / Movement slots, buying abilities with JP, weapon
  choice.
- Rules enforced in `CampaignState`, not the UI: prerequisites, the hybrid rule (no two top-tier
  jobs), Equip abilities opening weapon types.
- Drops the Lv −/+ debug buttons.
- *Done when:* a unit can unlock Warden from Cyber Knight Lv3 and fight with a Blade secondary.

### Phase 11 — Combat Depth ✅ (2026-10-25)
**Done.** Statuses (`GridUnit.ActiveStatus`): Buff/Debuff stat modifiers, Stun (turn lost), damage
and healing over time, Taunt - durations in the affected unit's own turns, ticked by `CombatManager`
before the turn is handed over. AoE: abilities aim at a tile; harmful blasts hit everyone in the
radius (friendly fire, flagged "ALLY!" in the preview), helpful ones only valid targets.
Reactions: Counter and Brace, each with its own trigger % (Brave replaced by fixed rates);
counters never chain. Passives: Support/Movement stat modifiers, Equip X weapon permission, JP
Boost. Weapons: `EquipmentDefinition`, an inventory, and a basic **Attack** (weapon Power, weapon
range/blast) first in every Act menu; weapon-bound abilities grey out with the reason. Tech scales
heals, reboots, hacks and Tech statuses. Height: ranged reach ±1 per 2 levels. End-of-turn facing
choice on Wait. Override: `Power + 3 × Tech − 4/level gap − resistance`, and a hacked unit's CT
resets to 0. `ActionResolver` reads the board through two delegates, staying scene-free.
**Decisions:** Override formula `Power 9 + 3 × Tech` (System Override Power 9). Hacked units join
permanently, locked. Reactions use per-ability rates. Wait keeps its stance - the facing choice
adds no new reward. Pure Tech abilities ignore evasion; weapon-bound Tech ones (Venom Edge) roll.
**Verified in the editor:** Attack 13 power → 9 damage on a Raider; Override 8% on the resistant
Kingpin, 32% on a Raider; Lag Spike skipped the Raider's turn and expired; Shield Wall buffed three
units; facing set by click; Counter Bash fired 4/10 at 35%; Shockwave hit a Raider and an ally.

- **Status effects with durations** on the CT clock (Buff / Debuff / StatusEffect stop being log-only).
- **AoE** — `AreaOfEffect` read at last; friendly fire on.
- **Reactions** — a post-hit event hook, fixed trigger % per reaction, no counter-to-a-counter.
- **Basic weapon attack** from the equipped weapon as the first Act entry; `EquipmentDefinition`
  (type, Mk tier, Power, bonus).
- **Tech scaling** for hack damage and healing.
- **End-of-turn facing choice**, and a decision on Wait's double reward once facing is chosen.
- **Height in combat** — e.g. ranged reach +1 from higher ground (design pillar "position matters").
- **Override formula and brakes** — the open `Power 9 + 3 × Tech` question, level penalty, boss
  resistance, CT 0 for a freshly hacked unit.
- *Done when:* every T2 ability in the slice can be built from data with no new code.

### Phase 12 — First Vertical Slice: Classes & Enemies ✅ (2026-10-25)
**Done.** Six player jobs with full kits and JP costs - Cyber Knight, Blade, Netrunner, Field Medic
(start) and Warden (Cyber Knight Lv3), Phantom (Blade Lv3) - and six fixed-kit enemy jobs: Gooner,
Raider, Slinger, Patch Rat, Chrome Brute, Kingpin (GAME_DESIGN.md's numbers). Fourteen weapons (Mk I
and II of every family). Override moved to the Netrunner and Reboot to the Field Medic, as the
design has it. A new game starts Aria (Cyber Knight), Rex (Netrunner) and Bram (Field Medic); the
six contracts use the real enemy jobs, with hack resistance on Kingpin (20) and Brute (30). Content
is authored by an idempotent editor script, so it can be re-run after tuning.
*Not in this slice:* Deadeye, Packet Thief, Corp Trooper, Sec-Drone, top-tier jobs.

- Classes: Cyber Knight, Blade, Netrunner, Field Medic, then Warden and Phantom.
- Enemies: Gooner, Raider, Slinger, Patch Rat; Chrome Brute as the boss. Each its own job with
  fixed abilities, replacing "a Gooner at level N".
- Starting crew and hires come with their job's cheapest active learned.
- *Done when:* the six contracts play with real enemy types and a crew built in the Crew screen.

### Phase 13 — Enemy AI II ✅ (2026-10-25)
**Done.** `EnemyAIController.EvaluateActions` scores every usable ability against every target it
can reach this turn (via `ActionResolver.IsInRangeFrom` over its standing tiles): expected damage
× hit chance, a kill bonus, a role bonus for medics and hackers, a friendly-fire penalty for blasts,
heals below 60% HP, reboots, buffs not already running, stuns, DoTs and debuffs on enemies, hack
chance. Taunt locks a unit's harmful options onto its taunter whenever it can reach it. The
lowest-HP search survives only as the approach fallback when nothing is in reach. New enemies:
Packet Thief (Ping, Lag Spike), Corp Trooper (Burst Fire), and the **Sec-Drone**, Neutral, hostile
to everyone - used in the new **Data Heist** contract.
**Verified in the editor:** troopers finished a wounded drone (kill bonus) while drones and
troopers fought each other; a Provoked trooper shot Aria over the softer Netrunner beside it;
Venom Edge ticked 6 per turn.

- Role-aware targeting (medics and hackers first), securable kills, exposure.
- Enemies that debuff and hack (Packet Thief); reactions to Taunt and Overwatch.
- Sec-Drone as the first real Neutral third party.
- *Done when:* the lowest-HP placeholder heuristic is gone.

### Phase 14 — World Map, Districts, Shop & Story ✅ (2026-10-25)
**Done.** `DistrictDefinition` (in `CampaignConfig.districts`) carries danger, connections, shop
stock, hiring jobs, job-board listings, story missions and road-encounter templates; missions gain
`kind` (Listing/Story), credits, loot, intro/outro text, `unlocksDistrict` and `joinsCrew`. The
district screen (`CampaignMenu.Map.cs`) replaces the Safehouse: Story, Job Board, Crew, Shop, Hire,
Travel. Travel rolls an ambush at 15% × danger (max 75%) from the destination's templates; winning
arrives and pays half, losing or jacking out leaves the crew where it was. Credits pay for hires
(80 × level) and Mk I–II weapons; spare weapons sell for half. Every fight goes through a briefing
(story text, objective, opposition, guests, pay, deploy toggles). Save v3 adds credits, district,
open districts and finished story; v1/v2 saves migrate into Neon Row with 300 cr.
Content: **Neon Row** (Velvet Room Raid → Yuki joins, opens Rustyard; Street Sweep, Gang Turf),
**Rustyard** (Chrome Brute → opens Spire Gate; Block Watch, Courier Run), **Spire Gate** (Cut the
Signal; Data Heist).
**Verified in the editor:** Velvet Room Raid briefing and fight; KO'ing Jacknife paid 250 cr +
Monoblade Mk I, Yuki joined (no duplicate Recruit button), Rustyard opened; travel refused with no
road, ambush at roll 0, clean arrival at 0.99; buy/sell; v3 save round trip; ambush win paid 50 cr
and arrived in Neon Row; the user's v2 save loads into Neon Row.
Decisions: road encounters use the normal flatline counter (jacking out is the retreat); HP is not
persistent - no rest system yet to give it meaning.

- A node or list map with 3 districts as the main menu (replacing the Safehouse screen): crew, shop,
  hiring and job board per district.
- Battle types: story battles, road encounters (with a retreat or longer flatline counter), job-board
  contracts. Current contracts become story battles and listings.
- Currency and a simple shop (Mk I–II); pre-battle screen (pick 4 of 8, enemy preview).
- Story text boxes; the opening raid where Yuki joins as a guest, then for good.
- Persistent HP between fights, if the district loop makes resting meaningful.
- *Done when:* a new game runs from the opening raid through one district's story line.

### Phase 15 — Polish (first pass ✅ 2026-10-25)
**First pass done** in `UI/UnitFeedback.cs`: faction rings on the ground (cyan crew, green guest,
red enemy, yellow neutral, magenta once hacked, faded while KO); floating damage/heal numbers, KO,
REBOOT, new status/stance tags (BRACED, Lag Spike...), HACKED, MISS and RESISTED; units hop
between tiles instead of snapping, shake when hit, fall over when knocked out and stand on reboot;
synthesised blips for hit, heal, KO, miss, status and hack.
**Verified in the editor:** -5 / -16 KO / +3 / HACKED callouts and rings on the Velvet Room board;
a KO'd Gooner lay down (Z 80°) and stood on Revive; Aria's move tweened from (10,0) to (9,1); a
non-effective Attack showed MISS.
**Prototype menu pass (2026-10-25).** Still black and grey, but every screen now shares one
frame: title, status strip (district, credits, crew, deployed), scrolling content, notice, Back
(Escape does the same). Colour means something: teal = on (deployed, current job, learned), blue =
the main action (Story, Fight, Continue), red = armed confirmation. Dismiss, New Game over a save
and Jack Out mid-fight need a second click. The unit screen (`CampaignMenu.Unit.cs`) fits one window, no scrolling: a
fixed character card (stats, weapon, passives) beside Jobs / Equipment / Learn tabs, with crew
prev/next; spare weapons and learned passives are picked directly instead of cycled; weapons read "PWR 3 RNG 1 +2 DEF"
and the shop says who can use each. Combat HUD: Act/Move grey out once spent, 1/2/3 hotkeys,
Escape or right click cancels, the Act menu shows range, area and a description per ability.
Story guests now join even if they flatlined in the fight (found in a real save: Velvet Room won,
no Yuki).
**Refactor (2026-10-25).** No behaviour change: `CampaignState` split into partials (roster /
Jobs / Equipment / City / Save), `CampaignMenu` into six (frame, Map, Unit, Deploy, Combat, Text),
`GridUnit` statuses and loadout into their own partials, `FloatingText` and `FeedbackAudio` into
their own files; menu colours single-sourced through `MenuBuilder.*Hex` and `MenuBuilder.On`;
dead code removed (`FindAffordable`, `DescribeStats`, `AddToInventory`, `IsStoryFinished`), and
the HUD's two button-state paths merged. Regression-tested in the editor.

**Deployment (2026-10-25).** Every fight - story, contract or road - now opens with a formation
step, FFT-style. The board is built and everyone spawned, then the crew is placed inside a lit
starting zone before the first turn: a compact patch on the player's edge, 2 tiles per crew member
(at least 6), so there is a choice of shape but no starting anywhere on the map. Guests and
opponents stay where the mission put them. Click a crew member (board or panel; the turn triangle
marks the pick), then a lit tile; Start Battle begins the fight, Back to Briefing clears the board
with no result. Rules in `MissionRunner` (`BeginBattle`, `CancelDeployment`, `CanPlace`,
`TryPlace`, `BuildDeploymentZone`), input and panel in `CampaignMenu.Deploy.cs`.
**Verified in the editor:** zone of 6 around the crew; placing inside works, outside is refused
("Outside the deployment zone."), clicking a crew member reselects; Start Battle began the fight
from the new tiles with the highlight gone; Back to Briefing left nothing on the board.
Still open: a walk clip for the Adam models (the hop is a stand-in), real sounds and music, status
icons instead of text, the real UI pass that replaces the prototype menus, and per-map authored
deployment zones (today's zone is derived from the spawn edge).

### Art track (parallel)
- **Character models — first pass done (2026-10-25).** Unity's Adam Character Pack replaces capsules:
  Cyber Knight → Adam, Gooner → Guard, Kingpin → Lu, Chrome Brute → a scaled-up Guard, Courier and
  Civilian → Crowd, all on a retargeted idle. Units fall back to capsules when the pack is not
  imported. Moves now hop and units shake/fall (Phase 15); next: a real walk clip and hit/KO clips. The pack is
  personal / non-commercial and may not be redistributed, so it is git-ignored and must be replaced
  before any commercial release.
- Class silhouettes or colours, and district palettes (gang vs corp) per GAME_DESIGN.md.

### Automated tests ✅ (2026-10-25)
58 EditMode tests in `Assets/Editor/Tests/` (run from Window > General > Test Runner > EditMode):
progression (job levels, JP and XP formulas), the combat rules in `ActionResolver` (damage and its
floor, hit chance by facing and stance, Override chance with level penalty, resistance and clamps,
height and range, weapon and MP requirements, KO targeting), `CampaignState` (new game, hiring,
shop, travel and road encounters, JP sharing, XP, flatline loss, story rewards and the guest join,
recruiting after a fight, job prerequisites, learning, locked units, save round trip, v1 and v2
migration, unreadable data) and the deployment zone. They use the real `CampaignConfig` and derive
expected numbers from the assets, so they pin rules, not balance. Two hooks were added for them:
`CampaignState.ToJson` / `FromJson` (the save format without the file) and the pure
`MissionRunner.BuildZone`. **Verified:** all 58 pass; breaking the side-evasion rule on purpose
failed exactly the facing test, and it passed again once restored.

### All eighteen jobs ✅ (2026-10-25)
The twelve missing jobs are in: **Deadeye** (start), **Sniper, Daemon, Surgeon, Duelist, Breacher**
(mid), **Juggernaut, Reaper, Railgunner, Overmind, Cyber Saint, Warlord** (top) - 63 new abilities,
tiers and unlocks exactly as GAME_DESIGN.md's tables, costs per its tier table. The design's own
kit tables (Ek A) are not in the repo, so the kits were designed here from what the combat code
supports; GAME_DESIGN.md lists them. Yuki is a Deadeye (guest and recruit), the opening raid's text
says so, and the five start jobs are what hiring halls offer. Every weapon type now has an Equip
ability (new: Rail Rifle, Launcher, Command Baton).
The unit screen's Jobs tab became a job tree - start / mid / top columns, pick a job to see its
tier, weapons, requirement or JLv, and Primary / Secondary / Learn; the Equipment tab edits one
passive slot at a time in a grid; Learn's job picker wraps. Fixed on the way: the AI's "cannot
reach" log printed a weapon ability's authored range (0) instead of its real reach.
**Tested:** 10 new EditMode tests (the 18 jobs, tiers and unlocks against the design, cost table,
weapon fit, Equip coverage, counter reach, the top-tier hybrid rule - testable at last - hiring
halls, Yuki); 68/68 pass. In the editor: the job tree fits one screen; Yuki fought the raid as a
Deadeye with a Rail Rifle and dropped the Slinger from 4 tiles with Aimed Shot.
**Not modelled (no code for it yet):** Hijack's bonus against wounded targets (Hijack is a stronger
Override instead), Mass Override's half chance on bosses (and it touches allies harmlessly), the
Breacher's Friendly Fuse, Overwatch - all done in the next section. Balance is untested - the new
kits are first drafts.

### Special rules and city-map depth ✅ (2026-10-26)
Everything from the gap list that needed no design decision, art, audio or story.
- **Overwatch** (Sniper, 300 JP, needs a Rail Rifle): a status that fires one weapon shot at the
  first enemy to move into range before the sniper's next turn, evasion applies, then it is spent.
  `GridManager.UnitMoved` -> `CombatManager` -> `ActionResolver.ResolveOverwatch`; a mover knocked
  out by it loses the rest of its turn.
- **Charged abilities:** `AbilityDefinition.chargeTime`. Piercing Beam (3 ticks) and Earthshaker
  (2 ticks) spend MP now and go off on the CT clock; the target area is orange, the turn queue lists
  pending charges, the preview says "[charges N ticks]", and a charge fizzles if its user is down.
- **Damage + status riders:** Marked Shot and Disarm hit and leave their status.
- **Hack family:** Hijack (Power 12, +20 against a target under half HP); Mass Override (half chance
  against bosses - Jacknife, Chrome Brute, Kingpin are flagged - and it skips allies).
- **Friendly Fuse** (Breacher support): the user's area abilities spare allies.
- **AI:** retreats below 30% HP when it has nothing good (score < 12); avoids tiles an overwatch
  covers unless every way in is covered; rates charged abilities lower and sets up Overwatch.
- **Generated contracts:** `ContractGenerator` (pure, seeded) builds jobs from the district's danger
  and enemy pool; the job board shows the authored listings plus 2 generated ones and changes after
  every mission.
- **Road ambushes** come from the same generator: level and size by danger, objective varies -
  defeat all, drop the leader, protect, or the new **Survive N turns** (`MissionObjective.Survive`).
- **Road rules:** a first visit is never ambushed; safe roads (per district) never are; one trip
  after a fight is safe; a **cleared** district (all contracts done) halves the chance and opens
  extra shop stock. Travel shows each road's chance and why.
- **Mk III weapons:** seven, Mk I power + 4, price Mk II x 1.8; stocked by cleared Rustyard and Spire
  Gate (cleared Neon Row adds Mk II).
- **Save v4:** visited districts, board version, safe trips; older saves count the current district
  as visited.
**Tested:** 27 new EditMode tests (`SpecialRulesTests`, `ContractGeneratorTests`, `CombatClockTests`,
more in `CampaignStateTests`); 95/95 pass. In the editor: a Survive ambush won after 3 crew turns; a
hurt Raider fell back; Piercing Beam charged 3 ticks and hit two enemies; Overwatch fired once on a
Gooner walking in; the board showed 2 authored + 2 generated contracts.
**Fixed on the way:** every fight ended before its first turn (the new charge check read the
pre-combat `CombatEnd` state) - `Tick()` now reports whether charges ended the fight, and
`CombatClockTests` pin it (re-introducing the bug fails exactly those 3). AI units froze when every
approach tile was under overwatch - they now walk in anyway.
**Untested:** balance - a charged Piercing Beam hits for 22-24, likely too strong.

### Next up
Content and design gaps against GAME_DESIGN.md, roughly in order of payoff:
- **Balance:** play through the new jobs; the kits and stats are paper numbers.
- **Story:** the design's opening (Yuki kills the owner mid-raid), the unnamed ex-cop lead, Seraph,
  per-character quests.
- **Decisions first:** unique weapons (where they drop), Mastery, save policy, persistent HP.
- **Polish:** walk and hit clips, real audio, status icons, the real UI pass.

### Open questions
Tracked in GAME_DESIGN.md's "Açık sorular". Settled so far: the Override formula (`Power 9 +
3 × Tech`) and hacked units joining permanently (Phase 11), per-ability reaction rates (Phase 11),
currency from contract pay and selling, and Jack Out as the road-encounter retreat (Phase 14).
Still open: the Mastery reward, where unique weapons drop, Override versus mind hack in the story,
and the save policy (manual + auto, or ironman).

---

## Deferred by design

Things we have deliberately not built yet, and what unblocks each. Not bugs, and not a backlog —
just decisions made once so they do not get re-litigated.

- **Persistent HP between fights.** Pools refill at every spawn. Carrying damage over only means
  something with a way to recover it (rest, items, clinics), which the city map does not have yet.
- **AI that changes behaviour when hurt** (retreat, play safe). The scored AI (Phase 13) already
  heals, reboots and buffs; fleeing needs maps with somewhere worth fleeing to.
- **Enemies that hack the player's crew.** The AI already scores Override and would use it, but no
  enemy job is given one: a crew member lost to a hack needs a counterplay story first.
- **Status icons.** Stance and statuses show as text tags in the status bar and as callouts; icons
  wait for the real UI pass.

---

## Decisions that cut across phases

- **Comments record *why*, not *what*.** Terse, and reserved for non-obvious invariants, platform
  gotchas, and decisions that look wrong but aren't.
- **ARCHITECTURE.md is living.** It is updated alongside any behaviour change, in the same commit —
  a stale architecture doc gets trusted and lies.
- **Determinism where it is cheap.** Turn order, tie-breaking, and highlight precedence are all
  fixed rather than left to whatever ran last.
- **Placeholders are labelled as placeholders.** Anything standing in for a real system says so in
  the code, so scaffolding is never mistaken for design.
