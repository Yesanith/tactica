using UnityEngine;

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
