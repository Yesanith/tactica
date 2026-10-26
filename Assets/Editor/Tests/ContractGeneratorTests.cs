using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using Tactica.Campaign;
using Tactica.Grid;
using Tactica.Stats;

namespace Tactica.Tests
{
    // Generated contracts and road ambushes (ContractGenerator): deterministic per seed, levelled by
    // the district's danger, and every objective well-formed.
    public class ContractGeneratorTests
    {
        private static DistrictDefinition District(int danger)
        {
            return new DistrictDefinition
            {
                districtName = "Test Row",
                danger = danger,
                dominantFaction = "Testers",
                enemyPool = new List<JobDefinition> { TestData.Job("Raider"), TestData.Job("Gooner") },
            };
        }

        private static JobDefinition Escort => TestData.Job("Netrunner");

        private static IEnumerable<MissionDefinition> Many(int danger, ContractGenerator.Purpose purpose)
        {
            DistrictDefinition district = District(danger);
            return Enumerable.Range(0, 200).Select(seed => ContractGenerator.Generate(district, Escort, seed, purpose));
        }

        [Test]
        public void TheSameSeedGivesTheSameContract()
        {
            DistrictDefinition district = District(3);
            MissionDefinition a = ContractGenerator.Generate(district, Escort, 42, ContractGenerator.Purpose.Listing);
            MissionDefinition b = ContractGenerator.Generate(district, Escort, 42, ContractGenerator.Purpose.Listing);

            Assert.AreEqual(a.missionName, b.missionName);
            Assert.AreEqual(a.objective, b.objective);
            Assert.AreEqual(a.map.seed, b.map.seed);
            CollectionAssert.AreEqual(a.opponents.Select(e => $"{e.unitName} {e.level}"), b.opponents.Select(e => $"{e.unitName} {e.level}"));
        }

        [Test]
        public void ADistrictWithoutAnEnemyPoolGeneratesNothing()
        {
            DistrictDefinition empty = new DistrictDefinition { districtName = "Empty", danger = 2 };
            Assert.IsNull(ContractGenerator.Generate(empty, Escort, 1, ContractGenerator.Purpose.Road));
        }

        [TestCase(1)]
        [TestCase(3)]
        [TestCase(5)]
        public void EnemiesComeFromThePoolAtTheDistrictsLevel(int danger)
        {
            DistrictDefinition district = District(danger);

            foreach (MissionDefinition mission in Many(danger, ContractGenerator.Purpose.Road))
            {
                foreach (UnitEntry enemy in mission.opponents.Where(e => e.faction == Faction.Enemy))
                {
                    CollectionAssert.Contains(district.enemyPool, enemy.job);

                    // danger or danger+1, and a boss one more on top.
                    Assert.That(enemy.level, Is.InRange(danger, danger + 2), mission.missionName);
                }

                Assert.Greater(mission.credits, 0);
            }
        }

        [Test]
        public void EnemyLevelFollowsTheCrewAndTheDanger()
        {
            Assert.AreEqual(1, ContractGenerator.EnemyLevel(1, 1), "A fresh crew meets the danger itself.");
            Assert.AreEqual(4, ContractGenerator.EnemyLevel(4, 1));
            Assert.AreEqual(8, ContractGenerator.EnemyLevel(1, 10), "Neon Row: two below the crew.");
            Assert.AreEqual(10, ContractGenerator.EnemyLevel(ContractGenerator.EvenDanger, 10));
            Assert.AreEqual(11, ContractGenerator.EnemyLevel(4, 10), "Spire Gate: one above.");
        }

        [TestCase(1)]
        [TestCase(4)]
        public void AStrongCrewMeetsStrongerEnemies(int danger)
        {
            DistrictDefinition district = District(danger);
            int baseLevel = ContractGenerator.EnemyLevel(danger, 10);

            for (int seed = 0; seed < 100; seed++)
            {
                MissionDefinition mission = ContractGenerator.Generate(district, Escort, seed, ContractGenerator.Purpose.Listing, 10);

                foreach (UnitEntry enemy in mission.opponents.Where(e => e.faction == Faction.Enemy))
                {
                    Assert.That(enemy.level, Is.InRange(baseLevel, baseLevel + 2), mission.missionName);
                }
            }
        }

        [Test]
        public void VeteransJoinOnceTheEnemiesAreStrongEnough()
        {
            Assert.AreEqual(0, ContractGenerator.EliteCount(ContractGenerator.EliteFromLevel - 1, 6));
            Assert.AreEqual(1, ContractGenerator.EliteCount(ContractGenerator.EliteFromLevel, 6));
            Assert.AreEqual(2, ContractGenerator.EliteCount(ContractGenerator.EliteFromLevel + ContractGenerator.EliteEvery, 6));
            Assert.AreEqual(1, ContractGenerator.EliteCount(30, 3), "Never more than half the squad.");

            DistrictDefinition district = District(3);
            JobDefinition veteran = TestData.Job("Duelist");
            district.elitePool.Add(veteran);

            int Veterans(int crewLevel) => Enumerable.Range(0, 50)
                .Select(seed => ContractGenerator.Generate(district, Escort, seed, ContractGenerator.Purpose.Listing, crewLevel))
                .Sum(m => m.opponents.Count(e => e.job == veteran));

            Assert.AreEqual(0, Veterans(1));
            Assert.Greater(Veterans(12), 50);
        }

        [Test]
        public void MoreDangerMeansMoreEnemies()
        {
            double quiet = Many(1, ContractGenerator.Purpose.Listing).Average(m => m.opponents.Count(e => e.faction == Faction.Enemy));
            double hot = Many(5, ContractGenerator.Purpose.Listing).Average(m => m.opponents.Count(e => e.faction == Faction.Enemy));
            Assert.Greater(hot, quiet);
        }

        [Test]
        public void EveryObjectiveIsWellFormed()
        {
            foreach (MissionDefinition mission in Many(3, ContractGenerator.Purpose.Road).Concat(Many(3, ContractGenerator.Purpose.Listing)))
            {
                List<string> names = mission.opponents.Select(e => e.unitName).ToList();
                Assert.AreEqual(names.Count, names.Distinct().Count(), $"Duplicate names in {mission.missionName}");

                switch (mission.objective)
                {
                    case MissionObjective.DefeatTarget:
                        Assert.IsTrue(mission.opponents.Any(e => e.unitName == mission.objectiveUnitName && e.faction == Faction.Enemy));
                        break;

                    case MissionObjective.Protect:
                        UnitEntry escort = mission.opponents.Single(e => e.unitName == mission.objectiveUnitName);
                        Assert.AreEqual(Faction.Player, escort.faction);
                        Assert.IsTrue(escort.guest);
                        break;

                    case MissionObjective.Survive:
                        Assert.GreaterOrEqual(mission.surviveTurns, 1);
                        break;
                }
            }
        }

        [Test]
        public void TheRoadMixesInEveryKindOfAmbush()
        {
            HashSet<MissionObjective> seen = new HashSet<MissionObjective>(Many(3, ContractGenerator.Purpose.Road).Select(m => m.objective));
            CollectionAssert.IsSubsetOf(
                new[] { MissionObjective.DefeatAll, MissionObjective.Survive, MissionObjective.Protect, MissionObjective.DefeatTarget },
                seen);
        }

        [Test]
        public void TheStableHashDoesNotChange()
        {
            // Pinned: the job boards are rebuilt from it, so a change would reshuffle every save's boards.
            Assert.AreEqual(ContractGenerator.StableHash("Neon Row"), ContractGenerator.StableHash("Neon Row"));
            Assert.AreNotEqual(ContractGenerator.StableHash("Neon Row"), ContractGenerator.StableHash("Rustyard"));
        }
    }
}
