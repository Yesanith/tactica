using System.Collections.Generic;
using System.IO;
using TMPro;
using UnityEngine;
using UnityEngine.TextCore.LowLevel;
using UnityEngine.UI;
using Tactica.Grid;

namespace Tactica.UI
{
    // The neon-terminal look: near-black navy panels with cut corners and thin cyan edges, magenta
    // for anything hostile or irreversible, a condensed display face for words and a monospace one
    // for numbers. Every screen gets it from here - MenuBuilder for the menus, Skin() for the
    // authored combat HUD - so the look can change in one file.
    //
    // Sprites are drawn in code (like UnitStatusBar's triangle): nothing to import, and the
    // chamfer, edge width and grid stay numbers in this file.
    public static class NeonTheme
    {
        // ---------------------------------------------------------------------
        // Palette
        // ---------------------------------------------------------------------

        public static readonly Color Void = new Color(0.012f, 0.02f, 0.04f);
        public static readonly Color Panel = new Color(0.024f, 0.04f, 0.08f, 0.94f);
        public static readonly Color PanelRaised = new Color(0.05f, 0.08f, 0.15f, 0.97f);
        public static readonly Color Cyan = new Color(0.16f, 0.94f, 1f);
        public static readonly Color CyanDeep = new Color(0.03f, 0.27f, 0.34f);
        public static readonly Color Magenta = new Color(1f, 0.2f, 0.62f);
        public static readonly Color MagentaDeep = new Color(0.32f, 0.04f, 0.2f);
        public static readonly Color Amber = new Color(1f, 0.76f, 0.28f);
        public static readonly Color Red = new Color(1f, 0.3f, 0.38f);
        public static readonly Color Green = new Color(0.4f, 1f, 0.62f);
        public static readonly Color Text = new Color(0.86f, 0.96f, 1f);
        public static readonly Color TextDim = new Color(0.5f, 0.64f, 0.74f);
        public static readonly Color TextFaint = new Color(0.28f, 0.37f, 0.46f);
        public static readonly Color Mp = new Color(0.52f, 0.45f, 1f);

        // A unit's side, everywhere it is drawn: bars, turn queue, unit panel.
        public static Color FactionColor(Faction faction)
        {
            switch (faction)
            {
                case Faction.Player: return Cyan;
                case Faction.Enemy: return Magenta;
                case Faction.Neutral: return Amber;
            }

            return Amber;
        }

        // Edges: a panel's is quiet so a screen of them does not buzz; a button's is brighter.
        public static readonly Color PanelEdge = new Color(0.16f, 0.94f, 1f, 0.28f);
        public static readonly Color ButtonEdge = new Color(0.16f, 0.94f, 1f, 0.55f);

        // What a button is for. Plain is most of them; the rest carry meaning, as the prototype's
        // colours did: on (deployed, current job), the button a screen is about, irreversible.
        public enum ButtonKind
        {
            Plain,
            Selected,
            Primary,
            Danger,
        }

        public enum TextRole
        {
            Body,
            Display,
            Data,
        }

        // ---------------------------------------------------------------------
        // Fonts
        // ---------------------------------------------------------------------

        // Loaded from the OS font folder at runtime, not imported: Bahnschrift ships with Windows
        // and may not be redistributed, Cascadia Mono does too (and is OFL). Missing either - another
        // OS, an old Windows - falls back to TMP's default face, so nothing breaks, it only looks
        // plainer. A real release imports licensed font assets instead.
        private static TMP_FontAsset display;
        private static TMP_FontAsset data;
        private static bool fontsLoaded;

        public static TMP_FontAsset DisplayFont
        {
            get
            {
                LoadFonts();
                return display;
            }
        }

        public static TMP_FontAsset DataFont
        {
            get
            {
                LoadFonts();
                return data;
            }
        }

        private static void LoadFonts()
        {
            if (fontsLoaded)
            {
                return;
            }

            fontsLoaded = true;
            TMP_FontAsset fallback = TMP_Settings.defaultFontAsset;
            data = LoadSystemFont("CascadiaMono.ttf", fallback) ?? LoadSystemFont("consola.ttf", fallback);
            display = LoadSystemFont("bahnschrift.ttf", data ?? fallback) ?? fallback;
            data = data ?? display;
        }

        private static TMP_FontAsset LoadSystemFont(string fileName, TMP_FontAsset fallback)
        {
            string path = Path.Combine(System.Environment.GetFolderPath(System.Environment.SpecialFolder.Fonts), fileName);

            if (!File.Exists(path))
            {
                return null;
            }

            TMP_FontAsset font = TMP_FontAsset.CreateFontAsset(path, 0, 90, 9, GlyphRenderMode.SDFAA, 1024, 1024);

            if (font == null)
            {
                return null;
            }

            font.name = Path.GetFileNameWithoutExtension(fileName) + " (runtime)";
            font.hideFlags = HideFlags.DontSave;

            // Glyphs the face lacks (arrows, blocks, symbols) come from the next font instead of
            // boxes - the display face borrows the monospace's, which borrows TMP's default.
            font.fallbackFontAssetTable = new List<TMP_FontAsset>();

            if (fallback != null)
            {
                font.fallbackFontAssetTable.Add(fallback);
            }

            if (TMP_Settings.defaultFontAsset != null && fallback != TMP_Settings.defaultFontAsset)
            {
                font.fallbackFontAssetTable.Add(TMP_Settings.defaultFontAsset);
            }

            return font;
        }

        public static void Style(TMP_Text text, TextRole role = TextRole.Body)
        {
            if (text == null)
            {
                return;
            }

            text.font = role == TextRole.Data ? DataFont : DisplayFont;

            // The display face is narrow; a little tracking reads as "terminal" without the width
            // of a real monospace, which the dense screens could not afford.
            text.characterSpacing = role == TextRole.Display ? 6f : role == TextRole.Body ? 1f : 0f;
        }


        // ---------------------------------------------------------------------
        // Panels and buttons
        // ---------------------------------------------------------------------

        private const int SpriteSize = 64;
        private const float Chamfer = 14f;
        private const float EdgeWidth = 1.6f;
        private const int SliceBorder = 20;
        private const int GridCell = 64;

        private static Sprite fillSprite;
        private static Sprite edgeSprite;
        private static Sprite gridSprite;
        private static Sprite solidSprite;

        private static Sprite FillSprite => fillSprite != null ? fillSprite : fillSprite = ChamferSprite(false);
        private static Sprite EdgeSprite => edgeSprite != null ? edgeSprite : edgeSprite = ChamferSprite(true);
        private static Sprite GridSprite => gridSprite != null ? gridSprite : gridSprite = MakeGridSprite();

        // Flat white, for bars: square ends read as a gauge, Unity's rounded default as a pill.
        public static Sprite SolidSprite => solidSprite != null ? solidSprite : solidSprite = MakeSolidSprite();

        // A panel: cut-corner fill with a thin edge drawn over it. The edge is a child that layout
        // groups ignore, so the panel's own layout is untouched.
        public static void SkinPanel(Image image, Color? fill = null, Color? edge = null)
        {
            if (image == null)
            {
                return;
            }

            image.sprite = FillSprite;
            image.type = Image.Type.Sliced;
            image.color = fill ?? Panel;
            SetEdge(image.rectTransform, edge ?? PanelEdge);
        }

        public static void SkinButton(Button button, ButtonKind kind = ButtonKind.Plain)
        {
            if (button == null || button.image == null)
            {
                return;
            }

            button.image.sprite = FillSprite;
            button.image.type = Image.Type.Sliced;
            button.transition = Selectable.Transition.ColorTint;
            SetButtonKind(button, kind);

            TMP_Text label = button.GetComponentInChildren<TMP_Text>();
            Style(label, TextRole.Display);
        }

        // Recolours a skinned button in place - the Jack Out button arms itself this way.
        public static void SetButtonKind(Button button, ButtonKind kind)
        {
            if (button == null || button.image == null)
            {
                return;
            }

            Color fill;
            Color edge;

            switch (kind)
            {
                case ButtonKind.Selected:
                    fill = CyanDeep;
                    edge = Cyan;
                    break;

                case ButtonKind.Primary:
                    fill = new Color(0.04f, 0.17f, 0.24f);
                    edge = Cyan;
                    break;

                case ButtonKind.Danger:
                    fill = MagentaDeep;
                    edge = Magenta;
                    break;

                default:
                    fill = PanelRaised;
                    edge = ButtonEdge;
                    break;
            }

            // The fill lives in the ColorBlock, not on the Image, so hover and press tint it the
            // same way for every kind. Selected is the gamepad's focus, so it has to show. A disabled "on" button keeps its colour (a current job is on,
            // not greyed out); a disabled plain one fades.
            button.image.color = Color.white;
            ColorBlock colors = button.colors;
            colors.normalColor = fill;
            colors.highlightedColor = Color.Lerp(fill, edge, 0.35f);
            colors.selectedColor = Color.Lerp(fill, edge, 0.45f);
            colors.pressedColor = Color.Lerp(fill, edge, 0.6f);
            colors.disabledColor = kind == ButtonKind.Plain ? new Color(fill.r, fill.g, fill.b, 0.45f) : fill;
            colors.colorMultiplier = 1f;
            colors.fadeDuration = 0.06f;
            button.colors = colors;

            // Assigning colors starts a fade from the current tint - Unity's default white on a
            // button made this frame - so every rebuilt screen flashed white. Snap to the state's
            // colour now; hover and press still fade as usual.
            button.targetGraphic.CrossFadeColor(button.interactable ? colors.normalColor : colors.disabledColor, 0f, true, true);

            SetEdge(button.image.rectTransform, edge);
        }

        private static void SetEdge(RectTransform owner, Color color)
        {
            Transform existing = owner.Find("Edge");
            Image edge;

            if (existing != null)
            {
                edge = existing.GetComponent<Image>();
            }
            else
            {
                RectTransform rect = MenuBuilder.CreateRect(owner, "Edge");
                MenuBuilder.Stretch(rect);
                rect.SetAsFirstSibling();
                rect.gameObject.AddComponent<LayoutElement>().ignoreLayout = true;

                edge = rect.gameObject.AddComponent<Image>();
                edge.sprite = EdgeSprite;
                edge.type = Image.Type.Sliced;
                edge.raycastTarget = false;
            }

            edge.color = color;
        }

        // The screen behind every menu: near-black with a faint grid, like a terminal's.
        public static void SkinBackdrop(Image image)
        {
            if (image == null)
            {
                return;
            }

            image.color = Void;

            RectTransform grid = MenuBuilder.CreateRect(image.rectTransform, "Grid");
            MenuBuilder.Stretch(grid);
            grid.SetAsFirstSibling();
            grid.gameObject.AddComponent<LayoutElement>().ignoreLayout = true;

            Image lines = grid.gameObject.AddComponent<Image>();
            lines.sprite = GridSprite;
            lines.type = Image.Type.Tiled;
            lines.color = new Color(Cyan.r, Cyan.g, Cyan.b, 0.025f);
            lines.raycastTarget = false;
        }

        // ---------------------------------------------------------------------
        // Authored UI
        // ---------------------------------------------------------------------

        // Restyles a hierarchy built in the editor (the combat HUD prefab): every button, every
        // label, and the background of every panel. Fill images (bars) keep their colours, since
        // their colour means something, but lose the rounded default sprite.
        public static void Skin(Transform root)
        {
            foreach (Button button in root.GetComponentsInChildren<Button>(true))
            {
                SkinButton(button);

                // Authored labels are TMP's default dark grey.
                TMP_Text label = button.GetComponentInChildren<TMP_Text>(true);

                if (label != null)
                {
                    label.color = Text;
                }
            }

            foreach (Image image in root.GetComponentsInChildren<Image>(true))
            {
                if (image.type == Image.Type.Filled)
                {
                    image.sprite = SolidSprite;
                    continue;
                }

                if (image.GetComponent<Button>() != null || image.GetComponentInParent<Button>(true) != null
                    || image.name == "Edge" || image.name == "Grid")
                {
                    continue;
                }

                bool isBar = image.name.Contains("Background") && image.transform.childCount > 0
                             && image.transform.GetChild(0).GetComponent<Image>() is Image child && child.type == Image.Type.Filled;

                if (isBar)
                {
                    image.sprite = SolidSprite;
                    image.type = Image.Type.Simple;
                    image.color = new Color(0f, 0f, 0f, 0.6f);
                    continue;
                }

                SkinPanel(image);
            }

            foreach (TMP_Text text in root.GetComponentsInChildren<TMP_Text>(true))
            {
                if (text.GetComponentInParent<Button>(true) != null)
                {
                    continue;
                }

                Style(text, LooksLikeData(text.text) ? TextRole.Data : TextRole.Body);
            }
        }

        private static bool LooksLikeData(string text)
        {
            return !string.IsNullOrEmpty(text) && (text.Contains("/") || char.IsDigit(text[0]));
        }

        // ---------------------------------------------------------------------
        // Sprite drawing
        // ---------------------------------------------------------------------

        // Top-left and bottom-right corners cut at 45 degrees. Distance-based, so the edges are
        // antialiased: the signed distance to the cut square, then either the inside (fill) or a
        // band just inside the boundary (edge).
        private static Sprite ChamferSprite(bool edgeOnly)
        {
            Texture2D texture = new Texture2D(SpriteSize, SpriteSize, TextureFormat.RGBA32, false)
            {
                name = edgeOnly ? "NeonEdge" : "NeonFill",
                filterMode = FilterMode.Bilinear,
                wrapMode = TextureWrapMode.Clamp,
                hideFlags = HideFlags.DontSave,
            };

            Color32[] pixels = new Color32[SpriteSize * SpriteSize];
            float s = SpriteSize;
            float root2 = Mathf.Sqrt(2f);

            for (int y = 0; y < SpriteSize; y++)
            {
                for (int x = 0; x < SpriteSize; x++)
                {
                    float px = x + 0.5f;
                    float py = y + 0.5f;

                    float box = Mathf.Max(Mathf.Max(-px, px - s), Mathf.Max(-py, py - s));
                    float topLeft = (Chamfer - (px + (s - py))) / root2;
                    float bottomRight = (Chamfer - ((s - px) + py)) / root2;
                    float distance = Mathf.Max(box, Mathf.Max(topLeft, bottomRight));

                    float inside = Mathf.Clamp01(0.5f - distance);
                    float alpha = edgeOnly ? inside * Mathf.Clamp01(distance + EdgeWidth + 0.5f) : inside;
                    pixels[y * SpriteSize + x] = new Color32(255, 255, 255, (byte)(alpha * 255f));
                }
            }

            texture.SetPixels32(pixels);
            texture.Apply(false, true);

            Sprite sprite = Sprite.Create(texture, new Rect(0f, 0f, SpriteSize, SpriteSize), new Vector2(0.5f, 0.5f), 100f, 0,
                SpriteMeshType.FullRect, new Vector4(SliceBorder, SliceBorder, SliceBorder, SliceBorder));
            sprite.hideFlags = HideFlags.DontSave;
            return sprite;
        }

        private static Sprite MakeSolidSprite()
        {
            Texture2D texture = new Texture2D(4, 4, TextureFormat.RGBA32, false) { name = "NeonSolid", hideFlags = HideFlags.DontSave };
            Color32[] pixels = new Color32[16];

            for (int i = 0; i < pixels.Length; i++)
            {
                pixels[i] = new Color32(255, 255, 255, 255);
            }

            texture.SetPixels32(pixels);
            texture.Apply(false, true);

            Sprite sprite = Sprite.Create(texture, new Rect(0f, 0f, 4f, 4f), new Vector2(0.5f, 0.5f), 100f);
            sprite.hideFlags = HideFlags.DontSave;
            return sprite;
        }

        // One grid cell: a single-pixel line along the left and bottom edges, tiled.
        private static Sprite MakeGridSprite()
        {
            Texture2D texture = new Texture2D(GridCell, GridCell, TextureFormat.RGBA32, false)
            {
                name = "NeonGrid",
                filterMode = FilterMode.Point,
                wrapMode = TextureWrapMode.Repeat,
                hideFlags = HideFlags.DontSave,
            };

            Color32[] pixels = new Color32[GridCell * GridCell];

            for (int y = 0; y < GridCell; y++)
            {
                for (int x = 0; x < GridCell; x++)
                {
                    bool line = x == 0 || y == 0;
                    pixels[y * GridCell + x] = new Color32(255, 255, 255, (byte)(line ? 255 : 0));
                }
            }

            texture.SetPixels32(pixels);
            texture.Apply(false, true);

            Sprite sprite = Sprite.Create(texture, new Rect(0f, 0f, GridCell, GridCell), new Vector2(0.5f, 0.5f), 100f, 0, SpriteMeshType.FullRect);
            sprite.hideFlags = HideFlags.DontSave;
            return sprite;
        }
    }
}
