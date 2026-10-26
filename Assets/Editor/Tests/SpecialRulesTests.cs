using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using UnityEngine;
using Tactica.Combat;
using Tactica.Grid;
using Tactica.Stats;

namespace Tactica.Tests
{
    // The special combat rules: damage that also inflicts a status, Override's wounded bonus and
    // its reduced strength against bosses, area hacks and Friendly Fuse sparing the user's side,
    // charged abilities, and Overwatch. Abilities are built in memory so the rules are pinned
    // independently of the authored kits.
    public class SpecialRulesTests
    {
        private UnitFactory units;
        private AbilityFactory abilities;
        private ActionResolver resolver;
        private readonly List<GridUnit> board = new List<GridUnit>();

        [SetUp]
        public void SetUp()
        {
            units = new UnitFactory();
            abilities = new AbilityFactory();
            resolver = new ActionResolver();
            board.Clear();
            resolver.UnitAt = coords => board.FirstOrDefault(unit => unit.GridCoords == coords);
        }

        [TearDown]
        public void TearDown()
        {
            units.DestroyAll();
            abilities.DestroyAll();
        }

        private GridUnit Place(string job, int level, Faction faction, Vector2Int at, Facing facing = Facing.North)
        {
            GridUnit unit = units.Make(job, level, faction, at, facing);
            board.Add(unit);
            return unit;
        }

        // A position directly behind `target`, so evasion never decides the roll.
        private static Vector2Int Behind(GridUnit target)
        {
            return target.GridCoords - FacingUtility.ToVector(target.CurrentFacing);
        }

        // --- Damage with a status rider ----------------------------------------------------------

        [Test]
        public void ADamageRiderAppliesItsStatusWhenTheHitLands()
        {
            AbilityDefinition marked = abilities.Make("Marked", AbilityEffectType.Damage, 1, 1, so =>
            {
                so.FindProperty("inflictsStatus").boolValue = true;
                so.FindProperty("duration").intValue = 3;
                so.FindProperty("statModifier").FindPropertyRelative("Defense").intValue = -2;
            });

            GridUnit target = Place("Raider", 1, Faction.Enemy, new Vector2Int(5, 5));
            GridUnit shooter = Place("Deadeye", 1, Faction.Player, Behind(target));
            int defense = target.EffectiveStats.Defense;

            Assert.IsTrue(resolver.TryExecuteAbility(shooter, target, marked, out string message), message);
            Assert.IsTrue(target.HasStatusFrom(marked));
            Assert.AreEqual(defense - 2, target.EffectiveStats.Defense);
        }

        [Test]
        public void APlainDamageAbilityAppliesNoStatus()
        {
            AbilityDefinition plain = abilities.Make("Plain", AbilityEffectType.Damage, 1, 1, so =>
                so.FindProperty("statModifier").FindPropertyRelative("Defense").intValue = -2);

            GridUnit target = Place("Raider", 1, Faction.Enemy, new Vector2Int(5, 5));
            GridUnit shooter = Place("Deadeye", 1, Faction.Player, Behind(target));

            resolver.TryExecuteAbility(shooter, target, plain, out _);
            Assert.IsFalse(target.HasStatusFrom(plain));
        }

        // --- Override ------------------------------------------------------------------------------

        [Test]
        public void HijackGainsItsBonusAgainstAWoundedTarget()
        {
            AbilityDefinition hijack = abilities.Make("Hijack", AbilityEffectType.Override, 0, 3, so =>
                so.FindProperty("woundedBonus").intValue = 20);

            GridUnit hacker = Place("Netrunner", 1, Faction.Player, new Vector2Int(0, 0));
            GridUnit target = Place("Raider", 1, Faction.Enemy, new Vector2Int(0, 2));

            int healthy = resolver.GetOverrideChance(hacker, target, hijack);
            Assume.That(healthy + 20 <= ActionResolver.MaxOverrideChance && healthy >= ActionResolver.MinOverrideChance);

            target.ApplyDamage(target.CurrentStats.HP - target.EffectiveStats.MaxHP / 2 + 1);
            Assert.Less(target.CurrentStats.HP * 2, target.EffectiveStats.MaxHP);
            Assert.AreEqual(healthy + 20, resolver.GetOverrideChance(hacker, target, hijack));
        }

        [Test]
        public void BossChancePercentScalesTheChanceAgainstBossesOnly()
        {
            AbilityDefinition mass = abilities.Make("Mass", AbilityEffectType.Override, 40, 3, so =>
                so.FindProperty("bossChancePercent").intValue = 50);

            GridUnit hacker = Place("Netrunner", 1, Faction.Player, new Vector2Int(0, 0));
            GridUnit boss = Place("Raider", 1, Faction.Enemy, new Vector2Int(0, 2));

            int normal = resolver.GetOverrideChance(hacker, boss, mass);
            boss.SetBoss(true);
            int raw = mass.Power + ActionResolver.OverrideTechWeight * hacker.EffectiveStats.Tech;

            Assert.AreEqual(Mathf.Clamp(raw / 2, ActionResolver.MinOverrideChance, ActionResolver.MaxOverrideChance),
                resolver.GetOverrideChance(hacker, boss, mass));
            Assert.Greater(normal, resolver.GetOverrideChance(hacker, boss, mass));
        }

        [Test]
        public void AnAreaHackPassesOverTheUsersOwnSide()
        {
            AbilityDefinition mass = abilities.Make("Mass", AbilityEffectType.Override, 5, 3, so =>
                so.FindProperty("areaOfEffect").intValue = 1);

            GridUnit hacker = Place("Netrunner", 1, Faction.Player, new Vector2Int(0, 0));
            GridUnit ally = Place("CyberKnight", 1, Faction.Player, new Vector2Int(3, 3));
            GridUnit enemy = Place("Raider", 1, Faction.Enemy, new Vector2Int(3, 4));

            List<GridUnit> hit = resolver.GetAffectedUnits(hacker, new Vector2Int(3, 3), mass);
            CollectionAssert.Contains(hit, enemy);
            CollectionAssert.DoesNotContain(hit, ally);
        }

        // --- Friendly Fuse -----------------------------------------------------------------------------

        [Test]
        public void FriendlyFuseKeepsTheUsersBlastsOffItsAllies()
        {
            AbilityDefinition blast = abilities.Make("Blast", AbilityEffectType.Damage, 3, 3, so =>
                so.FindProperty("areaOfEffect").intValue = 1);
            AbilityDefinition fuse = abilities.Make("Fuse", AbilityEffectType.Damage, 0, 1, so =>
            {
                so.FindProperty("slot").enumValueIndex = (int)AbilitySlot.Support;
                so.FindProperty("sparesAllies").boolValue = true;
            });

            GridUnit breacher = Place("CyberKnight", 1, Faction.Player, new Vector2Int(0, 0));
            GridUnit ally = Place("Blade", 1, Faction.Player, new Vector2Int(3, 3));
            GridUnit enemy = Place("Raider", 1, Faction.Enemy, new Vector2Int(3, 4));

            CollectionAssert.Contains(resolver.GetAffectedUnits(breacher, new Vector2Int(3, 3), blast), ally,
                "Without Friendly Fuse the blast hits friends.");

            breacher.SetPassives(null, fuse, null);
            List<GridUnit> hit = resolver.GetAffectedUnits(breacher, new Vector2Int(3, 3), blast);
            CollectionAssert.DoesNotContain(hit, ally);
            CollectionAssert.Contains(hit, enemy);
        }

        // --- Charged abilities -----------------------------------------------------------------------------

        [Test]
        public void AChargedAbilityOnlyStartsWhenChosenAndLandsWhenResolved()
        {
            AbilityDefinition beam = abilities.Make("Beam", AbilityEffectType.Damage, 6, 4, so =>
                so.FindProperty("chargeTime").intValue = 3);

            GridUnit target = Place("Raider", 1, Faction.Enemy, new Vector2Int(0, 3));
            GridUnit gunner = Place("Railgunner", 1, Faction.Player, Behind(target) - new Vector2Int(0, 1));
            int hp = target.CurrentStats.HP;

            bool started = false;
            resolver.OnChargeStarted += (user, center, ability) => started = user == gunner && center == target.GridCoords && ability == beam;

            Assert.IsTrue(resolver.TryExecuteAt(gunner, target.GridCoords, beam, out string message), message);
            Assert.IsTrue(started, "Choosing a charged ability announces the charge.");
            Assert.AreEqual(hp, target.CurrentStats.HP, "Nothing lands until it fires.");

            Assert.IsTrue(resolver.ResolveCharge(gunner, target.GridCoords, beam, out _));
            Assert.Less(target.CurrentStats.HP, hp);
        }

        [Test]
        public void AChargeHitsWhoeverStandsThereWhenItFires()
        {
            AbilityDefinition beam = abilities.Make("Beam", AbilityEffectType.Damage, 6, 4, so =>
                so.FindProperty("chargeTime").intValue = 3);

            GridUnit gunner = Place("Railgunner", 1, Faction.Player, new Vector2Int(0, 0));
            GridUnit target = Place("Raider", 1, Faction.Enemy, new Vector2Int(0, 3));
            Vector2Int aim = target.GridCoords;

            resolver.TryExecuteAt(gunner, aim, beam, out _);
            target.SetGridCoords(new Vector2Int(5, 5));

            Assert.IsFalse(resolver.ResolveCharge(gunner, aim, beam, out string message), "The target walked out.");
            StringAssert.Contains("hits nothing", message);
        }

        // --- Overwatch ---------------------------------------------------------------------------------------

        private AbilityDefinition OverwatchAbility()
        {
            return abilities.Make("Watch", AbilityEffectType.StatusEffect, 3, 0, so =>
            {
                so.FindProperty("statusKind").enumValueIndex = (int)StatusKind.Overwatch;
                so.FindProperty("duration").intValue = 1;
                so.FindProperty("usesWeapon").boolValue = true;
                so.FindProperty("targetsSelf").boolValue = true;
                so.FindProperty("targetsEnemies").boolValue = false;
            });
        }

        [Test]
        public void OverwatchFiresOnceOnAnEnemyThatStopsInReach()
        {
            AbilityDefinition watch = OverwatchAbility();
            GridUnit mover = Place("Raider", 1, Faction.Enemy, new Vector2Int(5, 5), Facing.North);
            GridUnit sniper = units.Make("Sniper", 1, Faction.Player, Behind(mover), weaponAsset: "RailRifle_Mk1");
            board.Add(sniper);
            sniper.AddStatus(watch, sniper, 1);
            int hp = mover.CurrentStats.HP;

            string shot = resolver.ResolveOverwatch(board, mover);

            StringAssert.Contains("OVERWATCH", shot);
            Assert.Less(mover.CurrentStats.HP, hp, "From behind it cannot miss.");
            Assert.IsNull(sniper.FindStatus(StatusKind.Overwatch), "The watch is spent.");
            Assert.IsNull(resolver.ResolveOverwatch(board, mover), "It fires once.");
        }

        [Test]
        public void OverwatchIgnoresAlliesAndAnyoneOutOfReach()
        {
            AbilityDefinition watch = OverwatchAbility();
            GridUnit sniper = units.Make("Sniper", 1, Faction.Player, new Vector2Int(0, 0), weaponAsset: "RailRifle_Mk1");
            board.Add(sniper);
            sniper.AddStatus(watch, sniper, 1);

            GridUnit friend = Place("Blade", 1, Faction.Player, new Vector2Int(0, 1));
            GridUnit far = Place("Raider", 1, Faction.Enemy, new Vector2Int(9, 9));

            Assert.IsNull(resolver.ResolveOverwatch(board, friend));
            Assert.IsNull(resolver.ResolveOverwatch(board, far));
            Assert.IsNotNull(sniper.FindStatus(StatusKind.Overwatch), "Still watching.");
        }
    }
}
