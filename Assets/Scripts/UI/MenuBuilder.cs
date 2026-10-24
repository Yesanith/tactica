using TMPro;
using UnityEngine;
using UnityEngine.Events;
using UnityEngine.UI;

namespace Tactica.UI
{
    // Code-built uGUI primitives for the prototype menus: panels, columns, rows, labels, buttons.
    //
    // Same reasoning as UnitStatusBar building its bars in code - nothing authored, so the menus
    // exist in any scene the campaign flow boots into. Deliberately plain: grey boxes and white
    // text on black. Styling belongs to a real UI pass, not to this.
    public static class MenuBuilder
    {
        public static readonly Color ButtonColor = new Color(0.22f, 0.22f, 0.24f);
        public static readonly Color TextColor = Color.white;
        public static readonly Color DimTextColor = new Color(0.65f, 0.65f, 0.68f);

        public static Canvas CreateOverlayCanvas(string name, Transform parent, int sortingOrder)
        {
            GameObject canvasObject = new GameObject(name);
            canvasObject.transform.SetParent(parent, false);

            Canvas canvas = canvasObject.AddComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;

            // Above the combat HUD, so a full-screen menu covers it rather than interleaving.
            canvas.sortingOrder = sortingOrder;

            CanvasScaler scaler = canvasObject.AddComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(1920f, 1080f);
            scaler.matchWidthOrHeight = 0.5f;

            // Required for clicks - and also what makes GridInput.IsPointerOverUI see these panels,
            // so the board ignores clicks that land on a menu.
            canvasObject.AddComponent<GraphicRaycaster>();

            return canvas;
        }

        // A full-screen opaque panel. Its Image is a raycast target on purpose: it swallows every
        // click, so nothing on the board underneath reacts while a menu is up.
        public static RectTransform CreateFullScreenPanel(Transform parent, string name, Color color)
        {
            RectTransform rect = CreateRect(parent, name);
            Stretch(rect);

            Image image = rect.gameObject.AddComponent<Image>();
            image.color = color;

            return rect;
        }

        // A centred vertical stack that sizes itself to its content.
        public static RectTransform CreateColumn(Transform parent, string name, float width, float spacing)
        {
            RectTransform rect = CreateRect(parent, name);
            rect.anchorMin = rect.anchorMax = new Vector2(0.5f, 0.5f);
            rect.pivot = new Vector2(0.5f, 0.5f);
            rect.sizeDelta = new Vector2(width, 0f);

            AddVerticalLayout(rect.gameObject, spacing);

            ContentSizeFitter fitter = rect.gameObject.AddComponent<ContentSizeFitter>();
            fitter.verticalFit = ContentSizeFitter.FitMode.PreferredSize;

            return rect;
        }

        public static void AddVerticalLayout(GameObject target, float spacing, int padding = 0)
        {
            VerticalLayoutGroup layout = target.AddComponent<VerticalLayoutGroup>();
            layout.spacing = spacing;
            layout.padding = new RectOffset(padding, padding, padding, padding);
            layout.childAlignment = TextAnchor.UpperCenter;
            layout.childControlWidth = true;
            layout.childControlHeight = true;
            layout.childForceExpandWidth = true;
            layout.childForceExpandHeight = false;
        }

        // A horizontal strip inside a column. Children with flexibleWidth take the slack, so a
        // label beside fixed-width buttons fills whatever is left.
        public static RectTransform AddRow(Transform parent, float height, float spacing = 8f)
        {
            RectTransform rect = CreateRect(parent, "Row");
            SetHeight(rect.gameObject, height);

            HorizontalLayoutGroup layout = rect.gameObject.AddComponent<HorizontalLayoutGroup>();
            layout.spacing = spacing;
            layout.childAlignment = TextAnchor.MiddleLeft;
            layout.childControlWidth = true;
            layout.childControlHeight = true;
            layout.childForceExpandWidth = false;
            layout.childForceExpandHeight = true;

            return rect;
        }

        public static TextMeshProUGUI AddText(
            Transform parent,
            string text,
            float fontSize = 26f,
            TextAlignmentOptions alignment = TextAlignmentOptions.Center,
            float height = -1f,
            Color? color = null)
        {
            RectTransform rect = CreateRect(parent, "Text");

            TextMeshProUGUI label = rect.gameObject.AddComponent<TextMeshProUGUI>();
            label.text = text;
            label.fontSize = fontSize;
            label.alignment = alignment;
            label.color = color ?? TextColor;
            label.raycastTarget = false;
            label.textWrappingMode = TextWrappingModes.Normal;

            LayoutElement element = rect.gameObject.AddComponent<LayoutElement>();
            element.flexibleWidth = 1f;

            if (height > 0f)
            {
                element.preferredHeight = height;
            }

            return label;
        }

        // width <= 0 lets the button stretch with its layout instead of taking a fixed width.
        public static Button AddButton(
            Transform parent,
            string label,
            UnityAction onClick,
            float width = -1f,
            float height = 52f,
            bool interactable = true)
        {
            RectTransform rect = CreateRect(parent, $"Button_{label}");

            Image image = rect.gameObject.AddComponent<Image>();
            image.color = ButtonColor;

            Button button = rect.gameObject.AddComponent<Button>();
            button.targetGraphic = image;
            button.interactable = interactable;

            if (onClick != null)
            {
                button.onClick.AddListener(onClick);
            }

            LayoutElement element = rect.gameObject.AddComponent<LayoutElement>();
            element.preferredHeight = height;
            element.minHeight = height;

            if (width > 0f)
            {
                element.preferredWidth = width;
                element.minWidth = width;
            }
            else
            {
                element.flexibleWidth = 1f;
            }

            RectTransform labelRect = CreateRect(rect, "Label");
            Stretch(labelRect);

            TextMeshProUGUI text = labelRect.gameObject.AddComponent<TextMeshProUGUI>();
            text.text = label;
            text.fontSize = 24f;
            text.alignment = TextAlignmentOptions.Center;
            text.color = interactable ? TextColor : DimTextColor;
            text.raycastTarget = false;
            text.textWrappingMode = TextWrappingModes.NoWrap;
            text.overflowMode = TextOverflowModes.Ellipsis;

            return button;
        }

        public static void AddSpacer(Transform parent, float height)
        {
            RectTransform rect = CreateRect(parent, "Spacer");
            SetHeight(rect.gameObject, height);
        }

        // Removes every child. Deactivated first because Destroy is deferred to the end of the
        // frame, and a layout group would otherwise still lay out the dying children alongside
        // the replacements for that frame.
        public static void Clear(Transform parent)
        {
            for (int i = parent.childCount - 1; i >= 0; i--)
            {
                GameObject child = parent.GetChild(i).gameObject;
                child.SetActive(false);
                Object.Destroy(child);
            }
        }

        public static RectTransform CreateRect(Transform parent, string name)
        {
            GameObject gameObject = new GameObject(name, typeof(RectTransform));
            RectTransform rect = (RectTransform)gameObject.transform;
            rect.SetParent(parent, false);
            return rect;
        }

        public static void Stretch(RectTransform rect)
        {
            rect.anchorMin = Vector2.zero;
            rect.anchorMax = Vector2.one;
            rect.offsetMin = Vector2.zero;
            rect.offsetMax = Vector2.zero;
        }

        private static void SetHeight(GameObject target, float height)
        {
            LayoutElement element = target.AddComponent<LayoutElement>();
            element.preferredHeight = height;
            element.minHeight = height;
        }
    }
}
