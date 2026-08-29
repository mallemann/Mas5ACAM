using System;
using System.Collections.Generic;

namespace Mas5ACAM
{
    /// <summary>Ein Dreieck des Facettenmodells inklusive Flächennormale.</summary>
    public struct Tri
    {
        public Vec3 A, B, C;
        public Vec3 N;      // normiert, zeigt nach aussen

        public Tri(Vec3 a, Vec3 b, Vec3 c)
        {
            A = a; B = b; C = c;
            N = Vec3.Cross(b - a, c - a).Normalized;
        }

        public Vec3 Centroid { get { return (A + B + C) / 3.0; } }
    }

    /// <summary>Dreiecksnetz (STL-Modell) im Werkstück-Koordinatensystem.</summary>
    public sealed partial class Mesh
    {
        public readonly List<Tri> Tris = new List<Tri>();
        public Aabb Bounds = Aabb.Empty;
        public string Name = "Modell";

        public int Count { get { return Tris.Count; } }

        public void Add(Tri t)
        {
            Tris.Add(t);
            Bounds.Add(t.A); Bounds.Add(t.B); Bounds.Add(t.C);
        }

        public void AddQuad(Vec3 a, Vec3 b, Vec3 c, Vec3 d)
        {
            Add(new Tri(a, b, c));
            Add(new Tri(a, c, d));
        }

        public void RecomputeBounds()
        {
            Bounds = Aabb.Empty;
            for (int i = 0; i < Tris.Count; i++)
            {
                Bounds.Add(Tris[i].A); Bounds.Add(Tris[i].B); Bounds.Add(Tris[i].C);
            }
        }


        // --- Geglättete Eckennormalen -------------------------------------------------
        // Ein STL kennt nur Facettennormalen. Für saubere Werkzeugachsen brauchen wir
        // aber die Normale der *gemeinten* Fläche. Deshalb werden die Normalen der an
        // einer Ecke anliegenden Dreiecke gemittelt – aber nur, solange sie flacher als
        // der Knickwinkel zueinander stehen, damit echte Kanten scharf bleiben.
        private Vec3[] _vn;                       // 3 Einträge je Dreieck

        public bool HasSmoothNormals { get { return _vn != null; } }

        public void BuildSmoothNormals(double creaseDeg = 40.0)
        {
            double cosCrease = Math.Cos(creaseDeg * Math.PI / 180.0);
            var map = new Dictionary<VKey, List<int>>(Tris.Count * 2);   // Ort -> Ecken-Slots

            for (int i = 0; i < Tris.Count; i++)
            {
                Tri t = Tris[i];
                Key(map, t.A, 3 * i + 0);
                Key(map, t.B, 3 * i + 1);
                Key(map, t.C, 3 * i + 2);
            }

            _vn = new Vec3[Tris.Count * 3];
            foreach (var kv in map)
            {
                List<int> slots = kv.Value;
                for (int a = 0; a < slots.Count; a++)
                {
                    int ia = slots[a] / 3;
                    Vec3 na = Tris[ia].N;
                    Vec3 acc = Vec3.Zero;
                    for (int b = 0; b < slots.Count; b++)
                    {
                        Vec3 nb = Tris[slots[b] / 3].N;
                        if (Vec3.Dot(na, nb) >= cosCrease) acc = acc + nb;
                    }
                    Vec3 n = acc.Normalized;
                    _vn[slots[a]] = n.LengthSq > 0.5 ? n : na;
                }
            }
        }

        /// <summary>Ort auf 1/1000 mm gerundet. Als Schluessel mit echtem Vergleich, nicht als
        /// Hash mit Kollisionen - sonst landen weit auseinanderliegende Ecken im selben Topf
        /// und ihre Normalen werden miteinander verrechnet.</summary>
        private readonly struct VKey : IEquatable<VKey>
        {
            private readonly long _x, _y, _z;

            public VKey(Vec3 p)
            {
                _x = (long)Math.Round(p.X * 1000.0);
                _y = (long)Math.Round(p.Y * 1000.0);
                _z = (long)Math.Round(p.Z * 1000.0);
            }

            public bool Equals(VKey o) { return _x == o._x && _y == o._y && _z == o._z; }
            public override bool Equals(object o) { return o is VKey && Equals((VKey)o); }

            public override int GetHashCode()
            {
                unchecked
                {
                    int h = 17;
                    h = h * 31 + _x.GetHashCode();
                    h = h * 31 + _y.GetHashCode();
                    h = h * 31 + _z.GetHashCode();
                    return h;
                }
            }
        }

        private static void Key(Dictionary<VKey, List<int>> map, Vec3 p, int slot)
        {
            VKey k = new VKey(p);
            List<int> l;
            if (!map.TryGetValue(k, out l)) { l = new List<int>(6); map[k] = l; }
            l.Add(slot);
        }

        /// <summary>Geglättete Eckennormale (Diagnose).</summary>
        public Vec3 VertexNormal(int tri, int corner) { return _vn == null ? Tris[tri].N : _vn[3 * tri + corner]; }

        /// <summary>Baryzentrische Gewichte (Diagnose).</summary>
        public void Bary(int tri, Vec3 p, out double u, out double v, out double w)
        {
            Tri t = Tris[tri];
            Vec3 v0 = t.B - t.A, v1 = t.C - t.A, v2 = p - t.A;
            double d00 = Vec3.Dot(v0, v0), d01 = Vec3.Dot(v0, v1), d11 = Vec3.Dot(v1, v1);
            double d20 = Vec3.Dot(v2, v0), d21 = Vec3.Dot(v2, v1);
            double den = d00 * d11 - d01 * d01;
            v = (d11 * d20 - d01 * d21) / den;
            w = (d00 * d21 - d01 * d20) / den;
            u = 1.0 - v - w;
        }

        /// <summary>Normale im Punkt p des Dreiecks i – baryzentrisch interpoliert,
        /// falls geglättete Normalen vorliegen, sonst die Facettennormale.</summary>
        public Vec3 NormalAt(int tri, Vec3 p)
        {
            Tri t = Tris[tri];
            if (_vn == null) return t.N;

            Vec3 v0 = t.B - t.A, v1 = t.C - t.A, v2 = p - t.A;
            double d00 = Vec3.Dot(v0, v0), d01 = Vec3.Dot(v0, v1), d11 = Vec3.Dot(v1, v1);
            double d20 = Vec3.Dot(v2, v0), d21 = Vec3.Dot(v2, v1);
            double den = d00 * d11 - d01 * d01;
            if (Math.Abs(den) < 1e-18) return t.N;

            double v = (d11 * d20 - d01 * d21) / den;
            double w = (d00 * d21 - d01 * d20) / den;
            double u = 1.0 - v - w;
            u = MathUtil.Clamp(u, 0, 1); v = MathUtil.Clamp(v, 0, 1); w = MathUtil.Clamp(w, 0, 1);

            Vec3 n = _vn[3 * tri] * u + _vn[3 * tri + 1] * v + _vn[3 * tri + 2] * w;
            return n.LengthSq > 1e-12 ? n.Normalized : t.N;
        }

        /// <summary>Verschiebt das gesamte Netz.</summary>
        public void Translate(Vec3 d)
        {
            for (int i = 0; i < Tris.Count; i++)
            {
                Tri t = Tris[i];
                Tris[i] = new Tri(t.A + d, t.B + d, t.C + d);
            }
            RecomputeBounds();
            if (_vn != null) BuildSmoothNormals();
        }
    }

    /// <summary>Abstands- und Schnittberechnungen für Dreiecke.</summary>
    public static class TriMath
    {
        /// <summary>Möller-Trumbore: Schnitt Strahl/Dreieck. t = Parameter entlang dir (dir normiert).</summary>
        public static bool RayHit(Vec3 orig, Vec3 dir, in Tri tr, out double t)
        {
            const double eps = 1e-12;
            t = 0.0;
            Vec3 e1 = tr.B - tr.A;
            Vec3 e2 = tr.C - tr.A;
            Vec3 p = Vec3.Cross(dir, e2);
            double det = Vec3.Dot(e1, p);
            if (det > -eps && det < eps) return false;      // Strahl parallel zur Dreiecksebene
            double inv = 1.0 / det;
            Vec3 s = orig - tr.A;
            double u = Vec3.Dot(s, p) * inv;
            if (u < -1e-9 || u > 1.0 + 1e-9) return false;
            Vec3 q = Vec3.Cross(s, e1);
            double v = Vec3.Dot(dir, q) * inv;
            if (v < -1e-9 || u + v > 1.0 + 1e-9) return false;
            t = Vec3.Dot(e2, q) * inv;
            return t > eps;
        }

        /// <summary>Nächstgelegener Punkt auf dem Dreieck zu p (Ericson, Real-Time Collision Detection).</summary>
        public static Vec3 ClosestPointOnTriangle(Vec3 p, in Tri tr)
        {
            Vec3 a = tr.A, b = tr.B, c = tr.C;
            Vec3 ab = b - a, ac = c - a, ap = p - a;
            double d1 = Vec3.Dot(ab, ap), d2 = Vec3.Dot(ac, ap);
            if (d1 <= 0 && d2 <= 0) return a;

            Vec3 bp = p - b;
            double d3 = Vec3.Dot(ab, bp), d4 = Vec3.Dot(ac, bp);
            if (d3 >= 0 && d4 <= d3) return b;

            double vc = d1 * d4 - d3 * d2;
            if (vc <= 0 && d1 >= 0 && d3 <= 0) return a + ab * (d1 / (d1 - d3));

            Vec3 cp = p - c;
            double d5 = Vec3.Dot(ab, cp), d6 = Vec3.Dot(ac, cp);
            if (d6 >= 0 && d5 <= d6) return c;

            double vb = d5 * d2 - d1 * d6;
            if (vb <= 0 && d2 >= 0 && d6 <= 0) return a + ac * (d2 / (d2 - d6));

            double va = d3 * d6 - d5 * d4;
            if (va <= 0 && (d4 - d3) >= 0 && (d5 - d6) >= 0)
                return b + (c - b) * ((d4 - d3) / ((d4 - d3) + (d5 - d6)));

            double denom = 1.0 / (va + vb + vc);
            return a + ab * (vb * denom) + ac * (vc * denom);
        }

        public static double PointTriangleDistance(Vec3 p, in Tri tr)
        {
            return (p - ClosestPointOnTriangle(p, tr)).Length;
        }

        /// <summary>Kleinster Abstand zwischen zwei Strecken.</summary>
        public static double SegSegDistance(Vec3 p1, Vec3 q1, Vec3 p2, Vec3 q2)
        {
            const double eps = 1e-12;
            Vec3 d1 = q1 - p1, d2 = q2 - p2, r = p1 - p2;
            double a = Vec3.Dot(d1, d1), e = Vec3.Dot(d2, d2), f = Vec3.Dot(d2, r);
            double s, t;

            if (a <= eps && e <= eps) return r.Length;
            if (a <= eps) { s = 0; t = MathUtil.Clamp(f / e, 0, 1); }
            else
            {
                double c = Vec3.Dot(d1, r);
                if (e <= eps) { t = 0; s = MathUtil.Clamp(-c / a, 0, 1); }
                else
                {
                    double b = Vec3.Dot(d1, d2);
                    double denom = a * e - b * b;
                    s = denom > eps ? MathUtil.Clamp((b * f - c * e) / denom, 0, 1) : 0.0;
                    t = (b * s + f) / e;
                    if (t < 0) { t = 0; s = MathUtil.Clamp(-c / a, 0, 1); }
                    else if (t > 1) { t = 1; s = MathUtil.Clamp((b - c) / a, 0, 1); }
                }
            }
            return ((p1 + d1 * s) - (p2 + d2 * t)).Length;
        }

        /// <summary>Kleinster Abstand zwischen der Strecke p0-p1 und einem Dreieck.
        /// Damit lässt sich eine Kapsel (Radius r) exakt gegen das Netz prüfen.</summary>
        public static double SegTriDistance(Vec3 p0, Vec3 p1, in Tri tr)
        {
            // Durchstösst die Strecke die Dreiecksebene innerhalb des Dreiecks -> Abstand 0
            Vec3 n = tr.N;
            double d0 = Vec3.Dot(p0 - tr.A, n);
            double d1 = Vec3.Dot(p1 - tr.A, n);
            if ((d0 > 0 && d1 < 0) || (d0 < 0 && d1 > 0))
            {
                double u = d0 / (d0 - d1);
                Vec3 x = p0 + (p1 - p0) * u;
                if ((x - ClosestPointOnTriangle(x, tr)).LengthSq < 1e-18) return 0.0;
            }

            double best = Math.Min(PointTriangleDistance(p0, tr), PointTriangleDistance(p1, tr));
            best = Math.Min(best, SegSegDistance(p0, p1, tr.A, tr.B));
            best = Math.Min(best, SegSegDistance(p0, p1, tr.B, tr.C));
            best = Math.Min(best, SegSegDistance(p0, p1, tr.C, tr.A));
            return best;
        }
    }
}
