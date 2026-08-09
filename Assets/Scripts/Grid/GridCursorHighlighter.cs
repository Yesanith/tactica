using UnityEngine;

namespace Tactica.Grid
{
    // Tactica.Camera is a sibling namespace, so an unqualified "Camera" inside Tactica.* resolves
    // to that namespace instead of UnityEngine.Camera (CS0118). The alias must sit inside the
    // namespace block to win that lookup - at file scope it would be checked too late.
    using Camera = UnityEngine.Camera;

    // Drives GridManager.UpdateCursorHighlight once per frame. Attach next to a GridManager.
    //
    // Separate component rather than an Update() on GridManager: keeps input policy and the camera
    // reference out of the data layer, avoids a per-frame call on grids that don't need hover, and
    // lets you toggle hover off in the Inspector. Polled rather than event-driven because the tile
    // under the cursor also changes when the camera moves.
    public class GridCursorHighlighter : MonoBehaviour
    {
        [Tooltip("Grid to highlight. Defaults to a GridManager on this GameObject, then to the first one in the scene.")]
        [SerializeField] private GridManager Grid;

        [Tooltip("Camera the hover ray is cast from. Defaults to Camera.main.")]
        [SerializeField] private Camera Cam;

        [Tooltip("Log each coordinate as the cursor enters it. Debugging aid only.")]
        [SerializeField] private bool LogHoveredCoords;

        private Vector2Int? LastLoggedCoords;

        private void Awake()
        {
            if (Grid == null)
            {
                Grid = GetComponent<GridManager>();
            }

            if (Grid == null)
            {
                // FindAnyObjectByType, not the deprecated FindObjectOfType/FindFirstObjectByType.
                // Full scene scan - fine once in Awake, never in Update.
                Grid = FindAnyObjectByType<GridManager>();
            }

            if (Cam == null)
            {
                Cam = Camera.main;
            }
        }

        private void OnEnable()
        {
            if (Grid == null)
            {
                Debug.LogWarning($"{nameof(GridCursorHighlighter)}: no {nameof(GridManager)} found, disabling.", this);
                enabled = false;
                return;
            }

            if (Cam == null)
            {
                Debug.LogWarning(
                    $"{nameof(GridCursorHighlighter)}: no camera assigned and no Camera.main in the scene " +
                    "(is your camera tagged MainCamera?), disabling.",
                    this);
                enabled = false;
            }
        }

        private void Update()
        {
            Grid.UpdateCursorHighlight(Cam);

            if (LogHoveredCoords)
            {
                LogOnChange();
            }

            if (GridInput.WasLeftClickThisFrame())
            {
                HandleLeftClick();
            }
        }

        // TEMPORARY TEST BEHAVIOUR - clicking a range tile moves GridManager's TestUnit.
        // Stands in for real selection input; remove alongside the TestUnit wiring.
        private void HandleLeftClick()
        {
            if (!Grid.GetTileUnderCursor(Cam, out Vector2Int coords))
            {
                return;
            }

            // Only tiles currently showing as in-range are valid targets. TryMoveUnitToTile
            // re-validates against live grid data, so this is a UI gate, not the authority.
            if ((Grid.GetTileHighlightState(coords) & TileHighlightState.InMoveRange) == 0)
            {
                return;
            }

            Grid.TryMoveTestUnitTo(coords);
        }

        // Otherwise the last hovered tile stays lit with nothing left running to clear it.
        private void OnDisable()
        {
            if (Grid != null)
            {
                Grid.ClearCursorHighlight();
            }
        }

        private void LogOnChange()
        {
            Vector2Int? current = Grid.GetTileUnderCursor(Cam, out Vector2Int coords)
                ? coords
                : (Vector2Int?)null;

            if (current == LastLoggedCoords)
            {
                return;
            }

            LastLoggedCoords = current;
            Debug.Log(current.HasValue ? $"Hovering tile {current.Value}" : "Cursor off grid");
        }
    }
}
