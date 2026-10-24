# Tactica — Progress

> **Companion to [ARCHITECTURE.md](ARCHITECTURE.md), not a duplicate of it.**
> That document describes *what the system is today*. This one tracks *how far along we are* and
> *why things were decided the way they were*. Update it when a phase starts or finishes, or when a
> decision is made that a future reader would otherwise have to reverse-engineer.
>
> High-level by design. If you need the mechanism, the invariant, or the gotcha, it is in
> ARCHITECTURE.md.

Last updated 2026-10-24.

---

## Phases

- [x] **Phase 0** — Setup
- [x] **Phase 1** — Grid System
- [x] **Phase 2** — Camera
- [x] **Phase 3** — Job / Stat System
- [x] **Phase 4** — Combat Core
- [x] **Phase 5** — UI
- [x] **Phase 6** — Enemy AI
- [ ] **Phase 7** — Recruitment / Mission Flow ← *currently here*
- [ ] **Phase 8** — Polish

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
the start. Movement allows **one diagonal step per path** — so `(2,2)` costs 3 — while ability
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
built from Speed at the start of an encounter.

*Decisions:* ties in initiative break on **original roster index**, explicitly, because a
non-deterministic turn order makes bugs reproduce only sometimes. `ActionResolver` is a plain C#
class, not a MonoBehaviour, so combat maths is constructible and testable without a scene. Damage is
floored at 1 — a negative result would otherwise *heal* the target.

### Phase 5 — UI

World-space HP/MP bars, a screen-space HUD with active-unit and target panels, and the action menu
that drives the whole player turn.

*Decisions:* unit bars are built **entirely in code** — a hand-authored hierarchy cannot be
pre-made for a unit that gets spawned at runtime for a recruitment roster. Turn economy is
**move once per turn, attack uncapped**. **Wait grants a defensive stance** (+5 Defense until the
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

---

## Currently working on

### Phase 7 — Recruitment / Mission Flow

**Prototype loop in place.** Main menu → headquarters → roster / recruit / mission select → combat
→ result screen → headquarters, with a JSON save. Menus are deliberately bare — black background,
grey buttons, built in code — because their job right now is to make factions and recruitment
*testable*, not to be the UI. A new `Tactica.Campaign` namespace holds the data and rules
(`CampaignState`, plain C#) and the one scene-facing piece (`MissionRunner`, which spawns units and
reports results); `CampaignMenu` in `UI` drives both and boots itself, so no scene edit was needed.

*Decisions:* missions carry **no map data** — spawn tiles are chosen per side at runtime, so a
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

**Still open in Phase 7:** a real neutral hostility table, persistent HP, XP, and mission objectives
beyond "one side is down".

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

**FFT research.** [FFT_DESIGN_NOTES.md](FFT_DESIGN_NOTES.md) records how Final Fantasy Tactics'
systems work, with numbers from the Battle Mechanics Guide, why each works, and a prioritised list
of what fits Tactica — one action per turn and a CT turn system first.

---

## Deferred by design

Things we have deliberately not built yet, and what unblocks each. Not bugs, and not a backlog —
just decisions made once so they do not get re-litigated.

- **A distinct basic weapon attack.** Attack currently runs through the same path as any job
  ability: `SelectAbility` picks the first unlocked one and resolves it normally. A separate basic
  attack — no MP cost, damage sourced from the equipped weapon — needs somewhere for "equipped
  weapon" to live, so it waits on an equipment/inventory system. Nothing to change in the meantime;
  a job's first ability stands in perfectly well.
- **Smarter AI target selection and behaviour variation.** The AI currently picks the enemy with
  the **lowest current HP** among those it can reach this turn — "finish the wounded one" —
  falling back to the nearest valid target when nothing is in reach. It is flagged as a placeholder
  in `EnemyAIController` and ignores what a target can actually *do*: damage output, healers and
  buffers, whether a kill is securable, how exposed attacking leaves the attacker.
  Behaviour that varies with the AI's own state — retreating or playing safe at low HP — waits on
  equipment and items, because "act differently when hurt" needs something to actually do about it.
  Fleeing, healing and defending are all either unavailable or meaningless right now, so the
  variation would be a branch with nothing behind it.
- **A status icon for the defensive stance** — see Phase 5. Console-only until the HUD grows one.

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
