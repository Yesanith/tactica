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

        // The few colours the prototype uses to carry meaning - not styling. A toggle that is on
        // (deployed, current job), the button a screen is about, and anything that cannot be
        // undone. Text accents: section headers, money, story beats, warnings.
        public static readonly Color SelectedColor = new Color(0.08f, 0.38f, 0.42f);
        public static readonly Color PrimaryColor = new Color(0.12f, 0.3f, 0.5f);
        public static readonly Color DangerColor = new Color(0.45f, 0.13f, 0.15f);
        public static readonly Color AccentTextColor = new Color(0.3f, 0.95f, 0.95f);
        public static readonly Color CreditsTextColor = new Color(1f, 0.85f, 0.3f);
        public static readonly Color StoryTextColor = new Color(1f, 0.55f, 0.85f);
        public static readonly Color WarningTextColor = new Color(1f, 0.8f, 0.35f);
        public static readonly Color DangerTextColor = new Color(1f, 0.35f, 0.35f);
        public static readonly Color FaintTextColor = new Color(0.42f, 0.42f, 0.44f);

        // The same colours as rich-text tags, for colouring part of a label. Derived from the
        // Colors above so the two can never drift apart.
        public static readonly string DimHex = Hex(DimTextColor);
        public static readonly string FaintHex = Hex(FaintTextColor);
        public static readonly string AccentHex = Hex(AccentTextColor);
        public static readonly string CreditsHex = Hex(CreditsTextColor);
        public static readonly string WarningHex = Hex(WarningTextColor);
        public static readonly string DangerHex = Hex(DangerTextColor);

        private static string Hex(Color color)
        {
            return "#" + ColorUtility.ToHtmlStringRGB(color);
        }

        // The colour argument for an on/off button: teal when on, plain when off.
        public static Color? On(bool selected)
        {
            return selected ? SelectedColor : (Color?)null;
        }

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

        // A scrollable version of CreateColumn: the column is the content of a full-size ScrollRect.
        // Short screens sit centred as before; a screen taller than the window scrolls with the
        // mouse wheel instead of running off the top and bottom.
        public static RectTransform CreateScrollColumn(Transform parent, string name, float width, float spacing, out ScrollRect scroll)
        {
            RectTransform viewport = CreateRect(parent, name + "Viewport");
            Stretch(viewport);
            viewport.gameObject.AddComponent<RectMask2D>();

            RectTransform content = CreateColumn(viewport, name, width, spacing);
            content.anchorMin = content.anchorMax = new Vector2(0.5f, 1f);
            content.pivot = new Vector2(0.5f, 1f);

            VerticalLayoutGroup layout = content.GetComponent<VerticalLayoutGroup>();
            layout.padding = new RectOffset(0, 0, 32, 32);

            scroll = viewport.gameObject.AddComponent<ScrollRect>();
            scroll.content = content;
            scroll.viewport = viewport;
            scroll.horizontal = false;
            scroll.movementType = ScrollRect.MovementType.Clamped;
            scroll.scrollSensitivity = 40f;
            scroll.inertia = false;

            viewport.gameObject.AddComponent<CenterWhenShort>();
            return content;
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

        public static readonly Color PanelColor = new Color(0.07f, 0.07f, 0.09f);

        // A dark box inside a row - a card or a pane. width <= 0 takes the row's slack. Children
        // stack top-down with padding, like a small column.
        public static RectTransform AddPanel(Transform parent, float width, int padding = 16, float spacing = 6f)
        {
            RectTransform rect = CreateRect(parent, "Panel");
            rect.gameObject.AddComponent<Image>().color = PanelColor;

            VerticalLayoutGroup layout = rect.gameObject.AddComponent<VerticalLayoutGroup>();
            layout.spacing = spacing;
            layout.padding = new RectOffset(padding, padding, padding, padding);
            layout.childAlignment = TextAnchor.UpperLeft;
            layout.childControlWidth = true;
            layout.childControlHeight = true;
            layout.childForceExpandWidth = true;
            layout.childForceExpandHeight = false;

            LayoutElement element = rect.gameObject.AddComponent<LayoutElement>();

            // A fixed panel says so explicitly: its labels are flexible, and a layout group would
            // otherwise pass that up and let the panel grow.
            if (width > 0f)
            {
                element.preferredWidth = width;
                element.minWidth = width;
                element.flexibleWidth = 0f;
            }
            else
            {
                element.flexibleWidth = 1f;
            }

            return rect;
        }

        // Soaks up the spare height at the bottom of a panel, so its rows keep their own heights
        // instead of being stretched to fill it.
        public static void AddFiller(Transform parent)
        {
            RectTransform rect = CreateRect(parent, "Filler");
            LayoutElement element = rect.gameObject.AddComponent<LayoutElement>();
            element.flexibleHeight = 1000f;
        }

        // A section title inside a screen: left-aligned, accent-coloured, with a little room above.
        public static TextMeshProUGUI AddHeader(Transform parent, string text)
        {
            AddSpacer(parent, 6f);
            return AddText(parent, text.ToUpperInvariant(), 22f, TextAlignmentOptions.Left, 30f, AccentTextColor);
        }

        // width <= 0 lets the button stretch with its layout instead of taking a fixed width.
        // color: null is the plain grey; pass SelectedColor, PrimaryColor or DangerColor to say
        // what kind of button it is.
        public static Button AddButton(
            Transform parent,
            string label,
            UnityAction onClick,
            float width = -1f,
            float height = 52f,
            bool interactable = true,
            Color? color = null)
        {
            RectTransform rect = CreateRect(parent, $"Button_{label}");

            Image image = rect.gameObject.AddComponent<Image>();
            image.color = color ?? ButtonColor;

            Button button = rect.gameObject.AddComponent<Button>();
            button.targetGraphic = image;
            button.interactable = interactable;

            // A coloured button keeps its colour when disabled - a current job or a learned
            // ability is "on and nothing to click", not greyed out. The dim label says the rest.
            if (color.HasValue)
            {
                ColorBlock colors = button.colors;
                colors.disabledColor = Color.white;
                button.colors = colors;
            }

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
            text.fontSize = height < 40f ? 20f : 24f;
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

        // Keeps a ScrollRect's content centred while it is shorter than the viewport, and lets it
        // scroll from the top once it is taller. ScrollRect alone pins short content to the top,
        // which would leave the main menu hanging off the ceiling.
        private class CenterWhenShort : MonoBehaviour
        {
            private ScrollRect scroll;
            private RectTransform viewport;

            private void Awake()
            {
                scroll = GetComponent<ScrollRect>();
                viewport = (RectTransform)transform;
            }

            private void LateUpdate()
            {
                Settle();
            }

            public void Settle()
            {
                RectTransform content = scroll.content;
                float spare = viewport.rect.height - content.rect.height;
                bool fits = spare >= 0f;
                scroll.vertical = !fits;

                // Short: centred. Tall: kept inside the scroll range - ScrollRect only clamps while
                // it is being scrolled, so content that just grew would otherwise keep the
                // centring offset of the screen before it.
                float y = fits ? -spare * 0.5f : Mathf.Clamp(content.anchoredPosition.y, 0f, -spare);
                content.anchoredPosition = new Vector2(content.anchoredPosition.x, y);
            }
        }

        // Lays a freshly built scroll column out now rather than next frame, so a new screen
        // appears in place instead of jumping after one frame at the old screen's offset.
        public static void SettleScroll(ScrollRect scroll)
        {
            LayoutRebuilder.ForceRebuildLayoutImmediate(scroll.content);

            if (scroll.TryGetComponent(out CenterWhenShort centering))
            {
                centering.Settle();
            }
        }

        private static void SetHeight(GameObject target, float height)
        {
            LayoutElement element = target.AddComponent<LayoutElement>();
            element.preferredHeight = height;
            element.minHeight = height;
        }
    }
}
