using System;
using System.Collections.Generic;
using UnityEngine;

namespace Tactica.Campaign
{
    // What a contract asks for, on top of "one side is down". CombatManager's end condition still
    // applies to every mission; an objective can only END a fight earlier, never keep a won or lost
    // one going. That is the split PROGRESS.md argued for: end conditions answer "is there still a
    // fight", objectives answer "did you do what you came for".
    //
    // Values PINNED - serialised in CampaignConfig.
    public enum MissionObjective
    {
        // Win when the enemy side is down. The plain fight.
        DefeatAll = 0,

        // Win the moment the named unit is knocked out or hacked off the enemy side - FFT's
        // "defeat the boss". The guards do not have to fall.
        DefeatTarget = 1,

        // Lose the moment the named unit goes down - FFT's guest escort.
        Protect = 2,
    }

    // One selectable encounter. Opponents only - the player side is whoever is deployed from the
    // roster when the mission starts, plus any opponent entry with faction Player (a guest).
    // Spawn tiles are chosen by MissionRunner, so a mission does not need to know the map.
    [Serializable]
    public class MissionDefinition
    {
        public string missionName = "New Mission";

        [TextArea]
        public string description;

        public MissionObjective objective = MissionObjective.DefeatAll;

        [Tooltip("The unit DefeatTarget and Protect are about, matched against an opponent's unitName.")]
        public string objectiveUnitName;

        [Tooltip("Every non-roster unit in the encounter: enemies, neutrals, and Player-side guests.")]
        public List<UnitEntry> opponents = new List<UnitEntry>();

        public string DescribeObjective()
        {
            switch (objective)
            {
                case MissionObjective.DefeatTarget: return $"Neutralize {objectiveUnitName}";
                case MissionObjective.Protect: return $"Keep {objectiveUnitName} alive";
                default: return "Defeat all enemies";
            }
        }
    }
}
