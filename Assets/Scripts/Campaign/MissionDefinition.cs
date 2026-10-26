using System;
using System.Collections.Generic;
using UnityEngine;
using Tactica.Grid;

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

        // Win once the crew has taken surviveTurns turns - an ambush to hold out against. Clearing
        // the enemy still wins early, as always.
        Survive = 3,
    }

    // Where a mission comes from, which decides its rewards and whether it repeats.
    // Values PINNED.
    public enum MissionKind
    {
        // A job-board contract: repeatable, pays credits.
        Listing = 0,

        // A step of the story: plays once, may unlock a district or bring someone into the crew.
        Story = 1,

        // Not authored as such - a listing used as a template when the road is cut on the way to a
        // district. Set at runtime; pays half.
        Road = 2,
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

        [Tooltip("The battlefield. Leave width/depth at 0 to fight on the scene's default flat grid.")]
        public MapSettings map;

        public MissionObjective objective = MissionObjective.DefeatAll;

        [Header("Campaign")]
        public MissionKind kind = MissionKind.Listing;

        [Tooltip("Paid on victory. Road encounters pay half.")]
        [Min(0)]
        public int credits = 100;

        [Tooltip("Weapons added to the inventory on victory.")]
        public List<Tactica.Stats.EquipmentDefinition> loot = new List<Tactica.Stats.EquipmentDefinition>();

        [Tooltip("Shown on the briefing screen before the fight.")]
        [TextArea(2, 6)]
        public string introText;

        [Tooltip("Shown on the result screen after a victory.")]
        [TextArea(2, 6)]
        public string outroText;

        [Tooltip("District opened by winning this (story) mission.")]
        public string unlocksDistrict;

        [Tooltip("Guests (by unitName) who join the crew for good when this mission is won.")]
        public List<string> joinsCrew = new List<string>();

        [Tooltip("Survive: turns the crew has to hold out for.")]
        [Min(1)]
        public int surviveTurns = 6;

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
                case MissionObjective.Survive: return $"Hold out for {surviveTurns} turns";
                default: return "Defeat all enemies";
            }
        }
    }
}
