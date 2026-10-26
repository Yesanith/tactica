using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
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
    public partial class MissionRunner : MonoBehaviour
    {
        [Tooltip("Board to spawn on. Defaults to the first GridManager in the scene.")]
        [SerializeField] private GridManager gridManagerRef;

        [Tooltip("Combat to run. Defaults to the first CombatManager in the scene.")]
        [SerializeField] private CombatManager combatManagerRef;

        [Tooltip("Basic attack and weapon catalogue. Defaults to Resources/CampaignConfig.")]
        [SerializeField] private CampaignConfig config;

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

        // Side each unit started on, so the result can tell a hacked unit from a guest.
        private readonly Dictionary<GridUnit, Faction> startingFactions = new Dictionary<GridUnit, Faction>();

        // JP and XP earned per crew member this fight, from ActionResolver.OnActionResolved.
        private readonly Dictionary<GridUnit, (int JP, int XP)> earned = new Dictionary<GridUnit, (int JP, int XP)>();

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

        // Deployment: between spawning and the first turn, the player places the crew inside a
        // starting zone - FFT's formation step. The zone is a compact patch on the player's edge,
        // DeployZonePerUnit tiles per crew member (at least MinDeployZone), so there is room to
        // choose a shape but not to start anywhere on the map. Guests and opponents stay where
        // the mission put them.
        public const int DeployZonePerUnit = 2;
        public const int MinDeployZone = 6;

        private readonly HashSet<Vector2Int> deploymentZone = new HashSet<Vector2Int>();

        public bool IsDeploying { get; private set; }
        public IReadOnlyCollection<Vector2Int> DeploymentZone => deploymentZone;

        // The crew members on the board - the units the player places and commands.
        public IEnumerable<GridUnit> Crew => spawnedUnits.Where(unit => unit != null && rosterSources.ContainsKey(unit));

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

            if (config == null)
            {
                config = Resources.Load<CampaignConfig>(CampaignConfig.ResourcePath);
            }
        }

        private void OnEnable()
        {
            if (combatManagerRef != null)
            {
                combatManagerRef.OnCombatEnded += HandleCombatEnded;
                combatManagerRef.OnActiveUnitChanged += CountSurvivedTurn;
            }
        }

        private void OnDisable()
        {
            if (combatManagerRef != null)
            {
                combatManagerRef.OnCombatEnded -= HandleCombatEnded;
                combatManagerRef.OnActiveUnitChanged -= CountSurvivedTurn;
            }
        }

        // Spawns both sides and opens deployment; BeginBattle starts the fight once the crew is
        // placed. Returns false with a reason instead of starting a half-built encounter.
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
                GridUnit unit = Spawn(rosterUnit.Name, rosterUnit.Job, rosterUnit.Level, Faction.Player, false, null, 1f);

                if (unit != null)
                {
                    rosterSources[unit] = rosterUnit;

                    // What the unit has learned, not its job's level unlocks - after the basic Attack.
                    unit.SetActionLoadout(WithBasicAttack(rosterUnit.ActionLoadout()));
                    unit.SetWeapon(rosterUnit.Weapon);
                    unit.SetPassives(rosterUnit.Reaction, rosterUnit.Support, rosterUnit.Movement);
                }
            }

            foreach (UnitEntry entry in mission.opponents)
            {
                GridUnit unit = Spawn(entry.unitName, entry.job, entry.level, entry.faction, entry.guest, entry.modelOverride, entry.modelScale);

                if (unit == null)
                {
                    continue;
                }

                // Opponents fight with their job's fixed kit by level, its default weapon raised to
                // the tier for their level, and the first Reaction ability the kit unlocks.
                List<AbilityDefinition> kit = entry.job.GetUnlockedAbilities(entry.level);
                unit.SetActionLoadout(WithBasicAttack(kit.Where(a => a.Slot == AbilitySlot.Action)));
                unit.SetWeapon(config != null ? config.WeaponForLevel(entry.job.DefaultWeapon, entry.level) : entry.job.DefaultWeapon);
                unit.SetPassives(kit.FirstOrDefault(a => a.Slot == AbilitySlot.Reaction), null, null);
                unit.SetHackResistance(entry.hackResistance);
                unit.SetBoss(entry.boss);
            }

            if (spawnedUnits.Count == 0)
            {
                error = "No unit could be placed on the board.";
                ClearBoard();
                currentMission = null;
                return false;
            }

            Debug.Log($"Mission start: {mission.missionName} ({spawnedUnits.Count} units). " +
                      $"Objective: {mission.DescribeObjective()}. Deploying.", this);

            BuildDeploymentZone();
            IsDeploying = true;
            gridManagerRef.ShowDeployZone(deploymentZone);
            return true;
        }

        // Ends deployment and starts the fight with the crew where the player left it.
        public bool BeginBattle()
        {
            if (!IsDeploying)
            {
                return false;
            }

            IsDeploying = false;
            deploymentZone.Clear();
            gridManagerRef.ClearDeployZone();

            foreach (GridUnit unit in spawnedUnits)
            {
                startingFactions[unit] = unit.CurrentFaction;
            }

            earned.Clear();
            combatManagerRef.Resolver.OnActionResolved += RecordProgress;

            survivedTurns = 0;
            combatManagerRef.StartCombat(spawnedUnits);
            WatchObjective(currentMission);
            return true;
        }

        // JP and XP for crew members' effective actions. JP uses the unit's current job level
        // and character level at the moment it acts, which is what the formula describes.
        private void RecordProgress(ActionOutcome outcome)
        {
            if (!outcome.Effective || outcome.User == null)
            {
                return;
            }

            if (!rosterSources.TryGetValue(outcome.User, out RosterUnit source) || source.Locked)
            {
                return;
            }

            int jp = Progression.JPForAction(source.JobLevel(source.Job), source.Level);

            // JP Boost and the like.
            if (source.Support != null && source.Support.JPBonusPercent > 0)
            {
                jp = jp * (100 + source.Support.JPBonusPercent) / 100;
            }
            int targetLevel = outcome.Target != null ? outcome.Target.Level : outcome.User.Level;
            int xp = Progression.XPForAction(outcome.User.Level, targetLevel, outcome.KnockedOut);

            earned.TryGetValue(outcome.User, out (int JP, int XP) total);
            earned[outcome.User] = (total.JP + jp, total.XP + xp);
        }

        // The configured basic Attack first, then the rest - so the Act menu always opens on it.
        private List<AbilityDefinition> WithBasicAttack(IEnumerable<AbilityDefinition> abilities)
        {
            List<AbilityDefinition> loadout = new List<AbilityDefinition>();
            AbilityDefinition basic = config != null ? config.BasicAttack : null;

            if (basic != null)
            {
                loadout.Add(basic);
            }

            loadout.AddRange(abilities.Where(a => a != null && a != basic));
            return loadout;
        }

        // Leaves without a decision. The result still goes out, as a Retreat, so the menu has
        // one path back to the hub whichever way a mission ends.
        public void Retreat()
        {
            if (!IsRunning || IsDeploying || finishRoutine != null)
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
            combatManagerRef.Resolver.OnActionResolved -= RecordProgress;

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

                startingFactions.TryGetValue(unit, out Faction startFaction);
                earned.TryGetValue(unit, out (int JP, int XP) progress);

                result.Units.Add(new MissionUnitReport
                {
                    Name = unit.DisplayName,
                    Job = unit.CurrentJob,
                    Level = unit.Level,
                    FinalFaction = unit.CurrentFaction,
                    KnockedOut = unit.IsKnockedOut,
                    Flatlined = unit.IsFlatlined,
                    Hacked = startFaction != Faction.Player && unit.CurrentFaction == Faction.Player,
                    EarnedJP = progress.JP,
                    EarnedXP = progress.XP,
                    RosterSource = GetRosterSource(unit),
                });
            }

            return result;
        }

    }
}
