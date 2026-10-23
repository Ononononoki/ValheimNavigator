using System;
using System.Collections.Generic;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace ValheimNavigator
{
    // the marker layer on the large map. every place of the kinds that are switched on is drawn by one mesh, only what is in view,
    // single places as dots and places closer than the cluster size as one numbered circle. it adds no game pins, so the number of
    // places does not slow the map down. the pin limit only counts the single dots
    internal static class Layers
    {
        private const float SingleRadius = 5f;
        private const float DoneRadius = 4f;
        private const float OutlinePixels = 1.5f;
        private const float GroupBase = 9f;
        private const float GroupGrow = 3f;
        private const float GroupMax = 24f;
        private const float PortalRadius = 3f;
        private const float LineWidth = 2f;
        private const float HoverPixels = 5f;
        private const float DoneAlpha = 90f;
        private const float ViewMargin = 0.03f;
        private const int LabelPool = 80;
        private const float TipOffset = 14f;

        private static readonly Color32 Outline = new Color32(0, 0, 0, 200);
        private static readonly Color32 PortalColor = new Color32(174, 157, 177, 58);
        private static readonly Color32 MixedColor = new Color32(235, 235, 235, 235);
        private static readonly Color LabelColor = new Color(0.05f, 0.05f, 0.05f, 1f);

        private struct Mark
        {
            internal float U, V;
            internal Entry Entry;
            internal int Category;
        }

        private struct Target
        {
            internal float X, Y, Radius;
            internal Entry Entry;
            internal int Count;
            internal int Category;
            internal PortalInfo Portal;
            internal KnownMark Known;
            internal string Group;
        }

        private struct KnownGroup
        {
            internal KnownMark Mark;
            internal int Count;
            internal float X, Y;
            internal bool Mixed;
        }

        private struct SingleItem
        {
            internal int Cell;
            internal float Distance;
        }

        private static Mark[] _marks = new Mark[0];
        private static int _count;
        private static bool _marksDirty = true;
        private static bool _drawDirty = true;
        private static bool _warned;
        private static int _indexVersion = -1;
        private static int _lastPortalVersion = -2;
        private static Rect _lastUv;
        private static Vector2 _lastSize;
        private static MarkerGraphic _graphic;
        private static RectTransform _rect;
        private static Canvas _canvas;
        private static int[] _cellCount = new int[0];
        private static int[] _cellFirst = new int[0];
        private static int[] _cellCategory = new int[0];
        private static bool[] _cellMixed = new bool[0];
        private static float[] _cellX = new float[0];
        private static float[] _cellY = new float[0];
        private static readonly List<SingleItem> Singles = new List<SingleItem>();
        private static readonly List<Target> Targets = new List<Target>();
        private static readonly List<TMP_Text> Labels = new List<TMP_Text>();
        private static readonly List<KeyValuePair<PortalInfo, PortalInfo>> Links = new List<KeyValuePair<PortalInfo, PortalInfo>>();
        private static readonly Comparison<SingleItem> ByDistance = (a, b) => a.Distance.CompareTo(b.Distance);
        private static readonly Dictionary<long, KnownGroup> KnownCells = new Dictionary<long, KnownGroup>();
        private static readonly List<KeyValuePair<float, KnownMark>> KnownSingles = new List<KeyValuePair<float, KnownMark>>();
        private static readonly int[] FamilyShown = new int[16];
        private static int[] CategoryShown = new int[0];
        private static GameObject _tip;
        private static TMP_Text _tipText;
        private static TMP_Text _hidden;

        // numbers for the layers tab
        internal static int Wanted;
        internal static int InView;
        internal static int SinglesShown;
        internal static int SinglesHidden;
        internal static int GroupsShown;
        internal static int KnownHidden;

        // the layer was drawn again, the layers tab shows the new numbers
        internal static event Action Drawn;

        // a switch, the index or the done marks changed, the places are collected and drawn again
        internal static void Dirty()
        {
            _marksDirty = true;
            _drawDirty = true;
        }

        // the large map closes
        internal static void Close()
        {
            if (_tip != null) _tip.SetActive(false);
            MarkerMenu.Hide();
        }

        // every frame while the large map is open
        internal static void Frame(Minimap map)
        {
            if (!Plugin.Enabled.Value) return;
            try
            {
                var image = map.m_mapImageLarge;
                if (image == null) return;
                if (_graphic == null) Create(image);
                if (Session.Ready && Session.Version != _indexVersion)
                {
                    _indexVersion = Session.Version;
                    _marksDirty = true;
                }
                if (_marksDirty)
                {
                    Collect(map);
                    _drawDirty = true;
                }
                bool lines = Settings.PortalLines.Value && Settings.PortalsOn.Value;
                if (lines) Portals.Refresh(Settings.PortalSeconds.Value);
                int portals = lines ? Portals.Version : -1;
                var rect = _rect.rect;
                var uv = image.uvRect;
                if (_drawDirty || uv != _lastUv || rect.size != _lastSize || portals != _lastPortalVersion)
                {
                    Draw(rect, uv, lines);
                    _lastUv = uv;
                    _lastSize = rect.size;
                    _lastPortalVersion = portals;
                    _drawDirty = false;
                }
                Hover(map);
                CursorReadout.Frame(map, _rect, CanvasCamera());
            }
            catch (Exception ex)
            {
                if (!_warned) Plugin.Log.LogWarning("the marker layer failed: " + ex);
                _warned = true;
            }
        }

        private static void Create(RawImage image)
        {
            // the canvas renderer is added here, a graphic from a plugin dll does not get it from its RequireComponent
            var go = new GameObject("ValheimNavigator_Markers", typeof(RectTransform), typeof(CanvasRenderer), typeof(RectMask2D));
            go.transform.SetParent(image.transform, false);
            _rect = (RectTransform)go.transform;
            _rect.anchorMin = Vector2.zero;
            _rect.anchorMax = Vector2.one;
            _rect.pivot = new Vector2(0.5f, 0.5f);
            _rect.offsetMin = Vector2.zero;
            _rect.offsetMax = Vector2.zero;
            _graphic = go.AddComponent<MarkerGraphic>();
            _graphic.raycastTarget = false;
            _canvas = image.GetComponentInParent<Canvas>();
            if (Plugin.LogCounters.Value) Plugin.Log.LogInfo("marker layer built on the large map");
        }

        // the places of the kinds that are switched on, with their position on the map texture
        private static void Collect(Minimap map)
        {
            _count = 0;
            _marksDirty = false;
            if (!Session.Ready) { Wanted = 0; return; }
            float size = map.m_textureSize, pixel = map.m_pixelSize, half = size / 2f;
            foreach (var kind in Session.Kinds.Values)
            {
                if (!Settings.KindOn(kind)) continue;
                int category = Array.IndexOf(Categories.Order, kind.Category);
                var items = kind.Items;
                for (int i = 0; i < items.Count; i++)
                {
                    if (_count == _marks.Length) Array.Resize(ref _marks, Math.Max(256, _marks.Length * 2));
                    var e = items[i];
                    if (Settings.SpawnFilter.Value != 0 && e.Placed != (Settings.SpawnFilter.Value == 1)) continue;
                    _marks[_count++] = new Mark { U = (e.X / pixel + half) / size, V = (e.Z / pixel + half) / size, Entry = e, Category = category };
                }
            }
            Wanted = _count;
        }

        private static Color32 ColorOf(int category)
        {
            return category < 0 ? MixedColor : Ui.DotColor(Categories.Order[category]);
        }

        private static void EnsureCells(int cells)
        {
            if (_cellCount.Length >= cells) return;
            int size = Math.Max(cells, 1024);
            _cellCount = new int[size];
            _cellFirst = new int[size];
            _cellCategory = new int[size];
            _cellMixed = new bool[size];
            _cellX = new float[size];
            _cellY = new float[size];
        }

        private static void AddDot(List<Dot> dots, float x, float y, float radius, Color32 color)
        {
            dots.Add(new Dot { X = x, Y = y, Radius = radius + OutlinePixels, Color = Outline });
            dots.Add(new Dot { X = x, Y = y, Radius = radius, Color = color });
        }

        private static TMP_Text LabelAt(int index)
        {
            while (Labels.Count <= index)
            {
                var text = Ui.Label(_rect, "Count" + Labels.Count, 0f, 0f, 48f, 20f, 13f, TextAlignmentOptions.Midline, LabelColor);
                var rect = (RectTransform)text.transform;
                rect.anchorMin = rect.anchorMax = rect.pivot = new Vector2(0.5f, 0.5f);
                text.fontStyle = FontStyles.Bold;
                text.overflowMode = TextOverflowModes.Overflow;
                Labels.Add(text);
            }
            return Labels[index];
        }

        private const float PortalJump = 7f;
        private static readonly Color32 PortalJumpColor = new Color32(170, 120, 210, 235);

        private struct GroupItem { internal float X, Y; internal int Count, Category; }
        private static readonly List<GroupItem> Groups = new List<GroupItem>();
        // above this many places in view the grouping cell grows
        private const float CrowdedPlaces = 300f;

        private static float GroupRadius(int count) { return Mathf.Min(GroupMax, GroupBase + GroupGrow * Mathf.Log(count, 2f)); }

        // groups whose circles overlap become one group at their weighted middle, repeated until none overlap, so circles never pile up
        private static void MergeGroups()
        {
            bool merged = true;
            float far = 2f * GroupMax;
            for (int pass = 0; merged && pass < 4; pass++)
            {
                merged = false;
                // sorted left to right, only groups within two of the largest radii to the right can overlap
                Groups.Sort((a, b) => a.X.CompareTo(b.X));
                for (int i = 0; i < Groups.Count; i++)
                {
                    var a = Groups[i];
                    for (int j = i + 1; j < Groups.Count && Groups[j].X - a.X <= far; j++)
                    {
                        var b = Groups[j];
                        float reach = (GroupRadius(a.Count) + GroupRadius(b.Count)) * 0.85f, dx = a.X - b.X, dy = a.Y - b.Y;
                        if (dx * dx + dy * dy > reach * reach) continue;
                        int n = a.Count + b.Count;
                        a = new GroupItem { X = (a.X * a.Count + b.X * b.Count) / n, Y = (a.Y * a.Count + b.Y * b.Count) / n, Count = n, Category = a.Category == b.Category ? a.Category : -1 };
                        Groups.RemoveAt(j--);
                        merged = true;
                    }
                    Groups[i] = a;
                }
            }
        }

        // sorts the places in view into cells, a cell with one place is a dot, a cell with more is a numbered circle
        private static void Draw(Rect rect, Rect uv, bool portalLines)
        {
            float width = rect.width, height = rect.height;
            var dots = _graphic.Dots;
            var segs = _graphic.Segs;
            dots.Clear();
            segs.Clear();
            _graphic.Symbols.Clear();
            Targets.Clear();
            Singles.Clear();
            InView = 0;
            SinglesShown = 0;
            GroupsShown = 0;
            int labels = 0;
            if (width < 1f || height < 1f || uv.width <= 0f || uv.height <= 0f) { _graphic.Refresh(); HideLabels(0); return; }

            // crowded views group more: the cell grows with the number of places in view, up to twice the set size
            float invW0 = 1f / uv.width, invH0 = 1f / uv.height;
            int crowd = 0;
            for (int i = 0; i < _count; i++)
            {
                float lx = (_marks[i].U - uv.x) * invW0, ly = (_marks[i].V - uv.y) * invH0;
                if (lx >= 0f && lx <= 1f && ly >= 0f && ly <= 1f) crowd++;
            }
            float cell = Settings.ClusterPixels.Value * Mathf.Clamp(Mathf.Sqrt(crowd / CrowdedPlaces), 1f, 2f);
            int cols = (int)(width / cell) + 1, rows = (int)(height / cell) + 1, cells = cols * rows;
            EnsureCells(cells);
            Array.Clear(_cellCount, 0, cells);
            Array.Clear(_cellX, 0, cells);
            Array.Clear(_cellY, 0, cells);
            float invW = 1f / uv.width, invH = 1f / uv.height;
            bool covered = Panel.CoveredArea(_rect, out var cover);
            for (int i = 0; i < _count; i++)
            {
                float lx = (_marks[i].U - uv.x) * invW, ly = (_marks[i].V - uv.y) * invH;
                if (lx < -ViewMargin || lx > 1f + ViewMargin || ly < -ViewMargin || ly > 1f + ViewMargin) continue;
                if (covered && cover.Contains(new Vector2(rect.xMin + lx * width, rect.yMin + ly * height))) continue;
                int cx = Mathf.Clamp((int)(lx * width / cell), 0, cols - 1), cy = Mathf.Clamp((int)(ly * height / cell), 0, rows - 1);
                int c = cy * cols + cx;
                if (_cellCount[c] == 0)
                {
                    _cellFirst[c] = i;
                    _cellCategory[c] = _marks[i].Category;
                    _cellMixed[c] = false;
                }
                else if (_cellCategory[c] != _marks[i].Category) _cellMixed[c] = true;
                _cellCount[c]++;
                _cellX[c] += rect.xMin + lx * width;
                _cellY[c] += rect.yMin + ly * height;
                InView++;
            }

            // single places are ranked by distance to the player, or to the middle of the view when the player is not in it
            float centerX = rect.center.x, centerY = rect.center.y;
            var player = Session.PlayerPosition();
            if (player.HasValue)
            {
                var map = Minimap.instance;
                float span = map.m_textureSize * map.m_pixelSize;
                float px = rect.xMin + ((player.Value.x / span + 0.5f - uv.x) / uv.width) * rect.width, py = rect.yMin + ((player.Value.z / span + 0.5f - uv.y) / uv.height) * rect.height;
                if (rect.Contains(new Vector2(px, py))) { centerX = px; centerY = py; }
            }
            Groups.Clear();
            for (int c = 0; c < cells; c++)
            {
                int n = _cellCount[c];
                if (n == 0) continue;
                float x = _cellX[c] / n, y = _cellY[c] / n;
                if (n == 1)
                {
                    Singles.Add(new SingleItem { Cell = c, Distance = (x - centerX) * (x - centerX) + (y - centerY) * (y - centerY) });
                    continue;
                }
                Groups.Add(new GroupItem { X = x, Y = y, Count = n, Category = _cellMixed[c] ? -1 : _cellCategory[c] });
            }
            MergeGroups();
            // single places hidden under a group's circle join that group
            for (int k = Singles.Count - 1; k >= 0; k--)
            {
                int c = Singles[k].Cell;
                for (int g = 0; g < Groups.Count; g++)
                {
                    var group = Groups[g];
                    float r = GroupRadius(group.Count), dx = _cellX[c] - group.X, dy = _cellY[c] - group.Y;
                    if (dx * dx + dy * dy > r * r) continue;
                    if (group.Category != _marks[_cellFirst[c]].Category) group.Category = -1;
                    group.Count++; Groups[g] = group;
                    Singles.RemoveAt(k);
                    break;
                }
            }
            // larger groups first, so smaller groups and single places are drawn on top of them
            Groups.Sort((a, b) => b.Count.CompareTo(a.Count));
            foreach (var group in Groups)
            {
                float radius = GroupRadius(group.Count);
                AddDot(dots, group.X, group.Y, radius, ColorOf(group.Category));
                Targets.Add(new Target { X = group.X, Y = group.Y, Radius = radius, Count = group.Count, Category = group.Category });
                if (labels < LabelPool)
                {
                    var text = LabelAt(labels++);
                    text.text = group.Count.ToString();
                    text.fontSize = 13f; text.color = LabelColor;
                    ((RectTransform)text.transform).anchoredPosition = new Vector2(group.X, group.Y);
                    text.gameObject.SetActive(true);
                }
                GroupsShown++;
            }
            HideLabels(labels);

            Singles.Sort(ByDistance);
            // the pin limit counts per category, the nearest of each category are kept
            int limit = Settings.MaxPins.Value, take = 0;
            if (CategoryShown.Length < Categories.Order.Length + 1) CategoryShown = new int[Categories.Order.Length + 1];
            Array.Clear(CategoryShown, 0, CategoryShown.Length);
            for (int k = 0; k < Singles.Count; k++)
            {
                int c = Singles[k].Cell;
                var mark = _marks[_cellFirst[c]];
                if (limit > 0 && CategoryShown[mark.Category + 1]++ >= limit) continue;
                take++;
                float x = _cellX[c], y = _cellY[c];
                var color = ColorOf(mark.Category);
                float radius = Settings.DotSize.Value;
                if (mark.Entry.Done) { color.a = (byte)DoneAlpha; radius = DoneRadius; }
                AddDot(dots, x, y, radius, color);
                if (Settings.TypeIcons.Value && TypeSymbols.Code(mark.Entry) > 0) _graphic.Symbols.Add(new Symbol { X = x, Y = y, Radius = radius, Code = TypeSymbols.Code(mark.Entry), Color = new Color32(35, 30, 23, mark.Entry.Done ? (byte)110 : (byte)235) });
                if (Settings.Labels.Value && uv.width < 0.35f && labels < LabelPool)
                {
                    var text = LabelAt(labels++); text.text = mark.Entry.Kind;
                    text.fontSize = 12f; text.color = Ui.Ink;
                    ((RectTransform)text.transform).anchoredPosition = new Vector2(x, y + radius + 12f);
                    text.gameObject.SetActive(true);
                }
                Targets.Add(new Target { X = x, Y = y, Radius = radius, Entry = mark.Entry, Category = mark.Category });
                SinglesShown++;
            }

            SinglesHidden = Singles.Count - take;
            if (portalLines) DrawPortals(rect, uv, dots, segs, covered, cover);
            DrawKnown(rect, uv, dots, segs, covered, cover, ref labels);
            DrawRoute(rect, uv, segs, dots, ref labels);
            HideLabels(labels);
            ShowHidden(SinglesHidden + KnownHidden);
            _graphic.Refresh();
            Drawn?.Invoke();
        }

        // a line at the top of the map says how many markers the pin limit holds back
        private static void ShowHidden(int count)
        {
            if (_hidden == null)
            {
                _hidden = Ui.Label(_rect, "HiddenNote", 0f, 0f, 520f, 26f, 15f, TextAlignmentOptions.Midline, Ui.Ink);
                var rect = (RectTransform)_hidden.transform;
                rect.anchorMin = rect.anchorMax = rect.pivot = new Vector2(0.5f, 1f);
                rect.anchoredPosition = new Vector2(0f, -8f);
                _hidden.fontStyle = FontStyles.Bold;
                _hidden.outlineWidth = 0.2f;
                _hidden.outlineColor = new Color32(0, 0, 0, 255);
            }
            bool show = count > 0;
            if (show) _hidden.text = count + " markers hidden by the pin limit. Zoom in or raise Most pins.";
            if (_hidden.gameObject.activeSelf != show) _hidden.gameObject.SetActive(show);
        }

        private static void HideLabels(int used)
        {
            for (int i = used; i < Labels.Count; i++)
                if (Labels[i].gameObject.activeSelf) Labels[i].gameObject.SetActive(false);
        }

        // your portals as dots, and a line between portals that share a name, in the order the game lists them
        private static void DrawPortals(Rect rect, Rect uv, List<Dot> dots, List<Seg> segs, bool covered, Rect cover)
        {
            var map = Minimap.instance;
            float size = map.m_textureSize, pixel = map.m_pixelSize, half = size / 2f;
            var byTag = new Dictionary<string, PortalInfo>();
            foreach (var portal in Portals.List)
            {
                float lx = ((portal.X / pixel + half) / size - uv.x) / uv.width, ly = ((portal.Z / pixel + half) / size - uv.y) / uv.height;
                float x = rect.xMin + lx * rect.width, y = rect.yMin + ly * rect.height;
                if (!string.IsNullOrEmpty(portal.Tag) && byTag.TryGetValue(portal.Tag, out var other))
                {
                    float ox = rect.xMin + (((other.X / pixel + half) / size - uv.x) / uv.width) * rect.width;
                    float oy = rect.yMin + (((other.Z / pixel + half) / size - uv.y) / uv.height) * rect.height;
                    DrawDashes(rect, segs, ox, oy, x, y);
                }
                if (!string.IsNullOrEmpty(portal.Tag)) byTag[portal.Tag] = portal;
                if (lx < 0f || lx > 1f || ly < 0f || ly > 1f) continue;
                if (covered && cover.Contains(new Vector2(x, y))) continue;
                dots.Add(new Dot { X = x, Y = y, Radius = PortalRadius, Color = PortalColor });
                Targets.Add(new Target { X = x, Y = y, Radius = PortalRadius, Portal = portal });
            }
        }

        private static void DrawDashes(Rect rect, List<Seg> segs, float x0, float y0, float x1, float y1)
        {
            if (!Clip(rect, ref x0, ref y0, ref x1, ref y1)) return;
            float length = Vector2.Distance(new Vector2(x0, y0), new Vector2(x1, y1));
            if (length < 0.01f) return;
            var color = PortalColor; color.a = 88;
            for (float at = 0f; at < length && segs.Count < 10000; at += 18f)
            {
                float a = at / length, b = Math.Min(at + 10f, length) / length;
                segs.Add(new Seg { X0 = Mathf.Lerp(x0, x1, a), Y0 = Mathf.Lerp(y0, y1, a), X1 = Mathf.Lerp(x0, x1, b), Y1 = Mathf.Lerp(y0, y1, b), Width = LineWidth, Color = color });
            }
        }

        // known objects in view are grouped per family and screen cell. lone objects nearest the middle of the view are kept up to the pin limit of their family
        private static void DrawKnown(Rect rect, Rect uv, List<Dot> dots, List<Seg> segs, bool covered, Rect cover, ref int labels)
        {
            KnownHidden = 0;
            if (!KnownWorld.Enabled) return;
            var map = Minimap.instance;
            float span = map.m_textureSize * map.m_pixelSize;
            float cell = Settings.ClusterPixels.Value, size = Settings.KnownSize.Value;
            int cols = (int)(rect.width / cell) + 1;
            KnownCells.Clear();
            foreach (var mark in KnownWorld.Marks)
            {
                // spawn protection is drawn by its own filled layer
                if (mark.Kind == 1 || !KnownWorld.Visible(mark)) continue;
                float x = rect.xMin + ((mark.X / span + 0.5f - uv.x) / uv.width) * rect.width;
                float y = rect.yMin + ((mark.Z / span + 0.5f - uv.y) / uv.height) * rect.height;
                if (!rect.Contains(new Vector2(x, y)) || covered && cover.Contains(new Vector2(x, y))) continue;
                int c = (int)((y - rect.yMin) / cell) * cols + (int)((x - rect.xMin) / cell);
                long key = (long)c * 16 + mark.Kind;
                if (KnownCells.TryGetValue(key, out var group)) { group.Count++; group.X += x; group.Y += y; if (group.Mark.Subtype != mark.Subtype) group.Mixed = true; KnownCells[key] = group; }
                else KnownCells[key] = new KnownGroup { Mark = mark, Count = 1, X = x, Y = y };
            }
            KnownSingles.Clear();
            float centerX = rect.center.x, centerY = rect.center.y;
            foreach (var group in KnownCells.Values)
            {
                float x = group.X / group.Count, y = group.Y / group.Count;
                if (group.Count == 1)
                {
                    KnownSingles.Add(new KeyValuePair<float, KnownMark>((x - centerX) * (x - centerX) + (y - centerY) * (y - centerY), group.Mark));
                    continue;
                }
                var color = group.Mixed ? KnownWorld.FamilyColor(group.Mark) : KnownWorld.Color(group.Mark);
                // sea rocks line whole coasts, so their groups stay small unnumbered dots. hovering still names the count
                bool rocks = group.Mark.Kind == 2;
                float radius = rocks ? size : Mathf.Min(GroupMax, GroupBase + GroupGrow * Mathf.Log(group.Count, 2f));
                AddDot(dots, x, y, radius, color);
                string name = KnownWorld.GroupName(group.Mark, group.Mixed);
                Targets.Add(new Target { X = x, Y = y, Radius = radius, Count = group.Count, Category = -1, Group = name });
                if (!rocks && labels < LabelPool)
                {
                    var text = LabelAt(labels++);
                    text.text = group.Count.ToString();
                    text.fontSize = 13f; text.color = LabelColor;
                    ((RectTransform)text.transform).anchoredPosition = new Vector2(x, y);
                    text.gameObject.SetActive(true);
                }
            }
            KnownSingles.Sort((a, b) => a.Key.CompareTo(b.Key));
            int limit = Settings.MaxPins.Value;
            Array.Clear(FamilyShown, 0, FamilyShown.Length);
            foreach (var single in KnownSingles)
            {
                var mark = single.Value;
                if (limit > 0 && FamilyShown[mark.Kind]++ >= limit) { KnownHidden++; continue; }
                float x = rect.xMin + ((mark.X / span + 0.5f - uv.x) / uv.width) * rect.width;
                float y = rect.yMin + ((mark.Z / span + 0.5f - uv.y) / uv.height) * rect.height;
                AddDot(dots, x, y, size, KnownWorld.Color(mark));
                if (mark.Kind >= 3) _graphic.Symbols.Add(new Symbol { X = x, Y = y, Radius = size, Code = mark.Kind + 10, Color = new Color32(35, 30, 23, 235) });
                Targets.Add(new Target { X = x, Y = y, Radius = size, Known = mark });
            }
        }


        internal static bool Clip(Rect rect, ref float x0, ref float y0, ref float x1, ref float y1)
        {
            float dx = x1 - x0, dy = y1 - y0, a = 0f, b = 1f;
            if (!Bound(-dx, x0 - rect.xMin, ref a, ref b) || !Bound(dx, rect.xMax - x0, ref a, ref b)
                || !Bound(-dy, y0 - rect.yMin, ref a, ref b) || !Bound(dy, rect.yMax - y0, ref a, ref b)) return false;
            x1 = x0 + b * dx; y1 = y0 + b * dy; x0 += a * dx; y0 += a * dy;
            return true;
        }

        private static bool Bound(float p, float q, ref float a, ref float b)
        {
            if (Math.Abs(p) < 0.00001f) return q >= 0f;
            float r = q / p;
            if (p < 0f) { if (r > b) return false; a = Math.Max(a, r); }
            else { if (r < a) return false; b = Math.Min(b, r); }
            return true;
        }

        private static void DrawRoute(Rect rect, Rect uv, List<Seg> segs, List<Dot> dots, ref int labels)
        {
            var route = Navigator.Visible;
            if (route == null || !route.Finished || route.Cancelled) return;
            var map = Minimap.instance;
            float span = map.m_textureSize * map.m_pixelSize;
            int jumps = 0;
            for (int i = 1; i < route.Points.Count && segs.Count < 14000; i++)
            {
                var a = route.Points[i - 1]; var b = route.Points[i];
                float x0 = rect.xMin + ((a.X / span + 0.5f - uv.x) / uv.width) * rect.width;
                float y0 = rect.yMin + ((a.Z / span + 0.5f - uv.y) / uv.height) * rect.height;
                float x1 = rect.xMin + ((b.X / span + 0.5f - uv.x) / uv.width) * rect.width;
                float y1 = rect.yMin + ((b.Z / span + 0.5f - uv.y) / uv.height) * rect.height;
                // a portal jump is not walked, it is shown as a violet ring at the portal you enter and the one you leave
                if (b.Mode == 2)
                {
                    if (rect.Contains(new Vector2(x0, y0))) AddDot(dots, x0, y0, PortalJump, PortalJumpColor);
                    if (rect.Contains(new Vector2(x1, y1))) AddDot(dots, x1, y1, PortalJump, PortalJumpColor);
                    // the jump as the faint dashed line of the portal lines layer, so the two ends read as one portal pair
                    DrawDashes(rect, segs, x0, y0, x1, y1);
                    // a small tag below the portal to walk into: its order on the route and its name (test 10: which portal was unclear)
                    jumps++;
                    if (rect.Contains(new Vector2(x0, y0)) && labels < LabelPool)
                    {
                        var text = LabelAt(labels++);
                        string tag = PortalTag(a.X, a.Z, b.X, b.Z);
                        text.text = jumps + ". " + (string.IsNullOrEmpty(tag) ? "portal" : tag);
                        text.fontSize = 12f; text.color = PortalTagColor;
                        ((RectTransform)text.transform).anchoredPosition = new Vector2(x0, y0 - PortalJump - 10f);
                        text.gameObject.SetActive(true);
                    }
                    continue;
                }
                if (!Clip(rect, ref x0, ref y0, ref x1, ref y1)) continue;
                Color color = RouteColor(a, b);
                segs.Add(new Seg { X0 = x0, Y0 = y0, X1 = x1, Y1 = y1, Width = 3.5f, Color = color });
            }
        }

        private static readonly Color32 PortalTagColor = new Color32(225, 200, 255, 255);

        // the name of the portal pair a jump uses: both ends must match, the entered portal near the jump's start and its partner near the
        // landing, each within two grid cells. the nearest portal alone was wrong at a hub of many portals (test 11: bonemass named for elder2)
        internal static string PortalTag(float ax, float az, float bx, float bz)
        {
            string tag = null;
            float reach = Settings.CellMeters.Value * 2f, best = float.MaxValue;
            var list = Portals.List;
            for (int i = 0; i < list.Count; i++)
            {
                var p = list[i];
                if (string.IsNullOrEmpty(p.Tag)) continue;
                float da = NavSearch.Distance(ax, az, p.X, p.Z);
                if (da > reach) continue;
                for (int j = 0; j < list.Count; j++)
                {
                    var q = list[j];
                    if (j == i || q.Tag != p.Tag) continue;
                    float db = NavSearch.Distance(bx, bz, q.X, q.Z);
                    if (db <= reach && da + db < best) { best = da + db; tag = p.Tag; }
                }
            }
            return tag;
        }

        internal static Color RouteColor(NavPoint a, NavPoint b)
        {
            if (a.Surface == NavSurface.AshWater || b.Surface == NavSurface.AshWater) { var boiling = TerrainOverlay.BoilingColor; boiling.a = 0.96f; return boiling; }
            if (a.Mode == 1 && b.Mode == 1) return new Color32(46, 92, 168, 245);
            if (b.Mode == 2) return new Color32(170, 120, 210, 110); // a portal jump, drawn faint
            if (a.Surface == NavSurface.Water || b.Surface == NavSurface.Water) return new Color32(93, 153, 179, 245);
            float degrees = Mathf.Atan2(Math.Abs(b.Height - a.Height), NavSearch.Distance(a.X, a.Z, b.X, b.Z)) * Mathf.Rad2Deg;
            return Color.Lerp(new Color32(139, 174, 112, 245), new Color32(187, 103, 78, 245), Mathf.Clamp01(degrees / Settings.MaxSlope.Value));
        }

        private static Camera CanvasCamera()
        {
            return _canvas == null || _canvas.renderMode == RenderMode.ScreenSpaceOverlay ? null : _canvas.worldCamera;
        }

        internal static bool PickUnderMouse()
        {
            var map = Minimap.instance;
            if (!Plugin.Enabled.Value || map == null || Panel.PointerOver || _rect == null || !RectTransformUtility.ScreenPointToLocalPointInRectangle(_rect, Input.mousePosition, CanvasCamera(), out var local))
            {
                MarkerMenu.Hide();
                return false;
            }
            Target? picked = null; float best = float.MaxValue;
            foreach (var target in Targets)
            {
                if (target.Entry == null && target.Known == null) continue;
                float distance = Vector2.Distance(local, new Vector2(target.X, target.Y));
                if (distance <= target.Radius + HoverPixels && distance < best) { best = distance; picked = target; }
            }
            if (!picked.HasValue || MarkerMenu.SavedPinUnderMouse(map))
            {
                MarkerMenu.Hide();
                return false;
            }
            MarkerMenu.Show(map, picked.Value.Entry, picked.Value.Known, CanvasCamera());
            if (Plugin.LogCounters.Value) Plugin.Log.LogInfo("picked map marker");
            return true;
        }

        // the text next to the mouse for the dot or circle under it
        private static void Hover(Minimap map)
        {
            if (Panel.PointerOver || MarkerMenu.Open || Targets.Count == 0 || !RectTransformUtility.ScreenPointToLocalPointInRectangle(_rect, Input.mousePosition, CanvasCamera(), out var local)
                || !_rect.rect.Contains(local))
            {
                if (_tip != null && _tip.activeSelf) _tip.SetActive(false);
                return;
            }
            int best = -1;
            float bestDistance = float.MaxValue;
            for (int i = 0; i < Targets.Count; i++)
            {
                float dx = Targets[i].X - local.x, dy = Targets[i].Y - local.y, d = dx * dx + dy * dy, reach = Targets[i].Radius + HoverPixels;
                if (d <= reach * reach && d < bestDistance) { bestDistance = d; best = i; }
            }
            if (best < 0)
            {
                if (_tip != null && _tip.activeSelf) _tip.SetActive(false);
                return;
            }
            ShowTip(map, TipText(Targets[best]));
        }

        private static string TipText(Target target)
        {
            if (target.Group != null) return target.Count + " " + target.Group + "\nzoom in to see them";
            if (target.Known != null) return target.Known.Name + (string.IsNullOrEmpty(target.Known.Contents) ? "" : "\n" + target.Known.Contents.Substring(0, Math.Min(90, target.Known.Contents.Length)));
            if (target.Portal != null) return "Portal " + (string.IsNullOrEmpty(target.Portal.Tag) ? "(no name)" : target.Portal.Tag);
            if (target.Entry == null)
                return target.Count + " places" + (target.Category >= 0 ? ", " + Categories.Titles[Categories.Order[target.Category]] : ", zoom in to see them");
            var e = target.Entry;
            string text = e.Kind + (e.Possible ? " (possible site)" : "") + (e.Done ? " (done)" : "");
            var at = Session.PlayerPosition();
            if (at != null)
            {
                float dx = e.X - at.Value.x, dz = e.Z - at.Value.z;
                text += "\n" + Nearest.Distance(dx, dz).ToString("F0") + " m " + Nearest.Direction(dx, dz);
            }
            return text;
        }

        private static void ShowTip(Minimap map, string text)
        {
            var root = map.m_largeRoot != null ? (RectTransform)map.m_largeRoot.transform : null;
            if (root == null) return;
            if (_tip == null)
            {
                _tip = new GameObject("ValheimNavigator_Tip", typeof(RectTransform), typeof(Image));
                _tip.GetComponent<Image>().color = new Color(0f, 0f, 0f, 0.75f);
                _tip.GetComponent<Image>().raycastTarget = false;
                var rect = (RectTransform)_tip.transform;
                rect.SetParent(root, false);
                rect.anchorMin = rect.anchorMax = rect.pivot = Vector2.zero;
                _tipText = Ui.Label(_tip.transform, "Text", 6f, 4f, 240f, 40f, 15f, TextAlignmentOptions.TopLeft, Ui.Ink);
                _tipText.textWrappingMode = TextWrappingModes.NoWrap;
                _tipText.overflowMode = TextOverflowModes.Overflow;
            }
            if (!RectTransformUtility.ScreenPointToLocalPointInRectangle(root, Input.mousePosition, CanvasCamera(), out var p)) return;
            _tipText.text = text;
            var tipRect = (RectTransform)_tip.transform;
            int lines = 1;
            foreach (char c in text) if (c == '\n') lines++;
            tipRect.sizeDelta = new Vector2(Mathf.Max(60f, _tipText.GetPreferredValues(text).x + 14f), 12f + lines * 19f);
            ((RectTransform)_tipText.transform).sizeDelta = tipRect.sizeDelta - new Vector2(12f, 8f);
            tipRect.anchoredPosition = p - root.rect.min + new Vector2(TipOffset, TipOffset);
            _tip.SetActive(true);
            _tip.transform.SetAsLastSibling();
        }
    }
}
