using UnityEngine;
using UnityEngine.Serialization;
using Tactica.Grid;

namespace Tactica.Camera
{
    // Orbit/pan/zoom camera for the tactical grid. Attach to the Main Camera.
    //
    // Everything is driven from four values - pivot, yaw, pitch, distance - and the transform is
    // rebuilt from them once per frame. Mutating the transform directly instead would make the
    // three controls fight each other (orbiting would drift the pivot, panning would tilt).
    public class TacticsCameraController : MonoBehaviour
    {
        [Header("Pan")]
        [Tooltip("World units per second.")]
        [FormerlySerializedAs("PanSpeed")]
        [SerializeField] private float panSpeed = 12f;

        [Header("Zoom")]
        [Tooltip("Distance change per scroll notch.")]
        [FormerlySerializedAs("ZoomSpeed")]
        [SerializeField] private float zoomSpeed = 3f;
        [FormerlySerializedAs("MinZoomDistance")]
        [SerializeField] private float minZoomDistance = 5f;
        [FormerlySerializedAs("MaxZoomDistance")]
        [SerializeField] private float maxZoomDistance = 40f;

        [Header("Rotate")]
        [Tooltip("Seconds for one 90-degree snap. Q rotates counterclockwise, E clockwise.")]
        [FormerlySerializedAs("SnapRotationDuration")]
        [SerializeField] private float snapRotationDuration = 0.25f;

        [Header("Auto Framing")]
        [Tooltip("Grid to frame on start. Leave empty to fall back to the manual values below.")]
        [FormerlySerializedAs("GridManagerRef")]
        [SerializeField] private GridManager gridManagerRef;

        [Tooltip("Camera distance as a multiple of the grid's longest side. Higher pulls back " +
                 "and leaves more margin around the board.")]
        [FormerlySerializedAs("FramingPadding")]
        [SerializeField] private float framingPadding = 1.6f;

        [Header("Initial Framing (fallback)")]
        [Tooltip("Used only when gridManagerRef is unset.")]
        [FormerlySerializedAs("InitialFocusPoint")]
        [SerializeField] private Vector3 initialFocusPoint = Vector3.zero;

        [Tooltip("Used only when gridManagerRef is unset.")]
        [FormerlySerializedAs("InitialDistance")]
        [SerializeField] private float initialDistance = 20f;

        [Tooltip("Downward tilt in degrees. 45-50 is the usual tactics-game read.")]
        [Range(10f, 85f)]
        [FormerlySerializedAs("InitialPitch")]
        [SerializeField] private float initialPitch = 48f;

        [Tooltip("Compass rotation in degrees. 45 puts the grid on a diagonal, isometric-style.")]
        [FormerlySerializedAs("InitialYaw")]
        [SerializeField] private float initialYaw = 45f;

        // The point being orbited and panned around. Everything else is polar coordinates about it.
        private Vector3 pivotPoint;
        private float yaw;
        private float pitch;
        private float distance;

        // In-progress snap. Interpolated in Update rather than in a coroutine: Update already runs
        // every frame for pan and zoom and ends with a single ApplyTransform, so keeping the
        // rotation there means one writer and one transform write per frame. A coroutine resumes
        // after Update, so it would either need its own ApplyTransform call or leave the camera a
        // frame stale - plus StopCoroutine bookkeeping on disable and re-entry. Explicit state is
        // also trivially inspectable, which a yield-machine is not.
        private bool isSnapping;
        private float snapStartYaw;
        private float snapTargetYaw;
        private float snapElapsed;

        private void Awake()
        {
            ResetFraming();
        }

        // Start, not Awake: GridManager's dimensions are serialised so they would be readable
        // either way, but framing after every Awake means anything that resizes the grid during
        // its own Awake is already accounted for.
        private void Start()
        {
            AutoFrameGrid();
        }

        private void Update()
        {
            HandlePan();
            HandleZoom();
            HandleSnapRotation();
            ApplyTransform();
        }

        // Places the camera at the configured starting angle and distance from initialFocusPoint.
        [ContextMenu("Reset Framing")]
        public void ResetFraming()
        {
            pivotPoint = initialFocusPoint;
            yaw = initialYaw;
            pitch = initialPitch;
            distance = Mathf.Clamp(initialDistance, minZoomDistance, maxZoomDistance);

            // Abandon any snap in flight, otherwise it would drag yaw off the value just set.
            isSnapping = false;

            ApplyTransform();
        }

        // Centres the camera on gridManagerRef's grid and pulls back far enough to see all of it.
        // yaw and pitch are left alone - those are angle preferences, not size-dependent.
        [ContextMenu("Auto Frame Grid")]
        public void AutoFrameGrid()
        {
            if (gridManagerRef == null)
            {
                Debug.LogWarning(
                    $"{nameof(TacticsCameraController)}: no {nameof(gridManagerRef)} assigned, falling back to " +
                    $"{nameof(initialFocusPoint)}/{nameof(initialDistance)}.",
                    this);

                pivotPoint = initialFocusPoint;
                distance = Mathf.Clamp(initialDistance, minZoomDistance, maxZoomDistance);
                ApplyTransform();
                return;
            }

            float tileSize = gridManagerRef.TileSize;
            int width = gridManagerRef.GridWidth;
            int depth = gridManagerRef.GridDepth;

            // Tile *centres* run from 0 to (count-1) * TileSize, so the midpoint is half of that -
            // not count * TileSize / 2, which would sit half a tile past the far edge. Offset by
            // the grid object's own position, exactly as GridToWorldPosition does.
            Vector3 centre = new Vector3(
                (width - 1) * tileSize * 0.5f,
                0f,
                (depth - 1) * tileSize * 0.5f) + gridManagerRef.transform.position;

            // distance scales with the longer side, so a 20x20 board pulls back twice as far as a
            // 10x10 one and both read at the same relative size. The longer side is what decides
            // it: fitting that guarantees the shorter one fits too.
            //
            // framingPadding is a plain multiplier rather than a true fit, which would need the
            // camera's FOV and aspect - roughly (span / 2) / tan(fov / 2), adjusted for pitch.
            // A tunable constant is easier to art-direct and does not break if the FOV changes.
            //
            // TODO: ignores elevation, which is safe while grids are flat. Once Height varies
            // significantly, tall terrain near the camera will clip or occlude the board and this
            // should also factor in the grid's height range (max Height - min Height) * HeightStep,
            // pulling back further for vertical maps.
            float longestSpan = Mathf.Max(width, depth) * tileSize;

            pivotPoint = centre;

            // Clamped to the zoom limits so auto-framing cannot put the camera somewhere the
            // player could not scroll back to. Large maps may need maxZoomDistance raised.
            distance = Mathf.Clamp(longestSpan * framingPadding, minZoomDistance, maxZoomDistance);

            ApplyTransform();
        }

        // Moves the pivot to look at a specific world position, keeping the current angle/zoom.
        // INTENTIONAL API: unused today, the hook for camera-follows-active-unit.
        public void FocusOn(Vector3 worldPoint)
        {
            pivotPoint = worldPoint;
            ApplyTransform();
        }

        private void HandlePan()
        {
            // ClampMagnitude, not per-axis clamping: without it a diagonal press moves sqrt(2)
            // times faster than a straight one.
            Vector2 input = Vector2.ClampMagnitude(CameraInput.GetPanInput(), 1f);

            if (input == Vector2.zero)
            {
                return;
            }

            // Basis built from yaw alone rather than transform.forward/right. Using the transform
            // would drag the camera's pitch into the movement (panning would slide you into the
            // ground) and would degenerate to a zero vector when looking straight down.
            Quaternion flatRotation = Quaternion.Euler(0f, yaw, 0f);
            Vector3 forward = flatRotation * Vector3.forward;
            Vector3 right = flatRotation * Vector3.right;

            pivotPoint += (right * input.x + forward * input.y) * (panSpeed * Time.deltaTime);
        }

        private void HandleZoom()
        {
            float notches = CameraInput.GetScrollNotches();

            if (Mathf.Approximately(notches, 0f))
            {
                return;
            }

            // Changing distance from the pivot rather than sliding along the camera's local Z, so
            // zoom reads as "closer to what I am looking at" and cannot overshoot past the pivot.
            distance = Mathf.Clamp(distance - notches * zoomSpeed, minZoomDistance, maxZoomDistance);
        }

        // pitch is never touched here - it stays at whatever ResetFraming set.
        private void HandleSnapRotation()
        {
            // Input is only read when idle, which is what drops presses mid-snap. Mashing Q/E
            // cannot queue up rotations; the extra presses are simply discarded.
            if (isSnapping)
            {
                AdvanceSnap();
                return;
            }

            int direction = CameraInput.GetYawSnapInput();

            if (direction == 0)
            {
                return;
            }

            snapStartYaw = yaw;
            snapTargetYaw = yaw + direction * 90f;
            snapElapsed = 0f;
            isSnapping = true;
        }

        private void AdvanceSnap()
        {
            snapElapsed += Time.deltaTime;

            // Land exactly on the target rather than easing asymptotically toward it. A plain
            // Lerp-toward-target each frame never quite arrives, which would let 90-degree steps
            // accumulate error over a session and leave the grid slightly off-axis.
            if (snapElapsed >= snapRotationDuration)
            {
                // Wrapped so yaw does not grow without bound over a long session.
                yaw = Mathf.Repeat(snapTargetYaw, 360f);
                isSnapping = false;
                return;
            }

            // Mathf.Lerp, not LerpAngle: start and target are a known 90 apart and the direction
            // is deliberate. LerpAngle takes the shortest path, which discards that intent.
            float t = snapElapsed / snapRotationDuration;
            yaw = Mathf.Lerp(snapStartYaw, snapTargetYaw, Mathf.SmoothStep(0f, 1f, t));
        }

        // Rebuilds position and rotation from pivot/yaw/pitch/distance. The one place the
        // transform is written.
        private void ApplyTransform()
        {
            Quaternion rotation = Quaternion.Euler(pitch, yaw, 0f);
            Vector3 position = pivotPoint - rotation * Vector3.forward * distance;

            transform.SetPositionAndRotation(position, rotation);
        }
    }
}
