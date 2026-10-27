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
        // Gamepad zoom: notches per second with a trigger held all the way.
        private const float TriggerZoomRate = 6f;

        // Below this the right stick is resting, not pushed.
        private const float StickDeadZone = 0.2f;

        // WASD and arrow keys, or the right stick, as (-1..1, -1..1). X is strafe, Y is forward.
        public static Vector2 GetPanInput()
        {
#if ENABLE_INPUT_SYSTEM
            Keyboard keyboard = Keyboard.current;
            Vector2 stick = Gamepad.current != null ? Gamepad.current.rightStick.ReadValue() : Vector2.zero;

            if (stick.magnitude < StickDeadZone)
            {
                stick = Vector2.zero;
            }

            if (keyboard == null)
            {
                return stick;
            }

            float x = stick.x;
            float y = stick.y;

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
        // The triggers zoom too: right in, left out, at a steady rate while held.
        public static float GetScrollNotches()
        {
#if ENABLE_INPUT_SYSTEM
            Gamepad pad = Gamepad.current;
            float triggers = pad != null ? (pad.rightTrigger.ReadValue() - pad.leftTrigger.ReadValue()) * TriggerZoomRate * Time.unscaledDeltaTime : 0f;

            if (Mouse.current == null)
            {
                return triggers;
            }

            float scroll = Mouse.current.scroll.ReadValue().y;
            bool uniform = InputSystem.settings.scrollDeltaBehavior ==
                           InputSettings.ScrollDeltaBehavior.UniformAcrossAllPlatforms;

            return (uniform ? scroll : scroll / 120f) + triggers;
#elif ENABLE_LEGACY_INPUT_MANAGER
            return Input.GetAxis("Mouse ScrollWheel") * 10f;
#else
            return 0f;
#endif
        }

        // -1 for Q or the left shoulder (counterclockwise), +1 for E or the right shoulder
        // (clockwise), 0 for neither or both. Edge-triggered, not held: one press is one snap.
        public static int GetYawSnapInput()
        {
#if ENABLE_INPUT_SYSTEM
            Keyboard keyboard = Keyboard.current;
            int direction = 0;

            if (Tactica.Grid.GridInput.WasPadPressed(Tactica.Grid.GridInput.PadButton.ShoulderLeft)) direction -= 1;
            if (Tactica.Grid.GridInput.WasPadPressed(Tactica.Grid.GridInput.PadButton.ShoulderRight)) direction += 1;

            if (keyboard == null)
            {
                return direction;
            }

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
