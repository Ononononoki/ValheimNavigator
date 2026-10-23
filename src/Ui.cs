using System;
using BepInEx.Configuration;
using TMPro;
using UnityEngine;
using UnityEngine.Events;
using UnityEngine.UI;

namespace ValheimNavigator
{
    // the small uGUI helpers the panel is built from, in the game's font
    internal static class Ui
    {
        // the game's own toggle boxes on the large map (ShipPins Options, Cartography Table) are black at about 44 percent over the map
        // with orange text (255 161 61), measured on a screenshot. the panel and its rows follow that look
        internal static Color Back => new Color(0.035f, 0.030f, 0.022f, Settings.Opacity == null ? 0.78f : Settings.Opacity.Value);
        // plain dark boxes such as the search field keep this in both looks
        internal static readonly Color Box = new Color(0f, 0f, 0f, 0.30f);
        // with the game look the button colors tint the game's own button sprite, so they are light
        internal static Color Face => ButtonLook ? new Color(1f, 1f, 1f, 0.92f) : Box;
        internal static Color Hover => ButtonLook ? new Color(1f, 0.90f, 0.70f, 1f) : new Color(0.30f, 0.19f, 0.06f, 0.60f);
        internal static Color Press => ButtonLook ? new Color(0.85f, 0.70f, 0.50f, 1f) : new Color(0.50f, 0.31f, 0.09f, 0.75f);
        internal static Color Chosen => ButtonLook ? new Color(1f, 0.74f, 0.42f, 1f) : new Color(0.55f, 0.33f, 0.10f, 0.60f);
        internal static Color Enabled => ButtonLook ? new Color(0.96f, 0.84f, 0.62f, 1f) : new Color(0.36f, 0.22f, 0.06f, 0.55f);
        private static bool ButtonLook => GameLook && ButtonSprite != null;

        // the game's inventory window and button images, read from the loaded InventoryGui
        internal static Sprite PanelSprite, ButtonSprite;
        internal static Image.Type PanelType = Image.Type.Sliced, ButtonType = Image.Type.Sliced;
        internal static bool GameLook => Settings.GameLook != null && Settings.GameLook.Value && PanelSprite != null;

        internal static void FindGameLook()
        {
            if (PanelSprite != null && ButtonSprite != null) return;
            var gui = InventoryGui.instance;
            // the chest window's wood background sits on a child image of the container panel, the largest wood or background image there is it
            Image panel = null; float best = 0f;
            foreach (var root in new[] { gui != null ? gui.m_container : null, gui != null ? gui.m_player : null })
            {
                if (root == null || panel != null) continue;
                // IngameGui.prefab, Container/Bkg holds woodpanel_container, sliced
                var bkg = root.Find("Bkg");
                var direct = bkg != null ? bkg.GetComponent<Image>() : null;
                if (direct != null && direct.sprite != null) { panel = direct; break; }
                foreach (var image in root.GetComponentsInChildren<Image>(true))
                {
                    if (image.sprite == null) continue;
                    string n = image.sprite.name.ToLowerInvariant();
                    if (n.Contains("mask") || !n.Contains("woodpanel") && !n.Contains("bkg") && !n.Contains("panel")) continue;
                    var size = image.rectTransform.rect.size;
                    if (size.x * size.y > best) { best = size.x * size.y; panel = image; }
                }
            }
            if (panel != null) { PanelSprite = panel.sprite; PanelType = panel.type == Image.Type.Simple ? Image.Type.Sliced : panel.type; }
            else
            {
                // the game's window images by name, in order of preference
                var sprites = Resources.FindObjectsOfTypeAll<Sprite>();
                foreach (string name in new[] { "woodpanel_container", "inv_bkg", "woodpanel_512x512", "woodpanel_400_tileable", "panel_bkg_256", "panel_bkg" })
                {
                    var sprite = Array.Find(sprites, s => s != null && s.name == name);
                    if (sprite != null) { PanelSprite = sprite; PanelType = sprite.border.sqrMagnitude > 0f ? Image.Type.Sliced : Image.Type.Simple; break; }
                }
            }
            var button = gui != null && gui.m_craftButton != null && gui.m_craftButton.image != null && gui.m_craftButton.image.sprite != null ? gui.m_craftButton : gui != null ? gui.m_takeAllButton : null;
            if (button != null && button.image != null && button.image.sprite != null) { ButtonSprite = button.image.sprite; ButtonType = button.image.type; }
            if (Plugin.LogCounters.Value) Plugin.Log.LogInfo("game window look, panel " + (PanelSprite != null ? PanelSprite.name : "not found") + ", button " + (ButtonSprite != null ? ButtonSprite.name : "not found"));
        }

        // a window background in the chosen look
        // with the game look a missing game image gives a solid dark window, not a see-through one
        internal static void Window(Image image, float alpha)
        {
            // the wood is tiled inside its frame, stretching made the grain blurry
            // sliced stretches the wood without repeating it, tiled left a seam where the texture repeated near the right edge (test 7)
            if (GameLook) { image.sprite = PanelSprite; image.type = PanelSprite.border.sqrMagnitude > 0f ? Image.Type.Sliced : PanelType; image.color = new Color(1f, 1f, 1f, Math.Max(alpha, 0.94f)); }
            else if (Settings.GameLook != null && Settings.GameLook.Value) { image.sprite = null; image.color = new Color(0.10f, 0.08f, 0.06f, 0.96f); }
            else { image.sprite = null; image.color = new Color(Back.r, Back.g, Back.b, alpha); }
        }

        // every button under a root follows the chosen look, the pages tint their picked rows again when shown
        internal static void ApplyLook(Transform root)
        {
            if (root == null) return;
            foreach (var button in root.GetComponentsInChildren<Button>(true))
            {
                var face = button.targetGraphic as Image;
                if (face == null) continue;
                face.sprite = GameLook ? ButtonSprite : null;
                face.type = GameLook && ButtonSprite != null ? ButtonType : Image.Type.Simple;
                Tint(button, Face);
            }
        }
        // body text is a light cream, which reads on dark plates and wood. titles and headers keep the game's orange
        internal static readonly Color Ink = new Color(0.97f, 0.91f, 0.79f, 1f);
        internal static readonly Color Dim = new Color(0.80f, 0.74f, 0.64f, 1f);
        internal static readonly Color Accent = new Color(1f, 0.63f, 0.24f, 1f);

        // one dot and name color per category, in the order of Categories.Order. the layers list and the overview share them
        private static readonly Color[] CategoryColors =
        {
            new Color(0.95f, 0.95f, 0.95f), new Color(0.90f, 0.25f, 0.22f), new Color(0.95f, 0.80f, 0.25f), new Color(0.98f, 0.60f, 0.20f),
            new Color(0.70f, 0.40f, 0.90f), new Color(0.55f, 0.75f, 0.35f), new Color(0.90f, 0.40f, 0.65f), new Color(0.35f, 0.70f, 0.95f),
            new Color(0.40f, 0.85f, 0.85f), new Color(0.65f, 0.50f, 0.35f), new Color(0.60f, 0.60f, 0.95f), new Color(0.95f, 0.90f, 0.55f),
            new Color(0.85f, 0.55f, 0.45f), new Color(0.55f, 0.55f, 0.55f), new Color(0.75f, 0.75f, 0.75f)
        };

        internal static Color CategoryColor(string category)
        {
            int index = Array.IndexOf(Categories.Order, category);
            return index < 0 ? Ink : CategoryColors[index % CategoryColors.Length];
        }

        // bone, clay, ochre, sage and slate follow the muted parchment map
        private static readonly Color32[] DotColors =
        {
            new Color32(229, 221, 197, 255), new Color32(172, 88, 65, 255), new Color32(211, 180, 105, 255), new Color32(179, 148, 92, 255),
            new Color32(162, 150, 165, 255), new Color32(154, 164, 113, 255), new Color32(169, 119, 112, 255), new Color32(127, 155, 164, 255),
            new Color32(132, 162, 151, 255), new Color32(169, 142, 103, 255), new Color32(155, 163, 176, 255), new Color32(207, 196, 148, 255),
            new Color32(173, 137, 113, 255), new Color32(157, 151, 137, 255), new Color32(188, 183, 168, 255)
        };

        internal static Color32 DotColor(string category)
        {
            int index = Array.IndexOf(Categories.Order, category);
            var color = DotColors[index < 0 ? DotColors.Length - 1 : index % DotColors.Length];
            color.a = (byte)(255f * Settings.DotOpacity.Value);
            return color;
        }

        internal static TMP_FontAsset Font;
        // true while the search box has the keyboard, the game's own keys are told to keep out
        internal static bool Typing;

        internal static RectTransform Place(GameObject go, Transform parent, float x, float y, float w, float h)
        {
            var rect = (RectTransform)go.transform;
            rect.SetParent(parent, false);
            rect.anchorMin = rect.anchorMax = rect.pivot = new Vector2(0f, 1f);
            rect.anchoredPosition = new Vector2(x, -y);
            rect.sizeDelta = new Vector2(w, h);
            return rect;
        }

        internal static void Stretch(RectTransform rect, float pad)
        {
            rect.anchorMin = Vector2.zero;
            rect.anchorMax = Vector2.one;
            rect.pivot = new Vector2(0.5f, 0.5f);
            rect.offsetMin = new Vector2(pad, pad);
            rect.offsetMax = new Vector2(-pad, -pad);
        }

        internal static TMP_Text Label(Transform parent, string name, float x, float y, float w, float h, float size, TextAlignmentOptions align, Color color)
        {
            var go = new GameObject(name, typeof(RectTransform));
            Place(go, parent, x, y, w, h);
            var text = go.AddComponent<TextMeshProUGUI>();
            if (Font != null) text.font = Font;
            var outline = Outline();
            if (outline != null) text.fontSharedMaterial = outline;
            text.fontSize = size;
            text.alignment = align;
            text.color = color;
            text.textWrappingMode = TextWrappingModes.NoWrap;
            text.overflowMode = TextOverflowModes.Ellipsis;
            text.raycastTarget = false;
            return text;
        }

        internal static Button Tag(Transform parent, string name, string label, float x, float y, float w, float h, float size, TextAlignmentOptions align, UnityAction click, out TMP_Text text)
        {
            var go = new GameObject(name, typeof(RectTransform), typeof(Image), typeof(Button));
            Place(go, parent, x, y, w, h);
            var face = go.GetComponent<Image>();
            face.color = Color.white;
            if (GameLook && ButtonSprite != null) { face.sprite = ButtonSprite; face.type = ButtonType; }
            var button = go.GetComponent<Button>();
            button.targetGraphic = face;
            Tint(button, Face);
            button.onClick.AddListener(click);
            if (ClickSfx != null) go.AddComponent<ButtonSfx>().m_sfxPrefab = Settings.ClickSounds.Value ? ClickSfx : null;
            text = Label(go.transform, "Text", 8f, 0f, w - 16f, h, size, align, Ink);
            text.text = label;
            return button;
        }

        // the game's own menu click sound, taken from a button of the game's interface
        internal static GameObject ClickSfx;

        internal static void FindClickSound(Transform root)
        {
            if (ClickSfx != null) return;
            if (root != null)
                foreach (var sfx in root.GetComponentsInChildren<ButtonSfx>(true))
                    if (sfx.m_sfxPrefab != null) { ClickSfx = sfx.m_sfxPrefab; break; }
            if (ClickSfx == null)
                foreach (var sfx in Resources.FindObjectsOfTypeAll<ButtonSfx>())
                    if (sfx.m_sfxPrefab != null) { ClickSfx = sfx.m_sfxPrefab; break; }
            if (Plugin.LogCounters.Value) Plugin.Log.LogInfo("menu click sound " + (ClickSfx != null ? ClickSfx.name : "not found"));
        }

        // the click sound follows the setting on buttons that already exist
        internal static void ApplySounds(Transform root)
        {
            if (root == null) return;
            foreach (var sfx in root.GetComponentsInChildren<ButtonSfx>(true)) sfx.m_sfxPrefab = Settings.ClickSounds.Value ? ClickSfx : null;
        }

        // one shared font material with a soft drop shadow like the game's own labels, a thick outline looked harsh
        private static Material _outline;

        private static Material Outline()
        {
            if (_outline != null || Font == null || Font.material == null) return _outline;
            _outline = new Material(Font.material) { name = "ValheimNavigator shadowed text" };
            _outline.DisableKeyword("OUTLINE_ON");
            _outline.EnableKeyword("UNDERLAY_ON");
            _outline.SetColor("_UnderlayColor", new Color(0f, 0f, 0f, 0.6f));
            _outline.SetFloat("_UnderlayOffsetX", 0.45f);
            _outline.SetFloat("_UnderlayOffsetY", -0.45f);
            _outline.SetFloat("_UnderlayDilate", 0.1f);
            _outline.SetFloat("_UnderlaySoftness", 0.55f);
            return _outline;
        }

        // the game's dark inset image of the inventory grid, behind the panel's pages
        internal static Sprite PlateSprite;

        internal static void FindPlate()
        {
            if (PlateSprite != null) return;
            var gui = InventoryGui.instance;
            var sunken = gui != null && gui.m_container != null ? gui.m_container.Find("sunken") : null;
            var image = sunken != null ? sunken.GetComponent<Image>() : null;
            PlateSprite = image != null ? image.sprite : Array.Find(Resources.FindObjectsOfTypeAll<Sprite>(), s => s != null && s.name == "sunken");
        }

        // a dark plate behind a page, the sunken image with the game look and a plain dark plate otherwise
        internal static void Plate(Image image)
        {
            if (GameLook && PlateSprite != null) { image.sprite = PlateSprite; image.type = Image.Type.Sliced; image.color = new Color(1f, 1f, 1f, 0.95f); }
            else if (GameLook) { image.sprite = null; image.color = new Color(0.07f, 0.055f, 0.04f, 0.88f); }
            else { image.sprite = null; image.color = new Color(0f, 0f, 0f, 0f); }
        }

        internal static void Tint(Button button, Color normal)
        {
            var colors = button.colors;
            colors.normalColor = normal;
            colors.highlightedColor = Hover;
            colors.pressedColor = Press;
            colors.selectedColor = normal;
            colors.colorMultiplier = 1f;
            colors.fadeDuration = 0.08f;
            button.colors = colors;
        }

        // a one-line text box
        internal static TMP_InputField Input(Transform parent, string name, string hint, float x, float y, float w, float h, UnityAction<string> changed, UnityAction<string> submitted)
        {
            var go = new GameObject(name, typeof(RectTransform), typeof(Image), typeof(TMP_InputField));
            Place(go, parent, x, y, w, h);
            go.GetComponent<Image>().color = Box;

            var area = new GameObject("Area", typeof(RectTransform), typeof(RectMask2D));
            area.transform.SetParent(go.transform, false);
            Stretch((RectTransform)area.transform, 6f);

            var text = new GameObject("Text", typeof(RectTransform)).AddComponent<TextMeshProUGUI>();
            text.transform.SetParent(area.transform, false);
            Stretch((RectTransform)text.transform, 0f);
            var placeholder = new GameObject("Hint", typeof(RectTransform)).AddComponent<TextMeshProUGUI>();
            placeholder.transform.SetParent(area.transform, false);
            Stretch((RectTransform)placeholder.transform, 0f);
            foreach (var t in new[] { text, placeholder })
            {
                if (Font != null) t.font = Font;
                t.fontSize = 16f;
                t.alignment = TextAlignmentOptions.MidlineLeft;
                t.textWrappingMode = TextWrappingModes.NoWrap;
            }
            text.color = Ink;
            placeholder.color = Dim;
            placeholder.text = hint;

            var field = go.GetComponent<TMP_InputField>();
            field.textViewport = (RectTransform)area.transform;
            field.textComponent = text;
            field.placeholder = placeholder;
            field.lineType = TMP_InputField.LineType.SingleLine;
            field.onValueChanged.AddListener(changed);
            field.onSubmit.AddListener(submitted);
            return field;
        }

        // a list that scrolls with the mouse wheel and by dragging, with a thin bar on its right
        internal sealed class Scroller
        {
            private const float BarWidth = 6f;
            internal ScrollRect Rect;
            internal RectTransform Content;
            internal float Width;
            internal float Height;
            internal Image Handle;

            // the rows go inside Content, its height is the sum of the rows
            internal void SetHeight(float total)
            {
                Content.sizeDelta = new Vector2(Content.sizeDelta.x, Math.Max(total, Height));
                Refresh();
            }

            internal void Refresh()
            {
                float content = Content.sizeDelta.y;
                bool needed = content > Height + 0.5f;
                Handle.transform.parent.gameObject.SetActive(needed);
                if (!needed) return;
                float handle = Math.Max(24f, Height * Height / content);
                float top = (1f - Rect.verticalNormalizedPosition) * (Height - handle);
                var rect = (RectTransform)Handle.transform;
                rect.sizeDelta = new Vector2(BarWidth, handle);
                rect.anchoredPosition = new Vector2(0f, -top);
            }
        }

        internal static Scroller Scroll(Transform parent, string name, float x, float y, float w, float h)
        {
            const float bar = 6f;
            var go = new GameObject(name, typeof(RectTransform), typeof(Image), typeof(RectMask2D), typeof(ScrollRect));
            Place(go, parent, x, y, w, h);
            // nearly clear, it only has to take the wheel and the drag
            go.GetComponent<Image>().color = new Color(0f, 0f, 0f, 0.01f);
            var content = new GameObject("Content", typeof(RectTransform));
            var rect = (RectTransform)content.transform;
            rect.SetParent(go.transform, false);
            rect.anchorMin = new Vector2(0f, 1f);
            rect.anchorMax = new Vector2(1f, 1f);
            rect.pivot = new Vector2(0f, 1f);
            rect.anchoredPosition = Vector2.zero;
            rect.sizeDelta = new Vector2(-(bar + 4f), h);
            var scroll = go.GetComponent<ScrollRect>();
            scroll.content = rect;
            scroll.viewport = (RectTransform)go.transform;
            scroll.horizontal = false;
            scroll.vertical = true;
            scroll.movementType = ScrollRect.MovementType.Clamped;
            scroll.scrollSensitivity = Settings.ScrollSpeed.Value;
            scroll.inertia = false;

            var track = new GameObject("Bar", typeof(RectTransform), typeof(Image));
            Place(track, go.transform, w - bar, 0f, bar, h);
            track.GetComponent<Image>().color = new Color(0f, 0f, 0f, 0.25f);
            track.GetComponent<Image>().raycastTarget = false;
            var handle = new GameObject("Handle", typeof(RectTransform), typeof(Image));
            Place(handle, track.transform, 0f, 0f, bar, 24f);
            handle.GetComponent<Image>().color = new Color(Ink.r, Ink.g, Ink.b, 0.6f);
            handle.GetComponent<Image>().raycastTarget = false;

            var result = new Scroller { Rect = scroll, Content = rect, Width = w - bar - 4f, Height = h, Handle = handle.GetComponent<Image>() };
            scroll.onValueChanged.AddListener(_ => result.Refresh());
            return result;
        }

        // a row with a name on the left and its state on the right. the state text and the tint follow Refresh
        internal sealed class Switch
        {
            internal Button Button;
            internal TMP_Text Name;
            internal TMP_Text State;
            internal Func<string> Read;
            internal Func<bool> On;

            // rows keep one neutral face, the state on the right says On in green and Off in grey so it reads as state, not as a pattern
            internal void Refresh()
            {
                State.text = Read();
                Tint(Button, Face);
                State.color = State.text == "On" ? OnInk : State.text == "Off" ? OffInk : Ink;
                State.fontStyle = State.text == "On" ? FontStyles.Bold : FontStyles.Normal;
            }
        }

        internal static readonly Color OnInk = new Color(0.55f, 0.90f, 0.45f, 1f);
        internal static readonly Color OffInk = new Color(0.62f, 0.58f, 0.52f, 1f);
        internal const float RowHeight = 32f;
        internal const float RowText = 17f;

        internal static Switch Row(Transform parent, string name, string label, float x, float y, float w, Func<string> read, Func<bool> on, Action click)
        {
            var row = new Switch { Read = read, On = on };
            row.Button = Tag(parent, name, label, x, y, w, RowHeight, RowText, TextAlignmentOptions.MidlineLeft, () => { click(); row.Refresh(); }, out var nameText);
            row.Name = nameText;
            row.State = Label(row.Button.transform, "State", w - 100f, 0f, 92f, RowHeight, RowText, TextAlignmentOptions.MidlineRight, Ink);
            row.Refresh();
            return row;
        }

        internal static Switch Toggle(Transform parent, string name, string label, float x, float y, float w, ConfigEntry<bool> entry, Action changed)
        {
            return Row(parent, name, label, x, y, w, () => entry.Value ? "On" : "Off", () => entry.Value, () => { entry.Value = !entry.Value; changed?.Invoke(); });
        }

        // a number that steps through the given choices on each click
        internal static Switch Cycle(Transform parent, string name, string label, float x, float y, float w, ConfigEntry<float> entry, float[] choices, string unit, Action changed = null)
        {
            return Row(parent, name, label, x, y, w, () => entry.Value.ToString("0.#") + unit, () => false, () =>
            {
                int next = 0;
                for (int i = 0; i < choices.Length; i++)
                    if (choices[i] > entry.Value + 0.001f) { next = i; break; }
                entry.Value = choices[next];
                changed?.Invoke();
            });
        }

        // the steps wrap around to the first choice, so a 0 meaning "off" put first comes after the largest one
        internal static Switch Cycle(Transform parent, string name, string label, float x, float y, float w, ConfigEntry<int> entry, int[] choices, Action changed = null, string zeroLabel = null)
        {
            return Row(parent, name, label, x, y, w, () => entry.Value == 0 && zeroLabel != null ? zeroLabel : entry.Value.ToString(), () => false, () =>
            {
                int next = 0;
                for (int i = 0; i < choices.Length; i++)
                    if (choices[i] > entry.Value) { next = i; break; }
                entry.Value = choices[next];
                changed?.Invoke();
            });
        }
    }
}
