using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using Tactica.Grid;
using Tactica.Stats;

namespace Tactica.Combat
{
    // Where to stand: tiles the unit can reach this turn, overwatch avoidance, falling back,
    // and the distance helpers the target search uses.
    public partial class EnemyAIController : MonoBehaviour
    {
        // Where it can stand now (first) and after moving, in path-cost order.
        private List<Vector2Int> StandingTiles(GridUnit aiUnit)
        {
            List<Vector2Int> moves = gridManagerRef.GetTilesInMoveRange(aiUnit.GridCoords, aiUnit.EffectiveStats.Move, aiUnit.EffectiveStats.Jump);
            List<Vector2Int> tiles = new List<Vector2Int> { aiUnit.GridCoords };
            tiles.AddRange(moves.Where(tile => !Watched(aiUnit, tile)));
            tiles.AddRange(moves.Where(tile => Watched(aiUnit, tile)));
            return tiles;
        }

        // Whether ending a move on `tile` would draw an enemy's Overwatch shot.
        private bool Watched(GridUnit aiUnit, Vector2Int tile)
        {
            ActionResolver resolver = combatManagerRef.Resolver;

            foreach (GridUnit watcher in combatManagerRef.TurnOrder)
            {
                if (watcher == null || watcher.IsKnockedOut || watcher.CurrentFaction == aiUnit.CurrentFaction)
                {
                    continue;
                }

                GridUnit.ActiveStatus watch = watcher.FindStatus(StatusKind.Overwatch);

                if (watch != null && GridManager.GetGridDistance(watcher.GridCoords, tile) <= resolver.GetBaseRange(watcher, watch.Ability))
                {
                    return true;
                }
            }

            return false;
        }

        // The standing tile farthest from every standing enemy, ties to the one nearest an ally -
        // out of reach, and back where the healers are. No decision if staying put is already best.
        private AITargetDecision FindRetreat(GridUnit aiUnit)
        {
            List<GridUnit> enemies = combatManagerRef.TurnOrder
                .Where(unit => unit != null && !unit.IsKnockedOut && !unit.IsFlatlined
                               && unit.CurrentFaction != aiUnit.CurrentFaction)
                .ToList();

            if (enemies.Count == 0)
            {
                return default;
            }

            List<GridUnit> allies = combatManagerRef.TurnOrder
                .Where(unit => unit != null && unit != aiUnit && !unit.IsKnockedOut && !unit.IsFlatlined
                               && unit.CurrentFaction == aiUnit.CurrentFaction)
                .ToList();

            int Threat(Vector2Int tile) => enemies.Min(enemy => GridManager.GetGridDistance(enemy.GridCoords, tile));
            int Company(Vector2Int tile) => allies.Count > 0 ? allies.Min(ally => GridManager.GetGridDistance(ally.GridCoords, tile)) : 0;

            Vector2Int best = aiUnit.GridCoords;

            foreach (Vector2Int tile in StandingTiles(aiUnit))
            {
                if (Watched(aiUnit, tile) && tile != aiUnit.GridCoords)
                {
                    continue;
                }

                int threat = Threat(tile);
                int bestThreat = Threat(best);

                if (threat > bestThreat || (threat == bestThreat && Company(tile) < Company(best)))
                {
                    best = tile;
                }
            }

            if (best == aiUnit.GridCoords)
            {
                return default;
            }

            GridUnit nearest = enemies.OrderBy(enemy => GridManager.GetGridDistance(enemy.GridCoords, aiUnit.GridCoords)).First();
            return new AITargetDecision(nearest, null, false, 0, 0, best, nearest.GridCoords, 0f, retreat: true);
        }

        // The cheapest tile from which the ability reaches `center`, if any.
        private Vector2Int? FirstStandingTileFor(GridUnit aiUnit, List<Vector2Int> standingTiles, Vector2Int center, AbilityDefinition ability)
        {
            foreach (Vector2Int tile in standingTiles)
            {
                if (combatManagerRef.Resolver.IsInRangeFrom(aiUnit, tile, center, ability))
                {
                    return tile;
                }
            }

            return null;
        }

        // The cheapest tile to attack from. standingTiles is ordered by path cost with the current
        // position first, so the first acceptable entry is the one requiring the fewest steps -
        // and staying put wins whenever it is already good enough.
        private static Vector2Int FirstTileInRangeOf(List<Vector2Int> standingTiles, Vector2Int target, int range)
        {
            for (int i = 0; i < standingTiles.Count; i++)
            {
                if (GridManager.GetGridDistance(standingTiles[i], target) <= range)
                {
                    return standingTiles[i];
                }
            }

            // Unreachable by construction - the caller only calls this for a target already known
            // to be in range from somewhere. Falling back to the current tile keeps it total.
            return standingTiles[0];
        }

        // The tile that gets closest to the target.
        //
        // Manhattan distance alone ties often: every tile on the same diagonal band around the
        // target is equidistant, so the pick would fall to whatever order the flood fill emitted -
        // deterministic, but it makes the unit drift sideways instead of walking at its target.
        //
        // Squared Euclidean breaks those ties the way a person would expect, and stays exact
        // integer arithmetic (no sqrt, no float comparison). It is only a tie-break: Manhattan
        // still decides which tile is genuinely closer, because that is the metric movement and
        // range are measured in.
        private static Vector2Int ClosestTileTo(List<Vector2Int> standingTiles, Vector2Int target, Dictionary<Vector2Int, int> walk)
        {
            Vector2Int best = standingTiles[0];
            int bestDistance = WalkOrStraight(best, target, walk);
            int bestSpread = SquaredDistance(best, target);

            for (int i = 1; i < standingTiles.Count; i++)
            {
                int distance = WalkOrStraight(standingTiles[i], target, walk);
                int spread = SquaredDistance(standingTiles[i], target);

                if (distance < bestDistance || (distance == bestDistance && spread < bestSpread))
                {
                    best = standingTiles[i];
                    bestDistance = distance;
                    bestSpread = spread;
                }
            }

            return best;
        }

        // Walking steps when the tile is connected to the target; otherwise a large penalty plus the
        // straight-line distance, so any connected tile beats any cut-off one.
        private static int WalkOrStraight(Vector2Int tile, Vector2Int target, Dictionary<Vector2Int, int> walk)
        {
            const int Unreachable = 10000;
            return walk.TryGetValue(tile, out int steps) ? steps : Unreachable + GridManager.GetGridDistance(tile, target);
        }

        private static int SquaredDistance(Vector2Int a, Vector2Int b)
        {
            int dx = a.x - b.x;
            int dy = a.y - b.y;

            return dx * dx + dy * dy;
        }

        // Fewest steps between the target and any tile the unit could attack from.
        //
        // O(reachable tiles x candidates), which at Move 2 on a 10x10 board is a few dozen integer
        // comparisons per turn. Worth revisiting only if move ranges get large enough that this
        // shows up in a profile.
        private static int DistanceFromClosestStandingTile(List<Vector2Int> standingTiles, Vector2Int target)
        {
            int best = int.MaxValue;

            for (int i = 0; i < standingTiles.Count; i++)
            {
                int distance = GridManager.GetGridDistance(standingTiles[i], target);

                if (distance < best)
                {
                    best = distance;
                }
            }

            return best;
        }
    }
}
