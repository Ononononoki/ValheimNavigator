using System;
using System.Collections.Generic;
using System.Diagnostics;

namespace ValheimNavigator
{
    // the fine part of a route inside a base: a 2 m grid around a point where every cell can hold several floors (the ground, a floor
    // above it, a walkway, a second storey). a step joins two floors of neighbour cells when the rise stays under the slide limit and nothing
    // stands in the space a player fills between them (doors count as open). an A* over (cell, floor) finds the way from where you stand to a
    // point of the coarse route at the edge of the area, and that way replaces the coarse route's start. the coarse 24 m grid sees one surface
    // per point, so upper floors, stairs, door gaps and raised walkways only exist here. no unity: the game gives the floors and the clear test
    internal sealed class LocalPlan
    {
        internal const float Cell = 2f;
        // the floors where a player can stand at a point, lowest first
        private readonly Func<float, float, float[]> _floors;
        // true when nothing blocks the step between two standing points (x, y, z of each)
        private readonly Func<float, float, float, float, float, float, bool> _clear;
        private readonly float _sx, _sy, _sz, _ex, _ey, _ez, _radius, _walk, _maxRise;
        private readonly int _maxNodes;

        private sealed class Node { internal int I, J, Level; internal float Y, G = float.MaxValue; internal Node Parent; internal bool Closed; }

        private readonly Dictionary<long, float[]> _cells = new Dictionary<long, float[]>();
        private readonly Dictionary<long, Node> _nodes = new Dictionary<long, Node>();
        private readonly List<(float f, Node n)> _heap = new List<(float, Node)>();
        private int _endI, _endJ;
        private bool _started;

        internal bool Finished, Found;
        internal int Expanded, CellsRead;
        // the way found, start first, as world points with their floor heights
        internal readonly List<(float x, float y, float z)> Path = new List<(float, float, float)>();

        internal LocalPlan(float sx, float sy, float sz, float ex, float ey, float ez, float radius, float walkSpeed, float maxSlope,
            Func<float, float, float[]> floors, Func<float, float, float, float, float, float, bool> clear, int maxNodes = 20000)
        {
            _sx = sx; _sy = sy; _sz = sz; _ex = ex; _ey = ey; _ez = ez; _radius = radius; _walk = walkSpeed; _maxRise = maxSlope;
            _floors = floors; _clear = clear; _maxNodes = maxNodes;
        }

        private float X(int i) { return _sx + i * Cell; }
        private float Z(int j) { return _sz + j * Cell; }
        private static long Key(int i, int j) { return ((long)i << 32) | (uint)j; }
        private static long Key(int i, int j, int level) { return ((long)(i * 16 + level) << 32) | (uint)j; }

        private float[] Floors(int i, int j)
        {
            long key = Key(i, j);
            if (_cells.TryGetValue(key, out var f)) return f;
            f = _floors(X(i), Z(j)) ?? new float[0];
            _cells[key] = f;
            CellsRead++;
            return f;
        }

        private static int Nearest(float[] floors, float y)
        {
            int best = -1; float d = float.MaxValue;
            for (int k = 0; k < floors.Length; k++) if (Math.Abs(floors[k] - y) < d) { d = Math.Abs(floors[k] - y); best = k; }
            return best;
        }

        private Node Get(int i, int j, int level, float y)
        {
            long key = Key(i, j, level);
            if (!_nodes.TryGetValue(key, out var n)) { n = new Node { I = i, J = j, Level = level, Y = y }; _nodes[key] = n; }
            return n;
        }

        private float H(Node n)
        {
            float dx = X(n.I) - _ex, dz = Z(n.J) - _ez;
            return (float)Math.Sqrt(dx * dx + dz * dz) / _walk;
        }

        private void Push(Node n)
        {
            _heap.Add((n.G + H(n), n));
            int i = _heap.Count - 1;
            while (i > 0) { int up = (i - 1) / 2; if (_heap[up].f <= _heap[i].f) break; (_heap[up], _heap[i]) = (_heap[i], _heap[up]); i = up; }
        }

        private Node Pop()
        {
            while (_heap.Count > 0)
            {
                var top = _heap[0]; var last = _heap[_heap.Count - 1]; _heap.RemoveAt(_heap.Count - 1);
                if (_heap.Count > 0)
                {
                    _heap[0] = last; int i = 0;
                    while (true)
                    {
                        int l = 2 * i + 1, r = l + 1, m = i;
                        if (l < _heap.Count && _heap[l].f < _heap[m].f) m = l;
                        if (r < _heap.Count && _heap[r].f < _heap[m].f) m = r;
                        if (m == i) break;
                        (_heap[m], _heap[i]) = (_heap[i], _heap[m]); i = m;
                    }
                }
                if (!top.n.Closed && Math.Abs(top.f - (top.n.G + H(top.n))) < 0.001f) return top.n;
            }
            return null;
        }

        private static readonly int[] Di = { 1, 0, -1, 0, 1, -1, -1, 1 }, Dj = { 0, 1, 0, -1, 1, 1, -1, -1 };

        // works for about the given time, true once finished
        internal bool Step(float milliseconds)
        {
            if (Finished) return true;
            var clock = Stopwatch.StartNew();
            if (!_started)
            {
                _started = true;
                _endI = (int)Math.Round((_ex - _sx) / Cell); _endJ = (int)Math.Round((_ez - _sz) / Cell);
                var floors = Floors(0, 0);
                int level = Nearest(floors, _sy);
                if (level < 0) { Finished = true; return true; }
                var start = Get(0, 0, level, floors[level]);
                start.G = 0f; Push(start);
            }
            while (clock.Elapsed.TotalMilliseconds < milliseconds)
            {
                var n = Pop();
                if (n == null || _nodes.Count > _maxNodes) { Finished = true; return true; }
                n.Closed = true; Expanded++;
                if (Math.Abs(n.I - _endI) <= 1 && Math.Abs(n.J - _endJ) <= 1 && Math.Abs(n.Y - _ey) <= 2.5f)
                {
                    for (var p = n; p != null; p = p.Parent) Path.Add((X(p.I), p.Y, Z(p.J)));
                    Path.Reverse();
                    Path.Add((_ex, _ey, _ez));
                    Found = Finished = true;
                    return true;
                }
                for (int d = 0; d < 8; d++)
                {
                    int i = n.I + Di[d], j = n.J + Dj[d];
                    float x = X(i), z = Z(j);
                    if ((x - _sx) * (x - _sx) + (z - _sz) * (z - _sz) > _radius * _radius && (Math.Abs(i - _endI) > 1 || Math.Abs(j - _endJ) > 1)) continue;
                    float run = d < 4 ? Cell : Cell * 1.41421356f;
                    var floors = Floors(i, j);
                    for (int k = 0; k < floors.Length; k++)
                    {
                        float rise = floors[k] - n.Y;
                        if (Math.Abs(rise) > _maxRise * run) continue;
                        var next = Get(i, j, k, floors[k]);
                        if (next.Closed) continue;
                        float g = n.G + (float)Math.Sqrt(run * run + rise * rise) / _walk;
                        if (g >= next.G) continue;
                        if (!_clear(X(n.I), n.Y, Z(n.J), x, floors[k], z)) continue;
                        next.G = g; next.Parent = n; Push(next);
                    }
                }
            }
            return false;
        }
    }
}
