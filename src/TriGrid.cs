using System;
using System.Collections.Generic;

namespace Mas5ACAM
{
    /// <summary>
    /// Gleichmässiges Voxelgitter über dem Dreiecksnetz. Beschleunigt die beiden
    /// Abfragen, die die Bahnberechnung braucht: Strahlschnitt (Flächenpunkt suchen)
    /// und Boxabfrage (Kollisionsprüfung Werkzeug gegen Modell).
    /// </summary>
    public sealed class TriGrid
    {
        private readonly Mesh _mesh;
        private readonly Vec3 _min;
        private readonly double _cell;
        private readonly int _nx, _ny, _nz;
        private readonly List<int>[] _cells;

        public Mesh Mesh { get { return _mesh; } }

        public TriGrid(Mesh mesh, int targetPerAxis = 48)
        {
            _mesh = mesh;
            Aabb b = mesh.Bounds.Expanded(1e-6);
            _min = b.Min;
            Vec3 size = b.Size;
            double maxDim = Math.Max(size.X, Math.Max(size.Y, size.Z));
            if (maxDim < 1e-9) maxDim = 1.0;

            _cell = maxDim / Math.Max(1, targetPerAxis);
            _nx = Math.Max(1, (int)Math.Ceiling(size.X / _cell));
            _ny = Math.Max(1, (int)Math.Ceiling(size.Y / _cell));
            _nz = Math.Max(1, (int)Math.Ceiling(size.Z / _cell));
            _cells = new List<int>[_nx * _ny * _nz];

            for (int i = 0; i < mesh.Tris.Count; i++)
            {
                Tri t = mesh.Tris[i];
                Vec3 lo = Vec3.Min(t.A, Vec3.Min(t.B, t.C));
                Vec3 hi = Vec3.Max(t.A, Vec3.Max(t.B, t.C));
                int x0, y0, z0, x1, y1, z1;
                CellOf(lo, out x0, out y0, out z0);
                CellOf(hi, out x1, out y1, out z1);
                for (int z = z0; z <= z1; z++)
                    for (int y = y0; y <= y1; y++)
                        for (int x = x0; x <= x1; x++)
                        {
                            int idx = Index(x, y, z);
                            if (_cells[idx] == null) _cells[idx] = new List<int>(4);
                            _cells[idx].Add(i);
                        }
            }
        }

        private int Index(int x, int y, int z) { return (z * _ny + y) * _nx + x; }

        private void CellOf(Vec3 p, out int x, out int y, out int z)
        {
            x = MathUtil.Clamp((int)((p.X - _min.X) / _cell), 0, _nx - 1);
            y = MathUtil.Clamp((int)((p.Y - _min.Y) / _cell), 0, _ny - 1);
            z = MathUtil.Clamp((int)((p.Z - _min.Z) / _cell), 0, _nz - 1);
        }

        /// <summary>Erster Treffer eines Strahls (dir normiert). Liefert Abstand und Dreiecksindex.</summary>
        public bool RayFirstHit(Vec3 orig, Vec3 dir, out double tHit, out int triIndex)
        {
            tHit = 0; triIndex = -1;

            double tEnter, tExit;
            if (!ClipRayToBounds(orig, dir, out tEnter, out tExit)) return false;

            double t = Math.Max(tEnter, 0.0);
            Vec3 p = orig + dir * (t + 1e-9);
            int x, y, z; CellOf(p, out x, out y, out z);

            int stepX = dir.X > 0 ? 1 : (dir.X < 0 ? -1 : 0);
            int stepY = dir.Y > 0 ? 1 : (dir.Y < 0 ? -1 : 0);
            int stepZ = dir.Z > 0 ? 1 : (dir.Z < 0 ? -1 : 0);

            double tMaxX = NextBoundary(orig.X, dir.X, _min.X, x, stepX);
            double tMaxY = NextBoundary(orig.Y, dir.Y, _min.Y, y, stepY);
            double tMaxZ = NextBoundary(orig.Z, dir.Z, _min.Z, z, stepZ);

            double tDeltaX = stepX != 0 ? Math.Abs(_cell / dir.X) : double.MaxValue;
            double tDeltaY = stepY != 0 ? Math.Abs(_cell / dir.Y) : double.MaxValue;
            double tDeltaZ = stepZ != 0 ? Math.Abs(_cell / dir.Z) : double.MaxValue;

            int guard = (_nx + _ny + _nz) * 3 + 10;
            while (guard-- > 0)
            {
                List<int> cell = _cells[Index(x, y, z)];
                if (cell != null)
                {
                    double best = double.MaxValue; int bestTri = -1;
                    for (int i = 0; i < cell.Count; i++)
                    {
                        double tt;
                        Tri tr = _mesh.Tris[cell[i]];
                        if (TriMath.RayHit(orig, dir, tr, out tt) && tt < best) { best = tt; bestTri = cell[i]; }
                    }
                    // Nur akzeptieren, wenn der Treffer noch in dieser Zelle liegt – sonst
                    // könnte eine spätere Zelle einen näheren Treffer enthalten.
                    double tCellExit = Math.Min(tMaxX, Math.Min(tMaxY, tMaxZ));
                    if (bestTri >= 0 && best <= tCellExit + 1e-9)
                    {
                        tHit = best; triIndex = bestTri; return true;
                    }
                    if (bestTri >= 0 && best < double.MaxValue && tCellExit >= tExit)
                    {
                        tHit = best; triIndex = bestTri; return true;
                    }
                }

                if (tMaxX <= tMaxY && tMaxX <= tMaxZ)
                {
                    x += stepX; if (x < 0 || x >= _nx) return false;
                    tMaxX += tDeltaX;
                }
                else if (tMaxY <= tMaxZ)
                {
                    y += stepY; if (y < 0 || y >= _ny) return false;
                    tMaxY += tDeltaY;
                }
                else
                {
                    z += stepZ; if (z < 0 || z >= _nz) return false;
                    tMaxZ += tDeltaZ;
                }
            }
            return false;
        }

        private double NextBoundary(double o, double d, double min, int cellIdx, int step)
        {
            if (step == 0) return double.MaxValue;
            double edge = min + (cellIdx + (step > 0 ? 1 : 0)) * _cell;
            return (edge - o) / d;
        }

        private bool ClipRayToBounds(Vec3 o, Vec3 d, out double tMin, out double tMax)
        {
            Vec3 lo = _min;
            Vec3 hi = new Vec3(_min.X + _nx * _cell, _min.Y + _ny * _cell, _min.Z + _nz * _cell);
            tMin = double.MinValue; tMax = double.MaxValue;

            if (!Slab(o.X, d.X, lo.X, hi.X, ref tMin, ref tMax)) return false;
            if (!Slab(o.Y, d.Y, lo.Y, hi.Y, ref tMin, ref tMax)) return false;
            if (!Slab(o.Z, d.Z, lo.Z, hi.Z, ref tMin, ref tMax)) return false;
            return tMax >= Math.Max(tMin, 0.0);
        }

        private static bool Slab(double o, double d, double lo, double hi, ref double tMin, ref double tMax)
        {
            if (Math.Abs(d) < 1e-15) return o >= lo && o <= hi;
            double t1 = (lo - o) / d, t2 = (hi - o) / d;
            if (t1 > t2) { double tmp = t1; t1 = t2; t2 = tmp; }
            if (t1 > tMin) tMin = t1;
            if (t2 < tMax) tMax = t2;
            return tMin <= tMax;
        }

        /// <summary>Alle Dreiecksindizes, deren Zellen die Box schneiden (ohne Duplikate).</summary>
        public void QueryBox(Vec3 lo, Vec3 hi, List<int> result, HashSet<int> scratch)
        {
            result.Clear(); scratch.Clear();
            if (hi.X < _min.X || hi.Y < _min.Y || hi.Z < _min.Z) return;

            int x0, y0, z0, x1, y1, z1;
            CellOf(lo, out x0, out y0, out z0);
            CellOf(hi, out x1, out y1, out z1);
            for (int z = z0; z <= z1; z++)
                for (int y = y0; y <= y1; y++)
                    for (int x = x0; x <= x1; x++)
                    {
                        List<int> c = _cells[Index(x, y, z)];
                        if (c == null) continue;
                        for (int i = 0; i < c.Count; i++)
                            if (scratch.Add(c[i])) result.Add(c[i]);
                    }
        }
    }
}
