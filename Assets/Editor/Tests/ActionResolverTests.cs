using NUnit.Framework;
using UnityEngine;
using Tactica.Combat;
using Tactica.Grid;
using Tactica.Stats;

namespace Tactica.Tests
{
    // The combat rules in ActionResolver: damage, hit chance by facing, Override chance, height
    // and range, and what may be used at all. Scene-free - the resolver reads the board only
    // through its UnitAt / HeightAt delegates.
    public class ActionResolverTests
    {
        private UnitFactory units;
        private ActionResolver resolver;

        [SetUp]
        public void SetUp()
        {
            units = new UnitFactory();
            resolver = new ActionResolver();
        }

        [TearDown]
        public void TearDown()
        {
            units.DestroyAll();
        }

        // --- Damage -------------------------------------------------------------------------

        [Test]
        public void AttackDamageIsPowerPlusStrengthPlusWeaponMinusDefense()
        {
            AbilityDefinition attack = TestData.Ability("Attack");
            GridUnit knight = units.Make("CyberKnight", 2, Faction.Player, new Vector2Int(0, 0), weaponAsset: "ShieldGauntlet_Mk1");
            GridUnit raider = units.Make("Raider", 1, Faction.Enemy, new Vector2Int(0, 1));

            int power = attack.Power + knight.EffectiveStats.Strength + knight.Weapon.Power;
            Assert.AreEqual(power, resolver.GetPower(knight, attack));
            Assert.AreEqual(Mathf.Max(1, power - raider.EffectiveStats.Defense, Mathf.CeilToInt(power / (float)ActionResolver.MinDamageDivisor)), resolver.CalculateDamage(knight, raider, attack));
        }

        [Test]
        public void DefenseSoaksAtMostThreeQuarters()
        {
            AbilityDefinition attack = TestData.Ability("Attack");
            GridUnit gooner = units.Make("Gooner", 1, Faction.Enemy, new Vector2Int(0, 0));
            GridUnit brute = units.Make("ChromeBrute", 40, Faction.Player, new Vector2Int(0, 1));
            int power = resolver.GetPower(gooner, attack);

            Assume.That(power <= brute.EffectiveStats.Defense, "Needs a defender whose Defense outweighs the attack.");
            Assert.AreEqual(Mathf.Max(1, Mathf.CeilToInt(power / (float)ActionResolver.MinDamageDivisor)),
                resolver.CalculateDamage(gooner, brute, attack));
        }

        [Test]
        public void TheDamageFloorIsAQuarterOfThePower()
        {
            Assert.AreEqual(1, ActionResolver.DamageAfterDefense(0, 10));
            Assert.AreEqual(1, ActionResolver.DamageAfterDefense(3, 10));
            Assert.AreEqual(5, ActionResolver.DamageAfterDefense(20, 30));
            Assert.AreEqual(5, ActionResolver.DamageAfterDefense(20, 15));
            Assert.AreEqual(12, ActionResolver.DamageAfterDefense(20, 8));
        }

        // --- Hit chance and facing -------------------------------------------------------------

        [Test]
        public void EvasionCountsFullFromTheFrontHalfFromTheSideNoneFromBehind()
        {
            AbilityDefinition attack = TestData.Ability("Attack");
            Vector2Int at = new Vector2Int(5, 5);
            GridUnit raider = units.Make("Raider", 1, Faction.Enemy, at, Facing.North);
            int evasion = raider.EffectiveStats.Evasion;
            Assume.That(evasion > 1, "Needs a target with some Evasion.");

            Vector2Int forward = FacingUtility.ToVector(Facing.North);
            Vector2Int sideways = new Vector2Int(forward.y, forward.x);

            GridUnit front = units.Make("Gooner", 1, Faction.Player, at + forward);
            GridUnit side = units.Make("Gooner", 1, Faction.Player, at + sideways);
            GridUnit back = units.Make("Gooner", 1, Faction.Player, at - forward);

            Assert.AreEqual(AttackSide.Front, resolver.GetAttackSide(front, raider));
            Assert.AreEqual(AttackSide.Side, resolver.GetAttackSide(side, raider));
            Assert.AreEqual(AttackSide.Back, resolver.GetAttackSide(back, raider));

            Assert.AreEqual(100 - evasion, resolver.GetHitChance(front, raider, attack));
            Assert.AreEqual(100 - evasion / 2, resolver.GetHitChance(side, raider, attack));
            Assert.AreEqual(100, resolver.GetHitChance(back, raider, attack));
        }

        [Test]
        public void TheDefensiveStanceDoublesEvasionFromTheFront()
        {
            AbilityDefinition attack = TestData.Ability("Attack");
            Vector2Int at = new Vector2Int(5, 5);
            GridUnit raider = units.Make("Raider", 1, Faction.Enemy, at, Facing.North);
            GridUnit front = units.Make("Gooner", 1, Faction.Player, at + FacingUtility.ToVector(Facing.North));
            int evasion = raider.EffectiveStats.Evasion;

            raider.BeginDefending();

            Assert.AreEqual(Mathf.Clamp(100 - evasion * 2, 0, 100), resolver.GetHitChance(front, raider, attack));
        }

        [Test]
        public void OverrideIgnoresEvasion()
        {
            AbilityDefinition hack = TestData.Ability("SystemOverride");
            GridUnit knight = units.Make("CyberKnight", 2, Faction.Player, new Vector2Int(0, 0));
            GridUnit raider = units.Make("Raider", 1, Faction.Enemy, new Vector2Int(0, 1), Facing.South);

            Assert.AreEqual(100, resolver.GetHitChance(knight, raider, hack));
        }

        // --- Override -------------------------------------------------------------------------

        [Test]
        public void OverrideChanceIsPowerPlusThreeTech()
        {
            AbilityDefinition hack = TestData.Ability("SystemOverride");
            GridUnit knight = units.Make("CyberKnight", 2, Faction.Player, new Vector2Int(0, 0));
            GridUnit raider = units.Make("Raider", 1, Faction.Enemy, new Vector2Int(0, 1));

            int expected = hack.Power + ActionResolver.OverrideTechWeight * knight.EffectiveStats.Tech;
            Assert.AreEqual(Mathf.Clamp(expected, ActionResolver.MinOverrideChance, ActionResolver.MaxOverrideChance),
                resolver.GetOverrideChance(knight, raider, hack));
        }

        [Test]
        public void OverrideLosesFourPerLevelTheTargetHasOverTheHacker()
        {
            AbilityDefinition hack = TestData.Ability("SystemOverride");
            GridUnit knight = units.Make("CyberKnight", 2, Faction.Player, new Vector2Int(0, 0));
            GridUnit sameLevel = units.Make("Raider", 2, Faction.Enemy, new Vector2Int(0, 1));
            GridUnit higher = units.Make("Raider", 4, Faction.Enemy, new Vector2Int(1, 0));

            int baseChance = resolver.GetOverrideChance(knight, sameLevel, hack);
            Assume.That(baseChance - 2 * ActionResolver.OverrideLevelPenalty >= ActionResolver.MinOverrideChance);
            Assert.AreEqual(baseChance - 2 * ActionResolver.OverrideLevelPenalty, resolver.GetOverrideChance(knight, higher, hack));
        }

        [Test]
        public void HackResistanceLowersTheChanceDownToTheFloor()
        {
            AbilityDefinition hack = TestData.Ability("SystemOverride");
            GridUnit knight = units.Make("CyberKnight", 2, Faction.Player, new Vector2Int(0, 0));
            GridUnit boss = units.Make("Raider", 1, Faction.Enemy, new Vector2Int(0, 1));

            int open = resolver.GetOverrideChance(knight, boss, hack);
            boss.SetHackResistance(10);
            Assert.AreEqual(Mathf.Max(ActionResolver.MinOverrideChance, open - 10), resolver.GetOverrideChance(knight, boss, hack));

            boss.SetHackResistance(500);
            Assert.AreEqual(ActionResolver.MinOverrideChance, resolver.GetOverrideChance(knight, boss, hack));
        }

        [Test]
        public void OverrideChanceIsCappedAtTheCeiling()
        {
            AbilityDefinition hack = TestData.Ability("SystemOverride");
            GridUnit hacker = units.Make("Netrunner", 99, Faction.Player, new Vector2Int(0, 0));
            GridUnit raider = units.Make("Raider", 1, Faction.Enemy, new Vector2Int(0, 1));

            Assert.AreEqual(ActionResolver.MaxOverrideChance, resolver.GetOverrideChance(hacker, raider, hack));
        }

        // --- Range and height --------------------------------------------------------------------

        [Test]
        public void RangedReachGrowsFromHighGroundAndShrinksFromLowGround()
        {
            AbilityDefinition attack = TestData.Ability("Attack");
            GridUnit rifle = units.Make("Raider", 1, Faction.Enemy, new Vector2Int(0, 0), weaponAsset: "RailRifle_Mk1");
            int baseRange = resolver.GetBaseRange(rifle, attack);
            Assume.That(baseRange >= 2, "Needs a ranged weapon.");

            Vector2Int target = new Vector2Int(0, 3);
            int userHeight = 0;
            int targetHeight = 0;
            resolver.HeightAt = coords => coords == rifle.GridCoords ? userHeight : targetHeight;

            userHeight = 4;
            Assert.AreEqual(baseRange + 4 / ActionResolver.HeightPerRangeStep, resolver.GetRange(rifle, attack, target));

            userHeight = 0;
            targetHeight = 4;
            Assert.AreEqual(Mathf.Max(1, baseRange - 4 / ActionResolver.HeightPerRangeStep), resolver.GetRange(rifle, attack, target));
        }

        [Test]
        public void MeleeReachIgnoresHeight()
        {
            AbilityDefinition attack = TestData.Ability("Attack");
            GridUnit gooner = units.Make("Gooner", 1, Faction.Enemy, new Vector2Int(0, 0));
            int baseRange = resolver.GetBaseRange(gooner, attack);
            Assume.That(baseRange < 2, "Needs a melee attack.");

            resolver.HeightAt = coords => coords == gooner.GridCoords ? 6 : 0;
            Assert.AreEqual(baseRange, resolver.GetRange(gooner, attack, new Vector2Int(0, 1)));
        }

        [Test]
        public void RangeIsManhattanDistance()
        {
            AbilityDefinition attack = TestData.Ability("Attack");
            GridUnit rifle = units.Make("Raider", 1, Faction.Enemy, new Vector2Int(0, 0), weaponAsset: "RailRifle_Mk1");
            int range = resolver.GetBaseRange(rifle, attack);

            Assert.IsTrue(resolver.IsInRange(rifle, new Vector2Int(range, 0), attack));
            Assert.IsFalse(resolver.IsInRange(rifle, new Vector2Int(range, 1), attack));
        }

        // --- What may be used --------------------------------------------------------------------

        [Test]
        public void AnAbilityNeedsItsWeaponInHand()
        {
            AbilityDefinition cut = TestData.Ability("QuickCut");
            Assume.That(cut.RequiredWeapon != WeaponType.None);

            GridUnit unarmed = units.Make("Blade", 1, Faction.Player, new Vector2Int(0, 0));
            Assert.IsFalse(resolver.CanUse(unarmed, cut, out string reason));
            StringAssert.Contains(cut.RequiredWeapon.ToString(), reason);

            GridUnit armed = units.Make("Blade", 1, Faction.Player, new Vector2Int(1, 0), weaponAsset: "Monoblade_Mk1");
            Assert.IsTrue(resolver.CanUse(armed, cut, out _));
        }

        [Test]
        public void AnAbilityNeedsTheMP()
        {
            AbilityDefinition hack = TestData.Ability("SystemOverride");
            GridUnit knight = units.Make("CyberKnight", 2, Faction.Player, new Vector2Int(0, 0));

            Assert.IsTrue(resolver.CanUse(knight, hack, out _));

            knight.SpendMP(knight.CurrentStats.MP - hack.MPCost + 1);
            Assert.IsFalse(resolver.CanUse(knight, hack, out string reason));
            StringAssert.Contains("MP", reason);
        }

        [Test]
        public void AKnockedOutUnitIsOnlyAValidTargetForRevive()
        {
            AbilityDefinition attack = TestData.Ability("Attack");
            AbilityDefinition reboot = TestData.Ability("RebootProtocol");
            GridUnit knight = units.Make("CyberKnight", 2, Faction.Player, new Vector2Int(0, 0));
            GridUnit ally = units.Make("Netrunner", 1, Faction.Player, new Vector2Int(0, 1));
            GridUnit enemy = units.Make("Raider", 1, Faction.Enemy, new Vector2Int(1, 0));

            ally.ApplyDamage(999);
            enemy.ApplyDamage(999);

            Assert.IsTrue(resolver.IsValidTarget(knight, ally, reboot));
            Assert.IsFalse(resolver.IsValidTarget(knight, enemy, attack));
        }
    }
}
