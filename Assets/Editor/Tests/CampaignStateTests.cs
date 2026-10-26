using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using UnityEngine;
using Tactica.Campaign;
using Tactica.Grid;
using Tactica.Stats;

namespace Tactica.Tests
{
    // The campaign rules in CampaignState: a new game, hiring, the shop, travel and road
    // encounters, mission results (JP sharing, XP, story rewards), the job rules, and the save
    // format with its migrations.
    //
    // Runs on the real CampaignConfig, so the city-map tests also check the authored content
    // (Neon Row → Rustyard via Velvet Room Raid). Nothing here touches the player's save: the
    // format is exercised through ToJson / FromJson.
    public class CampaignStateTests
    {
        private CampaignConfig config;
        private CampaignState state;

        [SetUp]
        public void SetUp()
        {
            config = TestData.Config;
            state = CampaignState.NewGame(config);
        }

        // --- New game ----------------------------------------------------------------------------

        [Test]
        public void ANewGameStartsWithTheConfiguredCrewMoneyAndDistrict()
        {
            Assert.AreEqual(config.StartingRoster.Count(entry => entry.job != null), state.Roster.Count);
            Assert.AreEqual(config.StartingCredits, state.Credits);
            Assert.AreEqual(config.StartingDistrict, state.CurrentDistrictName);
            Assert.IsTrue(state.IsDistrictOpen(config.StartingDistrict));
            Assert.LessOrEqual(state.DeployedCount, state.MaxDeployed);
        }

        [Test]
        public void NewCrewKnowTheirJobsCheapestActionAndHoldItsDefaultWeapon()
        {
            foreach (RosterUnit unit in state.Roster)
            {
                AbilityDefinition cheapest = unit.Job.AllAbilities()
                    .Where(a => a.Slot == AbilitySlot.Action).OrderBy(a => a.JPCost).First();
                Assert.IsTrue(unit.HasLearned(cheapest), $"{unit.Name} should know {cheapest.AbilityName}.");
                Assert.AreEqual(unit.Job.DefaultWeapon, unit.Weapon);
            }
        }

        // --- Hiring and the shop -----------------------------------------------------------------

        [Test]
        public void HiringCostsCreditsByLevel()
        {
            RosterUnit candidate = state.RecruitPool.First();
            int cost = candidate.Level * config.HireCostPerLevel;
            Assume.That(state.Credits >= cost);

            Assert.AreEqual(cost, state.HireCost(candidate));
            Assert.IsTrue(state.Recruit(candidate));
            Assert.AreEqual(config.StartingCredits - cost, state.Credits);
            Assert.Contains(candidate, state.Roster.ToList());
            Assert.IsFalse(state.RecruitPool.Contains(candidate));
        }

        [Test]
        public void BuyingSpendsThePriceAndSellingReturnsHalf()
        {
            EquipmentDefinition item = state.CurrentDistrict.shopStock.First();
            int start = state.Credits;
            int owned = state.Inventory.Count(i => i == item);

            Assert.IsTrue(state.Buy(item));
            Assert.AreEqual(start - item.Price, state.Credits);
            Assert.AreEqual(owned + 1, state.Inventory.Count(i => i == item));

            Assert.IsTrue(state.Sell(item));
            Assert.AreEqual(start - item.Price + item.Price / CampaignState.SellDivisor, state.Credits);
        }

        [Test]
        public void TheShopOnlySellsItsDistrictsStockAndOnlyWhatYouCanAfford()
        {
            EquipmentDefinition notHere = config.Districts
                .SelectMany(d => d.shopStock)
                .FirstOrDefault(item => item != null && !state.CurrentDistrict.shopStock.Contains(item));
            Assume.That(notHere != null, "Needs an item stocked only elsewhere.");
            Assert.IsFalse(state.CanBuy(notHere, out string elsewhere));
            StringAssert.Contains("Not sold here", elsewhere);

            EquipmentDefinition item = state.CurrentDistrict.shopStock.First();

            while (state.Credits >= item.Price)
            {
                state.Buy(item);
            }

            Assert.IsFalse(state.CanBuy(item, out string broke));
            StringAssert.Contains("cr needed", broke);
        }

        // --- Travel and road encounters -----------------------------------------------------------

        [Test]
        public void TravelNeedsARoadAndAnOpenDistrict()
        {
            DistrictDefinition neighbour = state.Connections(state.CurrentDistrict).First();
            DistrictDefinition farAway = config.Districts.First(d =>
                d != state.CurrentDistrict && !state.Connections(state.CurrentDistrict).Contains(d));

            Assert.IsFalse(state.TryTravel(farAway, 0.99f, out _, out string noRoad));
            StringAssert.Contains("No road", noRoad);

            Assume.That(!state.IsDistrictOpen(neighbour.districtName), "The neighbour should start locked.");
            Assert.IsFalse(state.TryTravel(neighbour, 0.99f, out _, out string locked));
            StringAssert.Contains("Locked", locked);
        }

        [Test]
        public void AmbushChanceGrowsWithDangerAndIsCapped()
        {
            DistrictDefinition district = state.CurrentDistrict;
            int danger = district.danger;

            Assert.AreEqual(Mathf.Min(CampaignState.MaxAmbushChance, danger * CampaignState.AmbushChancePerDanger),
                CampaignState.AmbushChance(district), 0.0001f);

            district.danger = 100;
            try
            {
                Assert.AreEqual(CampaignState.MaxAmbushChance, CampaignState.AmbushChance(district), 0.0001f);
            }
            finally
            {
                district.danger = danger;
            }
        }

        // Sets up an ambush: win the opening story, ride the safe first visit into the next
        // district, then head home - visited, no safe trips left - on a roll of 0.
        private MissionDefinition AmbushOnTheWayHome(out DistrictDefinition home, out DistrictDefinition away)
        {
            WinStory(state, "Velvet Room Raid");
            home = state.CurrentDistrict;
            DistrictDefinition next = state.Connections(home).First(d => state.IsDistrictOpen(d.districtName));
            away = next;

            Assert.IsTrue(state.TryTravel(next, 0f, out MissionDefinition first, out _));
            Assert.IsNull(first, "A first visit is never ambushed.");
            Assert.AreEqual(0, state.SafeTrips, "The trip used up the post-fight safe trip.");

            Assert.IsTrue(state.TryTravel(home, 0f, out MissionDefinition ambush, out _));
            Assert.IsNotNull(ambush, "Visited, no safe trips: roll 0 is an ambush.");
            Assert.AreEqual(home.districtName, state.TravelDestination);
            return ambush;
        }

        [Test]
        public void LosingAnAmbushLeavesTheCrewWhereItSetOut()
        {
            MissionDefinition ambush = AmbushOnTheWayHome(out _, out DistrictDefinition away);

            state.ApplyResult(new MissionResult { Mission = ambush, Outcome = MissionOutcome.Defeat });
            Assert.AreEqual(away.districtName, state.CurrentDistrictName);
            Assert.IsNull(state.TravelDestination);
        }

        [Test]
        public void WinningAnAmbushArrivesAndPaysHalf()
        {
            MissionDefinition ambush = AmbushOnTheWayHome(out DistrictDefinition home, out _);
            int credits = state.Credits;
            MissionResult win = new MissionResult { Mission = ambush, Outcome = MissionOutcome.Victory };
            state.ApplyResult(win);

            Assert.IsTrue(win.WasRoadEncounter);
            Assert.AreEqual(home.districtName, state.CurrentDistrictName);
            Assert.AreEqual(ambush.credits / 2, win.CreditsEarned);
            Assert.AreEqual(credits + ambush.credits / 2, state.Credits);
            Assert.AreEqual(0, win.LootGained.Count, "Road encounters pay no loot.");
        }

        [Test]
        public void AFightBuysOneSafeTrip()
        {
            WinStory(state, "Velvet Room Raid");
            Assert.AreEqual(CampaignState.SafeTripsAfterAFight, state.SafeTrips);

            DistrictDefinition home = state.CurrentDistrict;
            DistrictDefinition next = state.Connections(home).First(d => state.IsDistrictOpen(d.districtName));
            state.TryTravel(next, 0.999f, out _, out _);

            // Back home: visited, but a fresh fight would have bought another quiet trip.
            state.ApplyResult(new MissionResult { Mission = config.Missions[0], Outcome = MissionOutcome.Retreat });
            Assert.AreEqual(0f, state.AmbushChanceFor(home, out string why));
            StringAssert.Contains("lying low", why);
        }

        [Test]
        public void AClearedDistrictHalvesItsAmbushesAndOpensItsClearedStock()
        {
            DistrictDefinition home = config.FindDistrict(config.StartingDistrict);
            Assert.IsFalse(state.IsCleared(home));

            WinStory(state, "Velvet Room Raid");
            Assume.That(home.story.Count == 1, "Neon Row's story is the opening raid alone.");
            Assert.IsTrue(state.IsCleared(home));

            DistrictDefinition next = state.Connections(home).First(d => state.IsDistrictOpen(d.districtName));
            state.TryTravel(next, 0.999f, out _, out _);

            Assert.AreEqual(CampaignState.AmbushChance(home) * CampaignState.ClearedAmbushFactor,
                state.AmbushChanceFor(home, out string why), 0.0001f);
            Assert.AreEqual("cleared", why);

            foreach (EquipmentDefinition item in home.clearedStock.Where(i => i != null))
            {
                CollectionAssert.Contains(state.ShopStock(home), item);
            }
        }

        [Test]
        public void ASafeRoadIsNeverAmbushed()
        {
            WinStory(state, "Velvet Room Raid");
            DistrictDefinition home = state.CurrentDistrict;
            DistrictDefinition next = state.Connections(home).First(d => state.IsDistrictOpen(d.districtName));
            state.TryTravel(next, 0.999f, out _, out _);

            next.safeRoads.Add(home.districtName);

            try
            {
                Assert.AreEqual(0f, state.AmbushChanceFor(home, out string why));
                Assert.AreEqual("safe road", why);
                Assert.IsTrue(state.TryTravel(home, 0f, out MissionDefinition ambush, out _));
                Assert.IsNull(ambush);
            }
            finally
            {
                next.safeRoads.Remove(home.districtName);
            }
        }

        [Test]
        public void TheJobBoardChangesAfterEveryMission()
        {
            DistrictDefinition home = state.CurrentDistrict;
            Assume.That(home.enemyPool.Count > 0 && config.GeneratedListings > 0, "Needs generated contracts.");

            List<MissionDefinition> before = state.Listings(home);
            Assert.AreEqual(home.listings.Count + config.GeneratedListings, before.Count);
            Assert.AreEqual(before.Select(Signature), state.Listings(home).Select(Signature), "Stable between visits.");

            state.ApplyResult(new MissionResult { Mission = before[0], Outcome = MissionOutcome.Retreat });
            CollectionAssert.AreNotEqual(before.Select(Signature), state.Listings(home).Select(Signature));
        }

        [Test]
        public void GeneratedContractsFollowTheCrewsLevel()
        {
            DistrictDefinition home = state.CurrentDistrict;
            Assume.That(home.enemyPool.Count > 0 && config.GeneratedListings > 0, "Needs generated contracts.");
            int Lowest() => state.Listings(home).Skip(home.listings.Count)
                .SelectMany(m => m.opponents.Where(e => e.faction == Faction.Enemy)).Min(e => e.level);

            int before = Lowest();

            foreach (RosterUnit unit in state.Roster)
            {
                unit.Level = 12;
            }

            Assert.AreEqual(12, state.CrewLevel);
            Assert.AreEqual(ContractGenerator.EnemyLevel(home.danger, 12), Lowest());
            Assert.Greater(Lowest(), before);
        }

        [Test]
        public void OpponentsCarryWeaponsForTheirLevel()
        {
            EquipmentDefinition blade = TestData.Job("Raider").DefaultWeapon;
            Assume.That(blade.Tier == 1, "Raiders start with a Mk I.");

            Assert.AreEqual(blade, config.WeaponForLevel(blade, 3));
            Assert.AreEqual(2, config.WeaponForLevel(blade, 4).Tier);
            Assert.AreEqual(3, config.WeaponForLevel(blade, 9).Tier);
            Assert.AreEqual(blade.Type, config.WeaponForLevel(blade, 9).Type);

            EquipmentDefinition gauntlet = TestData.Job("ChromeBrute").DefaultWeapon;
            Assert.AreEqual(gauntlet, config.WeaponForLevel(gauntlet, 1), "Never downgraded.");
        }

        private static string Signature(MissionDefinition mission)
        {
            return $"{mission.missionName}|{mission.map.seed}|{string.Join(",", mission.opponents.Select(e => e.unitName))}";
        }

        [Test]
        public void AHighRollArrivesWithoutAFight()
        {
            WinStory(state, "Velvet Room Raid");
            DistrictDefinition next = state.Connections(state.CurrentDistrict).First(d => state.IsDistrictOpen(d.districtName));

            Assert.IsTrue(state.TryTravel(next, 0.999f, out MissionDefinition ambush, out _));
            Assert.IsNull(ambush);
            Assert.AreEqual(next.districtName, state.CurrentDistrictName);
        }

        // --- Mission results -------------------------------------------------------------------------

        [Test]
        public void JPGoesToThePrimaryJobAndAQuarterIsSharedWithTheRestOfTheCrew()
        {
            RosterUnit a = state.Roster[0];
            RosterUnit b = state.Roster[1];
            int aBefore = a.ProgressFor(a.Job).TotalJP;
            int bBefore = b.ProgressFor(b.Job).TotalJP;

            MissionResult result = new MissionResult { Mission = config.Missions[0], Outcome = MissionOutcome.Retreat };
            result.Units.Add(CrewReport(a, jp: 40));
            result.Units.Add(CrewReport(b, jp: 20));
            state.ApplyResult(result);

            Assert.AreEqual(aBefore + 40 + 20 / Progression.JPShareDivisor, a.ProgressFor(a.Job).TotalJP);
            Assert.AreEqual(bBefore + 20 + 40 / Progression.JPShareDivisor, b.ProgressFor(b.Job).TotalJP);
        }

        [Test]
        public void XPRaisesTheCharacterLevelEveryHundred()
        {
            RosterUnit unit = state.Roster[0];
            int level = unit.Level;

            MissionResult result = new MissionResult { Mission = config.Missions[0], Outcome = MissionOutcome.Retreat };
            result.Units.Add(CrewReport(unit, xp: Progression.XPPerLevel + 20));
            state.ApplyResult(result);

            Assert.AreEqual(level + 1, unit.Level);
            Assert.AreEqual(20, unit.XP);
        }

        [Test]
        public void AFlatlinedCrewMemberIsGoneForGood()
        {
            RosterUnit unit = state.Roster[0];
            MissionUnitReport report = CrewReport(unit);
            report.Flatlined = true;

            MissionResult result = new MissionResult { Mission = config.Missions[0], Outcome = MissionOutcome.Defeat };
            result.Units.Add(report);
            state.ApplyResult(result);

            Assert.IsFalse(state.Roster.Contains(unit));
        }

        [Test]
        public void TheOpeningStoryPaysOpensRustyardAndYukiJoinsEvenIfSheFlatlined()
        {
            MissionDefinition raid = config.FindMission("Velvet Room Raid");
            Assume.That(raid != null && raid.kind == MissionKind.Story);
            int credits = state.Credits;

            MissionResult result = WinStory(state, raid.missionName, guestFlatlined: true);

            Assert.AreEqual(raid.credits, result.CreditsEarned);
            Assert.AreEqual(credits + raid.credits, state.Credits);
            CollectionAssert.AreEquivalent(raid.loot, result.LootGained);
            Assert.AreEqual(raid.unlocksDistrict, result.DistrictUnlocked);
            Assert.IsTrue(state.IsDistrictOpen(raid.unlocksDistrict));
            CollectionAssert.AreEquivalent(raid.joinsCrew, result.JoinedCrew);
            Assert.IsTrue(state.Roster.Any(unit => raid.joinsCrew.Contains(unit.Name)));
            Assert.AreNotEqual(raid, state.NextStoryMission(config.FindDistrict(config.StartingDistrict)),
                "A story mission plays once.");
        }

        [Test]
        public void OnlyUnitsThatEndedOnYourSideCanBeRecruitedFromAFight()
        {
            MissionUnitReport hacked = new MissionUnitReport
            {
                Name = "Turned Raider", Job = TestData.Job("Raider"), Level = 1,
                FinalFaction = Faction.Player, Hacked = true,
            };
            MissionUnitReport enemy = new MissionUnitReport
            {
                Name = "Raider", Job = TestData.Job("Raider"), Level = 1, FinalFaction = Faction.Enemy,
            };
            MissionResult result = new MissionResult { Mission = config.Missions[0], Outcome = MissionOutcome.Victory };

            Assert.IsTrue(CampaignState.CanRecruitAfter(result, hacked));
            Assert.IsFalse(CampaignState.CanRecruitAfter(result, enemy));

            RosterUnit joined = state.RecruitFromMission(hacked);
            Assert.IsNotNull(joined);
            Assert.IsTrue(joined.Locked, "Hacked recruits keep their job and fixed kit.");
        }

        // --- Jobs and learning ---------------------------------------------------------------------------

        [Test]
        public void AMidTierJobOpensAtItsPrerequisiteLevel()
        {
            JobDefinition warden = TestData.Job("Warden");
            JobRequirement requirement = warden.Prerequisites.First();
            RosterUnit unit = RosterUnit.CreateFresh("Tester", requirement.job, 1);

            Assert.IsFalse(CampaignState.IsJobUnlocked(unit, warden));
            Assert.IsFalse(CampaignState.CanSetPrimary(unit, warden, out string reason));
            StringAssert.Contains("Needs", reason);

            unit.GrantJP(requirement.job, Progression.JobLevelThresholds[requirement.level]);

            Assert.IsTrue(CampaignState.IsJobUnlocked(unit, warden));
            Assert.IsTrue(CampaignState.SetPrimaryJob(unit, warden));
            Assert.AreEqual(warden, unit.Job);
        }

        [Test]
        public void LearningSpendsTheTeachingJobsJPOnce()
        {
            RosterUnit unit = RosterUnit.CreateFresh("Tester", TestData.Job("CyberKnight"), 1);
            AbilityDefinition ability = unit.Job.AllAbilities().First(a => !unit.HasLearned(a));

            Assert.IsFalse(CampaignState.CanLearn(unit, unit.Job, ability, out string poor));
            StringAssert.Contains("JP needed", poor);

            unit.GrantJP(unit.Job, ability.JPCost + 10);
            Assert.IsTrue(CampaignState.Learn(unit, unit.Job, ability));
            Assert.IsTrue(unit.HasLearned(ability));
            Assert.AreEqual(10, unit.ProgressFor(unit.Job).AvailableJP);
            Assert.AreEqual(ability.JPCost + 10, unit.ProgressFor(unit.Job).TotalJP, "Spending never lowers the job level.");

            Assert.IsFalse(CampaignState.Learn(unit, unit.Job, ability));
        }

        [Test]
        public void ALockedUnitCannotChangeJobOrLearn()
        {
            RosterUnit unit = new RosterUnit("Hacked", TestData.Job("Raider"), 1) { Locked = true };
            AbilityDefinition ability = TestData.Job("CyberKnight").AllAbilities().First();

            Assert.IsFalse(CampaignState.CanSetPrimary(unit, TestData.Job("CyberKnight"), out _));
            Assert.IsFalse(CampaignState.CanLearn(unit, TestData.Job("CyberKnight"), ability, out _));
        }

        [Test]
        public void JPEarnedWhileLockedIsIgnored()
        {
            RosterUnit unit = new RosterUnit("Hacked", TestData.Job("Raider"), 1) { Locked = true };
            unit.GrantJP(unit.Job, 500);
            Assert.AreEqual(0, unit.ProgressFor(unit.Job).TotalJP);
        }

        // --- Save format ------------------------------------------------------------------------------

        [Test]
        public void ASaveRoundTripsTheCrewAndTheCity()
        {
            WinStory(state, "Velvet Room Raid");
            RosterUnit unit = state.Roster[0];
            unit.GrantJP(unit.Job, 123);
            unit.XP = 42;

            CampaignState loaded = CampaignState.FromJson(config, state.ToJson());

            Assert.IsNotNull(loaded);
            Assert.AreEqual(state.Credits, loaded.Credits);
            Assert.AreEqual(state.CurrentDistrictName, loaded.CurrentDistrictName);
            Assert.AreEqual(state.MissionsWon, loaded.MissionsWon);
            Assert.AreEqual(state.BoardVersion, loaded.BoardVersion);
            Assert.AreEqual(state.SafeTrips, loaded.SafeTrips);
            Assert.IsTrue(loaded.HasVisited(state.CurrentDistrictName));
            CollectionAssert.AreEqual(state.Roster.Select(u => u.Name), loaded.Roster.Select(u => u.Name));
            CollectionAssert.AreEquivalent(state.Inventory, loaded.Inventory);

            RosterUnit copy = loaded.Roster[0];
            Assert.AreEqual(unit.Job, copy.Job);
            Assert.AreEqual(unit.Level, copy.Level);
            Assert.AreEqual(42, copy.XP);
            Assert.AreEqual(unit.Weapon, copy.Weapon);
            Assert.AreEqual(unit.ProgressFor(unit.Job).TotalJP, copy.ProgressFor(copy.Job).TotalJP);
            CollectionAssert.AreEquivalent(unit.Learned, copy.Learned);

            MissionDefinition raid = config.FindMission("Velvet Room Raid");
            Assert.IsTrue(loaded.IsDistrictOpen(raid.unlocksDistrict));
            Assert.AreNotEqual(raid, loaded.NextStoryMission(config.FindDistrict(config.StartingDistrict)),
                "Finished story stays finished after a reload.");
        }

        [Test]
        public void AVersionTwoSaveLandsInTheStartingDistrictWithStartingCredits()
        {
            string job = state.Roster[0].Job.name;
            string v2 = "{\"version\":2,\"missionsWon\":1,\"inventory\":[],\"recruitPool\":[]," +
                        "\"roster\":[{\"name\":\"Old\",\"job\":\"" + job + "\",\"level\":3,\"deployed\":true,\"xp\":7}]}";

            CampaignState loaded = CampaignState.FromJson(config, v2);

            Assert.IsNotNull(loaded);
            Assert.AreEqual(config.StartingDistrict, loaded.CurrentDistrictName);
            Assert.AreEqual(config.StartingCredits, loaded.Credits);
            Assert.AreEqual(1, loaded.MissionsWon);
            Assert.IsTrue(loaded.HasVisited(config.StartingDistrict), "An old save has been where it is.");
            Assert.AreEqual("Old", loaded.Roster[0].Name);
            Assert.AreEqual(3, loaded.Roster[0].Level);
            Assert.AreEqual(7, loaded.Roster[0].XP);
        }

        [Test]
        public void AVersionOneUnitBecomesAFreshUnitWithItsStarterAbility()
        {
            JobDefinition job = state.Roster[0].Job;
            string v1 = "{\"missionsWon\":0,\"roster\":[{\"name\":\"Veteran\",\"job\":\"" + job.name + "\",\"level\":2,\"deployed\":true}]}";

            CampaignState loaded = CampaignState.FromJson(config, v1);
            RosterUnit unit = loaded.Roster.Single();

            Assert.AreEqual("Veteran", unit.Name);
            Assert.AreEqual(2, unit.Level);
            Assert.IsTrue(unit.Learned.Any(), "A migrated unit gets its starter ability.");
            Assert.IsTrue(loaded.Inventory.Count > 0, "A v1 save gets the starting inventory.");
        }

        [Test]
        public void UnreadableSaveDataLoadsAsNothing()
        {
            UnityEngine.TestTools.LogAssert.ignoreFailingMessages = true;
            Assert.IsNull(CampaignState.FromJson(config, "this is not json"));
            UnityEngine.TestTools.LogAssert.ignoreFailingMessages = false;
        }

        // --- Helpers -------------------------------------------------------------------------------------

        private static MissionUnitReport CrewReport(RosterUnit unit, int jp = 0, int xp = 0)
        {
            return new MissionUnitReport
            {
                Name = unit.Name,
                Job = unit.Job,
                Level = unit.Level,
                FinalFaction = Faction.Player,
                RosterSource = unit,
                EarnedJP = jp,
                EarnedXP = xp,
            };
        }

        // A victory in a story mission, with its guests on the player's side at the end.
        private MissionResult WinStory(CampaignState campaign, string missionName, bool guestFlatlined = false)
        {
            MissionDefinition mission = config.FindMission(missionName);
            Assert.IsNotNull(mission, $"No mission '{missionName}'.");

            MissionResult result = new MissionResult { Mission = mission, Outcome = MissionOutcome.Victory };

            foreach (UnitEntry entry in mission.opponents.Where(e => e.faction == Faction.Player))
            {
                result.Units.Add(new MissionUnitReport
                {
                    Name = entry.unitName,
                    Job = entry.job,
                    Level = entry.level,
                    FinalFaction = Faction.Player,
                    KnockedOut = guestFlatlined,
                    Flatlined = guestFlatlined,
                });
            }

            campaign.ApplyResult(result);
            return result;
        }
    }
}
