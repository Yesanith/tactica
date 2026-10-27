using System.Collections.Generic;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

// Project uses Active Input Handling = "Input System Package (New)", under which the legacy
// UnityEngine.Input class throws at runtime. Both backends are handled here so the #if plumbing
// exists in exactly one place.
#if ENABLE_INPUT_SYSTEM
using UnityEngine.InputSystem;
#endif

namespace Tactica.Grid
{
    public static class GridInput
    {
        // Reused across calls: one UI raycast per frame otherwise allocates a results list and
        // an event-data object every frame, for the whole session.
        private static readonly List<RaycastResult> uiRaycastResults = new List<RaycastResult>();
        private static PointerEventData uiPointerData;

        // True when the cursor is over an interactive UI element, meaning the UI has already
        // claimed this click and world handlers must stay out of it.
        //
        // Performs a FRESH raycast rather than calling EventSystem.IsPointerOverGameObject(). That
        // method answers from the input module's cached pointer state, which brings two problems:
        //
        //   1. The parameterless overload asks about pointer id -1 (mouse-left, a legacy
        //      PointerInputModule constant). InputSystemUIInputModule assigns its own pointer ids,
        //      so the cached answer can be about a pointer that does not exist - reporting "not
        //      over UI" while the cursor sits on a button.
        //   2. The cache is filled during EventSystem.Update, so any caller running earlier in the
        //      same frame reads a stale value.
        //
        // Raycasting here removes both: the answer is computed from the pointer position at the
        // moment of asking, independent of module internals and of script execution order.
        //
        // Only GraphicRaycaster hits count. RaycastAll queries every registered raycaster, and a
        // PhysicsRaycaster on the camera would otherwise report world geometry as "UI" and block
        // every board click.
        //
        // Still mouse-only. Touch would need one call per active finger, using each finger's
        // position rather than the mouse's.
        public static bool IsPointerOverUI()
        {
            EventSystem eventSystem = EventSystem.current;

            if (eventSystem == null)
            {
                // No EventSystem means no UI is consuming anything.
                return false;
            }

            if (!TryGetMouseScreenPosition(out Vector3 screenPosition))
            {
                return false;
            }

            if (uiPointerData == null)
            {
                uiPointerData = new PointerEventData(eventSystem);
            }

            // PointerEventData.position is a Vector2; the screen position carries an unused z.
            uiPointerData.position = new Vector2(screenPosition.x, screenPosition.y);

            uiRaycastResults.Clear();
            eventSystem.RaycastAll(uiPointerData, uiRaycastResults);

            for (int i = 0; i < uiRaycastResults.Count; i++)
            {
                if (uiRaycastResults[i].module is GraphicRaycaster)
                {
                    return true;
                }
            }

            return false;
        }

        // False when no mouse is attached (Mouse.current is null) or no backend is available.
        public static bool TryGetMouseScreenPosition(out Vector3 screenPosition)
        {
#if ENABLE_INPUT_SYSTEM
            if (Mouse.current == null)
            {
                screenPosition = default;
                return false;
            }

            Vector2 mousePosition = Mouse.current.position.ReadValue();
            screenPosition = new Vector3(mousePosition.x, mousePosition.y, 0f);
            return true;
#elif ENABLE_LEGACY_INPUT_MANAGER
            screenPosition = Input.mousePosition;
            return true;
#else
            screenPosition = default;
            return false;
#endif
        }

        public static bool WasLeftClickThisFrame()
        {
#if ENABLE_INPUT_SYSTEM
            return Mouse.current != null && Mouse.current.leftButton.wasPressedThisFrame;
#elif ENABLE_LEGACY_INPUT_MANAGER
            return Input.GetMouseButtonDown(0);
#else
            return false;
#endif
        }

        // ---------------------------------------------------------------------
        // Gamepad
        // ---------------------------------------------------------------------

        // What a pad button means, so callers never name physical buttons. Xbox layout: A confirm,
        // B cancel, Y Act, X Move, Start Wait (or "begin" where there is no turn), View the fight's
        // own menu (Jack Out). The shoulders turn the camera on the board and page through the crew
        // in menus; the triggers switch tabs.
        public enum PadButton
        {
            Confirm,
            Cancel,
            Act,
            Move,
            Wait,
            View,
            ShoulderLeft,
            ShoulderRight,
            TriggerLeft,
            TriggerRight,
        }

        private const float StickThreshold = 0.5f;
        private const float MouseMoveThreshold = 3f;
        private const float RepeatDelay = 0.32f;
        private const float RepeatInterval = 0.11f;

        private static int deviceFrame = -1;
        private static bool gamepadActive;

        private static int stepFrame = -1;
        private static Vector2Int stepThisFrame;
        private static Vector2Int heldDirection;
        private static float nextRepeat;

        // Whichever of pad and mouse was touched last: the pad drives a tile cursor and focuses
        // menus, the mouse points. Switching is automatic - a pad press takes over, moving or
        // clicking the mouse hands it back. Computed once per frame, so every caller agrees.
        public static bool GamepadActive
        {
            get
            {
                RefreshActiveDevice();
                return gamepadActive;
            }
        }

        private static void RefreshActiveDevice()
        {
            if (deviceFrame == Time.frameCount)
            {
                return;
            }

            deviceFrame = Time.frameCount;
#if ENABLE_INPUT_SYSTEM
            Gamepad pad = Gamepad.current;

            if (pad != null && PadTouchedThisFrame(pad))
            {
                gamepadActive = true;
            }

            Mouse mouse = Mouse.current;

            if (mouse != null && (mouse.delta.ReadValue().sqrMagnitude > MouseMoveThreshold * MouseMoveThreshold
                                  || mouse.leftButton.wasPressedThisFrame || mouse.rightButton.wasPressedThisFrame))
            {
                gamepadActive = false;
            }
#endif
        }

#if ENABLE_INPUT_SYSTEM
        // Every button, and every stick or d-pad direction (they are buttons too, pressed at half
        // travel), so nudging a stick counts as picking the pad up.
        private static bool PadTouchedThisFrame(Gamepad pad)
        {
            foreach (InputControl control in pad.allControls)
            {
                if (control is UnityEngine.InputSystem.Controls.ButtonControl button && button.wasPressedThisFrame)
                {
                    return true;
                }
            }

            return false;
        }
#endif

        public static bool WasPadPressed(PadButton button)
        {
#if ENABLE_INPUT_SYSTEM
            Gamepad pad = Gamepad.current;

            if (pad == null)
            {
                return false;
            }

            switch (button)
            {
                case PadButton.Confirm: return pad.buttonSouth.wasPressedThisFrame;
                case PadButton.Cancel: return pad.buttonEast.wasPressedThisFrame;
                case PadButton.Act: return pad.buttonNorth.wasPressedThisFrame;
                case PadButton.Move: return pad.buttonWest.wasPressedThisFrame;
                case PadButton.Wait: return pad.startButton.wasPressedThisFrame;
                case PadButton.View: return pad.selectButton.wasPressedThisFrame;
                case PadButton.ShoulderLeft: return pad.leftShoulder.wasPressedThisFrame;
                case PadButton.ShoulderRight: return pad.rightShoulder.wasPressedThisFrame;
                case PadButton.TriggerLeft: return pad.leftTrigger.wasPressedThisFrame;
                case PadButton.TriggerRight: return pad.rightTrigger.wasPressedThisFrame;
            }
#endif
            return false;
        }

        // One step of the tile cursor in screen terms (x right, y up), from the d-pad or the left
        // stick: a step on the press, then after a pause a step every RepeatInterval while held -
        // a menu's key repeat, so crossing the board does not take twenty presses.
        public static Vector2Int GetCursorStep()
        {
            if (stepFrame == Time.frameCount)
            {
                return stepThisFrame;
            }

            stepFrame = Time.frameCount;
            stepThisFrame = Vector2Int.zero;
#if ENABLE_INPUT_SYSTEM
            Gamepad pad = Gamepad.current;

            if (pad == null)
            {
                return stepThisFrame;
            }

            Vector2 raw = pad.dpad.ReadValue() + pad.leftStick.ReadValue();
            Vector2Int direction = Vector2Int.zero;

            if (raw.magnitude >= StickThreshold)
            {
                direction = Mathf.Abs(raw.x) > Mathf.Abs(raw.y)
                    ? new Vector2Int(raw.x > 0f ? 1 : -1, 0)
                    : new Vector2Int(0, raw.y > 0f ? 1 : -1);
            }

            if (direction == Vector2Int.zero)
            {
                heldDirection = Vector2Int.zero;
                return stepThisFrame;
            }

            if (direction != heldDirection)
            {
                heldDirection = direction;
                nextRepeat = Time.unscaledTime + RepeatDelay;
                stepThisFrame = direction;
            }
            else if (Time.unscaledTime >= nextRepeat)
            {
                nextRepeat = Time.unscaledTime + RepeatInterval;
                stepThisFrame = direction;
            }
#endif
            return stepThisFrame;
        }

        // Whether a menu holds the pad's focus. While it does, the stick and A belong to the menu,
        // not to the board.
        public static bool IsUIFocused()
        {
            EventSystem eventSystem = EventSystem.current;
            GameObject selected = eventSystem != null ? eventSystem.currentSelectedGameObject : null;
            return selected != null && selected.activeInHierarchy;
        }
    }
}
