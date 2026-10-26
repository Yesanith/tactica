using System.Collections;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using Tactica.Combat;
using Tactica.Grid;
using Tactica.Stats;

namespace Tactica.Campaign
{
    // Deployment: before the fight the crew may be moved, but only within a compact zone near
    // their side of the board.
    public partial class MissionRunner : MonoBehaviour
    {
        // Backs out before the first turn: the board is cleared and no result is produced - the
        // fight never happened, so there is nothing to win, lose or earn.
        public void CancelDeployment()
        {
            if (!IsDeploying)
            {
                return;
            }

            IsDeploying = false;
            deploymentZone.Clear();
            ClearBoard();
            currentMission = null;
        }

        public bool CanPlace(GridUnit unit, Vector2Int coords, out string reason)
        {
            reason = null;

            if (!IsDeploying) reason = "Not deploying.";
            else if (unit == null || !rosterSources.ContainsKey(unit)) reason = "Only your crew can be placed.";
            else if (!deploymentZone.Contains(coords)) reason = "Outside the deployment zone.";
            else if (!gridManagerRef.TryGetTile(coords, out GridTile tile) || !tile.IsWalkable) reason = "Not a standing tile.";
            else if (tile.Occupant != null && tile.Occupant != unit.gameObject) reason = "Occupied.";

            return reason == null;
        }

        // Moves a crew member to another tile of the zone. Pure placement: no move range, no
        // facing change, nothing spent.
        public bool TryPlace(GridUnit unit, Vector2Int coords, out string reason)
        {
            if (!CanPlace(unit, coords, out reason))
            {
                return false;
            }

            Place(unit, coords);
            return true;
        }

        // A compact patch on the player's edge: the tiles nearest the first crew member's spawn
        // (Manhattan, then nearer the edge), counting tiles the crew already stands on as free.
        // Anyone who spawned outside it - a hole in the edge row can push a spawn far along it -
        // is moved onto a free tile inside, so the zone is one piece and holds the whole crew.
        private void BuildDeploymentZone()
        {
            deploymentZone.Clear();

            List<GridUnit> crew = Crew.ToList();

            if (crew.Count == 0)
            {
                return;
            }

            HashSet<GameObject> crewObjects = new HashSet<GameObject>(crew.Select(unit => unit.gameObject));
            deploymentZone.UnionWith(BuildZone(gridManagerRef.GridTiles.Values, crew[0].GridCoords, crewObjects, crew.Count));

            foreach (GridUnit unit in crew.Where(unit => !deploymentZone.Contains(unit.GridCoords)))
            {
                Vector2Int free = deploymentZone.FirstOrDefault(coords =>
                    gridManagerRef.TryGetTile(coords, out GridTile tile) && tile.Occupant == null);
                Place(unit, free);
            }
        }

        // The zone's tiles: the size for this many crew, nearest the anchor (Manhattan), then nearer
        // the player's edge, then nearer the anchor's column. A tile counts if it can be stood on
        // and is empty or held by the crew. Pure, for the tests.
        public static List<Vector2Int> BuildZone(
            IEnumerable<GridTile> tiles, Vector2Int anchor, ICollection<GameObject> crewObjects, int crewCount)
        {
            int size = Mathf.Max(MinDeployZone, crewCount * DeployZonePerUnit);

            return tiles
                .Where(tile => tile.IsWalkable && (tile.Occupant == null || crewObjects.Contains(tile.Occupant)))
                .Select(tile => tile.Coordinates)
                .OrderBy(coords => GridManager.GetGridDistance(anchor, coords))
                .ThenBy(coords => coords.y)
                .ThenBy(coords => Mathf.Abs(coords.x - anchor.x))
                .Take(size)
                .ToList();
        }

        private void Place(GridUnit unit, Vector2Int coords)
        {
            gridManagerRef.SetOccupant(unit.GridCoords, null);
            unit.SetGridCoords(coords);
            gridManagerRef.SetOccupant(coords, unit.gameObject);
        }
    }
}
