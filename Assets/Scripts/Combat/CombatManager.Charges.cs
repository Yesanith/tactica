using System.Collections.Generic;
using UnityEngine;
using Tactica.Grid;
using Tactica.Stats;

namespace Tactica.Combat
{
    // Charged abilities and Overwatch: things that happen between turns, on the clock or on a
    // move, rather than on a unit's own turn.
    public partial class CombatManager : MonoBehaviour
    {
        // A charged ability waiting to go off, counted down on the clock in ticks.
        public class PendingCharge
        {
            public GridUnit User;
            public Vector2Int Center;
            public AbilityDefinition Ability;
            public int TicksLeft;
            public List<Vector2Int> Tiles;
        }

        private readonly List<PendingCharge> pendingCharges = new List<PendingCharge>();

        public IReadOnlyList<PendingCharge> PendingCharges => pendingCharges;

        private void HandleChargeStarted(GridUnit user, Vector2Int center, AbilityDefinition ability)
        {
            List<Vector2Int> tiles = ActionResolver.GetAreaTiles(center, Resolver.GetArea(user, ability));

            pendingCharges.Add(new PendingCharge
            {
                User = user, Center = center, Ability = ability, TicksLeft = ability.ChargeTime, Tiles = tiles,
            });

            if (gridManagerRef != null)
            {
                gridManagerRef.AddChargeTiles(tiles);
            }
        }

        // Counts every charge down one tick and fires the ones that are due. A charge whose user
        // has been knocked out fizzles - FFT's rule, and the counterplay to a charged blast.
        // True if one of them ended the encounter.
        private bool TickCharges()
        {
            for (int i = 0; i < pendingCharges.Count; i++)
            {
                PendingCharge charge = pendingCharges[i];

                if (--charge.TicksLeft > 0)
                {
                    continue;
                }

                pendingCharges.RemoveAt(i--);

                if (gridManagerRef != null)
                {
                    gridManagerRef.RemoveChargeTiles(charge.Tiles);
                }

                if (charge.User == null || charge.User.IsKnockedOut || charge.User.IsFlatlined)
                {
                    Debug.Log($"{charge.Ability.AbilityName} fizzles - its user is down.", this);
                    continue;
                }

                Resolver.ResolveCharge(charge.User, charge.Center, charge.Ability, out string message);
                Debug.Log(message, charge.User);

                if (TryEndCombatNow())
                {
                    return true;
                }
            }

            return false;
        }

        private void ClearCharges()
        {
            pendingCharges.Clear();

            if (gridManagerRef != null)
            {
                gridManagerRef.ClearChargeTiles();
            }
        }

        // Overwatch fires on whoever just moved, mid-turn. The controllers check afterwards
        // whether their unit is still standing.
        private void HandleUnitMoved(GridUnit unit)
        {
            if (CurrentState == CombatState.CombatEnd)
            {
                return;
            }

            string message = Resolver.ResolveOverwatch(initiativeOrder, unit);

            if (message != null)
            {
                Debug.Log(message, unit);
                TryEndCombatNow();
            }
        }
    }
}
