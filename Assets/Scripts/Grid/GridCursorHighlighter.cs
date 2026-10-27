using System;
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

        // A left click on a tile that is not under UI. This component owns the raycast, not the
        // rules: PlayerActionController subscribes and decides what the click means.
        //
        // An event rather than a PlayerActionController field - that field was the only reason
        // Grid referenced Combat at all, and with it gone the Grid -> Combat edge is broken.
        public event Action<Vector2Int> TileClicked;

        // The tile under the cursor this frame, or null when off the board or over UI. Polled by
        // the HUD's targeting preview.
        public Vector2Int? HoveredTile { get; private set; }

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
            if (GridInput.GamepadActive)
            {
                UpdatePadCursor();
                return;
            }

            padCursor = null;

            // UI gets first refusal on the pointer. Without this the world raycast runs in parallel
            // with a Button's own onClick, so pressing Wait both ends the turn AND fires a board
            // click at whatever tile happened to be behind the HUD.
            //
            // Hover is cleared rather than merely skipped: returning early without this would leave
            // the last tile lit while the cursor sits on the HUD, and it would stay lit until the
            // pointer returned to the board.
            if (GridInput.IsPointerOverUI())
            {
                HoveredTile = null;
                grid.SetHoveredTile(null);
                return;
            }

            Vector2Int? hoveredCoords = grid.GetTileUnderCursor(cam, out Vector2Int coords)
                ? coords
                : (Vector2Int?)null;

            HoveredTile = hoveredCoords;
            grid.SetHoveredTile(hoveredCoords);

            if (logHoveredCoords)
            {
                LogOnChange(hoveredCoords);
            }

            if (hoveredCoords.HasValue && GridInput.WasLeftClickThisFrame())
            {
                TileClicked?.Invoke(hoveredCoords.Value);
            }
        }

        // ---------------------------------------------------------------------
        // Gamepad: a tile cursor instead of a pointer
        // ---------------------------------------------------------------------

        // Where the pad's cursor sits. Null until the pad takes over, and again once the mouse
        // does, so the next pad press picks up from where the player is looking.
        private Vector2Int? padCursor;

        // Whether a menu held the pad's focus last frame. A on a menu button closes the menu and
        // clears the focus in the same frame, so without this the press would land on the board too.
        private bool uiHadFocus;

        // Puts the pad's cursor on a tile - the acting unit at the start of its turn, say. Ignored
        // by the mouse, which points for itself.
        public void MoveCursorTo(Vector2Int coords)
        {
            if (grid != null && grid.HasTile(coords))
            {
                padCursor = coords;
            }
        }

        private void UpdatePadCursor()
        {
            bool uiFocused = GridInput.IsUIFocused();
            bool uiOwnsPad = uiFocused || uiHadFocus;
            uiHadFocus = uiFocused;

            if (!padCursor.HasValue || !grid.HasTile(padCursor.Value))
            {
                padCursor = HoveredTile ?? TileNearestScreenCentre();
            }

            if (!padCursor.HasValue)
            {
                HoveredTile = null;
                grid.SetHoveredTile(null);
                return;
            }

            if (!uiOwnsPad)
            {
                Vector2Int step = GridInput.GetCursorStep();

                if (step != Vector2Int.zero)
                {
                    padCursor = StepFrom(padCursor.Value, ScreenStepToGrid(step));
                }
            }

            HoveredTile = padCursor;
            grid.SetHoveredTile(padCursor);

            if (logHoveredCoords)
            {
                LogOnChange(padCursor);
            }

            if (!uiOwnsPad && GridInput.WasPadPressed(GridInput.PadButton.Confirm))
            {
                TileClicked?.Invoke(padCursor.Value);
            }
        }

        // "Up" on the stick is away from the camera, whichever way it faces: the screen direction
        // is turned into world space by the camera's yaw, then snapped to the nearest grid axis
        // (grid x is world x, grid y is world z). A camera at exactly 45 degrees sits between two
        // axes; the small bias makes it pick the same one every time.
        private Vector2Int ScreenStepToGrid(Vector2Int step)
        {
            Vector3 forward = Vector3.ProjectOnPlane(cam.transform.forward, Vector3.up).normalized;
            Vector3 right = Vector3.ProjectOnPlane(cam.transform.right, Vector3.up).normalized;
            Vector3 world = right * step.x + forward * step.y;

            float angle = Mathf.Atan2(world.z, world.x) * Mathf.Rad2Deg + 1f;
            int quadrant = Mathf.RoundToInt(angle / 90f);

            switch ((quadrant % 4 + 4) % 4)
            {
                case 0: return new Vector2Int(1, 0);
                case 1: return new Vector2Int(0, 1);
                case 2: return new Vector2Int(-1, 0);
                default: return new Vector2Int(0, -1);
            }
        }

        // Hops over a hole or two so the cursor can cross gaps in the board; stays put at the edge.
        private Vector2Int StepFrom(Vector2Int from, Vector2Int direction)
        {
            for (int distance = 1; distance <= 3; distance++)
            {
                Vector2Int next = from + direction * distance;

                if (grid.HasTile(next))
                {
                    return next;
                }
            }

            return from;
        }

        private Vector2Int? TileNearestScreenCentre()
        {
            Vector2Int? best = null;
            float bestDistance = float.MaxValue;

            foreach (Vector2Int coords in grid.GridTiles.Keys)
            {
                Vector3 viewport = cam.WorldToViewportPoint(grid.GridToWorldPosition(coords));

                if (viewport.z <= 0f)
                {
                    continue;
                }

                float distance = (new Vector2(viewport.x, viewport.y) - new Vector2(0.5f, 0.5f)).sqrMagnitude;

                if (distance < bestDistance)
                {
                    bestDistance = distance;
                    best = coords;
                }
            }

            return best;
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
