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
- [x] **Phase 6** — Enemy AI
- [ ] **Phase 7** — Recruitment / Mission Flow ← *currently here*
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

---

## Currently working on

### Phase 7 — Recruitment / Mission Flow

Not started. Combat is playable end to end; what is missing is everything around it — a roster that
persists between fights, units joining it, and missions to spend them on.

**Blocker carried over from Phase 6.** There is still no faction system. `GridUnit.isPlayerControlled`
is a bool and `ActionResolver` treats *any* unit that is not the user as an enemy — the same missing
concept seen from two sides. The AI works today only because every encounter is one side against
one other; it cannot be told not to attack its own allies, which a third party or a charmed unit
would require immediately. Recruitment makes this urgent rather than theoretical, since a recruited
unit changes side by definition.

**Also relevant.** Runtime unit spawning already works — unit status bars are built entirely in
code precisely so a unit that does not exist at author time can still have one.

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
