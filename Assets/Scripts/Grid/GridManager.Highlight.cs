using System.Collections.Generic;
using UnityEngine;

namespace Tactica.Grid
{
    using Camera = UnityEngine.Camera;

    // Cursor hover and the highlight layers: move range, deployment zone, pending charges and
    // ability targets. ApplyHighlightVisual is the one place that picks a tile's material.
    public partial class GridManager : MonoBehaviour
    {
        // True if the cursor is over a spawned tile, with coords set to it.
        public bool GetTileUnderCursor(Camera cam, out Vector2Int coords)
        {
            coords = default;

            if (cam == null || !GridInput.TryGetMouseScreenPosition(out Vector3 screenPosition))
            {
                return false;
            }

            Ray ray = cam.ScreenPointToRay(screenPosition);

            // Restricting to the Tiles layer makes unit colliders (and everything else) invisible
            // to this ray, so hovering a tile still works when a unit is standing on it.
            // Guard the shift: 1 << -1 would wrongly resolve to layer 31, since C# masks shift
            // counts to 5 bits. Missing layer falls back to hitting everything, which is the old
            // behaviour rather than hovering nothing at all.
            int layerMask = TileLayer >= 0 ? 1 << TileLayer : Physics.DefaultRaycastLayers;

            if (!Physics.Raycast(ray, out RaycastHit hit, Mathf.Infinity, layerMask))
            {
                return false;
            }

            GameObject hitObject = hit.collider.gameObject;

            if (objectToCoords.TryGetValue(hitObject, out coords))
            {
                return true;
            }

            // Walk up for prefabs whose collider sits on a child of the instantiated root.
            Transform root = hitObject.transform.parent;
            while (root != null)
            {
                if (objectToCoords.TryGetValue(root.gameObject, out coords))
                {
                    return true;
                }

                root = root.parent;
            }

            return false;
        }

        public TileHighlightState GetTileHighlightState(Vector2Int coords)
        {
            return highlightStates.TryGetValue(coords, out TileHighlightState state) ? state : TileHighlightState.None;
        }

        // Sets or clears one layer, leaving the others untouched. This is what stops hover and
        // move-range from overwriting each other.
        public void SetTileHighlight(Vector2Int coords, TileHighlightState layer, bool enabled)
        {
            TileHighlightState state = GetTileHighlightState(coords);
            TileHighlightState updated = enabled ? state | layer : state & ~layer;

            if (updated == state)
            {
                return;
            }

            if (updated == TileHighlightState.None)
            {
                highlightStates.Remove(coords);
            }
            else
            {
                highlightStates[coords] = updated;
            }

            ApplyHighlightVisual(coords);
        }

        // The single place that decides what a tile looks like. Precedence lives here rather than
        // in whoever wrote last, so the order the two systems run in stops mattering.
        private void ApplyHighlightVisual(Vector2Int coords)
        {
            if (!spawnedTiles.TryGetValue(coords, out GameObject tileObject) || tileObject == null)
            {
                return;
            }

            TileHighlightState state = GetTileHighlightState(coords);
            Material material = baseMaterial;

            // Bitwise rather than HasFlag - clearer intent for flags, and no boxing.
            if ((state & TileHighlightState.Hovered) != 0 && highlightMaterial != null)
            {
                material = highlightMaterial;
            }
            // Attackable outranks InMoveRange: when a tile is both, "I can hit this" is the more
            // urgent fact, and the move range is already legible from the tiles around it.
            else if ((state & TileHighlightState.Attackable) != 0 && attackableMaterial != null)
            {
                material = attackableMaterial;
            }
            else if ((state & TileHighlightState.Charging) != 0 && ChargeMaterial != null)
            {
                material = ChargeMaterial;
            }
            else if ((state & TileHighlightState.Deployable) != 0 && DeployMaterial != null)
            {
                material = DeployMaterial;
            }
            else if ((state & TileHighlightState.InMoveRange) != 0 && rangeMaterial != null)
            {
                material = rangeMaterial;
            }

            if (material != null)
            {
                ApplyMaterial(tileObject, material);
            }

            ApplyHeightTint(tileObject, coords, material == baseMaterial);
        }

        // A property block rather than a material per height: it tints without cloning materials,
        // and is removed while a highlight shows so the highlight colour reads unchanged.
        private void ApplyHeightTint(GameObject tileObject, Vector2Int coords, bool tint)
        {
            if (!tileObject.TryGetComponent(out Renderer tileRenderer))
            {
                return;
            }

            if (!tint)
            {
                tileRenderer.SetPropertyBlock(null);
                return;
            }

            int height = GridTiles.TryGetValue(coords, out GridTile tile) ? tile.Height : 0;
            float t = MaxTileHeight > 0 ? (float)height / MaxTileHeight : 0f;
            Color color = Color.Lerp(LowGroundColor, HighGroundColor, t);

            tintBlock ??= new MaterialPropertyBlock();
            tintBlock.SetColor(BaseColorId, color);
            tintBlock.SetColor(ColorId, color);
            tileRenderer.SetPropertyBlock(tintBlock);
        }

        // Moves the hover highlight to the given tile, or clears it when null.
        //
        // Takes an already-resolved coordinate rather than a Camera so the caller can raycast once
        // per frame and feed the result to every consumer. Previously this raycast internally,
        // which meant hover, click handling and hover logging each fired their own.
        public void SetHoveredTile(Vector2Int? currentCoords)
        {
            // Nullable == covers moved-between / moved-on / moved-off / didn't-move in one test,
            // and stops us reassigning materials every frame the cursor sits still.
            if (currentCoords == highlightedCoords)
            {
                return;
            }

            // Only the Hovered layer is touched; a tile's InMoveRange bit survives untouched.
            if (highlightedCoords.HasValue)
            {
                SetTileHighlight(highlightedCoords.Value, TileHighlightState.Hovered, false);
            }

            if (currentCoords.HasValue)
            {
                SetTileHighlight(currentCoords.Value, TileHighlightState.Hovered, true);
            }

            highlightedCoords = currentCoords;
        }

        public void ClearCursorHighlight()
        {
            if (highlightedCoords.HasValue)
            {
                SetTileHighlight(highlightedCoords.Value, TileHighlightState.Hovered, false);
                highlightedCoords = null;
            }
        }

        // sharedMaterial, not material: reading Renderer.material clones per object and leaks one
        // material per tile. Assigning sharedMaterial is safe; mutating one edits the asset itself.
        private static void ApplyMaterial(GameObject tileObject, Material material)
        {
            if (tileObject.TryGetComponent(out Renderer tileRenderer))
            {
                tileRenderer.sharedMaterial = material;
            }
        }

        // Highlights the unit's move range, replacing any previously shown range.
        // Touches only the InMoveRange layer, so an active hover highlight is preserved.
        public void ShowMoveRangeForUnit(GridUnit unit)
        {
            if (unit == null)
            {
                Debug.LogWarning($"{nameof(GridManager)}: {nameof(ShowMoveRangeForUnit)} called with a null unit.", this);
                return;
            }

            ClearMoveRangeHighlight();

            List<Vector2Int> tilesInRange = GetTilesInMoveRange(unit.GridCoords, unit.EffectiveStats.Move, unit.EffectiveStats.Jump);

            foreach (Vector2Int coords in tilesInRange)
            {
                SetTileHighlight(coords, TileHighlightState.InMoveRange, true);
                moveRangeHighlights.Add(coords);
            }
        }

        // Lights the pre-fight deployment zone, replacing any zone shown.
        public void ShowDeployZone(IEnumerable<Vector2Int> coords)
        {
            ClearDeployZone();

            foreach (Vector2Int tile in coords)
            {
                SetTileHighlight(tile, TileHighlightState.Deployable, true);
                deployHighlights.Add(tile);
            }
        }

        public void ClearDeployZone()
        {
            foreach (Vector2Int coords in deployHighlights)
            {
                SetTileHighlight(coords, TileHighlightState.Deployable, false);
            }

            deployHighlights.Clear();
        }

        public void AddChargeTiles(IEnumerable<Vector2Int> coords)
        {
            foreach (Vector2Int tile in coords)
            {
                chargeHighlights.TryGetValue(tile, out int count);
                chargeHighlights[tile] = count + 1;
                SetTileHighlight(tile, TileHighlightState.Charging, true);
            }
        }

        public void RemoveChargeTiles(IEnumerable<Vector2Int> coords)
        {
            foreach (Vector2Int tile in coords)
            {
                if (!chargeHighlights.TryGetValue(tile, out int count))
                {
                    continue;
                }

                if (count > 1)
                {
                    chargeHighlights[tile] = count - 1;
                    continue;
                }

                chargeHighlights.Remove(tile);
                SetTileHighlight(tile, TileHighlightState.Charging, false);
            }
        }

        public void ClearChargeTiles()
        {
            foreach (Vector2Int tile in new List<Vector2Int>(chargeHighlights.Keys))
            {
                SetTileHighlight(tile, TileHighlightState.Charging, false);
            }

            chargeHighlights.Clear();
        }

        // An orange copy of the range material, made once, like DeployMaterial.
        private Material ChargeMaterial => TintedRangeCopy(ref chargeMaterial, "ChargeZone (runtime)", ChargeZoneColor);

        // The authored deployMaterial, or a teal copy of the range material made once - so the
        // zone has its own colour without the scene needing a new asset.
        private Material DeployMaterial => TintedRangeCopy(ref deployMaterial, "DeployZone (runtime)", DeployZoneColor);

        // Fills `cache` with a recoloured copy of rangeMaterial unless it already holds one.
        private Material TintedRangeCopy(ref Material cache, string name, Color color)
        {
            if (cache == null && rangeMaterial != null)
            {
                cache = new Material(rangeMaterial) { name = name, hideFlags = HideFlags.DontSave };

                if (cache.HasProperty(BaseColorId)) cache.SetColor(BaseColorId, color);
                if (cache.HasProperty(ColorId)) cache.SetColor(ColorId, color);
            }

            return cache;
        }

        // Lights the given tiles as ability targets, replacing any previous set.
        // Touches only the Attackable layer, so hover and move range survive underneath.
        public void ShowAttackableTiles(IEnumerable<Vector2Int> coords)
        {
            ClearAttackableHighlights();

            if (coords == null)
            {
                return;
            }

            foreach (Vector2Int tile in coords)
            {
                SetTileHighlight(tile, TileHighlightState.Attackable, true);
                attackableHighlights.Add(tile);
            }
        }

        public void ClearAttackableHighlights()
        {
            foreach (Vector2Int coords in attackableHighlights)
            {
                SetTileHighlight(coords, TileHighlightState.Attackable, false);
            }

            attackableHighlights.Clear();
        }

        public void ClearMoveRangeHighlight()
        {
            foreach (Vector2Int coords in moveRangeHighlights)
            {
                SetTileHighlight(coords, TileHighlightState.InMoveRange, false);
            }

            moveRangeHighlights.Clear();
        }
    }
}
