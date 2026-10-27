using System.Collections.Generic;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;
using Tactica.Grid;

namespace Tactica.UI
{
    // Keeps a gamepad's focus on a menu. uGUI navigates between Selectables on its own once one
    // is selected, but nothing selects the first, and the menus rebuild their buttons on every
    // click - which destroys the selected one. So, once a frame for the menu on screen: with the
    // pad in hand, make sure something in it is selected, preferring the button at the position
    // the last one held (Buy on the third shop row stays on the third row); with the mouse, select
    // nothing, so no button sits highlighted under a pointer that is elsewhere.
    //
    // screen names what is shown: the position carries over only while it stays the same (the
    // shop rebuilt after a purchase), and a new screen starts on its first button.
    //
    // Buttons with navigation off (SkipPadFocus) are left out: they have a shoulder or trigger
    // shortcut instead, and the stick should land on the screen's content, not its page flips.
    // The focused button is kept scrolled into view on a screen taller than the window.
    public static class PadFocus
    {
        private static readonly List<Selectable> candidates = new List<Selectable>();
        private static Transform lastRoot;
        private static string lastScreen;
        private static int lastIndex;
        private static readonly Vector3[] corners = new Vector3[4];

        public static void Keep(Transform root, string screen = null)
        {
            EventSystem eventSystem = EventSystem.current;

            if (eventSystem == null || root == null)
            {
                return;
            }

            GameObject selected = eventSystem.currentSelectedGameObject;
            bool selectionInRoot = selected != null && selected.activeInHierarchy && selected.transform.IsChildOf(root);

            if (!GridInput.GamepadActive)
            {
                if (selectionInRoot)
                {
                    eventSystem.SetSelectedGameObject(null);
                }

                return;
            }

            Collect(root);

            bool sameScreen = lastRoot == root && lastScreen == screen;

            if (selectionInRoot && sameScreen && selected.TryGetComponent(out Selectable current) && current.IsInteractable())
            {
                lastIndex = candidates.IndexOf(current);
                ScrollIntoView((RectTransform)selected.transform);
                return;
            }

            if (candidates.Count == 0)
            {
                return;
            }

            int index = sameScreen ? Mathf.Clamp(lastIndex, 0, candidates.Count - 1) : 0;
            lastRoot = root;
            lastScreen = screen;
            lastIndex = index;
            eventSystem.SetSelectedGameObject(candidates[index].gameObject);
        }

        // Drops the pad's focus, e.g. when a menu closes and the board takes the stick back. The
        // remembered position goes too, so the menu opens on its first button next time.
        public static void Release(Transform root)
        {
            if (root != null && lastRoot != null && lastRoot.IsChildOf(root))
            {
                lastRoot = null;
            }

            EventSystem eventSystem = EventSystem.current;
            GameObject selected = eventSystem != null ? eventSystem.currentSelectedGameObject : null;

            if (selected != null && root != null && selected.transform.IsChildOf(root))
            {
                eventSystem.SetSelectedGameObject(null);
            }
        }

        // Mouse-only from here on: still clickable, never focused by the pad.
        public static void SkipPadFocus(Selectable selectable)
        {
            if (selectable != null)
            {
                selectable.navigation = new Navigation { mode = Navigation.Mode.None };
            }
        }

        // Nudges a ScrollRect so the item sits inside its viewport, with a little margin. Only
        // while the screen actually scrolls; a short screen is centred and left alone.
        private static void ScrollIntoView(RectTransform item)
        {
            ScrollRect scroll = item.GetComponentInParent<ScrollRect>();

            if (scroll == null || !scroll.vertical || scroll.content == null)
            {
                return;
            }

            RectTransform viewport = scroll.viewport != null ? scroll.viewport : (RectTransform)scroll.transform;
            item.GetWorldCorners(corners);

            float top = viewport.InverseTransformPoint(corners[1]).y;
            float bottom = viewport.InverseTransformPoint(corners[0]).y;
            Rect view = viewport.rect;
            const float margin = 24f;
            float shift = 0f;

            if (top > view.yMax - margin)
            {
                shift = top - (view.yMax - margin);
            }
            else if (bottom < view.yMin + margin)
            {
                shift = bottom - (view.yMin + margin);
            }

            if (shift != 0f)
            {
                // Content moves down to bring a high item down, up for a low one.
                Vector2 position = scroll.content.anchoredPosition;
                scroll.content.anchoredPosition = new Vector2(position.x, position.y - shift);
            }
        }

        private static void Collect(Transform root)
        {
            candidates.Clear();

            foreach (Selectable selectable in root.GetComponentsInChildren<Selectable>())
            {
                if (selectable.IsInteractable() && selectable.gameObject.activeInHierarchy
                    && selectable.navigation.mode != Navigation.Mode.None)
                {
                    candidates.Add(selectable);
                }
            }
        }
    }
}
