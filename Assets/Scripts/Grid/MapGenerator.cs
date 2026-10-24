using System.Collections.Generic;
using UnityEngine;

namespace Tactica.Grid
{
    // Builds a battlefield from MapSettings: terraced terrain, ragged edges, holes and cover.
    //
    // Plain static code with its own System.Random, so a seed reproduces the same map on any
    // machine and the generator is testable without a scene. GridManager.BuildMap is the only
    // caller.
    //
    // The order of the passes matters:
    //   1. heights     - Perlin noise quantised into terraces, then relaxed so neighbours never
    //                    differ by more than 1. Any Jump 1 unit can then cross the terrain itself.
    //   2. outline     - noise-driven bites out of the edges, so the board is not a rectangle.
    //   3. holes       - scattered gaps in the interior.
    //   4. connectivity- anything a Jump 1 unit cannot reach from the player's edge is removed,
    //                    so every remaining tile is part of the playable map.
    //   5. cover       - a few blocks raised by 2, placed LAST so they cannot disconnect the map:
    //                    each one only goes where its four neighbours exist and are not cover.
    public static class MapGenerator
    {
        // The rows nearest each edge stay mostly intact so both sides have somewhere to deploy.
        private const int SpawnBandRows = 2;
        private const float TerrainFrequency = 0.18f;
        private const float EdgeFrequency = 0.35f;
        private const int CoverRise = 2;
        private const int MaxAttempts = 6;

        // Fewer than this fraction of the rectangle left after pruning means the seed carved the
        // map apart; the next seed is tried instead.
        private const float MinimumKeptFraction = 0.55f;

        public static Dictionary<Vector2Int, GridTile> Generate(MapSettings settings)
        {
            Dictionary<Vector2Int, GridTile> tiles = null;

            for (int attempt = 0; attempt < MaxAttempts; attempt++)
            {
                tiles = TryGenerate(settings, settings.seed + attempt * 7919);

                if (IsPlayable(tiles, settings))
                {
                    return tiles;
                }
            }

            // Every seed failed - fall back to the last attempt rather than nothing. In practice
            // only absurd settings (huge holeChance on a tiny map) get here.
            Debug.LogWarning($"{nameof(MapGenerator)}: no attempt produced a well-connected map; using the last one.");
            return tiles;
        }

        private static Dictionary<Vector2Int, GridTile> TryGenerate(MapSettings settings, int seed)
        {
            System.Random random = new System.Random(seed);
            int width = settings.width;
            int depth = settings.depth;

            float terrainX = (float)random.NextDouble() * 1000f;
            float terrainY = (float)random.NextDouble() * 1000f;
            float edgeX = (float)random.NextDouble() * 1000f;
            float edgeY = (float)random.NextDouble() * 1000f;

            int[,] heights = new int[width, depth];

            for (int x = 0; x < width; x++)
            {
                for (int y = 0; y < depth; y++)
                {
                    float noise = Mathf.PerlinNoise(terrainX + x * TerrainFrequency, terrainY + y * TerrainFrequency);
                    heights[x, y] = Mathf.Clamp(Mathf.FloorToInt(noise * (settings.maxHeight + 1)), 0, settings.maxHeight);
                }
            }

            RelaxHeights(heights, width, depth);

            Dictionary<Vector2Int, GridTile> tiles = new Dictionary<Vector2Int, GridTile>();

            for (int x = 0; x < width; x++)
            {
                for (int y = 0; y < depth; y++)
                {
                    if (IsBittenOff(x, y, width, depth, settings.edgeDepth, edgeX, edgeY))
                    {
                        continue;
                    }

                    bool inSpawnBand = y < SpawnBandRows || y >= depth - SpawnBandRows;

                    if (!inSpawnBand && random.NextDouble() < settings.holeChance)
                    {
                        continue;
                    }

                    Vector2Int coords = new Vector2Int(x, y);
                    tiles[coords] = new GridTile(coords, heights[x, y], isWalkable: true);
                }
            }

            PruneUnreachable(tiles, width);
            AddCover(tiles, random, settings, depth);

            return tiles;
        }

        // Lowers whichever of two neighbours is higher until no pair differs by more than 1.
        // Lowering, never raising, so the relaxation always terminates.
        private static void RelaxHeights(int[,] heights, int width, int depth)
        {
            bool changed = true;

            while (changed)
            {
                changed = false;

                for (int x = 0; x < width; x++)
                {
                    for (int y = 0; y < depth; y++)
                    {
                        if (x + 1 < width) changed |= Relax(heights, x, y, x + 1, y);
                        if (y + 1 < depth) changed |= Relax(heights, x, y, x, y + 1);
                    }
                }
            }
        }

        private static bool Relax(int[,] heights, int ax, int ay, int bx, int by)
        {
            int difference = heights[ax, ay] - heights[bx, by];

            if (difference > 1)
            {
                heights[ax, ay] = heights[bx, by] + 1;
                return true;
            }

            if (difference < -1)
            {
                heights[bx, by] = heights[ax, ay] + 1;
                return true;
            }

            return false;
        }

        // A tile within `bite` of an edge is removed, where bite follows a noise field along the
        // border - which is what makes the outline ragged rather than uniformly inset. The player
        // and enemy edges bite at most 1 deep, so both sides keep a deployment line.
        private static bool IsBittenOff(int x, int y, int width, int depth, int edgeDepth, float offsetX, float offsetY)
        {
            if (edgeDepth <= 0)
            {
                return false;
            }

            float noise = Mathf.PerlinNoise(offsetX + x * EdgeFrequency, offsetY + y * EdgeFrequency);
            int sideBite = Mathf.RoundToInt(noise * edgeDepth);
            int endBite = Mathf.Min(1, sideBite);

            int fromSide = Mathf.Min(x, width - 1 - x);
            int fromEnd = Mathf.Min(y, depth - 1 - y);

            return fromSide < sideBite || fromEnd < endBite;
        }

        // Keeps only tiles a Jump 1 unit can reach from the middle of the player's edge.
        private static void PruneUnreachable(Dictionary<Vector2Int, GridTile> tiles, int width)
        {
            if (!TryFindStart(tiles, width, out Vector2Int start))
            {
                tiles.Clear();
                return;
            }

            HashSet<Vector2Int> reached = new HashSet<Vector2Int> { start };
            Queue<Vector2Int> frontier = new Queue<Vector2Int>();
            frontier.Enqueue(start);

            while (frontier.Count > 0)
            {
                Vector2Int current = frontier.Dequeue();
                int height = tiles[current].Height;

                foreach (Vector2Int next in Neighbours(current))
                {
                    if (reached.Contains(next) || !tiles.TryGetValue(next, out GridTile tile))
                    {
                        continue;
                    }

                    if (Mathf.Abs(tile.Height - height) > 1)
                    {
                        continue;
                    }

                    reached.Add(next);
                    frontier.Enqueue(next);
                }
            }

            List<Vector2Int> unreachable = new List<Vector2Int>();

            foreach (Vector2Int coords in tiles.Keys)
            {
                if (!reached.Contains(coords))
                {
                    unreachable.Add(coords);
                }
            }

            foreach (Vector2Int coords in unreachable)
            {
                tiles.Remove(coords);
            }
        }

        // The player-edge tile closest to the centre column.
        private static bool TryFindStart(Dictionary<Vector2Int, GridTile> tiles, int width, out Vector2Int start)
        {
            for (int y = 0; y < SpawnBandRows + 2; y++)
            {
                for (int offset = 0; offset < width; offset++)
                {
                    int x = width / 2 + (offset % 2 == 0 ? offset / 2 : -(offset / 2 + 1));
                    start = new Vector2Int(x, y);

                    if (tiles.ContainsKey(start))
                    {
                        return true;
                    }
                }
            }

            start = default;
            return false;
        }

        private static void AddCover(Dictionary<Vector2Int, GridTile> tiles, System.Random random, MapSettings settings, int depth)
        {
            if (settings.coverChance <= 0f)
            {
                return;
            }

            HashSet<Vector2Int> cover = new HashSet<Vector2Int>();
            List<Vector2Int> candidates = new List<Vector2Int>(tiles.Keys);

            // Sorted so the walk order, and therefore the result, does not depend on dictionary
            // iteration order.
            candidates.Sort((a, b) => a.y != b.y ? a.y.CompareTo(b.y) : a.x.CompareTo(b.x));

            foreach (Vector2Int coords in candidates)
            {
                if (coords.y < SpawnBandRows || coords.y >= depth - SpawnBandRows)
                {
                    continue;
                }

                if (random.NextDouble() >= settings.coverChance)
                {
                    continue;
                }

                bool surrounded = true;

                foreach (Vector2Int next in Neighbours(coords))
                {
                    if (!tiles.ContainsKey(next) || cover.Contains(next))
                    {
                        surrounded = false;
                        break;
                    }
                }

                if (!surrounded)
                {
                    continue;
                }

                GridTile tile = tiles[coords];
                tile.Height += CoverRise;
                tiles[coords] = tile;
                cover.Add(coords);
            }
        }

        // Both deployment lines must survive, or a side has nowhere to spawn.
        private static bool IsPlayable(Dictionary<Vector2Int, GridTile> tiles, MapSettings settings)
        {
            if (tiles == null || tiles.Count < settings.width * settings.depth * MinimumKeptFraction)
            {
                return false;
            }

            int nearRow = 0;
            int farRow = 0;

            foreach (Vector2Int coords in tiles.Keys)
            {
                if (coords.y < SpawnBandRows) nearRow++;
                if (coords.y >= settings.depth - SpawnBandRows) farRow++;
            }

            return nearRow >= 4 && farRow >= 4;
        }

        private static IEnumerable<Vector2Int> Neighbours(Vector2Int coords)
        {
            yield return coords + Vector2Int.right;
            yield return coords + Vector2Int.left;
            yield return coords + Vector2Int.up;
            yield return coords + Vector2Int.down;
        }
    }
}
