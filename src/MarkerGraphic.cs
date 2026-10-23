using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;

namespace ValheimNavigator
{
    internal struct Dot
    {
        internal float X, Y, Radius;
        internal Color32 Color;
    }

    internal struct Seg
    {
        internal float X0, Y0, X1, Y1, Width;
        internal Color32 Color;
    }

    internal struct Symbol
    {
        internal float X, Y, Radius;
        internal int Code;
        internal Color32 Color;
    }

    // every dot and line of the marker layer drawn as one mesh. dots use a small circle texture, lines use its solid middle.
    // it takes no clicks, so the large map keeps dragging and clicking as before
    internal sealed class MarkerGraphic : MaskableGraphic
    {
        private const int TextureSize = 64;
        private const int MaxShapes = 15000;
        private static Texture2D _circle;

        internal readonly List<Dot> Dots = new List<Dot>();
        internal readonly List<Seg> Segs = new List<Seg>();
        internal readonly List<Symbol> Symbols = new List<Symbol>();

        public override Texture mainTexture => Circle();

        private static Texture2D Circle()
        {
            if (_circle != null) return _circle;
            _circle = new Texture2D(TextureSize, TextureSize, TextureFormat.RGBA32, false) { filterMode = FilterMode.Bilinear, wrapMode = TextureWrapMode.Clamp };
            float half = TextureSize / 2f;
            var pixels = new Color32[TextureSize * TextureSize];
            for (int y = 0; y < TextureSize; y++)
                for (int x = 0; x < TextureSize; x++)
                {
                    float dx = x + 0.5f - half, dy = y + 0.5f - half;
                    float edge = Mathf.Clamp01(half - Mathf.Sqrt(dx * dx + dy * dy));
                    pixels[y * TextureSize + x] = new Color32(255, 255, 255, (byte)(edge * 255f));
                }
            _circle.SetPixels32(pixels);
            _circle.Apply(false);
            return _circle;
        }

        // the data changed, the mesh is made again
        internal void Refresh()
        {
            SetVerticesDirty();
        }

        protected override void OnPopulateMesh(VertexHelper vh)
        {
            vh.Clear();
            var middle = new Vector2(0.5f, 0.5f);
            int shapes = 0;
            for (int i = 0; i < Segs.Count && shapes < MaxShapes && vh.currentVertCount < 59996; i++, shapes++)
            {
                var s = Segs[i];
                float dx = s.X1 - s.X0, dy = s.Y1 - s.Y0, length = Mathf.Sqrt(dx * dx + dy * dy);
                if (length < 0.01f) continue;
                float nx = -dy / length * s.Width * 0.5f, ny = dx / length * s.Width * 0.5f;
                int n = vh.currentVertCount;
                vh.AddVert(new Vector3(s.X0 + nx, s.Y0 + ny), s.Color, middle);
                vh.AddVert(new Vector3(s.X0 - nx, s.Y0 - ny), s.Color, middle);
                vh.AddVert(new Vector3(s.X1 - nx, s.Y1 - ny), s.Color, middle);
                vh.AddVert(new Vector3(s.X1 + nx, s.Y1 + ny), s.Color, middle);
                vh.AddTriangle(n, n + 1, n + 2);
                vh.AddTriangle(n, n + 2, n + 3);
            }
            for (int i = 0; i < Dots.Count && shapes < MaxShapes && vh.currentVertCount < 59996; i++, shapes++)
            {
                var d = Dots[i];
                int n = vh.currentVertCount;
                vh.AddVert(new Vector3(d.X - d.Radius, d.Y - d.Radius), d.Color, new Vector2(0f, 0f));
                vh.AddVert(new Vector3(d.X - d.Radius, d.Y + d.Radius), d.Color, new Vector2(0f, 1f));
                vh.AddVert(new Vector3(d.X + d.Radius, d.Y + d.Radius), d.Color, new Vector2(1f, 1f));
                vh.AddVert(new Vector3(d.X + d.Radius, d.Y - d.Radius), d.Color, new Vector2(1f, 0f));
                vh.AddTriangle(n, n + 1, n + 2);
                vh.AddTriangle(n, n + 2, n + 3);
            }
            foreach (var symbol in Symbols) Glyph(vh, symbol);
        }

        private static void Stroke(VertexHelper vh, Symbol s, float x0, float y0, float x1, float y1)
        {
            if (vh.currentVertCount > 59990) return;
            float scale = s.Radius * 0.70f;
            var a = new Vector2(s.X + x0 * scale, s.Y + y0 * scale);
            var b = new Vector2(s.X + x1 * scale, s.Y + y1 * scale);
            var normal = new Vector2(-(b.y - a.y), b.x - a.x).normalized * Mathf.Max(0.45f, s.Radius * 0.10f);
            int n = vh.currentVertCount; var uv = new Vector2(0.5f, 0.5f);
            vh.AddVert((Vector3)(a + normal), s.Color, uv); vh.AddVert((Vector3)(a - normal), s.Color, uv);
            vh.AddVert((Vector3)(b - normal), s.Color, uv); vh.AddVert((Vector3)(b + normal), s.Color, uv);
            vh.AddTriangle(n, n + 1, n + 2); vh.AddTriangle(n, n + 2, n + 3);
        }

        private static void Glyph(VertexHelper vh, Symbol s)
        {
            if (s.Code == 13)
            {
                Stroke(vh, s, -1f, -0.6f, 1f, -0.6f); Stroke(vh, s, -1f, -1f, -1f, 0.8f); Stroke(vh, s, 1f, -1f, 1f, 0.2f); Stroke(vh, s, -1f, 0.2f, 1f, 0.2f); return;
            }
            if (s.Code == 14)
            {
                Stroke(vh, s, -1f, -0.3f, -0.5f, -0.9f); Stroke(vh, s, -0.5f, -0.9f, 0.5f, -0.9f); Stroke(vh, s, 0.5f, -0.9f, 1f, -0.3f); Stroke(vh, s, 0f, -0.3f, 0f, 1f); Stroke(vh, s, 0f, 1f, 0.9f, 0.1f); return;
            }
            if (s.Code == 15)
            {
                Stroke(vh, s, -0.6f, -1f, -0.6f, 0.6f); Stroke(vh, s, -0.6f, 0.6f, 0f, 1f); Stroke(vh, s, 0f, 1f, 0.6f, 0.6f); Stroke(vh, s, 0.6f, 0.6f, 0.6f, -1f); Stroke(vh, s, -0.6f, -1f, 0.6f, -1f); return;
            }
            if (s.Code == 16)
            {
                Stroke(vh, s, -0.8f, 0.8f, 0f, -0.8f); Stroke(vh, s, 0f, -0.8f, 0.8f, 0.8f); Stroke(vh, s, -0.8f, 0.8f, -1f, 1.2f); Stroke(vh, s, 0.8f, 0.8f, 1f, 1.2f); return;
            }
            if (s.Code == 17)
            {
                Stroke(vh, s, -1f, -0.8f, 1f, -0.8f); Stroke(vh, s, -1f, -0.8f, -1f, 0.8f); Stroke(vh, s, 1f, -0.8f, 1f, 0.8f); Stroke(vh, s, -1f, 0.8f, 1f, 0.8f); Stroke(vh, s, -1f, 0f, 1f, 0f); Stroke(vh, s, 0f, -0.4f, 0f, 0.3f); return;
            }
            if (s.Code >= 32)
            {
                int bits = s.Code - 32;
                Stroke(vh, s, 0f, -1f, 0f, 1f);
                for (int i = 0; i < 6; i++) if ((bits & (1 << i)) != 0)
                {
                    float y = (i / 2 - 1) * 0.65f, side = i % 2 == 0 ? -1f : 1f;
                    Stroke(vh, s, 0f, y - 0.25f, side * 0.8f, y + 0.25f);
                }
                return;
            }
            if (s.Code == 3)
            {
                Stroke(vh, s, -0.9f, 0f, 0.9f, 0f); Stroke(vh, s, -0.5f, -0.9f, 0.5f, 0.9f); Stroke(vh, s, -0.5f, 0.9f, 0.5f, -0.9f); return;
            }
            if (s.Code == 4 || s.Code == 11)
            {
                Stroke(vh, s, -0.8f, -0.8f, 0.8f, 0.8f); Stroke(vh, s, -0.8f, 0.8f, 0.8f, -0.8f);
                if (s.Code == 11) Stroke(vh, s, -0.8f, 0.9f, 0.8f, 0.9f);
                return;
            }
            if (s.Code >= 5 && s.Code <= 7)
            {
                Stroke(vh, s, -1f, -0.7f, 0f, 0.9f); Stroke(vh, s, 0f, 0.9f, 1f, -0.7f); Stroke(vh, s, -1f, -0.7f, 1f, -0.7f);
                if (s.Code != 5) Stroke(vh, s, -0.4f, s.Code == 6 ? -0.2f : 0.3f, 0.4f, -0.2f);
                return;
            }
            Stroke(vh, s, -0.75f, -0.9f, -0.75f, 0.7f); Stroke(vh, s, 0.75f, -0.9f, 0.75f, 0.7f); Stroke(vh, s, -0.75f, 0.7f, 0.75f, 0.7f);
            if (s.Code == 1 || s.Code == 2 || s.Code == 10) Stroke(vh, s, 0f, -0.9f, 0f, 0.3f);
            if (s.Code == 2 || s.Code == 9 || s.Code == 12) Stroke(vh, s, -0.75f, -0.2f, 0.75f, -0.2f);
            if (s.Code == 8 || s.Code == 9) { Stroke(vh, s, -0.75f, 0.7f, -0.75f, 1.1f); Stroke(vh, s, 0.75f, 0.7f, 0.75f, 1.1f); }
        }
    }
}
