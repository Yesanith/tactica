using UnityEngine;
using UnityEngine.Serialization;
using Tactica.Combat;

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

        [Tooltip("Decides what a click means. Defaults to the first one in the scene. Without it " +
                 "hover still works but clicks do nothing.")]
        [SerializeField] private PlayerActionController actionController;

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

            if (actionController == null)
            {
                actionController = FindAnyObjectByType<PlayerActionController>();
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
        //
        // LateUpdate, not Update, for two reasons:
        //   1. EventSystem processes input modules in its own Update, and Update-to-Update order
        //      between components is undefined. Running here means the UI has finished handling
        //      the pointer for this frame before any world click is considered, so a button press
        //      cannot also register as a board click.
        //   2. TacticsCameraController moves the camera in Update, so hover computed here reflects
        //      where the camera actually ended up rather than trailing it by a frame.
        //
        // Mouse.current.leftButton.wasPressedThisFrame stays true for the whole frame, so reading
        // the click later costs nothing.
        private void LateUpdate()
        {
            // UI gets first refusal on the pointer. Without this the world raycast runs in parallel
            // with a Button's own onClick, so pressing Wait both ends the turn AND fires a board
            // click at whatever tile happened to be behind the HUD.
            //
            // Hover is cleared rather than merely skipped: returning early without this would leave
            // the last tile lit while the cursor sits on the HUD, and it would stay lit until the
            // pointer returned to the board.
            if (GridInput.IsPointerOverUI())
            {
                grid.SetHoveredTile(null);
                return;
            }

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

        // Reports the clicked tile and lets PlayerActionController decide what it means - move,
        // ability target, or cancel. This component owns the raycast, not the rules.
        private void HandleLeftClick(Vector2Int coords)
        {
            if (actionController == null)
            {
                return;
            }

            actionController.HandleTileClicked(coords);
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
