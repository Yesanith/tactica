using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using Tactica.Combat;
using Tactica.Grid;
using Tactica.Stats;

namespace Tactica.Campaign
{
    // Turns a mission and a deployed roster into a live encounter, and the encounter's end back
    // into a MissionResult. Owns every GridUnit it spawns and is the only thing that destroys them.
    //
    // The bridge between Campaign data and the scene, in the same way PlayerActionController is
    // the bridge between combat and the grid. Neither CombatManager nor GridManager learns that
    // missions exist - they get a roster and a board, as they did when both were hand-built.
    public class MissionRunner : MonoBehaviour
    {
        [Tooltip("Board to spawn on. Defaults to the first GridManager in the scene.")]
        [SerializeField] private GridManager gridManagerRef;

        [Tooltip("Combat to run. Defaults to the first CombatManager in the scene.")]
        [SerializeField] private CombatManager combatManagerRef;

        [Tooltip("Pause between the deciding blow and the result screen, so it can be seen landing.")]
        [Min(0f)]
        [SerializeField] private float resultDelay = 1.0f;

        // Raised once per mission, after the board has been cleared.
        public event Action<MissionResult> OnMissionFinished;

        public bool IsRunning => currentMission != null;
        public IReadOnlyList<GridUnit> SpawnedUnits => spawnedUnits;
        public CombatManager Combat => combatManagerRef;

        private readonly List<GridUnit> spawnedUnits = new List<GridUnit>();
        private readonly Dictionary<GridUnit, RosterUnit> rosterSources = new Dictionary<GridUnit, RosterUnit>();

        // One shared material for every visor: a per-unit Renderer.material would clone a material
        // per spawn and leak each one.
        private static Material visorMaterial;
        private static readonly Color VisorColor = new Color(0.2f, 0.95f, 1f);

        private MissionDefinition currentMission;
        private Coroutine finishRoutine;

        // The unit a DefeatTarget or Protect objective is about. Watched through its own
        // StatsChanged, which covers damage, knockouts and faction changes alike - including a
        // hack, or the debug faction panel.
        private GridUnit objectiveUnit;

        public MissionDefinition CurrentMission => currentMission;
        private bool authoredUnitsRemoved;

        private void Awake()
        {
            if (gridManagerRef == null)
            {
                gridManagerRef = FindAnyObjectByType<GridManager>();
            }

            if (combatManagerRef == null)
            {
                combatManagerRef = FindAnyObjectByType<CombatManager>();
            }
        }

        private void OnEnable()
        {
            if (combatManagerRef != null)
            {
                combatManagerRef.OnCombatEnded += HandleCombatEnded;
            }
        }

        private void OnDisable()
        {
            if (combatManagerRef != null)
            {
                combatManagerRef.OnCombatEnded -= HandleCombatEnded;
            }
        }

        // Spawns both sides and starts combat. Returns false with a reason instead of starting a
        // half-built encounter.
        public bool StartMission(MissionDefinition mission, IReadOnlyList<RosterUnit> deployed, out string error)
        {
            error = null;

            if (gridManagerRef == null || combatManagerRef == null)
            {
                error = "No GridManager or CombatManager in the scene.";
                return false;
            }

            if (IsRunning)
            {
                error = "A mission is already running.";
                return false;
            }

            if (deployed.Count == 0)
            {
                error = "Deploy at least one unit first.";
                return false;
            }

            RemoveAuthoredUnits();

            // A fresh board per contract. Safe here because the previous mission's units were
            // destroyed in ClearBoard and the authored ones just above - nothing occupies a tile
            // that is about to disappear.
            gridManagerRef.BuildMap(mission.map);

            currentMission = mission;

            foreach (RosterUnit rosterUnit in deployed)
            {
                GridUnit unit = Spawn(rosterUnit.Name, rosterUnit.Job, rosterUnit.Level, Faction.Player);

                if (unit != null)
                {
                    rosterSources[unit] = rosterUnit;
                }
            }

            foreach (UnitEntry entry in mission.opponents)
            {
                Spawn(entry.unitName, entry.job, entry.level, entry.faction, entry.guest);
            }

            if (spawnedUnits.Count == 0)
            {
                error = "No unit could be placed on the board.";
                ClearBoard();
                currentMission = null;
                return false;
            }

            Debug.Log($"Mission start: {mission.missionName} ({spawnedUnits.Count} units). " +
                      $"Objective: {mission.DescribeObjective()}.", this);

            combatManagerRef.StartCombat(spawnedUnits);
            WatchObjective(mission);
            return true;
        }

        // Subscribed after StartCombat, so spawning and the first stat fill cannot trip it.
        private void WatchObjective(MissionDefinition mission)
        {
            objectiveUnit = null;

            if (mission.objective == MissionObjective.DefeatAll)
            {
                return;
            }

            objectiveUnit = spawnedUnits.Find(unit => unit != null && unit.DisplayName == mission.objectiveUnitName);

            if (objectiveUnit == null)
            {
                Debug.LogWarning(
                    $"{nameof(MissionRunner)}: objective unit '{mission.objectiveUnitName}' is not in " +
                    $"'{mission.missionName}'. Falling back to defeating all enemies.",
                    this);
                return;
            }

            objectiveUnit.StatsChanged += CheckObjective;
        }

        private void StopWatchingObjective()
        {
            if (objectiveUnit != null)
            {
                objectiveUnit.StatsChanged -= CheckObjective;
                objectiveUnit = null;
            }
        }

        // Ends the fight early when the objective is met or failed. Can run in the middle of an
        // action's resolution (a knockout raises StatsChanged from ApplyDamage); that is safe
        // because the controllers' own TryEndCombatNow afterwards just sees combat already over.
        private void CheckObjective()
        {
            if (!IsRunning || objectiveUnit == null || combatManagerRef.CurrentState == CombatState.CombatEnd)
            {
                return;
            }

            switch (currentMission.objective)
            {
                case MissionObjective.DefeatTarget:
                    if (objectiveUnit.IsKnockedOut || objectiveUnit.CurrentFaction != Faction.Enemy)
                    {
                        Debug.Log($"Objective met: {objectiveUnit.DisplayName} neutralized.", this);
                        combatManagerRef.EndCombat(CombatEndResult.PlayerVictory);
                    }
                    break;

                case MissionObjective.Protect:
                    if (objectiveUnit.IsKnockedOut)
                    {
                        Debug.Log($"Objective failed: {objectiveUnit.DisplayName} went down.", this);
                        combatManagerRef.EndCombat(CombatEndResult.PlayerDefeat);
                    }
                    break;
            }
        }

        // Leaves without a decision. The result still goes out, as a Retreat, so the menu has
        // one path back to the hub whichever way a mission ends.
        public void Retreat()
        {
            if (!IsRunning || finishRoutine != null)
            {
                return;
            }

            combatManagerRef.AbortCombat();
            Finish(MissionOutcome.Retreat, 0f);
        }

        public RosterUnit GetRosterSource(GridUnit unit)
        {
            return unit != null && rosterSources.TryGetValue(unit, out RosterUnit source) ? source : null;
        }

        private void HandleCombatEnded(CombatEndResult result)
        {
            if (!IsRunning || finishRoutine != null)
            {
                return;
            }

            MissionOutcome outcome = result == CombatEndResult.PlayerVictory
                ? MissionOutcome.Victory
                : MissionOutcome.Defeat;

            Finish(outcome, resultDelay);
        }

        private void Finish(MissionOutcome outcome, float delay)
        {
            finishRoutine = StartCoroutine(FinishRoutine(outcome, delay));
        }

        private IEnumerator FinishRoutine(MissionOutcome outcome, float delay)
        {
            // Always yields at least once. With no yield the whole routine would run inside
            // StartCoroutine, clear finishRoutine, and then have StartCoroutine's return value
            // assigned back over it - leaving it non-null forever and every later mission's end
            // ignored by the guard in HandleCombatEnded.
            if (delay > 0f)
            {
                yield return new WaitForSeconds(delay);
            }
            else
            {
                yield return null;
            }

            StopWatchingObjective();

            // Snapshotted before the board is cleared - the reports outlive the GridUnits.
            MissionResult result = BuildResult(outcome);

            ClearBoard();

            currentMission = null;
            finishRoutine = null;

            Debug.Log($"Mission over: {result.Mission.missionName} - {outcome}.", this);

            OnMissionFinished?.Invoke(result);
        }

        private MissionResult BuildResult(MissionOutcome outcome)
        {
            MissionResult result = new MissionResult { Mission = currentMission, Outcome = outcome };

            foreach (GridUnit unit in spawnedUnits)
            {
                if (unit == null)
                {
                    continue;
                }

                result.Units.Add(new MissionUnitReport
                {
                    Name = unit.DisplayName,
                    Job = unit.CurrentJob,
                    Level = unit.Level,
                    FinalFaction = unit.CurrentFaction,
                    KnockedOut = unit.IsKnockedOut,
                    Flatlined = unit.IsFlatlined,
                    RosterSource = GetRosterSource(unit),
                });
            }

            return result;
        }

        // Creates a capsule unit on a free tile on its side of the board.
        //
        // Built inactive and configured before activation, so GridUnit.Awake computes stats from
        // the right job and level on its one and only run. Activating first would initialise from
        // an empty job, log a warning, and then need a second, full-heal initialisation.
        private GridUnit Spawn(string unitName, JobDefinition job, int level, Faction faction, bool guest = false)
        {
            if (job == null)
            {
                Debug.LogWarning($"{nameof(MissionRunner)}: '{unitName}' has no job and was not spawned.", this);
                return null;
            }

            if (!TryFindSpawnTile(faction, out Vector2Int coords))
            {
                Debug.LogWarning($"{nameof(MissionRunner)}: no free tile for '{unitName}' ({faction}).", this);
                return null;
            }

            // Unity's Capsule is 2 tall with a centred pivot - GridUnit's default pivotHeight of
            // 1.0 is sized for exactly this.
            GameObject unitObject = GameObject.CreatePrimitive(PrimitiveType.Capsule);
            unitObject.SetActive(false);

            GridUnit unit = unitObject.AddComponent<GridUnit>();
            unit.Configure(unitName, job, level, faction, coords, guest);
            unitObject.name = $"{unit.DisplayName} ({faction})";

            // Each side starts looking across the board at the other. Neutrals face the player's
            // edge, which is where trouble usually comes from.
            unit.SetFacing(faction == Faction.Player ? Facing.North : Facing.South);
            AddVisor(unitObject);

            unitObject.SetActive(true);

            // Claimed now rather than in GridUnit.Start (next frame), so the next spawn in this
            // same loop sees the tile as taken.
            gridManagerRef.SetOccupant(coords, unitObject);
            unit.SnapToGridPosition();

            spawnedUnits.Add(unit);
            return unit;
        }

        // A neon strip on the capsule's +Z side, so facing - which decides back and side attacks -
        // can be read at a glance. Child of the unit, so it turns with GridUnit.SetFacing.
        private static void AddVisor(GameObject unitObject)
        {
            GameObject visor = GameObject.CreatePrimitive(PrimitiveType.Cube);
            visor.name = "Visor";

            // Purely visual. A collider here would be one more thing in front of the tile raycast
            // and the unit's own collider.
            UnityEngine.Object.DestroyImmediate(visor.GetComponent<Collider>());

            visor.transform.SetParent(unitObject.transform, false);
            visor.transform.localPosition = new Vector3(0f, 0.45f, 0.42f);
            visor.transform.localScale = new Vector3(0.55f, 0.14f, 0.2f);

            Renderer renderer = visor.GetComponent<Renderer>();

            if (visorMaterial == null)
            {
                visorMaterial = new Material(renderer.sharedMaterial) { color = VisorColor };
                visorMaterial.hideFlags = HideFlags.HideAndDontSave;
            }

            renderer.sharedMaterial = visorMaterial;
        }

        // Player fills rows from the near edge, Enemy from the far edge, Neutral from the middle
        // outwards. Within a row, columns closest to the centre go first, so small squads face
        // each other instead of hugging a corner.
        private bool TryFindSpawnTile(Faction faction, out Vector2Int coords)
        {
            int width = gridManagerRef.GridWidth;
            int depth = gridManagerRef.GridDepth;

            foreach (int z in RowOrder(faction, depth))
            {
                foreach (int x in CentreOutOrder(width))
                {
                    Vector2Int candidate = new Vector2Int(x, z);

                    if (gridManagerRef.TryGetTile(candidate, out GridTile tile) && tile.IsFree)
                    {
                        coords = candidate;
                        return true;
                    }
                }
            }

            coords = default;
            return false;
        }

        private static IEnumerable<int> RowOrder(Faction faction, int depth)
        {
            switch (faction)
            {
                case Faction.Player:
                    for (int z = 0; z < depth; z++) yield return z;
                    break;

                case Faction.Enemy:
                    for (int z = depth - 1; z >= 0; z--) yield return z;
                    break;

                case Faction.Neutral:
                    foreach (int z in CentreOutOrder(depth)) yield return z;
                    break;
            }
        }

        private static IEnumerable<int> CentreOutOrder(int count)
        {
            if (count <= 0)
            {
                yield break;
            }

            int centre = (count - 1) / 2;
            yield return centre;

            for (int distance = 1; distance < count; distance++)
            {
                if (centre + distance < count) yield return centre + distance;
                if (centre - distance >= 0) yield return centre - distance;
            }
        }

        // Destroys every spawned unit and releases its tile. GridUnit.OnDestroy releases it too,
        // but Destroy is deferred to the end of the frame; clearing now means the board is
        // genuinely empty the moment the result is announced.
        private void ClearBoard()
        {
            gridManagerRef.ClearMoveRangeHighlight();
            gridManagerRef.ClearAttackableHighlights();
            gridManagerRef.ClearCursorHighlight();

            foreach (GridUnit unit in spawnedUnits)
            {
                if (unit == null)
                {
                    continue;
                }

                gridManagerRef.SetOccupant(unit.GridCoords, null);
                Destroy(unit.gameObject);
            }

            spawnedUnits.Clear();
            rosterSources.Clear();
        }

        // The scene's hand-placed units were a test fixture for combat. Once the mission flow is
        // driving, they would only stand on spawn tiles and fight in no one's encounter.
        //
        // Deactivated before being destroyed: Destroy is deferred to the end of the frame, and a
        // unit whose Start has not run yet would otherwise still claim its tile in the meantime.
        private void RemoveAuthoredUnits()
        {
            if (authoredUnitsRemoved)
            {
                return;
            }

            authoredUnitsRemoved = true;

            foreach (GridUnit unit in FindObjectsByType<GridUnit>(FindObjectsInactive.Include))
            {
                if (spawnedUnits.Contains(unit))
                {
                    continue;
                }

                unit.gameObject.SetActive(false);

                if (gridManagerRef.TryGetTile(unit.GridCoords, out GridTile tile) && tile.Occupant == unit.gameObject)
                {
                    gridManagerRef.SetOccupant(unit.GridCoords, null);
                }

                Destroy(unit.gameObject);
            }
        }
    }
}
