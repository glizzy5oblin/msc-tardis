using UnityEngine;

namespace MscTardis
{
    // The computer's screen: a 56 x 21 grid of characters drawn from the glyph atlas (assets/tex/term_font.png, made by
    // tools/assets/make_term_font.py) into one greyscale texture, and an IMGUI overlay that shows it as a CRT: the world dimmed
    // behind, a bezel, the phosphor tint, a faint flicker, scanlines and a vignette. Only the cells that changed are redrawn.
    static class Terminal
    {
        public const int Cols = 56, Rows = 21;
        public const int Normal = 0, Dim = 1, Bright = 2, Inverse = 3;

        static int cw = 16, ch = 32, perRow = 32;
        static string chars = "";
        static Color32[] atlas;
        static int atlasW, atlasH;
        static Texture2D screen, scan, vignette, black;
        const float FlickerDepth = 0.02f;   // how far the screen's brightness wavers (was 0.06, too strong)
        static Color32[] pixels;
        static readonly char[] cell = new char[Cols * Rows];
        static readonly byte[] attr = new byte[Cols * Rows];
        static readonly char[] shown = new char[Cols * Rows];
        static readonly byte[] shownAttr = new byte[Cols * Rows];
        static bool dirty;

        public static readonly Color[] Phosphors =
        {
            new Color(0.35f, 1f, 0.45f), new Color(1f, 0.70f, 0.18f), new Color(0.88f, 0.95f, 1f),
        };
        public static readonly string[] PhosphorNames = { "GREEN", "AMBER", "WHITE" };

        public static Texture2D Picture { get { return screen; } }   // the grid's picture, for the room monitor
        public static Texture Overlay;       // a live picture drawn over a block of cells (the exterior scanner)
        public static Rect OverlayCells;     // that block: x y w h in cells
        public static float Open;   // 0 closed .. 1 fully open: the CRT power-on and collapse
        public static int Phosphor;

        public static void Init()
        {
            var info = Res.Lines("term_font.txt");
            var head = info[0].Split(' ');
            cw = int.Parse(head[0]); ch = int.Parse(head[1]); perRow = int.Parse(head[2]);
            chars = info[1];
            var tex = Res.Tex("term_font.png", false, TextureWrapMode.Clamp);
            atlas = tex.GetPixels32();
            atlasW = tex.width; atlasH = tex.height;
            screen = new Texture2D(Cols * cw, Rows * ch, TextureFormat.RGBA32, false);
            screen.filterMode = FilterMode.Bilinear;
            screen.wrapMode = TextureWrapMode.Clamp;
            pixels = new Color32[Cols * cw * Rows * ch];
            for (int i = 0; i < pixels.Length; i++) pixels[i] = new Color32(0, 0, 0, 255);
            screen.SetPixels32(pixels);
            screen.Apply(false);
            for (int i = 0; i < shown.Length; i++) { shown[i] = ' '; shownAttr[i] = 0; }
            Clear();

            scan = new Texture2D(1, 4, TextureFormat.RGBA32, false) { filterMode = FilterMode.Point, wrapMode = TextureWrapMode.Repeat };
            scan.SetPixels32(new[] { new Color32(0, 0, 0, 0), new Color32(0, 0, 0, 70), new Color32(0, 0, 0, 0), new Color32(0, 0, 0, 0) });
            scan.Apply(false);
            vignette = new Texture2D(64, 48, TextureFormat.RGBA32, false) { filterMode = FilterMode.Bilinear, wrapMode = TextureWrapMode.Clamp };
            var vp = new Color32[64 * 48];
            for (int y = 0; y < 48; y++)
                for (int x = 0; x < 64; x++)
                {
                    float dx = (x - 31.5f) / 32f, dy = (y - 23.5f) / 24f;
                    float r = Mathf.Clamp01((dx * dx + dy * dy - 0.7f) / 1.3f);
                    vp[y * 64 + x] = new Color32(0, 0, 0, (byte)(r * r * 150f));
                }
            vignette.SetPixels32(vp);
            vignette.Apply(false);
            black = new Texture2D(1, 1, TextureFormat.RGBA32, false);
            black.SetPixel(0, 0, Color.white);
            black.Apply(false);
        }

        // ---------------------------------------------------------------------------------------------------------
        // The grid.

        public static void Clear()
        {
            for (int i = 0; i < cell.Length; i++) { cell[i] = ' '; attr[i] = Normal; }
        }

        public static char At(int x, int y) { return cell[y * Cols + x]; }

        public static void Put(int x, int y, string s, int a = Normal)
        {
            if (y < 0 || y >= Rows) return;
            for (int i = 0; i < s.Length; i++)
            {
                int cx = x + i;
                if (cx < 0 || cx >= Cols) continue;
                cell[y * Cols + cx] = s[i];
                attr[y * Cols + cx] = (byte)a;
            }
        }

        public static void Fill(int x, int y, int w, int a)
        {
            for (int i = 0; i < w; i++)
            {
                int cx = x + i;
                if (cx < 0 || cx >= Cols || y < 0 || y >= Rows) continue;
                attr[y * Cols + cx] = (byte)a;
            }
        }

        public static void Center(int y, string s, int a = Normal) { Put((Cols - s.Length) / 2, y, s, a); }

        // A single or double line frame, x y w h in cells.
        public static void Box(int x, int y, int w, int h, bool dbl = false, int a = Normal)
        {
            string tl = dbl ? "╔" : "┌", tr = dbl ? "╗" : "┐", bl = dbl ? "╚" : "└", br = dbl ? "╝" : "┘", hz = dbl ? "═" : "─", vt = dbl ? "║" : "│";
            Put(x, y, tl + new string(hz[0], w - 2) + tr, a);
            Put(x, y + h - 1, bl + new string(hz[0], w - 2) + br, a);
            for (int i = 1; i < h - 1; i++) { Put(x, y + i, vt, a); Put(x + w - 1, y + i, vt, a); }
        }

        // A bar of w cells: █ filled, ░ empty, for 0..1.
        public static string Bar(float v, int w)
        {
            int n = Mathf.Clamp(Mathf.RoundToInt(Mathf.Clamp01(v) * w), 0, w);
            return new string('█', n) + new string('░', w - n);
        }

        // Only the first `limit` non-blank cells (in reading order) stay: a page typing itself in. -1 shows everything.
        public static int Mask(int limit)
        {
            if (limit < 0) return -1;
            int seen = 0, last = -1;
            for (int i = 0; i < cell.Length; i++)
            {
                if (cell[i] == ' ' && attr[i] != Inverse) continue;
                if (seen >= limit) { cell[i] = ' '; if (attr[i] != Inverse) attr[i] = Normal; }
                else { seen++; last = i; }
            }
            return last;
        }


        // ---------------------------------------------------------------------------------------------------------
        // Into the texture.

        static byte Level(int a) { return a == Dim ? (byte)120 : a == Bright ? (byte)255 : (byte)205; }

        public static void Flush()
        {
            if (screen == null) return;
            int w = Cols * cw;
            for (int i = 0; i < cell.Length; i++)
            {
                if (cell[i] == shown[i] && attr[i] == shownAttr[i]) continue;
                shown[i] = cell[i]; shownAttr[i] = attr[i];
                dirty = true;
                int gx = i % Cols, gy = i / Cols;
                int gi = chars.IndexOf(cell[i]);
                bool inv = attr[i] == Inverse;
                byte lv = Level(attr[i]);
                for (int y = 0; y < ch; y++)
                {
                    int py = (Rows - 1 - gy) * ch + (ch - 1 - y);   // textures are bottom-up
                    for (int x = 0; x < cw; x++)
                    {
                        int a = 0;
                        if (gi >= 0)
                        {
                            int ax = (gi % perRow) * cw + x, ay = (gi / perRow) * ch + y;
                            a = atlas[(atlasH - 1 - ay) * atlasW + ax].a;   // the atlas pixels are bottom-up too
                        }
                        int v = inv ? 235 - a * 235 / 255 : a * lv / 255;
                        if (inv && a == 0) v = 235;
                        pixels[py * w + gx * cw + x] = new Color32((byte)v, (byte)v, (byte)v, 255);
                    }
                }
            }
            if (dirty) { screen.SetPixels32(pixels); screen.Apply(false); dirty = false; }
        }

        // ---------------------------------------------------------------------------------------------------------
        // The CRT, drawn over the screen.

        public static void Draw()
        {
            if (Open <= 0.001f || screen == null) return;
            float sw = Screen.width, sh = Screen.height;
            GUI.depth = -60;
            var prev = GUI.color;
            GUI.color = new Color(0f, 0f, 0f, 0.72f * Mathf.Clamp01(Open * 2f));
            GUI.DrawTexture(new Rect(0, 0, sw, sh), black);

            float h = sh * 0.82f, w = h * 4f / 3f;
            if (w > sw * 0.96f) { w = sw * 0.96f; h = w * 3f / 4f; }
            // power on: a bright line opens to the full height; closing collapses it back
            float open = Open;
            float grow = Mathf.Clamp01(open * 1.6f), widen = Mathf.Clamp01((open - 0.55f) * 3f + 0.05f);
            float ph = Mathf.Max(3f, h * grow * grow), pw = Mathf.Max(8f, w * Mathf.Lerp(0.04f, 1f, widen));
            var rect = new Rect((sw - pw) / 2f, (sh - ph) / 2f, pw, ph);
            float bez = Mathf.Max(10f, h * 0.035f);
            GUI.color = new Color(0.04f, 0.045f, 0.04f, 1f);
            GUI.DrawTexture(new Rect(rect.x - bez, rect.y - bez, rect.width + 2f * bez, rect.height + 2f * bez), black);
            GUI.color = new Color(0f, 0.01f, 0f, 1f);
            GUI.DrawTexture(rect, black);

            var tint = Phosphors[Mathf.Clamp(Phosphor, 0, Phosphors.Length - 1)];
            float flick = 1f - FlickerDepth + FlickerDepth * Mathf.Sin(Time.time * 47f) * Mathf.Sin(Time.time * 7.3f);
            float lit = open < 1f ? 1.6f - open * 0.6f : 1f;
            GUI.color = new Color(tint.r * flick * lit, tint.g * flick * lit, tint.b * flick * lit, 1f);
            GUI.DrawTexture(rect, screen, ScaleMode.StretchToFill, false);

            if (Overlay != null)
            {
                var oc = OverlayCells;
                var orect = new Rect(rect.x + rect.width * oc.x / Cols, rect.y + rect.height * oc.y / Rows, rect.width * oc.width / Cols, rect.height * oc.height / Rows);
                GUI.color = new Color(tint.r, tint.g, tint.b, 1f) * 0.95f;
                GUI.DrawTexture(orect, Overlay, ScaleMode.StretchToFill, false);
            }
            GUI.color = Color.white;
            GUI.DrawTextureWithTexCoords(rect, scan, new Rect(0, 0, 1, rect.height / 4f));
            GUI.DrawTexture(rect, vignette, ScaleMode.StretchToFill, true);
            GUI.color = prev;
        }

    }
}
