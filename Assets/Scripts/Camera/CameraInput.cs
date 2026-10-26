using UnityEngine;

// Same conditional-compilation pattern as Grid/GridInput.cs: this project runs Active Input
// Handling = "Input System Package (New)", under which the legacy UnityEngine.Input class throws
// at runtime. All #if plumbing for camera input lives here.
#if ENABLE_INPUT_SYSTEM
using UnityEngine.InputSystem;
#endif

namespace Tactica.Camera
{
    public static class CameraInput
    {
        // WASD and arrow keys as (-1..1, -1..1). X is strafe, Y is forward.
        public static Vector2 GetPanInput()
        {
#if ENABLE_INPUT_SYSTEM
            Keyboard keyboard = Keyboard.current;

            if (keyboard == null)
            {
                return Vector2.zero;
            }

            float x = 0f;
            float y = 0f;

            if (keyboard.aKey.isPressed || keyboard.leftArrowKey.isPressed) x -= 1f;
            if (keyboard.dKey.isPressed || keyboard.rightArrowKey.isPressed) x += 1f;
            if (keyboard.sKey.isPressed || keyboard.downArrowKey.isPressed) y -= 1f;
            if (keyboard.wKey.isPressed || keyboard.upArrowKey.isPressed) y += 1f;

            return new Vector2(x, y);
#elif ENABLE_LEGACY_INPUT_MANAGER
            // The default Horizontal/Vertical axes already cover both WASD and arrows.
            return new Vector2(Input.GetAxisRaw("Horizontal"), Input.GetAxisRaw("Vertical"));
#else
            return Vector2.zero;
#endif
        }

        // Scroll in wheel notches, normalised across backends.
        //
        // The Input System has two scales, chosen in its settings: UniformAcrossAllPlatforms (the
        // default since 1.8) already reports about 1 per notch, while the older platform-specific
        // mode reports raw OS ticks (120 per notch on Windows). Dividing by 120 unconditionally made
        // zoom 120x too slow under the uniform mode. The legacy axis reports ~0.1 per notch.
        public static float GetScrollNotches()
        {
#if ENABLE_INPUT_SYSTEM
            if (Mouse.current == null)
            {
                return 0f;
            }

            float scroll = Mouse.current.scroll.ReadValue().y;
            bool uniform = InputSystem.settings.scrollDeltaBehavior ==
                           InputSettings.ScrollDeltaBehavior.UniformAcrossAllPlatforms;

            return uniform ? scroll : scroll / 120f;
#elif ENABLE_LEGACY_INPUT_MANAGER
            return Input.GetAxis("Mouse ScrollWheel") * 10f;
#else
            return 0f;
#endif
        }

        // -1 for Q (counterclockwise), +1 for E (clockwise), 0 for neither or both.
        // Edge-triggered, not held: one press is one snap.
        public static int GetYawSnapInput()
        {
#if ENABLE_INPUT_SYSTEM
            Keyboard keyboard = Keyboard.current;

            if (keyboard == null)
            {
                return 0;
            }

            int direction = 0;

            if (keyboard.qKey.wasPressedThisFrame) direction -= 1;
            if (keyboard.eKey.wasPressedThisFrame) direction += 1;

            return direction;
#elif ENABLE_LEGACY_INPUT_MANAGER
            int direction = 0;

            if (Input.GetKeyDown(KeyCode.Q)) direction -= 1;
            if (Input.GetKeyDown(KeyCode.E)) direction += 1;

            return direction;
#else
            return 0;
#endif
        }
    }
}
