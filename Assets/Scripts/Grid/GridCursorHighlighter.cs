using UnityEngine;
using UnityEngine.Serialization;

namespace Tactica.Grid
{
    // Tactica.Camera is a sibling namespace, so an unqualified "Camera" inside Tactica.* resolves
    // to that namespace instead of UnityEngine.Camera (CS0118). The alias must sit inside the
    // namespace block to win that lookup - at file scope it would be checked too late.
    using Camera = UnityEngine.Camera;

    // Drives GridManager.SetHoveredTile once per frame. Attach next to a GridManager.
    //
    // Separate component rather than an Update() on GridManager: keeps input policy and the camera
    // reference out of the data layer, avoids a per-frame call on grids that don't need hover, and
    // lets you toggle hover off in the Inspector. Polled rather than event-driven because the tile
    // under the cursor also changes when the camera moves.
    public class GridCursorHighlighter : MonoBehaviour
    {
        [Tooltip("Grid to highlight. Defaults to a GridManager on this GameObject, then to the first one in the scene.")]
        [FormerlySerializedAs("Grid")]
        [SerializeField] private GridManager grid;

        [Tooltip("Camera the hover ray is cast from. Defaults to Camera.main.")]
        [FormerlySerializedAs("Cam")]
        [SerializeField] private Camera cam;

        [Tooltip("Log each coordinate as the cursor enters it. Debugging aid only.")]
        [FormerlySerializedAs("LogHoveredCoords")]
        [SerializeField] private bool logHoveredCoords;

        private Vector2Int? lastLoggedCoords;

        private void Awake()
        {
            if (grid == null)
            {
                grid = GetComponent<GridManager>();
            }

            if (grid == null)
            {
                // FindAnyObjectByType, not the deprecated FindObjectOfType/FindFirstObjectByType.
                // Full scene scan - fine once in Awake, never in Update.
                grid = FindAnyObjectByType<GridManager>();
            }

            if (cam == null)
            {
                cam = Camera.main;
            }
        }

        private void OnEnable()
        {
            if (grid == null)
            {
                Debug.LogWarning($"{nameof(GridCursorHighlighter)}: no {nameof(GridManager)} found, disabling.", this);
                enabled = false;
                return;
            }

            if (cam == null)
            {
                Debug.LogWarning(
                    $"{nameof(GridCursorHighlighter)}: no camera assigned and no Camera.main in the scene " +
                    "(is your camera tagged MainCamera?), disabling.",
                    this);
                enabled = false;
            }
        }

        // One raycast per frame, shared by every consumer below. Hover, click handling and logging
        // all used to call GetTileUnderCursor independently, costing up to three physics raycasts
        // in a frame where the player clicked with logging on - and letting them disagree if the
        // cursor moved between calls.
        private void Update()
        {
            Vector2Int? hoveredCoords = grid.GetTileUnderCursor(cam, out Vector2Int coords)
                ? coords
                : (Vector2Int?)null;

            grid.SetHoveredTile(hoveredCoords);

            if (logHoveredCoords)
            {
                LogOnChange(hoveredCoords);
            }

            if (hoveredCoords.HasValue && GridInput.WasLeftClickThisFrame())
            {
                HandleLeftClick(hoveredCoords.Value);
            }
        }

        // Clicking a highlighted range tile moves the unit whose turn it is. GridManager resolves
        // who that is and whether the turn state permits it.
        private void HandleLeftClick(Vector2Int coords)
        {
            // Only tiles currently showing as in-range are valid targets. TryMoveUnitToTile
            // re-validates against live grid data, so this is a UI gate, not the authority.
            if ((grid.GetTileHighlightState(coords) & TileHighlightState.InMoveRange) == 0)
            {
                return;
            }

            grid.TryMoveActiveUnitTo(coords);
        }

        // Otherwise the last hovered tile stays lit with nothing left running to clear it.
        private void OnDisable()
        {
            if (grid != null)
            {
                grid.ClearCursorHighlight();
            }
        }

        private void LogOnChange(Vector2Int? current)
        {
            if (current == lastLoggedCoords)
            {
                return;
            }

            lastLoggedCoords = current;
            Debug.Log(current.HasValue ? $"Hovering tile {current.Value}" : "Cursor off grid");
        }
    }
}
