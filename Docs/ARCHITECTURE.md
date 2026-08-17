# Tactica — Architecture

> **This document is maintained alongside the code, not written once.**
> Any change that alters how a system behaves — a new class, a changed rule, a removed
> placeholder, a resolved limitation — should update the relevant section in the same commit.
> A stale architecture doc is worse than none: it gets trusted and it lies.
>
> Scope: **what exists today.** Not a roadmap, not a task list. Known limitations are recorded
> because they are current facts about the code, not because they are planned work.

Last verified against a clean compile of all 23 scripts (3,758 lines; 2,228 excluding comments and
blanks).

---

## Project conventions

| | |
|---|---|
| Unity | 6000.5.2f1, URP |
| Input | Input System package **only** (`activeInputHandler: 1`). Legacy `UnityEngine.Input` throws at runtime. |
| Namespaces | `Tactica.Grid`, `Tactica.Camera`, `Tactica.Stats`, `Tactica.Combat`, `Tactica.UI` — each maps 1:1 to a folder under `Assets/Scripts/` |
| Naming | `camelCase` private fields (serialized and not); `PascalCase` public members, properties, methods, consts, and `static readonly` |
| Field renames | Any rename of a `[SerializeField]` field carries `[FormerlySerializedAs("OldName")]`, because Unity serializes by field name and a bare rename silently discards Inspector values |
| Comments | Terse. They record *why*, not *what* — non-obvious invariants, platform gotchas, and decisions that look wrong but aren't |

### Namespace dependencies

```
Camera ──▶ Grid
  UI  ──▶ Grid, Combat, Stats     Grid ──▶ Combat, Stats, UI
Combat ──▶ Grid, Stats            Stats ──▶ (nothing)
```

`Stats` depends on nothing and is the only namespace safely extractable today.

**Two cycles remain — `Grid ↔ Combat` and `Grid ↔ UI` — but each now hangs on a single field in a
single file:**

| Cycle | The one `Grid →` edge | Why it is there |
|---|---|---|
| `Grid ↔ Combat` | `GridCursorHighlighter.actionController` | The input *source* reports a clicked tile to the input *policy* |
| `Grid ↔ UI` | `GridUnit.statusBar` | `GridUnit.Awake` attaches the bar — the design that makes runtime-spawned units possible |

Both are legal and harmless inside the single `Assembly-CSharp` assembly; either would be a hard
compile error under assembly definitions.

**`GridManager` specifically is free of `Combat`** — no `using Tactica.Combat` at all, after
`combatManagerRef`, the `OnActiveUnitChanged` subscription, and active-unit resolution moved to
`PlayerActionController`. That narrowed the cycle to one file; it did **not** break it. Deleting
`GridCursorHighlighter`'s field (an event or an interface would do it) is what would.

**Direction of control:** `Combat` drives `GridManager`, never the reverse — `PlayerActionController`
calls `ShowMoveRangeForUnit` / `TryMoveUnitToTile` / `ClearMoveRangeHighlight`, and the grid data
layer never asks whose turn it is. The exception is `GridCursorHighlighter`, which calls
`HandleTileClicked` on every board click; that call *is* the surviving `Grid → Combat` edge above.
`UI` observes `Combat` purely through events (`OnActiveUnitChanged`, `OnStatsChanged`,
`OnTargetSelected`) and is never called by it.

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

### Rendering

`RenderGrid()` instantiates one `tilePrefab` per tile, named `Tile_x_y`, scaled
`(tileSize, tileVisualHeight, tileSize)`, assigned to the **`Tiles` layer** (index 3 in
`TagManager.asset`).

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

Two different metrics, deliberately:

**`GetGridDistance(a, b)` — Chebyshev**, `max(|dx|, |dy|)`. Used for ability range. Static, pure
arithmetic, path-independent.

**`GetTilesInMoveRange(start, moveRange, maxStepHeightOverride = null)` — BFS with a
one-diagonal-per-path limit.** Open-ground cost:

```
dx + dy - 1   when both dx and dy are non-zero
dx + dy       otherwise
```

So `(1,1)` costs 1, `(2,1)` costs 2, `(2,2)` costs **3** — while `GetGridDistance` reports `(2,2)`
as 2. The divergence is intentional: movement is a path and pays for ground covered; targeting is
proximity and an arrow does not walk. **A unit can shoot a tile it cannot step onto this turn.**

The search runs over `(tile, diagonal-still-available)` states, with **two visited sets**. Reaching
a tile with the diagonal unspent dominates every state of that tile; reaching it spent only blocks
later spent candidates. One shared set would let an early diagonal-spending path lock out a
same-length orthogonal path arriving with the diagonal intact.

Rejections **never** mark a tile visited. The height rule depends on where you step *from*, so a
tile refused for a cliff on one side must stay eligible from a gentler slope elsewhere.

Every step costs 1, including diagonals, which is what keeps this a plain BFS. Variable terrain cost
would require Dijkstra.

Blocking rules: missing key (off-map or hole), `!IsWalkable`, `Occupant != null` (**allies block
too**), and `|Δheight| > stepLimit`. `maxStepHeightOverride` exists and is tested but **has no
callers** — `CharacterStats.Jump` is not wired through.

### `GridUnit`

Per-unit state and lifecycle:

- **`Awake`** — resolves `gridManagerRef`, adds a `UnitStatusBar` if absent, calls
  `RecalculateStats()`. Stats are computed here, not in `Start`, because every `Awake` precedes
  every `Start` and `GridManager.Start` reads `EffectiveStats.Move`. Two `Start`s have no defined
  order, so computing there is a coin flip that fails silently as `Move = 0`.
- **`Start`** — `SnapToGridPosition()` and claims its tile via `SetOccupant`. Both need `GridTiles`,
  built in `GridManager.Awake`, and Awake-to-Awake order is undefined.

`CurrentStats` is a **public field**, not a property: it is a struct, and C# forbids writing through
a struct-typed property (`unit.CurrentStats.HP -= 5` is `CS1612`).

`ApplyDamage` floors at 0 and ignores non-positive input; `Heal` caps at `EffectiveStats.MaxHP`.
`RefreshStatusBar()` is the single UI-refresh entry point, called from both plus `RecalculateStats`.

**Turn economy.** `HasMovedThisTurn` and `HasAttackedThisTurn` (read-only, not serialized) are both
reset by `BeginTurn()` and set by `MarkMoved()` / `MarkAttacked()`. They live on the unit rather
than in `PlayerActionController` because they are facts about the unit's turn, not about player
input — an AI turn handler needs the same answers, and a per-unit dictionary in the input controller
would have to be pruned when units are destroyed. **Only movement is capped**; `HasAttackedThisTurn`
does not block a second attack, it only gates the defensive stance below.

**Defensive stance.** `IsDefending` grants a flat `defenseBonus` (default 5, serialized so it is
tunable per unit). Granted by `PlayerActionController.Wait()` when `!HasAttackedThisTurn`; cleared
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
`Description`, `MPCost`, `Range` (Chebyshev), `AreaOfEffect`, `EffectType`, `Power`, and three
targeting bools.

**`AbilityUnlock`** (struct) — `RequiredLevel` + a direct `AbilityDefinition` reference.
`GetUnlockedAbilities(level)` skips unassigned rows rather than returning nulls, and assumes no
ordering of the authored list.

All ScriptableObject fields are exposed **read-only**: writing to one at runtime edits the project
asset, and in the editor that change survives leaving Play mode.

---

## Combat — `Assets/Scripts/Combat/`

### Turn order — `CombatManager`

`StartCombat()` builds initiative from `participatingUnits`, sorted by `Speed` descending with an
**explicit `ThenBy` on original list index**. That tiebreak is what makes the order deterministic:
`List<T>.Sort` is introsort and documented unstable, so ties alone could resolve differently between
runs. Determinism matters because a non-deterministic turn order makes bugs reproduce only
sometimes and any test asserting position flaky.

`EndCurrentTurn()` advances with wraparound; wrapping to index 0 increments `RoundNumber`. Nulls are
dropped from the order. Both `StartCombat` and `EndCurrentTurn` finish by calling `BeginTurnFor()`,
which resets the incoming unit's per-turn state and *then* raises `OnActiveUnitChanged` — fired
**last**, after state is settled, so handlers see a finished turn and already-reset flags.

The reset happens here rather than in a listener because it is an invariant of the turn system: it
must apply to AI-driven turns too, and must not depend on any particular listener existing.

Initiative is a **snapshot** — Speed changes mid-combat do not reshuffle an encounter underway.

### State — `CombatState`

`WaitingForInput`, `ResolvingAction`, `TurnTransition`, `CombatEnd`. Exactly one at a time.

### End conditions — `CheckCombatEndCondition()`

Returns `CombatEndResult` (`Ongoing` first, so `default` means "not over"). Called from
`EndCurrentTurn` after the state transition; a non-`Ongoing` result sets `CombatEnd`, logs, and
fires `OnActiveUnitChanged(null)` so the board clears.

Each side is only defeatable **if it had members** — otherwise "all player units are down" is
vacuously true on a roster with none, and a one-unit test scene would end instantly. Mutual wipeout
resolves to `PlayerDefeat`; the check order is the only thing deciding that.

`EndCurrentTurn` refuses to advance once `CombatEnd` is set, and logs why.

### Rules — `ActionResolver`

Plain C# class, **not** a MonoBehaviour — no scene state, constructible in a test. Owned by
`CombatManager` via `Resolver`.

- `IsTargetInRange` — `GetGridDistance <= ability.Range`. Range 0 is self-only.
- `IsValidTarget` — self requires `TargetsSelf`; **everyone else counts as an enemy** (placeholder).
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
| `BeginAttack()` | picks an ability, **clears the move range**, lights valid targets `Attackable`, `Mode = Targeting` |
| `Wait()` | grants the defensive stance if `!HasAttackedThisTurn`, then ends the turn |
| `HandleTileClicked(coords)` | routes by mode |
| `HandleActiveUnitChanged(unit)` | event handler; clears the outgoing range, draws nothing |

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

`ResolveTargetClick` calls `MarkAttacked()` **only on a successful** `TryExecuteAbility`, mirroring
`MarkMoved()`. A rejected ability costs nothing, so it must not forfeit the stance. The stance
condition is "did not attack", not "did nothing" — moving into cover and bracing is the choice being
rewarded, and demanding the unit stand still too would make Wait strictly worse than not moving.

While `Targeting`, clicking a non-attackable tile **cancels** rather than doing nothing, so the
player is never stuck. All entry points refuse unless `CurrentState == WaitingForInput`.

Ability selection is `GetUnlockedAbilities(Level)[0]` — a placeholder for a selection menu.
Target search uses `GetGridDistance`, not the move-range fill, because reach ignores walls and the
fill excludes occupied tiles — which is every target by definition.

---

## UI — `Assets/Scripts/UI/`

### World-space bars — `UnitStatusBar`

Added automatically by `GridUnit.Awake`; **builds its own hierarchy in code**. A `GridUnit`
component is the entire requirement for a unit, which is what makes runtime-spawned units possible.

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

### HUD panels — `UnitInfoPanel`, `CombatHUD`

`UnitInfoPanel` binds a `GridUnit` and re-reads it on `Refresh()`, so the HUD can refresh whatever
is displayed without tracking it. Shows `JobName` (placeholder for real unit names, falling back to
the GameObject name), `Lv.{n}`, and `{current}/{max}` labels beside filled `Image` bars. HP carries
faction colour. Null binding blanks the panel rather than throwing or leaving stale numbers.

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

## Known limitations and scaffolding

Current facts about the code, not planned work.

### Placeholders

| What | Where | Note |
|---|---|---|
| Everyone is an enemy | `ActionResolver.IsValidTarget` | No faction data. `TargetsAllies` is unreachable — an ally-only heal can target nobody. |
| `isPlayerControlled` bool | `GridUnit` | Same missing concept as above, seen from the other side. Cannot express neutrals or a third army. Only used by `CheckCombatEndCondition`. |
| First unlocked ability | `PlayerActionController.SelectAbility` | Stands in for an ability-selection menu. |
| `JobName` as unit name | `UnitInfoPanel` | Units have no names of their own. |
| Flat per-level growth | `JobDefinition` | Not a curve asset. |
| `Magic` unused | `ActionResolver.CalculateDamage` | All damage scales off `Strength`. |
| Buff/Debuff/StatusEffect | `ActionResolver.TryExecuteAbility` | Log only — but MP is spent and it returns `true`. |

### Scaffolding pending removal

- **`CombatManager.testAbility`** + `TestExecuteAbilityOnOtherUnit()` context-menu probe —
  deliberately does *not* gate on `CurrentState`, so resolver bugs are not hidden behind turn-state
  bugs.
- **`ActionResolver` calls `user.RefreshStatusBar()`** directly after spending MP, because MP is
  written as a raw field. Removable once MP spending goes through a `GridUnit` method.

### Scene requirements that fail silently

- **An `EventSystem` GameObject is required for the HUD.** Missing it produces no logs and no
  exceptions — the buttons are simply inert, and the HUD additionally becomes click-through because
  `GridInput.IsPointerOverUI()` returns `false` with no `EventSystem.current`. Nothing in the code
  can detect or warn about this. See the `CombatHUD` section for the mechanism and the fix.
- **The `Tiles` layer must exist** in *Project Settings → Tags and Layers*. This one *does* warn
  (once, from `GridManager.TileLayer`) and degrades to raycasting all layers, so unit colliders
  start blocking tile hover rather than hover failing outright.

### Ordering and state

- **Mid-turn KO timing** — `CheckCombatEndCondition` runs only at the turn boundary, so a KO does
  not end combat until the turn ends.
- **KO'd units still take turns** and still occupy their tile, blocking movement.
- **Stale `combatManagerRef` on reassignment** — `PlayerActionController` and `CombatHUD` read the
  field in `OnEnable`/`OnDisable`; changing it mid-play unsubscribes from the wrong object.
- **Turn-change highlight clearing needs `PlayerActionController` enabled.** It owns
  `HandleActiveUnitChanged` now; `GridManager` used to, and `GridManager` is effectively always on.
  Disabling the controller mid-combat would strand a lit move range.
- **Stale `EffectiveStats`** — changing `Level` at runtime does nothing until `RecalculateStats()`.
- **`CurrentStats` writes bypass the UI** — it is a public field, so any external write leaves bars
  and panels stale unless `RefreshStatusBar()` is called.

### Rules and economy

- **Partial action economy.** Movement is capped at once per turn (`GridUnit.HasMovedThisTurn`).
  **Attacking is not capped** — a unit can attack repeatedly until the turn ends.
  `HasAttackedThisTurn` records that it happened but gates only the defensive stance.
- **The defensive stance has no visual** — console logs only, so in-editor it is invisible unless
  the console is open. Status icon is future HUD work.
- **Allies block movement**, both as destinations and as pass-through.
- **Diagonal travel is strictly better than orthogonal** — both cost 1, so optimal play drifts onto
  diagonals.
- **`CharacterStats.Jump` is not wired** to `GetTilesInMoveRange`'s override parameter; the
  grid-wide `maxStepHeight` applies to everyone.
- **Ability `AreaOfEffect` is authored but unread** — every ability hits exactly one tile.

### Structural

- **`GridManager` is 845 lines** (469 code, the rest comment and blank) — **22%** of the codebase's
  3,758 lines, down from ~40% when it was last flagged. It grew ~120 lines while everything else
  grew ~900, and shed the combat-integration responsibility entirely. Five responsibilities remain:
  tile data, coordinate conversion, rendering, highlighting, pathfinding. A `GridView` split
  (rendering + highlighting out) is the obvious next move but is **deliberately deferred** —
  `ShowMoveRangeForUnit` spans both halves, so the split needs a facade or a two-hop at every call
  site. Revisit when highlighting grows a third consumer (path preview, AoE preview, threat overlay).
- **Two namespace cycles** (`Grid ↔ Combat`, `Grid ↔ UI`) block an assembly-definition split. Each
  is down to a single field in a single file — `GridCursorHighlighter.actionController` and
  `GridUnit.statusBar` — but narrowed is not broken, and both cycles are intact.
- **Single-camera assumption** — `Camera.main` in `UnitStatusBar` and `GridCursorHighlighter`;
  split-screen would need explicit references.
- **Scene-authored HUD** — unlike unit bars, `CombatHUD` and `UnitInfoPanel` hierarchies are built
  by hand in the editor.
