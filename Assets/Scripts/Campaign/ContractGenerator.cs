using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using Tactica.Grid;
using Tactica.Stats;

namespace Tactica.Campaign
{
    // Makes contracts and road ambushes from a district's enemy pool, so the job board refreshes
    // and the roads are not the same three fights forever. GAME_DESIGN.md: "İlan - pano zamanla
    // yenilenir", "Yol savaşları çeşitli olur", "Tehlike seviyesi - yol savaşlarındaki düşman
    // seviyesi".
    //
    // Pure and seeded: the same district and seed always give the same mission, so a board can be
    // rebuilt from (district, board version) instead of saved, and the tests can pin the rules.
    public static class ContractGenerator
    {
        public enum Purpose
        {
            Listing,
            Road,
        }

        private static readonly string[] Adjectives = { "Neon", "Chrome", "Static", "Midnight", "Broken", "Hot", "Quiet", "Red" };
        private static readonly string[] Nouns = { "Sweep", "Shakedown", "Pickup", "Turf War", "Cleanup", "Debt Run", "Raid", "Payback" };
        private static readonly string[] RoadNames = { "Roadblock", "Toll Gang", "Alley Ambush", "Overpass Jump", "Checkpoint" };
        private static readonly string[] EscortNames = { "Informant", "Courier", "Witness", "Fixer" };

        // Enemy level relative to the crew, per point of danger above or below this: a danger-3
        // district fights at the crew's level, Neon Row (1) two below, Spire Gate (4) one above.
        public const int EvenDanger = 3;

        // Generated enemies at this level or above bring veterans from the district's elite pool:
        // one at this level, one more every EliteEvery levels, never more than half the squad.
        // The start-tier enemy kits alone cannot keep up with a crew in mid and top jobs.
        public const int EliteFromLevel = 6;
        public const int EliteEvery = 3;

        public static int EliteCount(int enemyLevel, int squad)
        {
            int elites = enemyLevel < EliteFromLevel ? 0 : 1 + (enemyLevel - EliteFromLevel) / EliteEvery;
            return Mathf.Min(elites, squad / 2);
        }

        // The level a district's enemies start at for a crew of this level - FFT's random battles
        // follow the party the same way. Never below the danger itself, so a fresh crew still
        // finds the dangerous districts dangerous.
        public static int EnemyLevel(int danger, int crewLevel)
        {
            return Mathf.Max(danger, crewLevel + danger - EvenDanger);
        }

        // Null when the district has no enemy pool - nothing to generate from.
        public static MissionDefinition Generate(DistrictDefinition district, JobDefinition escortJob, int seed, Purpose purpose, int crewLevel = 1)
        {
            List<JobDefinition> pool = district?.enemyPool.Where(job => job != null).ToList();

            if (pool == null || pool.Count == 0)
            {
                return null;
            }

            System.Random rng = new System.Random(seed);
            int danger = Mathf.Clamp(district.danger, 1, 5);
            MissionObjective objective = PickObjective(rng, purpose, escortJob != null);
            int count = 2 + danger / 2 + rng.Next(2) + (objective == MissionObjective.Survive ? 1 : 0);

            MissionDefinition mission = new MissionDefinition
            {
                kind = MissionKind.Listing,
                objective = objective,
                map = new MapSettings
                {
                    width = 11 + rng.Next(5),
                    depth = 10 + rng.Next(4),
                    seed = rng.Next(1, 100000),
                    maxHeight = 1 + danger / 2,
                    heightStep = 0.5f,
                    edgeDepth = 2,
                    holeChance = 0.03f + rng.Next(4) * 0.01f,
                    coverChance = 0.04f + rng.Next(5) * 0.01f,
                },
            };

            // Opponents at the district's level for this crew, sometimes one above.
            int level = EnemyLevel(danger, crewLevel);
            List<UnitEntry> enemies = new List<UnitEntry>();

            List<JobDefinition> elites = district.elitePool.Where(job => job != null).ToList();
            int eliteCount = elites.Count > 0 ? EliteCount(level, count) : 0;

            for (int i = 0; i < count; i++)
            {
                JobDefinition job = i < eliteCount ? elites[rng.Next(elites.Count)] : pool[rng.Next(pool.Count)];
                enemies.Add(new UnitEntry { job = job, level = level + rng.Next(2), faction = Faction.Enemy });
            }

            NameEnemies(enemies);

            if (objective == MissionObjective.DefeatTarget)
            {
                UnitEntry boss = enemies[0];
                boss.unitName = $"{boss.job.JobName} Boss";
                boss.level += 1;
                boss.hackResistance = 15;
                mission.objectiveUnitName = boss.unitName;
            }

            mission.opponents.AddRange(enemies);

            if (objective == MissionObjective.Protect)
            {
                string escort = EscortNames[rng.Next(EscortNames.Length)];
                mission.opponents.Add(new UnitEntry { unitName = escort, job = escortJob, level = level, faction = Faction.Player, guest = true });
                mission.objectiveUnitName = escort;
            }

            if (objective == MissionObjective.Survive)
            {
                mission.surviveTurns = 4 + danger / 2 + rng.Next(2);
            }

            mission.credits = 60 + 30 * danger + 15 * count + 10 * (level - danger) + (objective != MissionObjective.DefeatAll ? 20 : 0);
            mission.missionName = purpose == Purpose.Road
                ? RoadNames[rng.Next(RoadNames.Length)]
                : $"{Adjectives[rng.Next(Adjectives.Length)]} {Nouns[rng.Next(Nouns.Length)]}";
            mission.description = Describe(objective, district);
            return mission;
        }

        // Board contracts lean on plain fights; the road mixes in holding out and escorts.
        private static MissionObjective PickObjective(System.Random rng, Purpose purpose, bool canEscort)
        {
            int roll = rng.Next(100);

            if (purpose == Purpose.Road)
            {
                if (roll < 45) return MissionObjective.DefeatAll;
                if (roll < 70) return MissionObjective.Survive;
                if (roll < 85 && canEscort) return MissionObjective.Protect;
                return MissionObjective.DefeatTarget;
            }

            if (roll < 55) return MissionObjective.DefeatAll;
            if (roll < 80) return MissionObjective.DefeatTarget;
            return canEscort ? MissionObjective.Protect : MissionObjective.DefeatAll;
        }

        // "Raider A", "Raider B" when a job repeats; a lone one keeps the plain job name.
        private static void NameEnemies(List<UnitEntry> enemies)
        {
            foreach (IGrouping<JobDefinition, UnitEntry> group in enemies.GroupBy(entry => entry.job))
            {
                List<UnitEntry> same = group.ToList();

                for (int i = 0; i < same.Count; i++)
                {
                    same[i].unitName = same.Count == 1 ? group.Key.JobName : $"{group.Key.JobName} {(char)('A' + i)}";
                }
            }
        }

        private static string Describe(MissionObjective objective, DistrictDefinition district)
        {
            string who = string.IsNullOrEmpty(district.dominantFaction) ? "Locals" : district.dominantFaction;

            switch (objective)
            {
                case MissionObjective.DefeatTarget: return $"{who} answer to someone. Take the boss down.";
                case MissionObjective.Protect: return $"{who} want your contact dead. Keep them breathing.";
                case MissionObjective.Survive: return $"{who} have you pinned. Hold out until the way is clear.";
                default: return $"{who} are making trouble. Clear them out.";
            }
        }

        // A stable string hash (string.GetHashCode is not guaranteed to be stable between runs).
        public static int StableHash(string text)
        {
            unchecked
            {
                int hash = (int)2166136261;

                foreach (char c in text ?? string.Empty)
                {
                    hash = (hash ^ c) * 16777619;
                }

                return hash;
            }
        }
    }
}
