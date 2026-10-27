using TMPro;
using UnityEngine;
using UnityEngine.UI;
using Tactica.Grid;

namespace Tactica.UI
{
    // The strip along the bottom that says what each pad button does right now ("A Select  B Back
    // LB/RB Crew"). Whoever owns the screen states its prompts every frame through Set; a frame
    // nobody does, or with the mouse in hand, the strip hides. Polled rather than pushed because
    // what the buttons mean changes with the mode (choosing a command, aiming, a menu), and every
    // owner already runs per frame.
    public class PromptBar : MonoBehaviour
    {
        private const int SortingOrder = 200;

        private static PromptBar instance;

        private RectTransform strip;
        private TextMeshProUGUI label;
        private string pending;
        private int pendingFrame = -1;

        // Xbox face-button colours, so "A" reads as the green button at a glance.
        public static string A => Glyph("A", "#6CE06C");
        public static string B => Glyph("B", "#FF5A5A");
        public static string X => Glyph("X", "#4DA6FF");
        public static string Y => Glyph("Y", "#FFD24D");

        public static string Glyph(string button, string colour = null)
        {
            return $"<b><color={colour ?? MenuBuilder.AccentHex}>{button}</color></b>";
        }

        // Prompt pairs, e.g. Join(A, "Select", B, "Back"): button then what it does. A pair whose
        // button is null is left out, so optional prompts can be written inline.
        public static string Join(params string[] pairs)
        {
            System.Text.StringBuilder text = new System.Text.StringBuilder();

            for (int i = 0; i + 1 < pairs.Length; i += 2)
            {
                if (pairs[i] == null)
                {
                    continue;
                }

                if (text.Length > 0)
                {
                    text.Append("      ");
                }

                text.Append(pairs[i]).Append(' ').Append(pairs[i + 1]);
            }

            return text.ToString();
        }

        // Shows text this frame. Later calls in the same frame win.
        public static void Set(string text)
        {
            if (instance == null)
            {
                Create();
            }

            instance.pending = text;
            instance.pendingFrame = Time.frameCount;
        }

        private static void Create()
        {
            Canvas canvas = MenuBuilder.CreateOverlayCanvas("PromptCanvas", null, SortingOrder);
            instance = canvas.gameObject.AddComponent<PromptBar>();
            instance.Build(canvas.transform);
        }

        private void Build(Transform parent)
        {
            strip = MenuBuilder.CreateRect(parent, "PromptBar");
            strip.anchorMin = strip.anchorMax = strip.pivot = new Vector2(0.5f, 0f);
            strip.anchoredPosition = new Vector2(0f, 12f);
            strip.sizeDelta = new Vector2(900f, 40f);

            Image background = strip.gameObject.AddComponent<Image>();
            background.raycastTarget = false;
            NeonTheme.SkinPanel(background, new Color(0.01f, 0.02f, 0.05f, 0.88f));

            RectTransform labelRect = MenuBuilder.CreateRect(strip, "Label");
            MenuBuilder.Stretch(labelRect);

            label = labelRect.gameObject.AddComponent<TextMeshProUGUI>();
            label.fontSize = 20f;
            label.alignment = TextAlignmentOptions.Center;
            label.color = NeonTheme.Text;
            label.raycastTarget = false;
            label.textWrappingMode = TextWrappingModes.NoWrap;
            NeonTheme.Style(label);

            strip.gameObject.SetActive(false);
        }

        // LateUpdate, after every owner's Update has stated its prompts for the frame.
        private void LateUpdate()
        {
            bool show = GridInput.GamepadActive && pendingFrame == Time.frameCount && !string.IsNullOrEmpty(pending);

            if (strip.gameObject.activeSelf != show)
            {
                strip.gameObject.SetActive(show);
            }

            if (show && label.text != pending)
            {
                label.text = pending;

                // Width follows the text, so a short prompt is a small strip.
                strip.sizeDelta = new Vector2(label.GetPreferredValues(pending).x + 64f, strip.sizeDelta.y);
            }
        }

        private void OnDestroy()
        {
            if (instance == this)
            {
                instance = null;
            }
        }
    }
}
