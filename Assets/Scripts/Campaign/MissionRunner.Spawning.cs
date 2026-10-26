using System.Collections;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using Tactica.Combat;
using Tactica.Grid;
using Tactica.Stats;

namespace Tactica.Campaign
{
    // Putting units on the board and taking them off again: bodies, spawn tiles, teardown.
    public partial class MissionRunner : MonoBehaviour
    {
        // Creates a capsule unit on a free tile on its side of the board.
        //
        // Built inactive and configured before activation, so GridUnit.Awake computes stats from
        // the right job and level on its one and only run. Activating first would initialise from
        // an empty job, log a warning, and then need a second, full-heal initialisation.
        private GridUnit Spawn(
            string unitName, JobDefinition job, int level, Faction faction, bool guest,
            GameObject modelOverride, float modelScale)
        {
            if (job == null)
            {
                Debug.LogWarning($"{nameof(MissionRunner)}: '{unitName}' has no job and was not spawned.", this);
                return null;
            }

            if (!TryFindSpawnTile(faction, out Vector2Int coords))
            {
                Debug.LogWarning($"{nameof(MissionRunner)}: no free tile for '{unitName}' ({faction}).", this);
                return null;
            }

            GameObject modelPrefab = modelOverride != null ? modelOverride : job.Model;
            GameObject unitObject = modelPrefab != null
                ? CreateModelBody(modelPrefab, job.AnimatorController, modelScale)
                : CreateCapsuleBody();

            GridUnit unit = unitObject.AddComponent<GridUnit>();
            unit.Configure(unitName, job, level, faction, coords, guest);
            unitObject.name = $"{unit.DisplayName} ({faction})";

            // A model's pivot is at its feet; the capsule's is at its centre, 1 above them.
            unit.SetPivotHeight(modelPrefab != null ? 0f : 1f);

            // Each side starts looking across the board at the other. Neutrals face the player's
            // edge, which is where trouble usually comes from.
            unit.SetFacing(faction == Faction.Player ? Facing.North : Facing.South);

            unitObject.SetActive(true);

            // Claimed now rather than in GridUnit.Start (next frame), so the next spawn in this
            // same loop sees the tile as taken.
            gridManagerRef.SetOccupant(coords, unitObject);
            unit.SnapToGridPosition();

            spawnedUnits.Add(unit);
            return unit;
        }

        // The placeholder body: Unity's Capsule (2 tall, centred pivot) with a visor showing facing.
        // Created inactive, like every body, so GridUnit's Awake runs only once configured.
        private static GameObject CreateCapsuleBody()
        {
            GameObject unitObject = GameObject.CreatePrimitive(PrimitiveType.Capsule);
            unitObject.SetActive(false);
            AddVisor(unitObject);
            return unitObject;
        }

        // A character model under an empty root. The root is what GridUnit moves and turns; the
        // model is a child so its own transform (scale, import rotation) is never overwritten.
        //
        // Root motion is off because GridUnit owns position: an idle clip with drift would walk
        // the model off its tile. The model's own colliders, if any, are left alone - the hover
        // raycast is masked to the Tiles layer, so they cannot block tile picking.
        private static GameObject CreateModelBody(GameObject modelPrefab, RuntimeAnimatorController controller, float scale)
        {
            GameObject unitObject = new GameObject("Unit");
            unitObject.SetActive(false);

            GameObject model = Instantiate(modelPrefab, unitObject.transform, false);
            model.name = modelPrefab.name;
            model.transform.localScale *= scale > 0f ? scale : 1f;

            if (model.TryGetComponent(out Animator animator))
            {
                animator.applyRootMotion = false;

                if (controller != null)
                {
                    animator.runtimeAnimatorController = controller;
                }
            }

            return unitObject;
        }

        // A neon strip on the capsule's +Z side, so facing - which decides back and side attacks -
        // can be read at a glance. Child of the unit, so it turns with GridUnit.SetFacing.
        private static void AddVisor(GameObject unitObject)
        {
            GameObject visor = GameObject.CreatePrimitive(PrimitiveType.Cube);
            visor.name = "Visor";

            // Purely visual. A collider here would be one more thing in front of the tile raycast
            // and the unit's own collider.
            UnityEngine.Object.DestroyImmediate(visor.GetComponent<Collider>());

            visor.transform.SetParent(unitObject.transform, false);
            visor.transform.localPosition = new Vector3(0f, 0.45f, 0.42f);
            visor.transform.localScale = new Vector3(0.55f, 0.14f, 0.2f);

            Renderer renderer = visor.GetComponent<Renderer>();

            if (visorMaterial == null)
            {
                visorMaterial = new Material(renderer.sharedMaterial) { color = VisorColor };
                visorMaterial.hideFlags = HideFlags.HideAndDontSave;
            }

            renderer.sharedMaterial = visorMaterial;
        }

        // Player fills rows from the near edge, Enemy from the far edge, Neutral from the middle
        // outwards. Within a row, columns closest to the centre go first, so small squads face
        // each other instead of hugging a corner.
        private bool TryFindSpawnTile(Faction faction, out Vector2Int coords)
        {
            int width = gridManagerRef.GridWidth;
            int depth = gridManagerRef.GridDepth;

            foreach (int z in RowOrder(faction, depth))
            {
                foreach (int x in CentreOutOrder(width))
                {
                    Vector2Int candidate = new Vector2Int(x, z);

                    if (gridManagerRef.TryGetTile(candidate, out GridTile tile) && tile.IsFree)
                    {
                        coords = candidate;
                        return true;
                    }
                }
            }

            coords = default;
            return false;
        }

        private static IEnumerable<int> RowOrder(Faction faction, int depth)
        {
            switch (faction)
            {
                case Faction.Player:
                    for (int z = 0; z < depth; z++) yield return z;
                    break;

                case Faction.Enemy:
                    for (int z = depth - 1; z >= 0; z--) yield return z;
                    break;

                case Faction.Neutral:
                    foreach (int z in CentreOutOrder(depth)) yield return z;
                    break;
            }
        }

        private static IEnumerable<int> CentreOutOrder(int count)
        {
            if (count <= 0)
            {
                yield break;
            }

            int centre = (count - 1) / 2;
            yield return centre;

            for (int distance = 1; distance < count; distance++)
            {
                if (centre + distance < count) yield return centre + distance;
                if (centre - distance >= 0) yield return centre - distance;
            }
        }

        // Destroys every spawned unit and releases its tile. GridUnit.OnDestroy releases it too,
        // but Destroy is deferred to the end of the frame; clearing now means the board is
        // genuinely empty the moment the result is announced.
        private void ClearBoard()
        {
            gridManagerRef.ClearMoveRangeHighlight();
            gridManagerRef.ClearAttackableHighlights();
            gridManagerRef.ClearDeployZone();
            gridManagerRef.ClearCursorHighlight();

            foreach (GridUnit unit in spawnedUnits)
            {
                if (unit == null)
                {
                    continue;
                }

                gridManagerRef.SetOccupant(unit.GridCoords, null);
                Destroy(unit.gameObject);
            }

            spawnedUnits.Clear();
            rosterSources.Clear();
            startingFactions.Clear();
        }

        // The scene's hand-placed units were a test fixture for combat. Once the mission flow is
        // driving, they would only stand on spawn tiles and fight in no one's encounter.
        //
        // Deactivated before being destroyed: Destroy is deferred to the end of the frame, and a
        // unit whose Start has not run yet would otherwise still claim its tile in the meantime.
        private void RemoveAuthoredUnits()
        {
            if (authoredUnitsRemoved)
            {
                return;
            }

            authoredUnitsRemoved = true;

            foreach (GridUnit unit in FindObjectsByType<GridUnit>(FindObjectsInactive.Include))
            {
                if (spawnedUnits.Contains(unit))
                {
                    continue;
                }

                unit.gameObject.SetActive(false);

                if (gridManagerRef.TryGetTile(unit.GridCoords, out GridTile tile) && tile.Occupant == unit.gameObject)
                {
                    gridManagerRef.SetOccupant(unit.GridCoords, null);
                }

                Destroy(unit.gameObject);
            }
        }
    }
}
