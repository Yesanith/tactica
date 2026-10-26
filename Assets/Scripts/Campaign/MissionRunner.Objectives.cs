using System.Collections;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using Tactica.Combat;
using Tactica.Grid;
using Tactica.Stats;

namespace Tactica.Campaign
{
    // Mission objectives on top of CombatManager's own end condition: a named target, a unit to
    // protect, or turns to hold out for. An objective can only end a fight earlier.
    public partial class MissionRunner : MonoBehaviour
    {
        // Survive: the crew's turns taken so far. The fight is won at the start of the turn after
        // the last one it had to hold out for.
        private int survivedTurns;

        private void CountSurvivedTurn(GridUnit unit)
        {
            if (!IsRunning || IsDeploying || unit == null || currentMission.objective != MissionObjective.Survive
                || unit.CurrentFaction != Faction.Player || combatManagerRef.CurrentState == CombatState.CombatEnd)
            {
                return;
            }

            survivedTurns++;

            if (survivedTurns > currentMission.surviveTurns)
            {
                Debug.Log($"Objective met: held out for {currentMission.surviveTurns} turns.", this);
                combatManagerRef.EndCombat(CombatEndResult.PlayerVictory);
            }
        }

        // The objective as the fight overlay shows it, with progress where there is any.
        public string ObjectiveStatus
        {
            get
            {
                if (currentMission == null)
                {
                    return string.Empty;
                }

                return currentMission.objective == MissionObjective.Survive
                    ? $"Hold out: turn {Mathf.Min(survivedTurns, currentMission.surviveTurns)}/{currentMission.surviveTurns}"
                    : currentMission.DescribeObjective();
            }
        }

        // Subscribed after StartCombat, so spawning and the first stat fill cannot trip it.
        private void WatchObjective(MissionDefinition mission)
        {
            objectiveUnit = null;

            if (mission.objective == MissionObjective.DefeatAll || mission.objective == MissionObjective.Survive)
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
    }
}
