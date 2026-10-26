using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using UnityEngine;
using Tactica.Campaign;
using Tactica.Grid;

namespace Tactica.Tests
{
    // The deployment zone (MissionRunner.BuildZone): its size, its shape, and which tiles it may
    // use. Pure - built on a hand-made list of tiles, no scene.
    public class DeploymentZoneTests
    {
        private readonly List<GameObject> objects = new List<GameObject>();

        [TearDown]
        public void TearDown()
        {
            foreach (GameObject item in objects)
            {
                Object.DestroyImmediate(item);
            }

            objects.Clear();
        }

        [TestCase(1)]
        [TestCase(3)]
        [TestCase(4)]
        [TestCase(5)]
        public void TheZoneHoldsTwoTilesPerCrewMemberAndNeverFewerThanTheMinimum(int crew)
        {
            List<Vector2Int> zone = MissionRunner.BuildZone(Board(12, 12), new Vector2Int(6, 0), new List<GameObject>(), crew);

            Assert.AreEqual(Mathf.Max(MissionRunner.MinDeployZone, crew * MissionRunner.DeployZonePerUnit), zone.Count);
            Assert.AreEqual(zone.Count, zone.Distinct().Count());
        }

        [Test]
        public void TheZoneIsTheTilesNearestTheAnchor()
        {
            Vector2Int anchor = new Vector2Int(6, 0);
            List<Vector2Int> zone = MissionRunner.BuildZone(Board(12, 12), anchor, new List<GameObject>(), 3);

            Assert.Contains(anchor, zone);

            // Nothing outside the zone is closer to the anchor than the farthest tile inside it.
            int farthestIn = zone.Max(c => GridManager.GetGridDistance(anchor, c));
            IEnumerable<Vector2Int> outside = Board(12, 12).Select(t => t.Coordinates).Except(zone);
            Assert.IsTrue(outside.All(c => GridManager.GetGridDistance(anchor, c) >= farthestIn));
        }

        [Test]
        public void TheZoneSkipsHolesAndTilesHeldByOthersButKeepsTheCrewsOwn()
        {
            Vector2Int anchor = new Vector2Int(6, 0);
            GameObject crewMember = Track(new GameObject("Crew"));
            GameObject guest = Track(new GameObject("Guest"));

            List<GridTile> board = Board(12, 12);
            Replace(board, new GridTile(new Vector2Int(5, 0), 0, false));            // a hole
            Replace(board, new GridTile(new Vector2Int(7, 0), 0, true, guest));      // someone else
            Replace(board, new GridTile(anchor, 0, true, crewMember));               // the crew

            List<Vector2Int> zone = MissionRunner.BuildZone(board, anchor, new List<GameObject> { crewMember }, 3);

            Assert.Contains(anchor, zone);
            CollectionAssert.DoesNotContain(zone, new Vector2Int(5, 0));
            CollectionAssert.DoesNotContain(zone, new Vector2Int(7, 0));
        }

        [Test]
        public void TheZonePrefersThePlayersEdgeOnATie()
        {
            // From the middle of the board the minimum zone (6) is the anchor, its 4 neighbours and
            // one of the 8 tiles two steps away - all equally near. The tie goes to the player's
            // edge (lowest y): two steps south, not two steps north.
            Vector2Int anchor = new Vector2Int(6, 6);
            List<Vector2Int> zone = MissionRunner.BuildZone(Board(12, 12), anchor, new List<GameObject>(), 1);

            Assert.AreEqual(MissionRunner.MinDeployZone, zone.Count);
            Assert.Contains(anchor + new Vector2Int(0, -2), zone);
            CollectionAssert.DoesNotContain(zone, anchor + new Vector2Int(0, 2));
        }

        private static List<GridTile> Board(int width, int depth)
        {
            List<GridTile> tiles = new List<GridTile>();

            for (int x = 0; x < width; x++)
            {
                for (int y = 0; y < depth; y++)
                {
                    tiles.Add(new GridTile(new Vector2Int(x, y), 0, true));
                }
            }

            return tiles;
        }

        private static void Replace(List<GridTile> board, GridTile tile)
        {
            board[board.FindIndex(t => t.Coordinates == tile.Coordinates)] = tile;
        }

        private GameObject Track(GameObject item)
        {
            objects.Add(item);
            return item;
        }
    }
}
