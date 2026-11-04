using System.Collections.Generic;
using System.Text.RegularExpressions;
using UnityEngine;

namespace GedoniaCoop
{
    // The mod's IMGUI windows drawn with the game's own look: sprites cut at
    // runtime from the game's NGUI atlas ("Fantasy Atlas": wooden window frame
    // of the pause menu, title bar, yellow-green buttons, text area, party
    // member frame) and its font (Overlock-Regular) with a black outline like
    // the game's labels. Nothing of the game is shipped; until the atlas is
    // found every call falls back to plain IMGUI.
    public static class GameSkin
    {
        public static bool Ready { get; private set; }
        static float nextTry;
        static Font font;

        class Slice
        {
            public Texture2D Tex;
            public RectOffset Border; // in atlas pixels
        }
        static readonly Dictionary<string, Slice> slices = new Dictionary<string, Slice>();

        static readonly string[] Wanted = { "WindowFrameSmall", "WindowTitle", "Button1", "TextArea", "TooltipBg",
            "PartyMemberBg", "PartyMemberPortraitFG", "Bright", "UpperPanelCharIcon", "ContentSliderDragBG", "ContentSliderButton" };

        static GUIStyle label, button, title, field, small, scrollV, scrollThumb;
        public static GUIStyle LabelStyle { get { Init(); return label ?? GUI.skin.label; } }

        // Colours of the game's labels.
        public static readonly Color Cream = new Color(1f, 0.97f, 0.79f);
        public static readonly Color Gold = new Color(0.96f, 0.82f, 0.36f);

        // One NGUI pixel at this screen height in IMGUI units (the mod scales
        // IMGUI by Screen.height / 900; the game's UI is 1:1 at 1440).
        static float Px { get { return 1f / InviteUi.Scale; } }

        // Draw calls only check Ready; the copy itself runs in Update (Load): copied
        // inside OnGUI the atlas came out much darker.
        // Styles need GUI.skin, so they are made in the first OnGUI after Load.
        static bool loaded;
        public static void Init() { if (loaded && !Ready) { BuildStyles(); Ready = true; } }

        public static void Load()
        {
            if (loaded || Time.realtimeSinceStartup < nextTry) return;
            nextTry = Time.realtimeSinceStartup + 2f;
            INGUIAtlas atlas = null;
            foreach (NGUIAtlas a in Resources.FindObjectsOfTypeAll<NGUIAtlas>())
                if (a.spriteList != null && a.GetSprite("WindowFrameSmall") != null && a.texture != null) { atlas = a; break; }
            if (atlas == null)
                foreach (UIAtlas a in Resources.FindObjectsOfTypeAll<UIAtlas>())
                    if (a.spriteList != null && a.GetSprite("WindowFrameSmall") != null && a.texture != null) { atlas = a; break; }
            if (font == null)
                foreach (Font f in Resources.FindObjectsOfTypeAll<Font>())
                    if (f.name == "Overlock-Regular") { font = f; break; }
            if (atlas == null || font == null) return;
            Texture tex = atlas.texture;
            RenderTexture rt = RenderTexture.GetTemporary(tex.width, tex.height, 0, RenderTextureFormat.ARGB32);
            RenderTexture prev = RenderTexture.active;
            Graphics.Blit(tex, rt);
            RenderTexture.active = rt;
            // The whole atlas once (as the atlasdump test step, whose PNG is upright),
            // then cut on the CPU, where rows count from the bottom.
            Texture2D all = new Texture2D(tex.width, tex.height, TextureFormat.RGBA32, false);
            all.ReadPixels(new Rect(0, 0, tex.width, tex.height), 0, 0);
            all.Apply();
            RenderTexture.active = prev;
            RenderTexture.ReleaseTemporary(rt);
            foreach (string n in Wanted)
            {
                UISpriteData d = atlas.GetSprite(n);
                if (d == null) continue;
                Texture2D t = new Texture2D(d.width, d.height, TextureFormat.RGBA32, false);
                t.SetPixels(all.GetPixels(d.x, tex.height - d.y - d.height, d.width, d.height));
                t.Apply();
                t.wrapMode = TextureWrapMode.Clamp;
                t.filterMode = FilterMode.Bilinear;
                slices[n] = new Slice { Tex = t, Border = new RectOffset(d.borderLeft, d.borderRight, d.borderTop, d.borderBottom) };
                // Test copies (atlasdump ran there): keep the cut for a look.
                string dbg = System.IO.Path.Combine(BepInEx.Paths.BepInExRootPath, "coop-atlas");
                if (System.IO.Directory.Exists(dbg)) System.IO.File.WriteAllBytes(System.IO.Path.Combine(dbg, "skin-" + n + ".png"), t.EncodeToPNG());
            }
            UnityEngine.Object.Destroy(all);
            loaded = true;
            List<string> got = new List<string>();
            foreach (KeyValuePair<string, Slice> kv in slices) got.Add(kv.Key + "(" + kv.Value.Tex.width + "x" + kv.Value.Tex.height + " b" + kv.Value.Border.left + "," + kv.Value.Border.right + "," + kv.Value.Border.top + "," + kv.Value.Border.bottom + ")");
            Plugin.Log.LogInfo("Game skin ready: font " + font.name + ", " + string.Join(" ", got.ToArray()));
        }

        static void BuildStyles()
        {
            label = new GUIStyle(GUI.skin.label);
            label.font = font;
            label.fontSize = 14;
            label.richText = true;
            label.wordWrap = true;
            label.normal.textColor = Color.white;
            label.padding = new RectOffset(2, 2, 2, 2);
            label.margin = new RectOffset(4, 4, 3, 3);
            button = new GUIStyle(label);
            button.alignment = TextAnchor.MiddleCenter;
            button.wordWrap = false;
            button.padding = new RectOffset(14, 14, 5, 5);
            button.margin = new RectOffset(4, 4, 4, 4);
            button.fixedHeight = 26f;
            title = new GUIStyle(label);
            title.alignment = TextAnchor.MiddleCenter;
            title.wordWrap = false;
            title.fontSize = 15;
            title.normal.textColor = Cream;
            field = new GUIStyle(GUI.skin.textField);
            field.font = font;
            field.fontSize = 14;
            field.normal.background = field.hover.background = field.focused.background = field.active.background = null;
            field.normal.textColor = field.hover.textColor = field.focused.textColor = field.active.textColor = Color.white;
            field.padding = new RectOffset(8, 8, 5, 4);
            field.margin = new RectOffset(4, 4, 4, 4);
            field.fixedHeight = 28f;
            small = new GUIStyle(label);
            small.fontSize = 13;
            small.alignment = TextAnchor.MiddleCenter;
            small.wordWrap = false;
            small.padding = new RectOffset(0, 0, 0, 0);
            small.margin = new RectOffset(0, 0, 0, 0);
            scrollV = new GUIStyle(GUI.skin.verticalScrollbar);
            scrollV.fixedWidth = 10f;
            scrollV.border = new RectOffset(0, 0, 0, 0);
            scrollV.normal.background = Solid(new Color(0.12f, 0.08f, 0.05f, 0.8f));
            scrollV.margin = new RectOffset(2, 0, 0, 0);
            scrollThumb = new GUIStyle(GUI.skin.verticalScrollbarThumb);
            scrollThumb.fixedWidth = 10f;
            scrollThumb.border = new RectOffset(0, 0, 0, 0);
            scrollThumb.normal.background = Solid(new Color(0.62f, 0.5f, 0.3f, 1f));
        }

        static Texture2D Solid(Color c)
        {
            Texture2D t = new Texture2D(1, 1);
            t.SetPixel(0, 0, c);
            t.Apply();
            return t;
        }

        // Gives a style of the caller the game's font (for CalcHeight and so on).
        public static void Fontify(GUIStyle s)
        {
            Init();
            if (Ready && s != null && s.font != font) s.font = font;
        }

        // ---------- drawing ----------

        // Sliced sprite: the borders keep their size in game pixels (shrunk when
        // the rect is too small for them), the middle stretches.
        public static void Draw9(Rect r, string sprite, Color color)
        {
            Slice s;
            if (Event.current.type != EventType.Repaint || !slices.TryGetValue(sprite, out s)) return;
            Texture2D t = s.Tex;
            float tw = t.width, th = t.height;
            float l = s.Border.left, rr = s.Border.right, top = s.Border.top, b = s.Border.bottom;
            float k = Px;
            if (l + rr > 0f) k = Mathf.Min(k, r.width / (l + rr));
            if (top + b > 0f) k = Mathf.Min(k, r.height / (top + b));
            float[] xs = { r.x, r.x + l * k, r.xMax - rr * k, r.xMax };
            float[] ys = { r.y, r.y + top * k, r.yMax - b * k, r.yMax };
            float[] us = { 0f, l / tw, 1f - rr / tw, 1f };
            float[] vs = { 1f, 1f - top / th, b / th, 0f }; // top to bottom
            Color old = GUI.color;
            GUI.color = Dim(old * color);
            for (int i = 0; i < 3; i++)
                for (int j = 0; j < 3; j++)
                {
                    Rect dst = new Rect(xs[i], ys[j], xs[i + 1] - xs[i], ys[j + 1] - ys[j]);
                    if (dst.width <= 0f || dst.height <= 0f) continue;
                    GUI.DrawTextureWithTexCoords(dst, t, new Rect(us[i], vs[j + 1], us[i + 1] - us[i], vs[j] - vs[j + 1]), true);
                }
            GUI.color = old;
        }

        public static void Draw9(Rect r, string sprite) { Draw9(r, sprite, Color.white); }

        public static void DrawSprite(Rect r, string sprite, Color color)
        {
            Slice s;
            if (Event.current.type != EventType.Repaint || !slices.TryGetValue(sprite, out s)) return;
            Color old = GUI.color;
            GUI.color = Dim(old * color);
            GUI.DrawTexture(r, s.Tex, ScaleMode.ScaleToFit, true);
            GUI.color = old;
        }

        public static Vector2 SpriteSize(string sprite)
        {
            Slice s;
            return slices.TryGetValue(sprite, out s) ? new Vector2(s.Tex.width * Px, s.Tex.height * Px) : Vector2.zero;
        }

        static Color Dim(Color c) { if (!GUI.enabled) c.a *= 0.5f; return c; }

        // style.Apply(colour): sets the text colour and returns the style (one-liners).
        public static GUIStyle Apply(this GUIStyle s, Color c) { s.normal.textColor = c; return s; }

        static readonly Regex Tags = new Regex("</?color[^>]*>", RegexOptions.IgnoreCase);

        // Text with the black outline of the game's labels.
        public static void Text(Rect r, string text, GUIStyle s, Color color)
        {
            if (Event.current.type != EventType.Repaint || string.IsNullOrEmpty(text)) return;
            Color oldText = s.normal.textColor, oldContent = GUI.contentColor;
            Texture2D oldBg = s.normal.background;
            s.normal.background = null;
            GUI.contentColor = Color.white;
            float o = Mathf.Max(0.6f, 1.2f * Px);
            s.normal.textColor = new Color(0f, 0f, 0f, GUI.enabled ? 0.9f : 0.45f);
            GUIContent plain = new GUIContent(Tags.Replace(text, ""));
            for (int dx = -1; dx <= 1; dx++)
                for (int dy = -1; dy <= 1; dy++)
                    if (dx != 0 || dy != 0) s.Draw(new Rect(r.x + dx * o, r.y + dy * o, r.width, r.height), plain, false, false, false, false);
            s.normal.textColor = Dim(color);
            s.Draw(r, new GUIContent(text), false, false, false, false);
            s.normal.textColor = oldText;
            s.normal.background = oldBg;
            GUI.contentColor = oldContent;
        }

        // ---------- windows ----------

        // A window like the pause menu: wooden frame, title bar over the top edge.
        // Falls back to the old dark box with IMGUI's title.
        public static void Window(Rect area, string caption, GUIStyle fallback)
        {
            Init();
            if (!Ready)
            {
                GUI.Box(area, "", fallback);
                GUI.Box(area, caption);
                return;
            }
            Draw9(area, "WindowFrameSmall");
            if (string.IsNullOrEmpty(caption)) return;
            float w = Mathf.Min(area.width - 24f, title.CalcSize(new GUIContent(caption)).x + 70f);
            Rect bar = new Rect(area.x + (area.width - w) / 2f, area.y - 5f, w, 27f);
            Draw9(bar, "WindowTitle");
            Text(bar, caption, title, Cream);
        }

        // A dark plate for notes and banners (the game's tooltip background).
        public static void Plate(Rect r, string text, GUIStyle fallback)
        {
            Init();
            if (!Ready) { GUI.Label(r, text, fallback); return; }
            Draw9(r, "TooltipBg", new Color(1f, 1f, 1f, 0.92f));
            GUIStyle s = new GUIStyle(label);
            s.alignment = TextAnchor.MiddleCenter;
            s.fontSize = fallback != null && fallback.fontSize > 0 ? Mathf.Max(14, fallback.fontSize - 1) : 15;
            Text(r, text, s, Color.white);
        }

        // ---------- controls (same signatures as GUI / GUILayout) ----------

        public static bool Button(Rect r, string text)
        {
            Init();
            if (!Ready) return GUI.Button(r, text);
            bool click = GUI.Button(r, GUIContent.none, GUIStyle.none);
            if (Event.current.type == EventType.Repaint)
            {
                bool hover = GUI.enabled && r.Contains(Event.current.mousePosition);
                bool down = hover && Input.GetMouseButton(0);
                Color tint = down ? new Color(0.8f, 0.8f, 0.8f) : hover ? new Color(1.15f, 1.15f, 1.15f) : Color.white;
                Draw9(r, "Button1", tint);
                GUIStyle s = r.width < 50f ? small : button;
                Text(r, text, s, hover ? Cream : Color.white);
            }
            return click;
        }

        public static bool LButton(string text, params GUILayoutOption[] options)
        {
            Init();
            if (!Ready) return GUILayout.Button(text, options);
            Rect r = GUILayoutUtility.GetRect(new GUIContent(text), button, options);
            return Button(r, text);
        }

        public static void Label(Rect r, string text) { Label(r, text, null); }

        public static void Label(Rect r, string text, GUIStyle style)
        {
            Init();
            if (!Ready) { if (style != null) GUI.Label(r, text, style); else GUI.Label(r, text); return; }
            GUIStyle s = style ?? label;
            if (style != null) Fontify(style);
            Text(r, text, s, style != null ? style.normal.textColor : Color.white);
        }

        public static void Label(Rect r, GUIContent c, GUIStyle style) { Label(r, c.text, style); }

        public static void LLabel(string text, params GUILayoutOption[] options) { LLabel(text, null, options); }

        public static void LLabel(string text, GUIStyle style, params GUILayoutOption[] options)
        {
            Init();
            if (!Ready) { if (style != null) GUILayout.Label(text, style, options); else GUILayout.Label(text, options); return; }
            GUIStyle s = style ?? label;
            if (style != null) Fontify(style);
            Rect r = GUILayoutUtility.GetRect(new GUIContent(text), s, options);
            Text(r, text, s, style != null ? style.normal.textColor : Color.white);
        }

        public static string TextField(Rect r, string text, int max)
        {
            Init();
            if (!Ready) return GUI.TextField(r, text, max);
            Draw9(r, "TextArea");
            return GUI.TextField(r, text, max, field);
        }

        public static string LTextField(string text, int max, params GUILayoutOption[] options)
        {
            Init();
            if (!Ready) return GUILayout.TextField(text, max, options);
            Rect r = GUILayoutUtility.GetRect(new GUIContent(text), field, options);
            return TextField(r, text, max);
        }

        // Scroll views with a slim wood-coloured bar instead of IMGUI's grey one.
        static GUIStyle savedThumb;

        public static Vector2 BeginScroll(Vector2 pos, params GUILayoutOption[] options)
        {
            Init();
            if (!Ready) return GUILayout.BeginScrollView(pos, options);
            savedThumb = GUI.skin.verticalScrollbarThumb;
            GUI.skin.verticalScrollbarThumb = scrollThumb;
            return GUILayout.BeginScrollView(pos, false, false, GUIStyle.none, scrollV, GUIStyle.none, options);
        }

        public static void EndScroll()
        {
            GUILayout.EndScrollView();
            if (savedThumb != null) { GUI.skin.verticalScrollbarThumb = savedThumb; savedThumb = null; }
        }
    }
}
