using System;
using System.Collections.Generic;

namespace Mas5ACAM
{
    /// <summary>
    /// Prüft, ob das Werkzeug in einer bestimmten Lage in das Modell greift.
    ///
    /// Das Werkzeug wird als Kombination von Kapseln beschrieben:
    ///   * Schneidkugel   – Kugel um den Kugelmittelpunkt, Radius R
    ///   * Schaft         – Kapsel vom Kugelmittelpunkt bis zur freien Länge, Radius Rs
    ///   * Halter         – Kapsel darüber, Radius Rh, mit Sicherheitsabstand
    ///
    /// Die Schneidkugel darf die Fläche berühren (sie soll ja schneiden); erst eine
    /// Unterschreitung um mehr als die Gouge-Toleranz gilt als Verletzung.
    /// </summary>
    public sealed class ToolCollision
    {
        private readonly TriGrid _grid;
        private readonly CamParameters _cp;
        private readonly List<int> _cand = new List<int>(256);
        private readonly HashSet<int> _seen = new HashSet<int>();

        public ToolCollision(TriGrid grid, CamParameters cp) { _grid = grid; _cp = cp; }

        /// <summary>true, wenn das Werkzeug in dieser Lage verletzt.</summary>
        public bool Collides(Vec3 center, Vec3 axis)
        {
            Tool t = _cp.Tool;

            // Schneidkugel: Berührung erlaubt, Eingriff über die Toleranz hinaus nicht
            if (Violates(center, center, t.Radius - _cp.GougeTolerance)) return true;

            // Schaft ab dem Kugelmittelpunkt bis zur freien Länge
            Vec3 shankTop = center + axis * t.FreeLength;
            double shankR = Math.Min(t.ShankRadius, t.Radius) - _cp.GougeTolerance;
            if (t.ShankRadius > t.Radius) shankR = t.ShankRadius + _cp.HolderClearance;
            if (Violates(center, shankTop, shankR)) return true;

            if (!_cp.CheckHolder || t.HolderLength <= 0) return false;

            Vec3 holderTop = shankTop + axis * t.HolderLength;
            return Violates(shankTop, holderTop, t.HolderRadius + _cp.HolderClearance);
        }

        /// <summary>Kleinster Abstand des Werkzeugs zum Modell minus geforderter Radius –
        /// negativ bedeutet Verletzung. Für Diagnosezwecke.</summary>
        public double Clearance(Vec3 center, Vec3 axis)
        {
            Tool t = _cp.Tool;
            Vec3 shankTop = center + axis * t.FreeLength;
            double d = MinDistance(center, center) - t.Radius;
            d = Math.Min(d, MinDistance(center, shankTop) - t.ShankRadius);
            if (_cp.CheckHolder && t.HolderLength > 0)
                d = Math.Min(d, MinDistance(shankTop, shankTop + axis * t.HolderLength) - t.HolderRadius);
            return d;
        }

        private bool Violates(Vec3 p0, Vec3 p1, double radius)
        {
            if (radius <= 0) return false;
            Collect(p0, p1, radius);
            for (int i = 0; i < _cand.Count; i++)
            {
                Tri tr = _grid.Mesh.Tris[_cand[i]];
                if (TriMath.SegTriDistance(p0, p1, tr) < radius) return true;
            }
            return false;
        }

        private double MinDistance(Vec3 p0, Vec3 p1)
        {
            Collect(p0, p1, Math.Max(_cp.Tool.HolderRadius, _cp.Tool.Radius) + _cp.HolderClearance + 1.0);
            double best = double.MaxValue;
            for (int i = 0; i < _cand.Count; i++)
            {
                double d = TriMath.SegTriDistance(p0, p1, _grid.Mesh.Tris[_cand[i]]);
                if (d < best) best = d;
            }
            return best;
        }

        private void Collect(Vec3 p0, Vec3 p1, double radius)
        {
            Vec3 e = new Vec3(radius, radius, radius);
            Vec3 lo = Vec3.Min(p0, p1) - e;
            Vec3 hi = Vec3.Max(p0, p1) + e;
            _grid.QueryBox(lo, hi, _cand, _seen);
        }
    }
}
