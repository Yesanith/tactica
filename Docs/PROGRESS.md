# Tactica — Progress

> **Companion to [ARCHITECTURE.md](ARCHITECTURE.md), not a duplicate of it.**
> That document describes *what the system is today*. This one tracks *how far along we are* and
> *why things were decided the way they were*. Update it when a phase starts or finishes, or when a
> decision is made that a future reader would otherwise have to reverse-engineer.
>
> High-level by design. If you need the mechanism, the invariant, or the gotcha, it is in
> ARCHITECTURE.md.

Last updated 2026-08-17.

---

## Phases

- [x] **Phase 0** — Setup
- [x] **Phase 1** — Grid System
- [x] **Phase 2** — Camera
- [x] **Phase 3** — Job / Stat System
- [x] **Phase 4** — Combat Core
- [x] **Phase 5** — UI
- [ ] **Phase 6** — Enemy AI ← *currently here*
- [ ] **Phase 7** — Recruitment / Mission Flow
- [ ] **Phase 8** — Polish

---

## Completed

### Phase 0 — Setup

Unity 6000.5.2f1 on URP, with Active Input Handling set to **Input System package only**. Five
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

---

## Currently working on

### Phase 6 — Enemy AI

Giving non-player units their own turns instead of the player driving everyone.

**Already in place for it.** The turn system was built so this drops in rather than forces a
rewrite: `CombatManager.BeginTurnFor` resets per-turn state for *every* unit, not just the player's,
and does so without depending on any listener existing. Per-turn flags (`HasMovedThisTurn`,
`HasAttackedThisTurn`, `IsDefending`) live on `GridUnit` rather than inside the input controller, so
an AI handler reads the same answers from the same place. `ActionResolver` needs no scene, and
`GetTilesInMoveRange` / `GetGridDistance` already answer "where can I go" and "what can I reach".

**The shape.** An AI turn handler sitting beside `PlayerActionController` as a sibling — same
inputs, same rules, different decision-maker.

**Blocker to settle first.** There is still no faction system: `GridUnit.isPlayerControlled` is a
bool, and `ActionResolver` treats *any* unit that is not the user as an enemy. Both are the same
missing concept seen from two sides, and an AI that picks targets will need the real thing — it
cannot currently be told not to attack its allies.

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
