using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using NUnit.Framework;
using UnityEngine;
using Tactica.Combat;
using Tactica.Grid;
using Tactica.Stats;

namespace Tactica.Tests
{
    // The CT clock in CombatManager: a fight hands out its first turn, and a charged ability goes
    // off on the clock. Written after a regression where every fight ended before its first turn.
    //
    // CombatManager's Awake (board lookups, event wiring) does not run in EditMode and would pick
    // up the open scene's GridManager anyway, so the tests wire the little they need by hand.
    public class CombatClockTests
    {
        private UnitFactory units;
        private AbilityFactory abilities;
        private GameObject managerObject;
        private CombatManager combat;
        private readonly List<GridUnit> board = new List<GridUnit>();

        [SetUp]
        public void SetUp()
        {
            units = new UnitFactory();
            abilities = new AbilityFactory();
            board.Clear();

            managerObject = new GameObject("Test CombatManager");
            managerObject.SetActive(false);
            combat = managerObject.AddComponent<CombatManager>();
            combat.Resolver.UnitAt = coords => board.FirstOrDefault(unit => unit.GridCoords == coords);

            // What Awake would subscribe, minus the scene's board.
            MethodInfo charge = typeof(CombatManager).GetMethod("HandleChargeStarted", BindingFlags.NonPublic | BindingFlags.Instance);
            combat.Resolver.OnChargeStarted += (user, center, ability) => charge.Invoke(combat, new object[] { user, center, ability });
        }

        [TearDown]
        public void TearDown()
        {
            units.DestroyAll();
            abilities.DestroyAll();
            Object.DestroyImmediate(managerObject);
        }

        private GridUnit Place(string job, Faction faction, Vector2Int at, Facing facing = Facing.North)
        {
            GridUnit unit = units.Make(job, 1, faction, at, facing);
            board.Add(unit);
            return unit;
        }

        [Test]
        public void StartingAFightHandsOutTheFirstTurn()
        {
            Place("CyberKnight", Faction.Player, new Vector2Int(0, 0));
            Place("Raider", Faction.Enemy, new Vector2Int(0, 5));

            combat.StartCombat(board);

            Assert.IsNotNull(combat.GetActiveUnit(), "Someone must hold the first turn.");
            Assert.AreEqual(CombatState.WaitingForInput, combat.CurrentState);
            Assert.AreEqual(1, combat.TurnNumber);
        }

        [Test]
        public void AChargedAbilityGoesOffOnTheClock()
        {
            AbilityDefinition beam = abilities.Make("Beam", AbilityEffectType.Damage, 6, 6, so =>
                so.FindProperty("chargeTime").intValue = 2);

            GridUnit target = Place("Raider", Faction.Enemy, new Vector2Int(0, 4), Facing.North);
            GridUnit gunner = Place("Railgunner", Faction.Player, new Vector2Int(0, 0));
            Place("Gooner", Faction.Enemy, new Vector2Int(9, 9));   // keeps the fight going after the target falls

            combat.StartCombat(board);
            int hp = target.CurrentStats.HP;

            Assert.IsTrue(combat.Resolver.TryExecuteAt(gunner, target.GridCoords, beam, out string message), message);
            Assert.AreEqual(1, combat.PendingCharges.Count);
            Assert.AreEqual(hp, target.CurrentStats.HP);

            // Turns pass; the clock ticks under them and the charge fires.
            for (int i = 0; i < 6 && combat.PendingCharges.Count > 0 && combat.CurrentState != CombatState.CombatEnd; i++)
            {
                combat.EndCurrentTurn();
            }

            Assert.AreEqual(0, combat.PendingCharges.Count, "The charge has gone off.");
            Assert.Less(target.CurrentStats.HP, hp);
        }

        [Test]
        public void AChargeFizzlesIfItsUserIsDown()
        {
            AbilityDefinition beam = abilities.Make("Beam", AbilityEffectType.Damage, 6, 6, so =>
                so.FindProperty("chargeTime").intValue = 2);

            GridUnit target = Place("Raider", Faction.Enemy, new Vector2Int(0, 4));
            GridUnit gunner = Place("Railgunner", Faction.Player, new Vector2Int(0, 0));
            Place("CyberKnight", Faction.Player, new Vector2Int(5, 0));

            combat.StartCombat(board);
            int hp = target.CurrentStats.HP;
            combat.Resolver.TryExecuteAt(gunner, target.GridCoords, beam, out _);
            gunner.ApplyDamage(999);

            for (int i = 0; i < 6 && combat.PendingCharges.Count > 0 && combat.CurrentState != CombatState.CombatEnd; i++)
            {
                combat.EndCurrentTurn();
            }

            Assert.AreEqual(0, combat.PendingCharges.Count);
            Assert.AreEqual(hp, target.CurrentStats.HP, "A downed user's charge never lands.");
        }
    }
}
