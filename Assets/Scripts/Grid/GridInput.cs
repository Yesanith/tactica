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
    }
}
