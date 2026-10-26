using System;
using System.Collections.Generic;
using UnityEngine;
using Tactica.Stats;

namespace Tactica.Campaign
{
    // One neighbourhood on the city map. The map IS the main menu: crew, shop, hiring and the job
    // board are all reached from the district the crew is in. GAME_DESIGN.md "Dünya haritası".
    //
    // Missions are referenced by name (MissionDefinition.missionName) rather than nested, so one
    // contract can be a listing in one district and a road encounter template in another.
    [Serializable]
    public class DistrictDefinition
    {
        public string districtName = "New District";

        [TextArea]
        public string description;

        [Tooltip("1-5. Drives the chance of a road encounter on the way in.")]
        [Range(1, 5)]
        public int danger = 1;

        [Tooltip("Who runs it - flavour for the map screen (gangs, corp, drones).")]
        public string dominantFaction;

        [Tooltip("Districts reachable from here, by name. Roads are listed on both ends.")]
        public List<string> connections = new List<string>();

        [Tooltip("Open from a new game. Others open through story missions.")]
        public bool openFromStart;

        [Header("Services")]
        public List<EquipmentDefinition> shopStock = new List<EquipmentDefinition>();

        [Tooltip("Jobs this district's hiring pool draws from. Empty uses the config's list.")]
        public List<JobDefinition> hireJobs = new List<JobDefinition>();

        [Tooltip("Job board contracts, by mission name. Repeatable.")]
        public List<string> listings = new List<string>();

        [Tooltip("Story missions here, in order. Each plays once.")]
        public List<string> story = new List<string>();

        [Tooltip("Mission names used as templates for road encounters on the way here - only when " +
                 "the district has no enemy pool to generate them from.")]
        public List<string> roadEncounters = new List<string>();

        [Header("Generated work")]
        [Tooltip("Enemy jobs this district's generated contracts and road ambushes draw from, at a " +
                 "level set by its danger and the crew's level. Empty: nothing is generated here.")]
        public List<JobDefinition> enemyPool = new List<JobDefinition>();

        [Tooltip("Crew jobs that turn up among generated enemies once they reach " +
                 "ContractGenerator.EliteFromLevel - veterans with a full kit, FFT's human enemies. " +
                 "Keep hackers out: enemies taking over the crew is still deferred.")]
        public List<JobDefinition> elitePool = new List<JobDefinition>();

        [Tooltip("Added to the shop once the district's story is finished.")]
        public List<EquipmentDefinition> clearedStock = new List<EquipmentDefinition>();

        [Tooltip("Districts reached from here by a safe road - no ambush either way. Listing it on " +
                 "one end is enough.")]
        public List<string> safeRoads = new List<string>();
    }
}
