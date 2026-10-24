# Tactica — Architecture

> **This document is maintained alongside the code, not written once.**
> Any change that alters how a system behaves — a new class, a changed rule, a removed
> placeholder, a resolved limitation — should update the relevant section in the same commit.
> A stale architecture doc is worse than none: it gets trusted and it lies.
>
> Scope: **what exists today.** Not a roadmap, not a task list. Known limitations are recorded
> because they are current facts about the code, not because they are planned work.

Last verified against a clean compile of all 39 scripts (8,201 lines; 5,293 excluding comments and
blanks).

---

## Project conventions

| | |
|---|---|
| Unity | 6000.6.7f1, URP |
| Input | Input System package **only** (`activeInputHandler: 1`). Legacy `UnityEngine.Input` throws at runtime. |
| Namespaces | `Tactica.Grid`, `Tactica.Camera`, `Tactica.Stats`, `Tactica.Combat`, `Tactica.Campaign`, `Tactica.UI` — each maps 1:1 to a folder under `Assets/Scripts/` |
| Naming | `camelCase` private fields (serialized and not); `PascalCase` public members, properties, methods, consts, and `static readonly` |
| Field renames | Any rename of a `[SerializeField]` field carries `[FormerlySerializedAs("OldName")]`, because Unity serializes by field name and a bare rename silently discards Inspector values |
| Comments | Terse. They record *why*, not *what* — non-obvious invariants, platform gotchas, and decisions that look wrong but aren't |

### Namespace dependencies

```
   Stats ──▶ (nothing)
    Grid ──▶ Stats
  Camera ──▶ Grid
  Combat ──▶ Grid, Stats
Campaign ──▶ Grid, Combat, Stats
      UI ──▶ Grid, Combat, Stats, Campaign
```

**The graph is acyclic.** The two cycles that used to block an assembly-definition split are gone,
each broken by turning a field into an event:

| Former cycle | Old `Grid →` edge | Now |
|---|---|---|
| `Grid ↔ Combat` | `GridCursorHighlighter.actionController` | `GridCursorHighlighter.TileClicked` event; `PlayerActionController` subscribes |
| `Grid ↔ UI` | `GridUnit.statusBar` | `GridUnit.Spawned` (static) and `GridUnit.StatsChanged` events; `UnitStatusBar` attaches itself and subscribes |

Splitting into asmdefs along these lines is now possible; it has not been done.

**Direction of control:** `Combat` drives `GridManager`, never the reverse — `PlayerActionController`
calls `ShowMoveRangeForUnit` / `TryMoveUnitToTile` / `ClearMoveRangeHighlight`, and the grid layer
never asks whose turn it is. Board clicks travel upward only as an event. `UI` observes `Combat`
and `Grid` purely through events (`OnActiveUnitChanged`, `OnStatsChanged`, `OnTargetSelected`,
`StatsChanged`) and is never called by either.

### The `Camera` namespace collision

`Tactica.Camera` is a sibling of every other `Tactica.*` namespace, so an unqualified `Camera`
inside any of them resolves to **the namespace**, not `UnityEngine.Camera` (`CS0118`). Files that
need the type declare an alias **inside** the namespace block:

```csharp
namespace Tactica.Grid
{
    using Camera = UnityEngine.Camera;
```

It must be inside the block — at file scope it is checked too late to win the lookup. Present in
`GridManager`, `GridCursorHighlighter`, and `UnitStatusBar`. IDEs suggest "simplify" on these;
applying that suggestion reintroduces the error.

---

## Grid — `Assets/Scripts/Grid/`

### Data layer

**`GridTile`** (struct) — `Coordinates`, `Height`, `IsWalkable`, `Occupant` (GameObject), plus
`IsFree` (`IsWalkable && Occupant == null`) — **unused today**; the BFS tests the two conditions
separately because it rejects them for different reasons. Kept as the natural predicate for spawn
placement and AI queries.

**`Faction`** (enum, `Tactica.Grid`) — `Player = 0`, `Enemy = 1`, `Neutral = 2`. Replaced the
`isPlayerControlled` bool, which could express only two sides and no way to change allegiance.
Values are **pinned**: Unity serializes an enum as its underlying int, so reordering the members
silently reassigns every authored unit with no diff to notice. It lives in `Grid` because `GridUnit`
owns it and both `Combat` and `UI` already depend on `Grid` — no new namespace edge.

**Side and control are separate**, as in FFT. `Faction` says which side a unit is on (targeting,
victory); `GridUnit.IsPlayerControlled` — Player faction **and not a guest** — says who gives it
orders. A **guest** (`isGuest`, set from `UnitEntry.guest`) is on the Player side but driven by
`EnemyAIController`: escorts and civilians. Derived rather than stored, so a unit hacked onto the
Player side with Override takes orders at once.

`Neutral` is reserved for genuine **third parties hostile to everyone** (rogue drones, a feral AI).
No contract uses it now; civilians are guests. That split is why there is no faction relationship
table.

**`GridManager.GridTiles`** is a `Dictionary<Vector2Int, GridTile>`, not a 2D array. The dictionary
costs a hash per lookup and loses cache locality; it buys sparse/irregular maps, negative
coordinates with no offset arithmetic, and O(1) runtime add/remove. At 100–10,000 tiles the cost is
noise.

Because `GridTile` is a **struct**, a dictionary lookup returns a *copy*. Mutating requires a
write-back, which `SetOccupant` / `SetHeight` / `SetWalkable` wrap:

```csharp
tile.Occupant = occupant;   // mutates the local copy...
GridTiles[coords] = tile;   // ...so it must be written back
```

`GenerateGrid()` builds a flat, fully walkable rectangle from `(0,0)` to
`(gridWidth-1, gridDepth-1)`, called from `Awake`.

### Coordinate conversion

- `GridToWorldPosition(Vector2Int)` — grid X→world X, grid Y→**world Z** (Unity is Y-up). Includes
  `Height * heightStep` and the GridManager's own transform offset.
- `WorldToGridPosition(Vector3)` — uses `RoundToInt`, not `FloorToInt`, because
  `GridToWorldPosition` places the tile *centre* at `coord * tileSize`. The two must agree or they
  stop being inverses.
- `TileSurfaceOffset` — half the tile's visual thickness; where things stand.

### Generated maps — `MapSettings`, `MapGenerator`, `BuildMap`

Each contract carries a `MapSettings` (width, depth, seed, maxHeight, heightStep, edgeDepth,
holeChance, coverChance); `MissionRunner` calls `GridManager.BuildMap(settings)` before spawning.
Same seed, same map. Undefined settings (width or depth 0) fall back to `ResetToDefaultMap()`, the
scene's flat rectangle.

`MapGenerator.Generate` runs five passes, in this order on purpose:

1. **Heights** — Perlin noise quantised into terraces, then relaxed (lowering only, so it
   terminates) until no neighbours differ by more than 1. Any Jump 1 unit can cross the terrain.
2. **Outline** — noise-driven bites out of the sides (up to `edgeDepth`), at most 1 out of the two
   deployment edges.
3. **Holes** — random interior gaps, never in the two spawn rows at each end.
4. **Connectivity** — everything a Jump 1 unit cannot reach from the middle of the player's edge is
   removed, so every tile left is playable.
5. **Cover** — blocks raised by 2, placed last so they cannot disconnect the map: only where all
   four neighbours exist and are not cover. Too tall for Jump 1 (Cyber Knights); Gooners have Jump 2.

A result keeping under 55% of the rectangle, or fewer than 4 tiles on either deployment edge, is
rejected and the next seed tried (up to 6). `GridRebuilt` fires afterwards; the camera reframes on
it, lifting its focus by half the tallest terrain.

### Rendering

`RenderGrid()` instantiates one `tilePrefab` per tile, named `Tile_x_y`, as a **column** from the
ground slab up to the tile's surface — scaled `(tileSize × 0.96, tileVisualHeight + Height × heightStep,
tileSize × 0.96)` and lowered by half its rise, so the surface stays exactly where units snap and the
4% gap draws a seam between tiles. Assigned to the **`Tiles` layer** (index 3 in `TagManager.asset`).

**Height tint.** Unhighlighted tiles get a `MaterialPropertyBlock` colour from dark slate (low) to
pale neon blue (high), relative to `MaxTileHeight` — without it every column rendered near-black and
terraces were unreadable. The block is removed while a highlight shows, so highlight materials read
unchanged, and no materials are cloned.

One GameObject per tile is fine at this scale and buys raycast picking for free.
`Graphics.RenderMeshInstanced` would be the alternative at ~10,000 tiles; the swap is local to this
method, and `WorldToGridPosition` exists as the picking fallback for that case.

`ClearRenderedTiles()` branches on `Application.isPlaying` — `Destroy` is deferred and is a no-op in
edit mode, which would leak a duplicate tile set on every context-menu render.

### Hover and highlighting

**`TileHighlightState`** is a `[Flags]` enum: `InMoveRange = 1<<0`, `Hovered = 1<<1`,
`Attackable = 1<<2`. Flags, not a plain enum, because several layers apply to one tile at once and
each system must set and clear only its own bit.

`ApplyHighlightVisual(coords)` is the **single** place that decides a tile's material, with fixed
precedence:

```
Hovered  >  Attackable  >  InMoveRange  >  base
```

Precedence lives there rather than in whoever wrote last, so the order the systems run in does not
matter. Four materials: `baseMaterial`, `highlightMaterial`, `rangeMaterial`, `attackableMaterial`.

The move range appears **only** on an explicit Move press (`PlayerActionController.BeginMove`).
`PlayerActionController.HandleActiveUnitChanged` clears the outgoing unit's range and draws nothing;
`GridManager` used to own that handler and paint the incoming unit's range automatically, which lit
tiles that ignored clicks until Move was pressed — indistinguishable from a broken grid.

Two other paths clear it, both for the same reason — a lit range must never advertise an
unavailable action:

- **A successful move** clears rather than redrawing from the new position (movement is once per
  turn).
- **`BeginAttack`** clears before lighting `Attackable`. Move-then-Attack without moving used to
  leave both layers up, and backing out of targeting then left move-range tiles glowing with `Mode`
  back at `None`.

Each layer records what it lit (`moveRangeHighlights`, `attackableHighlights`) rather than
recomputing at clear time — the grid may have changed in between, and a recomputed set would not
match what is on screen.

`GridCursorHighlighter` runs in **`LateUpdate`**, not `Update`. `EventSystem` processes input
modules during its own `Update`, and Update-to-Update order between components is undefined, so
running in `Update` let a button press also register as a board click. `LateUpdate` is guaranteed
after every `Update`, so the UI has finished with the pointer first. It also means hover reflects
the camera's final position for the frame rather than trailing it, since the camera moves in
`Update`.

It skips the whole frame when `GridInput.IsPointerOverUI()` is true, clearing hover as it bails.
That helper performs a **fresh `EventSystem.RaycastAll`** rather than calling
`IsPointerOverGameObject()`: the parameterless overload asks about pointer id `-1`, a legacy
`PointerInputModule` constant that `InputSystemUIInputModule` does not necessarily assign, and its
answer is cached during `EventSystem.Update`. Only `GraphicRaycaster` hits count, so a
`PhysicsRaycaster` on the camera could not report world geometry as UI. Only elements with
`raycastTarget` enabled register, so the world-space unit bars (all `raycastTarget = false`, no
`GraphicRaycaster`) never block the tile underneath them.

Otherwise it performs **one raycast per frame**, masked to the `Tiles` layer, and feeds
the result to `SetHoveredTile`, the hover log, and click handling. The mask is what lets a tile be
hovered while a unit stands on it; `1 << layer` is guarded against a missing layer, since C# masks
shift counts to 5 bits and `1 << -1` would silently select layer 31.

### Distance and movement

**One metric for both, Manhattan** — FFT's. Movement and ability ranges are diamonds.

**`GetGridDistance(a, b)`** = `|dx| + |dy|`. Used for ability range. Static, pure arithmetic,
path-independent: it ignores walls, height and occupancy, because a target always stands on an
occupied tile and an arrow does not walk. **A unit can still hit a tile it cannot step onto.**

**`GetTilesInMoveRange(start, moveRange, maxStepHeightOverride)`** is a plain BFS over the four
orthogonal neighbours, every step costing 1, results in increasing distance (the AI relies on that
order to take the cheapest tile first). **Every caller passes the unit's `EffectiveStats.Jump`** as
the step-height limit, so climbing is per job.

This replaced Chebyshev range plus an 8-directional "one diagonal per path" movement rule. The two
spoke different languages — `(2,2)` was range 2 but a walk of 3 — and diagonal travel stayed
strictly better than orthogonal, so optimal play drifted onto diagonals. The two-visited-set
search that rule needed is gone with it.

Rejections **never** mark a tile visited: the height rule depends on where you step *from*, so a tile
refused for a cliff on one side must stay eligible from a gentler slope elsewhere.

Blocking rules: missing key (off-map or hole), `!IsWalkable`, `Occupant != null` (**allies block
too**), and `|Δheight| > Jump`.

### Facing — `Facing`, `FacingUtility`

`GridUnit.CurrentFacing` is one of four directions (pinned enum). `SetFacing` rotates the unit's
transform about Y, so the spawned **visor** on the capsule's +Z side shows it; the status bar
billboards independently. A unit turns to where it walked (`GridManager.TryMoveUnitToTile`) and to
its target when it acts (`ActionResolver.TryExecuteAbility`). Spawned crew face north, everyone
else south.

`FacingUtility.SideOf(attacker, defender, facing)` is FFT's rule (BMG 1.4): compare the attacker's
offset along the defender's forward axis with its offset across it; the larger decides, ties go to
the more forward side (front over side, side over back). Height is ignored.

### `GridUnit`

Per-unit state and lifecycle:

- **`Awake`** — resolves `gridManagerRef`, raises the static **`Spawned`** event (the UI attaches
  a bar from it), then calls `RecalculateStats()`. Stats are computed here, not in `Start`, because every `Awake` precedes
  every `Start` and `GridManager.Start` reads `EffectiveStats.Move`. Two `Start`s have no defined
  order, so computing there is a coin flip that fails silently as `Move = 0`.
- **`Start`** — `SnapToGridPosition()` and claims its tile via `SetOccupant`. Both need `GridTiles`,
  built in `GridManager.Awake`, and Awake-to-Awake order is undefined.

`CurrentStats` is a **public field**, not a property: it is a struct, and C# forbids writing through
a struct-typed property (`unit.CurrentStats.HP -= 5` is `CS1612`).

`ApplyDamage` floors at 0 and ignores non-positive input; `Heal` caps at `EffectiveStats.MaxHP`;
`SpendMP` floors at 0. All three, plus `RecalculateStats` and `SetFaction`, end in
`NotifyStatsChanged()`, which raises **`StatsChanged`** — the single "re-read me" signal for bars
and anything else. `GridUnit` holds no reference to its display.

**`OnDestroy` releases its tile** if the tile still names this unit, so destroying a unit can no
longer leave a phantom occupant. (`MissionRunner` still clears tiles eagerly, because `Destroy` is
deferred to the end of the frame.)

`Spawned` is reset in a `SubsystemRegistration` hook, so with domain reload disabled a previous
play session's listeners are not invoked on destroyed objects.

**Turn economy.** `HasMovedThisTurn` and `HasActedThisTurn` (read-only, not serialized) are both
reset by `BeginTurn()` and set by `MarkMoved()` / `MarkActed()`. They live on the unit rather
than in `PlayerActionController` because they are facts about the unit's turn, not about player
input — an AI turn handler and the CT clock need the same answers. **One move and one action per
turn**, in either order (FFT's rule); any ability counts as the action. Together they decide what
`CombatManager` charges for the turn.

**Flatline.** Taking the last HP sets `FlatlineCounter` to `FlatlineTurns` (3). `TickFlatline()`
drops it on each of the unit's would-be turns; below 0, `Flatline()` sets `IsFlatlined`, releases
the tile and deactivates the GameObject (kept for the mission report; `MissionRunner` destroys it).
`Revive(hp)` stands a knocked-out, not-yet-flatlined unit back up. `Heal` now **refuses knocked-out
units** — standing one up without clearing the counter would leave a deadline running on a living
unit.

**Faction.** `GridUnit` owns the unit's `Faction` via a serialized field, exposed as
`CurrentFaction` with `SetFaction(newFaction)` for recruitment to call (`IsGuest` /
`IsPlayerControlled` above decide who drives it). `SetFaction` refreshes the
status bar (the only thing that caches a faction-derived value) and logs the change. It does **not**
rebuild turn order — initiative is a snapshot, so a unit switching sides mid-encounter keeps its
slot and simply fights for the other team from its next turn.

The migration from `isPlayerControlled` deliberately did **not** use `[FormerlySerializedAs]`. That
attribute redirects by name, not by type, and `bool` → `enum` is not a conversion Unity performs;
worse, had it coerced the raw value the mapping would have been **inverted**, since the old `true`
(1) meant Player and `1` is `Enemy`. The scene's two authored units were migrated in the YAML
directly instead.

**Defensive stance.** `IsDefending` grants a flat `defenseBonus` (default 5, serialized so it is
tunable per unit). Granted by `PlayerActionController.Wait()` when `!HasActedThisTurn`; cleared
in `BeginTurn()` — so it protects through everyone else's turns and lapses when the unit acts again,
which is the trade being paid for.

The bonus is applied inside `RecalculateStats()`, **not** in `ActionResolver.CalculateDamage`:

```csharp
if (IsDefending) EffectiveStats += new CharacterStats { Defense = defenseBonus };
```

The formula asks for "this unit's Defense" and must get one honest answer — a bonus applied only
inside the damage formula is invisible to every other consumer (info panel, future AI threat
estimate). This is the mostly-zeroes-modifier use `CharacterStats.operator +` exists for, and real
buffs will be a list summed the same way. Because `RecalculateStats` rebuilds `EffectiveStats` from
the job each call rather than incrementing it, the bonus cannot stack across repeated calls.

Entering and leaving are `Debug.Log`ged. **There is no status icon yet** — the console is currently
the only way to see the stance, which makes it easy to mistake for a broken bonus. Future HUD work.

`BeginDefending()` is a method, not a settable property, because the flag feeds `EffectiveStats`:
writing it without recalculating leaves the bonus declared but not applied — the same staleness trap
as changing `Level` directly.

---

## Camera — `Assets/Scripts/Camera/`

**`TacticsCameraController`** drives everything from four values — `pivotPoint`, `yaw`, `pitch`,
`distance` — and rebuilds the transform from them in `ApplyTransform()`, the only place the
transform is written. Mutating the transform per-control would make the three fight each other.

- **Pan** (WASD/arrows) — basis built from `yaw` alone, not `transform.forward`, so pitch never
  leaks into movement and there is no degenerate case looking straight down.
  `ClampMagnitude` prevents diagonals moving √2 faster.
- **Zoom** (scroll) — changes `distance` from the pivot, clamped, so it reads as "closer to what I
  am looking at" and cannot overshoot past the pivot.
- **Rotate** (Q/E) — 90° snaps over `snapRotationDuration`, `SmoothStep`-eased. Input is read only
  when idle, which is what discards mid-snap presses. Lands on the target exactly rather than
  easing asymptotically, so repeated snaps accumulate no drift. **No `Time.deltaTime`** on rotation
  input — a mouse/key delta is already per-frame.
- **`AutoFrameGrid()`** — centres on `(width-1)*tileSize*0.5` (tile *centres*, not tile count) and
  sets `distance = max(width, depth) * tileSize * framingPadding`, clamped to the zoom limits.
  Ignores elevation.

`pitch` is set only by `ResetFraming`; no control changes it.

**`CameraInput`** (static) holds all Input System `#if` plumbing for the camera, mirroring
`GridInput` for the grid.

---

## Stats — `Assets/Scripts/Stats/`

### The two-struct split

**`CharacterStats`** — `MaxHP`, `MaxMP`, `Strength`, `Magic`, `Defense`, `Speed`, `Move`, `Jump`.
Capabilities. **Derived**: recomputable at any moment from base + job + level, always giving the
same answer.

**`CurrentStats`** — `HP`, `MP`, plus `IsAlive`, `FullFrom`, `ClampedTo`. Live pools.
**Authoritative history**: nothing can recompute that a unit is at 12 HP.

They are split because in one struct, recalculating after any change (equipping, a buff expiring, a
job swap) would either overwrite HP or force every recalculation path to preserve two fields it has
no business touching. Split, that bug is unrepresentable. The dependency runs one way:
`CurrentStats` needs `MaxHP` to clamp; `CharacterStats` never needs current HP.

`GridUnit.RecalculateStats()` fills the pools on **first** run and only clamps on every later run —
so levelling up is not a free full heal, but unequipping +HP gear cannot strand HP above the new
cap.

**Operators.** `operator+` is component-wise and deliberately **unclamped** — clamping would make
the result depend on the order modifiers were summed. `operator*` scales by an int. Both are
additive-only; a percentage modifier does not commute with either and would need its own type.

### Data assets

**`JobDefinition`** (ScriptableObject, *Create → Tactica → Job Definition*) — `JobName`,
`BaseStats`, `StatGrowthPerLevel`, `AbilityUnlocks`.

`GetStatsAtLevel(level)` returns `baseStats + statGrowthPerLevel * (max(1, level) - 1)`. The
`level - 1` is what makes level 1 return `BaseStats` untouched; the `max(1, …)` stops a level of 0
or below returning stats *below* base.

**`AbilityDefinition`** (ScriptableObject, *Create → Tactica → Ability Definition*) — `AbilityName`,
`Description`, `MPCost`, `Range` (Manhattan), `AreaOfEffect`, `EffectType`, `Power`, and three
targeting bools.

**`AbilityUnlock`** (struct) — `RequiredLevel` + a direct `AbilityDefinition` reference.
`GetUnlockedAbilities(level)` skips unassigned rows rather than returning nulls, and assumes no
ordering of the authored list.

All ScriptableObject fields are exposed **read-only**: writing to one at runtime edits the project
asset, and in the editor that change survives leaving Play mode.

---

## Combat — `Assets/Scripts/Combat/`

### Turn order — `CombatManager`

**A CT (charge time) clock, after Final Fantasy Tactics** (see [FFT_DESIGN_NOTES.md](FFT_DESIGN_NOTES.md) §2).
Each clock tick every unit on the board gains CT equal to its live `Speed`; the unit with the most CT
at or above `TurnThreshold` (100) takes the turn. At the end of the turn it pays by what it did:

| Turn | Cost |
|---|---|
| Moved **and** acted | `CostMoveAndAct` = 100 |
| Moved **or** acted | `CostMoveOrAct` = 80 |
| Neither (Wait only) | `CostNeither` = 60 |

Leftover CT carries over, **capped at `MaxCarryOver` (60)** so idling cannot bank turns. Speed
therefore decides how *often* a unit acts, and holding back brings a unit round sooner.

`StartCombat()` still builds `initiativeOrder` (Speed descending, **explicit `ThenBy` on list
index**), but it is now only the **tie-break order** for equal CT and the stable roster that
`EnemyAIController` iterates. Determinism matters for the same reasons as before: a turn order that
depends on sort stability reproduces bugs only sometimes.

**Knocked-out units keep charging.** When a downed unit's CT would grant a turn, it pays
`CostNeither` and its flatline counter drops instead (FFT's death counter); when the counter falls
below 0 the unit flatlines — see `GridUnit`. `AdvanceToNextTurn` loops through such "turns" until a
standing unit is ready. If no unit becomes ready in `MaxTicksWithoutTurn` ticks (nobody has Speed above
0) it logs an error and aborts rather than hanging.

`PredictTurns(count)` simulates the clock on a copy of the CT table for the HUD's turn queue. It
assumes every turn costs a full 100, so a unit that holds back will come round earlier than shown.
`TurnNumber` and `ClockTick` replace the old `RoundNumber`; per-tick effects (poison, regen, status
durations) belong in `Tick`.

`StartCombat(IEnumerable<GridUnit>)` replaces `participatingUnits` and starts — the entry point for
runtime-spawned encounters (`MissionRunner`). The parameterless overload and its context menu remain
for hand-built test scenes. `AbortCombat()` ends an encounter with no winner (a retreat): it sets
`CombatEnd` and raises `OnActiveUnitChanged(null)` but **not** `OnCombatEnded`, since nothing was
decided. Both `StartCombat` and `EndCurrentTurn` finish by calling `BeginTurnFor()`,
which resets the incoming unit's per-turn state and *then* raises `OnActiveUnitChanged` — fired
**last**, after state is settled, so handlers see a finished turn and already-reset flags.

The reset happens here rather than in a listener because it is an invariant of the turn system: it
must apply to AI-driven turns too, and must not depend on any particular listener existing.

Speed is read **live** every tick, so haste/slow will take effect immediately once they exist.

### State — `CombatState`

`WaitingForInput`, `ResolvingAction`, `TurnTransition`, `CombatEnd`. Exactly one at a time.

### End conditions — `CheckCombatEndCondition()`

Returns `CombatEndResult` (`Ongoing` first, so `default` means "not over"). It is a pure query;
**`TryEndCombatNow()`** is what acts on it — setting `CombatEnd`, logging, firing
`OnActiveUnitChanged(null)` so the board clears, then `OnCombatEnded(result)`. It returns whether it ended the encounter, and is
**idempotent**: called again once combat is over it returns `true` without re-announcing, so a
caller that checks twice cannot fire the event twice.

**Two call sites, and that closes the old timing gap.** The check used to live inline in
`EndCurrentTurn`, so knocking out the last enemy left combat running until the player pressed Wait
— corpses stayed targetable and the HUD kept offering actions, which reads as the win being missed
rather than delayed. `TryEndCombatNow` is now called:

1. **The instant an action resolves** — from `PlayerActionController.ResolveTargetClick` and
   `EnemyAIController.TryAttack`, on success only (a rejected ability changed no HP, so no side can
   have been emptied by it).
2. **At the turn boundary**, from `EndCurrentTurn` as before — kept because a side can be emptied
   by something that is not an attack.

**Deliberately not called from `ActionResolver`**, which would be the obvious place. That class is a
plain C# object with no scene dependency, constructible in a test — the property that makes combat
maths testable at all. A `CombatManager` reference to call back through would end that, and the
resolver would have to learn about turn structure to know when the call was appropriate. Both
controllers already hold the reference *and* know an attack just landed, so nothing is gained by
pushing it down a layer.

Callers stop what they were doing when it returns `true`: `EnemyAIController` yields out of its turn
coroutine instead of calling `EndCurrentTurn`, and `PlayerActionController` logs that no further
input is accepted. Both still run their **clean-up first** — the `Attackable` highlights belong to
the controller, and the `OnActiveUnitChanged(null)` this raises only clears the move-range layer, so
skipping it on a win would leave the losing side lit over a finished encounter.

Counts **`Faction.Player` and `Faction.Enemy` only**. Each side is only defeatable **if it had
members** — otherwise "all player units are down" is vacuously true on a roster with none, and a
one-unit test scene would end instantly. "Had members" is the live roster **or a snapshot taken in
`StartCombat`** (`encounterHadPlayers` / `encounterHadEnemies`). The snapshot is what lets
converting the last enemy win: `SetFaction` can empty a side, and the live roster alone would then
read "never had any enemies" and leave combat `Ongoing` forever. Before `StartCombat` the snapshot
is all false, so the live roster decides, as it always did. Mutual wipeout resolves to `PlayerDefeat`; the check order
is the only thing deciding that.

**Guests count for neither side either.** As in FFT, the fight is lost when the player's own units
are down even if an escort still stands; losing the guest is a `Protect` objective, not an end
condition. `encounterHadPlayers` excludes guests for the same reason.

**`Faction.Neutral` counts for neither side.** A surviving neutral does not keep a won encounter
open, and wiping out every neutral wins nothing — otherwise a wandering critter could stall a
cleared map indefinitely. "Keep the merchant alive" is a *mission objective*, evaluated above this;
end conditions answer "is there still a fight", objectives answer "did you do what you came for".
An encounter of nothing but neutrals is therefore permanently `Ongoing`, which is correct: nobody in
it has anything to win.

`EndCurrentTurn` refuses to advance once `CombatEnd` is set, and logs why.

### Rules — `ActionResolver`

Plain C# class, **not** a MonoBehaviour — no scene state, constructible in a test. Owned by
`CombatManager` via `Resolver`. Takes an optional seed for its own `System.Random`, so the Override
roll can be pinned in a test.

**The AI reboots.** `EnemyAIController.FindBestTarget` first looks for a downed ally it can reboot
**this turn** with an affordable Revive ability, and otherwise attacks with its first Damage ability.
A reboot it cannot reach is not chased. Gooners learn Reboot Protocol at level 3, so guards revive
each other. The AI never uses Override.

**Knocked-out targeting lives here, once:** flatlined units are never valid; knocked-out units are
valid **only** for `Revive`, and `Revive` is valid only for them. The player highlight, the AI search
and execution all ask `IsValidTarget`, so they cannot disagree.

**Cyberpunk effects** (`AbilityEffectType`, values pinned):

| Effect | Ability | Rule |
|---|---|---|
| `Revive` (5) | Reboot Protocol | Stands a knocked-out ally up with `Power` HP before it flatlines — FFT's Raise |
| `Override` (6) | System Override | `Power + caster Magic` % (clamped 5–95) to `SetFaction` the target to the caster's side — FFT's Invite (`MA + 20`). A failed hack still spends the action and MP |

`DescribeOutcome(user, target, ability)` returns the one-line preview ("7 dmg -> 8/15", "41% to
hack") from the same formulas execution uses, so the preview cannot disagree with the result.

- `IsTargetInRange` — `GetGridDistance <= ability.Range`. Range 0 is self-only.
- `IsValidTarget` — three cases in order: **self** requires `TargetsSelf`, **same faction**
  requires `TargetsAllies`, **different faction** requires `TargetsEnemies`. Self is tested first
  because a unit shares its own faction, so otherwise `TargetsAllies` alone would make every
  ability self-targetable.
- `GetHitChance` — damaging abilities only: `100 - Evasion`, with the target's Evasion **halved from
  the side and ignored from behind** (`GetAttackSide`), and doubled while it is defending (FFT's
  Defend). Other effects always land; Override has its own roll. A miss spends the action and MP.
- `CalculateDamage` — `max(1, Power + user.Strength - target.Defense)`. The floor is not only
  tuning: a negative result passed to `ApplyDamage` would *heal* the target. Returns 0 for
  non-damage abilities. `Magic` is unused — magic scales off `Strength`.
- `TryExecuteAbility` — validates target → range → MP, **then** mutates. A rejected ability costs
  nothing. Damage and Heal are implemented; Buff/Debuff/StatusEffect log "not yet implemented",
  **spend MP, and return `true`**. Raises `OnStatsChanged` on success only.

`OnStatsChanged` is an event rather than a direct HUD call so the resolver keeps no MonoBehaviour
reference. It is coarse — "stats changed", not which — and listeners re-read what they display.

### Player input flow — `PlayerActionController`

Owns `PlayerActionMode` (`None`, `Moving`, `Targeting`) and translates intent into actions. Lives
here rather than in `GridManager` because action mode is input *policy*, and `GridManager` already
carries tile data, generation, rendering, hover, highlighting, and pathfinding.

**It is also the sole bridge between combat and the grid.** It holds `combatManagerRef`, subscribes
to `OnActiveUnitChanged`, and resolves the active unit — all of which lived on `GridManager` until
that dependency was cut. It already held both references and already gated on `CombatState`, so
moving these removed duplication rather than relocating it.

| Entry point | Behaviour |
|---|---|
| `BeginMove()` | refuses if `HasMovedThisTurn`; else cancels targeting, shows the move range (the only thing that does), `Mode = Moving` |
| `BeginAttack(ability)` | refuses if the unit already acted or lacks MP; **clears the move range**, lights valid targets `Attackable`, `Mode = Targeting`. The ability comes from the HUD's Act menu (`GetAvailableAbilities()`) |
| `DescribeTargetPreview(coords)` | the resolver's one-line outcome for the pending ability on that tile, or null — drives the HUD preview |
| `Wait()` | refuses unless the active unit is **Player**; grants the defensive stance if `!HasActedThisTurn`, then ends the turn |
| `HandleTileClicked(coords)` | subscribed to `GridCursorHighlighter.TileClicked`; routes by mode |
| `HandleActiveUnitChanged(unit)` | event handler; cancels targeting, resets `Mode` to `None`, clears the outgoing range, draws nothing |

**Only player-controlled units take orders** (`IsPlayerControlled`: Player side, not a guest).
`GetActionableUnit` and `Wait` refuse anyone else.
Before this gate the HUD drove whichever unit held the turn — Move during an AI turn walked the
enemy, and Wait ended the AI's turn with a stance nobody chose. `EnemyAIController` applies the
mirror-image gate, so every faction has exactly one driver. `CombatHUD` additionally greys the three
buttons on non-player turns, purely as a visual.

The mode reset moved into `HandleActiveUnitChanged` because Wait is no longer the only way a turn
ends: a retreat aborts mid-targeting, and the faction panel can end a fight.

**`OnTargetSelected`** (`event Action<GridUnit>`) fires with the chosen target, or `null` when
targeting is abandoned. `CombatHUD` subscribes it straight to `ShowTargetPanel`, which already
treats `null` as "hide" — so one handler covers both cases.

An event rather than a `CombatHUD` field: `UI` already depends on `Combat`, so a reference the other
way would rebuild the cycle that moving the turn handler off `GridManager` just removed. It also
keeps this class driveable with no HUD present, and matches how the HUD already learns about
`OnActiveUnitChanged` and `OnStatsChanged`.

`EndTurn()` is **private** — the shared tail of `Wait`, cancelling targeting and calling
`CombatManager.EndCurrentTurn()`. It was public for a separate End Turn button that has since been
removed; make it public again when something needs a stance-free end (an AI handler, a turn timer).

`Wait` reads the active unit via `GetActiveUnit()` rather than `GetActionableUnit()`, which refuses
outside `WaitingForInput` — otherwise the stance could silently fail to apply while the turn ended
anyway.

Every action requires an explicit button press first. Clicking the board while `Mode == None` does
nothing but log why — a silent click is indistinguishable from a broken one.

Movement is **once per turn**. `BeginMove` refuses if the unit has already moved, and `TryMove`
re-checks before committing so the rule covers every route to a move, not just the usual button.
A successful move calls `MarkMoved()` and returns to `None`, leaving Attack available —
move-then-attack and attack-without-moving both remain valid.

`TryMove` passes the unit `GetActionableUnit()` already resolved into
`GridManager.TryMoveUnitToTile(unit, coords)`. There was a `TryMoveActiveUnitTo(coords)` wrapper
that took a bare coordinate and re-derived both the unit and the `CombatState` gate — work this
class had already done — so one click resolved the turn owner twice and checked the state twice.
`MarkMoved()` is now provably about the unit that moved.

`ResolveTargetClick` calls `MarkActed()` **only on a successful** `TryExecuteAbility`, mirroring
`MarkMoved()`. A rejected ability costs nothing, so it must not forfeit the stance. The stance
condition is "did not act", not "did nothing" — moving into cover and bracing is the choice being
rewarded, and demanding the unit stand still too would make Wait strictly worse than not moving.

While `Targeting`, clicking a non-attackable tile **cancels** rather than doing nothing, so the
player is never stuck. All entry points refuse unless `CurrentState == WaitingForInput`.

Ability selection is `GetUnlockedAbilities(Level)[0]` — a placeholder for a selection menu.
Target search uses `GetGridDistance`, not the move-range fill, because reach ignores walls and the
fill excludes occupied tiles — which is every target by definition.

---

## Campaign — `Assets/Scripts/Campaign/`

Everything between fights. Plain data plus one MonoBehaviour that touches the scene.

| Type | Role |
|---|---|
| `CampaignConfig` | ScriptableObject at **`Assets/Resources/CampaignConfig.asset`** (loaded by path, see `ResourcePath`): starting roster, recruitable jobs, recruit names, roster/deploy caps, mission list, and `startWithMenu` |
| `MissionDefinition`, `UnitEntry` | A mission is a name, a description and a list of opponents (job, level, faction). No map data — spawn tiles are chosen at runtime |
| `RosterUnit` | Name, job, level, deployed flag. **No HP** — pools refill at every spawn |
| `CampaignState` | Plain C# like `ActionResolver`: roster and recruit-pool rules, mission results, JSON save in `persistentDataPath/campaign.json` |
| `MissionRunner` | Spawns the deployed roster and the opponents as `GridUnit`s, starts combat, and turns `OnCombatEnded` / a retreat into a `MissionResult` |

**Spawning.** Each unit is a primitive Capsule built **inactive**, given `GridUnit.Configure(...)`, then
activated — so `GridUnit.Awake` computes stats from the configured job on its one and only run.
Tiles: Player fills rows from the near edge, Enemy from the far edge, Neutral from the middle, each
row centre-out. Occupancy is claimed at spawn rather than in `GridUnit.Start` (next frame), so later
spawns in the same loop see the tile as taken. The scene's hand-placed units are **removed** on the
first mission — they were a combat test fixture.

**Teardown.** After `resultDelay` (so the deciding blow is visible) the runner snapshots a
`MissionUnitReport` per unit, clears highlights, **explicitly releases each tile** — `GridUnit` has no
`OnDestroy` — and destroys every unit it spawned. The finish routine always yields at least once:
with no yield it would complete inside `StartCoroutine`, and the returned handle assigned afterwards
would leave `finishRoutine` non-null forever, so every later mission end would be ignored.

**Recruitment rule** (`CampaignState.CanRecruitAfter`): only units that **ended the mission on the
Player side and did not flatline** can join — in practice, whoever was hacked with System Override
(FFT's Invite). The recruit is won in the fight; the old "after a victory anyone can join" stand-in
is gone.

**Permanent loss.** `ApplyResult` removes every crew member whose report says `Flatlined`, whatever
the outcome. Knocked out but not flatlined means they were pulled out in time and survive.

**Objectives** (`MissionDefinition.objective`, pinned enum). `CombatManager`'s end condition still
applies to every mission; an objective can only end a fight **earlier**:

| Objective | Ends when | Result |
|---|---|---|
| `DefeatAll` | (nothing extra) | — |
| `DefeatTarget` | the named unit is knocked out **or no longer Enemy** (hacked) | Victory |
| `Protect` | the named unit is knocked out | Defeat |

`MissionRunner` finds the unit by `objectiveUnitName` and subscribes to **its** `StatsChanged`
after `StartCombat`, which catches damage, knockouts and faction changes from any source. It calls
`CombatManager.EndCombat(result)` — the new public end-with-a-result entry point that
`TryEndCombatNow` also uses. This can fire mid-resolution (a knockout raises `StatsChanged` inside
`ApplyDamage`); the controllers' own `TryEndCombatNow` afterwards simply sees combat already over.

An opponent entry with faction **Player** and `guest: 1` is a **guest** (the Courier, the Civilian):
it fights on the crew's side under **AI control**, is not in the roster, and — having ended on the
Player side — can be recruited afterwards.

## UI — `Assets/Scripts/UI/`

### World-space bars — `UnitStatusBar`

Attached automatically: a `BeforeSceneLoad` hook subscribes to `GridUnit.Spawned` and adds the
component to every unit that lacks one, authored or spawned. It then redraws on
`GridUnit.StatsChanged` (subscribed in `OnEnable`, which also draws once to catch up). **Builds its
own hierarchy in code.** A `GridUnit` component is the entire requirement for a unit, which is what
makes runtime-spawned units possible — and `GridUnit` never names this type.

**Turn indicator.** A tip-down triangle above the HP bar, shown only on the unit holding the turn
(`SetTurnIndicator`, driven by `CombatHUD`). Two stacked `Image`s of one procedurally generated,
antialiased triangle sprite: black at full size, turquoise shrunk about the triangle's **incentre**,
which moves every edge inward by the same distance so the outline is equally thick on all sides.

Generates a child `StatusBarCanvas` (World Space, `CanvasScaler`, no `GraphicRaycaster`) sized
100×20 at 0.01 scale = 1 world unit, plus four `Image`s using a **statically cached 1×1 white
sprite** tinted per-image.

Order matters in `BuildHierarchy`: `Canvas` is added **before** any transform reference is cached,
because `Canvas` requires a `RectTransform` and adding it replaces the plain `Transform` — a
reference taken earlier can be left stale.

This component sits on the **unit**, so `transform` is the unit's; the billboard moves a cached
`barRoot` instead. Runs in `LateUpdate` because the camera moves in `Update`. Positioning happens
**before** the camera lookup, so a missing camera cannot also break placement.

Data-driven: `offset`, `allyColor`, `enemyColor`, `mpColor`, `backgroundColor`.
Code-driven: everything structural.

### Act menu, targeting preview, turn queue — built by `CombatHUD`

`CombatHUD.Awake` adds three code-built pieces to its own canvas, so the authored HUD prefab did not
change:

- **Act menu** — the Attack button is relabelled **Act** and opens a list of the acting unit's
  unlocked abilities (`PlayerActionController.GetAvailableAbilities`), greyed when MP is short or the
  unit has already acted. Picking one calls `BeginAttack(ability)`. Closes on turn change.
- **Targeting preview** — top-centre line, polled each frame from
  `GridCursorHighlighter.HoveredTile` → `PlayerActionController.DescribeTargetPreview`.
- **`TurnQueueStrip`** — top-left, the next 8 turns from `CombatManager.PredictTurns`, neon-coloured
  by faction, knocked-out units shown with their flatline counter. Polled 4×/s. The component sits
  on an always-active root and hides only its child panel — hiding itself stopped its own `Update`,
  so the strip never reappeared.

### HUD panels — `UnitInfoPanel`, `CombatHUD`

`UnitInfoPanel` binds a `GridUnit` and re-reads it on `Refresh()`, so the HUD can refresh whatever
is displayed without tracking it. Shows `GridUnit.DisplayName` (the unit's own name, else its job
name, else the GameObject name), `Lv.{n}`, and `{current}/{max}` labels beside filled `Image` bars. HP carries
faction colour. Null binding blanks the panel rather than throwing or leaving stale numbers.

Both `UnitInfoPanel` and `UnitStatusBar` map faction to HP-fill colour through a `ColorFor(Faction)`
switch — Player green, Enemy red, Neutral amber. Every member is listed rather than relying on a
`default` arm, so a future faction surfaces as a missing case instead of silently painting itself
ally-green. The two sets of colours are separate serialized fields on purpose: a HUD panel may want
a different tone from a bar floating over the board.

The **target panel** is driven by `PlayerActionController.OnTargetSelected`, subscribed in
`OnEnable` / unsubscribed in `OnDisable` alongside the combat events. It is announced *before* the
ability resolves, so the panel populates whether the attempt lands or is rejected; on success the
`OnStatsChanged` raised inside `TryExecuteAbility` refreshes it with post-damage numbers. It hides
on turn change and on abandoned targeting.

`CombatHUD` holds two instances — active (bottom-left) and target (hidden until used) — plus three
`Button`s: Attack, Move, Wait. There was also an End Turn button calling identical code with a
different log line; it was removed once Wait grew a rule of its own (the defensive stance), which is
the distinction the two buttons never actually had. `Button` rather than `Image` because these need
pointer states, navigation, a disabled state, and `onClick`; the bars need none of that.

Handlers report the press and nothing more — whether Wait also grants a stance is
`PlayerActionController`'s call, so the same actions can later be driven by a keyboard shortcut or
an AI without duplicating any rules.

Subscribes in `OnEnable` / unsubscribes in `OnDisable` to `OnActiveUnitChanged`,
`Resolver.OnStatsChanged`, `PlayerActionController.OnTargetSelected`, and the three button
`onClick`s. Symmetric because button listeners stack — `AddListener` in `Awake` without removal
fires the handler twice after a re-enable.

`actionController` is now load-bearing for two things: the buttons and the target panel. Left
unassigned, the buttons only log and the target panel never appears.

> **⚠ The scene must contain an `EventSystem` GameObject.** Without one the HUD fails *silently* —
> no logs, no exceptions, buttons simply inert. A `GraphicRaycaster` does not poll; it is called by
> the `EventSystem`'s input module, so with no `EventSystem` nothing ever raycasts the canvas and
> `Button.onClick` never fires. Listener registration in `OnEnable` still succeeds — it is just
> never invoked.
>
> Second symptom: `GridInput.IsPointerOverUI()` returns `false` when `EventSystem.current == null`,
> so the HUD also becomes **click-through** and board clicks land behind it.
>
> Fix: *GameObject → UI → Event System*. Under `activeInputHandler: 1` it must carry
> `InputSystemUIInputModule`, not the legacy `StandaloneInputModule` — that one calls
> `UnityEngine.Input` and throws. Unity offers a one-click replace in the Inspector if it adds the
> wrong one.
>
> This has bitten once already, when the object was deleted alongside `EndTurnButton` — Unity places
> `EventSystem` as a sibling root next to the Canvas, so a stray multi-select takes both.

Both bars and panels guard the divide: `max > 0 ? ratio : 0f`, because `0/0` is `NaN` and Unity
renders that as a **full** bar rather than empty.

---

### Prototype menus — `CampaignMenu`, `MenuBuilder`

Main menu → Headquarters → Roster / Recruit / Missions → combat → result → Headquarters. Black
full-screen panel, grey buttons, **built entirely in code** by `MenuBuilder`, and every screen is
rebuilt from scratch on each change — wasteful and fine at this size; a real UI pass replaces it
rather than styling it. English labels, matching the combat HUD.

**Bootstraps itself** (`[RuntimeInitializeOnLoadMethod(AfterSceneLoad)]`) into any scene that has a
`CombatManager`, no `CampaignMenu` already, and a `CampaignConfig` with `startWithMenu` on — so
**no scene edit is needed**. Turning `startWithMenu` off restores the old behaviour: hand-placed
units and the `CombatManager` context menu.

The canvas sorts at 100, above the HUD. The opaque background is a raycast target, so it swallows
board clicks while a menu is up; `GridInput.IsPointerOverUI` sees it because the canvas has a
`GraphicRaycaster`. During combat only a top-right bar remains: **Units / Factions** (every unit
with a button cycling Player → Enemy → Neutral, followed by `TryEndCombatNow`, since no attack
happened to check it) and **Retreat**. Creates an `EventSystem` if the scene has none.

## Known limitations and scaffolding

Current facts about the code, not planned work.

### Placeholders

| What | Where | Note |
|---|---|---|
| First unlocked ability (AI only) | `EnemyAIController.SelectAbility` | The AI never uses Override or Reboot. |
| +1 level per won mission | `CampaignState.ApplyResult` | Stands in for XP. Deployed survivors only. |
| `Lv -` / `Lv +` buttons | `CampaignMenu.ShowRoster` | Debug controls for testing stat growth. |
| Capsule units | `MissionRunner.Spawn` | No per-job model or colour; faction shows only on the bars. |
| Flat per-level growth | `JobDefinition` | Not a curve asset. |
| `Magic` unused | `ActionResolver.CalculateDamage` | All damage scales off `Strength`. |
| Buff/Debuff/StatusEffect | `ActionResolver.TryExecuteAbility` | Log only — but MP is spent and it returns `true`. |

### Scaffolding pending removal

- **`CombatManager.testAbility`** + `TestExecuteAbilityOnOtherUnit()` context-menu probe —
  deliberately does *not* gate on `CurrentState`, so resolver bugs are not hidden behind turn-state
  bugs.

### Scene requirements that fail silently

- **An `EventSystem` GameObject is required for the HUD.** Missing it produces no logs and no
  exceptions — the buttons are simply inert, and the HUD additionally becomes click-through because
  `GridInput.IsPointerOverUI()` returns `false` with no `EventSystem.current`. Nothing in the code
  can detect or warn about this. See the `CombatHUD` section for the mechanism and the fix.
- **The `Tiles` layer must exist** in *Project Settings → Tags and Layers*. This one *does* warn
  (once, from `GridManager.TileLayer`) and degrades to raycasting all layers, so unit colliders
  start blocking tile hover rather than hover failing outright.

### Ordering and state

- **KO'd units occupy their tile** until they flatline or are rebooted, blocking movement — as in
  FFT, where bodies stay on the field.
- **Stale `combatManagerRef` on reassignment** — `PlayerActionController` and `CombatHUD` read the
  field in `OnEnable`/`OnDisable`; changing it mid-play unsubscribes from the wrong object.
- **Turn-change highlight clearing needs `PlayerActionController` enabled.** It owns
  `HandleActiveUnitChanged` now; `GridManager` used to, and `GridManager` is effectively always on.
  Disabling the controller mid-combat would strand a lit move range.
- **Stale `EffectiveStats`** — changing `Level` at runtime does nothing until `RecalculateStats()`.
- **`CurrentStats` writes bypass the UI** — it is a public field, so any external write leaves bars
  and panels stale unless `NotifyStatsChanged()` is called. No code does this any more; MP now goes
  through `SpendMP`.

### Rules and economy

- **Wait rewards twice.** Not acting costs less CT (80 or 60) *and* grants the defensive stance.
  FFT gives only the CT refund; the stance is kept for now and may be dropped once facing/evasion
  give Wait a positional purpose.
- **The defensive stance has no visual** — console logs only, so in-editor it is invisible unless
  the console is open. Status icon is future HUD work.
- **Allies block movement**, both as destinations and as pass-through.
- **Facing is never chosen by hand.** FFT ends a turn by picking a direction; here units only turn
  to where they moved or what they hit. A facing prompt on Wait is the natural next step.
- **Evasion is one number.** FFT layers class, shield and accessory evasion; `CharacterStats.Evasion`
  stands in for all of them until equipment exists.
- **Ability `AreaOfEffect` is authored but unread** — every ability hits exactly one tile.

### Structural

- **`GridManager` is 845 lines** (469 code, the rest comment and blank) — **22%** of the codebase's
  3,758 lines, down from ~40% when it was last flagged. It grew ~120 lines while everything else
  grew ~900, and shed the combat-integration responsibility entirely. Five responsibilities remain:
  tile data, coordinate conversion, rendering, highlighting, pathfinding. A `GridView` split
  (rendering + highlighting out) is the obvious next move but is **deliberately deferred** —
  `ShowMoveRangeForUnit` spans both halves, so the split needs a facade or a two-hop at every call
  site. Revisit when highlighting grows a third consumer (path preview, AoE preview, threat overlay).
- **No assembly definitions yet.** The namespace graph is acyclic, so the split is possible, but
  everything still compiles into `Assembly-CSharp`.
- **Single-camera assumption** — `Camera.main` in `UnitStatusBar` and `GridCursorHighlighter`;
  split-screen would need explicit references.
- **Scene-authored HUD** — unlike unit bars, `CombatHUD` and `UnitInfoPanel` hierarchies are built
  by hand in the editor.
