using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using Tactica.Campaign;
using Tactica.Grid;
using Tactica.Stats;

namespace Tactica.Tests
{
    // The job content against GAME_DESIGN.md: all eighteen jobs, their tiers, the unlock table,
    // the JP cost table, and the weapon rules every kit has to respect.
    public class JobCatalogTests
    {
        // GAME_DESIGN.md "Sınıf ve ilerleme" and "Sınıf kilitleri", by asset name.
        private static readonly Dictionary<string, (JobTier Tier, (string Job, int Level)[] Needs)> Design =
            new Dictionary<string, (JobTier, (string, int)[])>
            {
                ["CyberKnight"] = (JobTier.Start, new (string, int)[0]),
                ["Blade"] = (JobTier.Start, new (string, int)[0]),
                ["Deadeye"] = (JobTier.Start, new (string, int)[0]),
                ["Netrunner"] = (JobTier.Start, new (string, int)[0]),
                ["FieldMedic"] = (JobTier.Start, new (string, int)[0]),
                ["Warden"] = (JobTier.Mid, new[] { ("CyberKnight", 3) }),
                ["Phantom"] = (JobTier.Mid, new[] { ("Blade", 3) }),
                ["Sniper"] = (JobTier.Mid, new[] { ("Deadeye", 3) }),
                ["Daemon"] = (JobTier.Mid, new[] { ("Netrunner", 3) }),
                ["Surgeon"] = (JobTier.Mid, new[] { ("FieldMedic", 3) }),
                ["Duelist"] = (JobTier.Mid, new[] { ("Blade", 2), ("CyberKnight", 2) }),
                ["Breacher"] = (JobTier.Mid, new[] { ("CyberKnight", 2), ("Deadeye", 2) }),
                ["Juggernaut"] = (JobTier.Top, new[] { ("Warden", 4), ("Breacher", 3) }),
                ["Reaper"] = (JobTier.Top, new[] { ("Phantom", 4), ("Duelist", 3) }),
                ["Railgunner"] = (JobTier.Top, new[] { ("Sniper", 4), ("Breacher", 3) }),
                ["CyberSaint"] = (JobTier.Top, new[] { ("Surgeon", 4), ("Warden", 3) }),
                ["Overmind"] = (JobTier.Top, new[] { ("Daemon", 4), ("Sniper", 3), ("Surgeon", 2) }),
                ["Warlord"] = (JobTier.Top, new[] { ("Duelist", 4), ("Warden", 3), ("Phantom", 2) }),
            };

        // GAME_DESIGN.md "Yetenek maliyetleri": actives, passives, and Equip at 400 everywhere.
        private static readonly Dictionary<JobTier, (int[] Actives, int[] Passives)> Costs =
            new Dictionary<JobTier, (int[], int[])>
            {
                [JobTier.Start] = (new[] { 100, 200, 300 }, new[] { 150, 250 }),
                [JobTier.Mid] = (new[] { 200, 300, 400 }, new[] { 250, 350 }),
                [JobTier.Top] = (new[] { 300, 450, 600 }, new[] { 400, 550 }),
            };

        private const int EquipCost = 400;

        private static IEnumerable<JobDefinition> PlayerJobs => TestData.Config.PlayerJobs();

        [Test]
        public void AllEighteenJobsArePlayable()
        {
            CollectionAssert.AreEquivalent(Design.Keys, PlayerJobs.Select(job => job.name));
        }

        [Test]
        public void TiersAndUnlocksMatchTheDesign()
        {
            foreach (JobDefinition job in PlayerJobs)
            {
                (JobTier tier, (string Job, int Level)[] needs) = Design[job.name];
                Assert.AreEqual(tier, job.Tier, job.name);
                CollectionAssert.AreEquivalent(
                    needs.Select(n => $"{n.Job} {n.Level}"),
                    job.Prerequisites.Select(r => $"{r.job.name} {r.level}"),
                    job.name);
            }
        }

        [Test]
        public void AbilityCostsFollowTheTierTable()
        {
            foreach (JobDefinition job in PlayerJobs)
            {
                (int[] actives, int[] passives) = Costs[job.Tier];

                foreach (AbilityDefinition ability in job.AllAbilities())
                {
                    if (ability.EquipWeapon != WeaponType.None)
                    {
                        Assert.AreEqual(EquipCost, ability.JPCost, $"{job.name}: {ability.AbilityName}");
                    }
                    else if (ability.Slot == AbilitySlot.Action)
                    {
                        CollectionAssert.Contains(actives, ability.JPCost, $"{job.name}: {ability.AbilityName}");
                    }
                    else
                    {
                        CollectionAssert.Contains(passives, ability.JPCost, $"{job.name}: {ability.AbilityName}");
                    }
                }
            }
        }

        [Test]
        public void EveryJobHasActionsANameAndDescriptions()
        {
            foreach (JobDefinition job in PlayerJobs)
            {
                Assert.IsFalse(string.IsNullOrEmpty(job.JobName), job.name);
                Assert.IsTrue(job.AllAbilities().Any(a => a.Slot == AbilitySlot.Action), $"{job.name} has no Action ability.");

                foreach (AbilityDefinition ability in job.AllAbilities())
                {
                    Assert.IsFalse(string.IsNullOrEmpty(ability.AbilityName), $"{job.name}: unnamed ability {ability.name}");
                    Assert.IsFalse(string.IsNullOrEmpty(ability.Description), $"{job.name}: {ability.AbilityName} has no description");
                    Assert.IsTrue(ability.HasAnyTarget || ability.Slot != AbilitySlot.Action, $"{job.name}: {ability.AbilityName} targets nothing");
                }
            }
        }

        [Test]
        public void JobsHoldTheirDefaultWeaponAndTheirWeaponAbilitiesFit()
        {
            foreach (JobDefinition job in PlayerJobs)
            {
                Assert.IsNotNull(job.DefaultWeapon, $"{job.name} has no default weapon.");
                Assert.IsTrue(job.AllowsWeapon(job.DefaultWeapon.Type), $"{job.name} cannot hold its own default weapon.");

                foreach (AbilityDefinition ability in job.AllAbilities().Where(a => a.RequiredWeapon != WeaponType.None))
                {
                    Assert.IsTrue(job.AllowsWeapon(ability.RequiredWeapon),
                        $"{job.name}: {ability.AbilityName} needs a {ability.RequiredWeapon} the job cannot hold.");
                }
            }
        }

        [Test]
        public void EveryWeaponTypeHasAnEquipAbility()
        {
            HashSet<WeaponType> equips = new HashSet<WeaponType>(PlayerJobs
                .SelectMany(job => job.AllAbilities())
                .Where(a => a.EquipWeapon != WeaponType.None)
                .Select(a => a.EquipWeapon));

            foreach (WeaponType type in System.Enum.GetValues(typeof(WeaponType)).Cast<WeaponType>().Where(t => t != WeaponType.None))
            {
                Assert.IsTrue(equips.Contains(type), $"No Equip ability for {type}.");
            }
        }

        [Test]
        public void CountersReachAtLeastOneTile()
        {
            foreach (AbilityDefinition ability in PlayerJobs.SelectMany(job => job.AllAbilities())
                         .Where(a => a.ReactionKind == ReactionKind.Counter))
            {
                Assert.GreaterOrEqual(ability.Range, 1, ability.AbilityName);
            }
        }

        [Test]
        public void TwoTopTierJobsCannotBeCombined()
        {
            JobDefinition reaper = TestData.Job("Reaper");
            JobDefinition warlord = TestData.Job("Warlord");
            RosterUnit unit = RosterUnit.CreateFresh("Veteran", TestData.Job("CyberKnight"), 20);

            foreach (JobDefinition job in PlayerJobs)
            {
                unit.GrantJP(job, Progression.JobLevelThresholds[Progression.MaxJobLevel]);
            }

            Assume.That(CampaignState.IsJobUnlocked(unit, reaper) && CampaignState.IsJobUnlocked(unit, warlord));

            Assert.IsTrue(CampaignState.SetPrimaryJob(unit, reaper));
            Assert.IsFalse(CampaignState.CanSetSecondary(unit, warlord, out string reason));
            StringAssert.Contains("top-tier", reason);
            Assert.IsTrue(CampaignState.CanSetSecondary(unit, TestData.Job("Duelist"), out _), "A mid-tier secondary is fine.");
        }

        [Test]
        public void HiringHallsOfferOnlyStartJobs()
        {
            CampaignConfig config = TestData.Config;

            foreach (JobDefinition job in config.RecruitableJobs.Concat(config.Districts.SelectMany(d => d.hireJobs)))
            {
                Assert.AreEqual(JobTier.Start, job.Tier, job.name);
            }
        }

        [Test]
        public void YukiJoinsTheOpeningRaidAsADeadeye()
        {
            MissionDefinition raid = TestData.Config.FindMission("Velvet Room Raid");
            UnitEntry yuki = raid.opponents.Single(entry => entry.unitName == "Yuki");

            Assert.AreEqual(TestData.Job("Deadeye"), yuki.job);
            Assert.AreEqual(Faction.Player, yuki.faction);
            Assert.IsTrue(yuki.guest);
            CollectionAssert.Contains(raid.joinsCrew, "Yuki");
        }
    }
}
