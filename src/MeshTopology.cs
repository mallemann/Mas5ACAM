using System;
using System.Collections.Generic;

namespace Mas5ACAM
{
    /// <summary>
    /// Topologie und Flächenauswahl.
    ///
    /// Ein STL kennt keine Flächen, nur lose Dreiecke. Für „diese Fläche bearbeiten"
    /// braucht es deshalb zwei Dinge: die Nachbarschaft über gemeinsame Kanten, und
    /// ein Wachstumsverfahren, das von einem angeklickten Dreieck aus so lange über
    /// Nachbarn läuft, wie die Fläche glatt weitergeht. An einer echten Kante
    /// (Knickwinkel überschritten) hört die Fläche auf – genau wie es das Auge sieht.
    /// </summary>
    public sealed partial class Mesh
    {
        private int[] _vid;          // Vertex-Nummer je Ecke (3 Einträge je Dreieck)
        private int[] _adj;          // Nachbardreieck je Kante (3 je Dreieck), -1 = offene Kante

        /// <summary>Auswahlmaske je Dreieck. <c>null</c> bedeutet: keine Einschränkung.</summary>
        public bool[] Selected;
        public int SelectedCount;

        public bool HasSelection { get { return Selected != null && SelectedCount > 0; } }

        /// <summary>Darf dieses Dreieck bearbeitet werden?</summary>
        public bool IsSelected(int tri)
        {
            return !HasSelection || (tri >= 0 && tri < Selected.Length && Selected[tri]);
        }

        /// <summary>Auswahl von einem anderen Netz mit gleicher Dreiecksreihenfolge uebernehmen.</summary>
        public void CopySelectionFrom(Mesh other)
        {
            if (other == null || other.Selected == null || other.Selected.Length != Tris.Count)
            {
                ClearSelection();
                return;
            }
            Selected = (bool[])other.Selected.Clone();
            SelectedCount = other.SelectedCount;
        }

        // ------------------------------------------------------------------ Topologie

        public bool HasTopology { get { return _adj != null && _adj.Length == Tris.Count * 3; } }

        /// <summary>Vertexnummern und Kantennachbarschaft aufbauen.</summary>
        public void BuildTopology()
        {
            int n = Tris.Count;
            _vid = new int[n * 3];
            var ids = new Dictionary<VKey, int>(n * 2);

            for (int i = 0; i < n; i++)
            {
                Tri t = Tris[i];
                _vid[3 * i + 0] = Id(ids, t.A);
                _vid[3 * i + 1] = Id(ids, t.B);
                _vid[3 * i + 2] = Id(ids, t.C);
            }

            _adj = new int[n * 3];
            for (int i = 0; i < _adj.Length; i++) _adj[i] = -1;

            // Jede Kante wird von höchstens zwei Dreiecken benutzt. Der erste Finder
            // legt sie ab, der zweite verbindet sich mit ihm.
            var edges = new Dictionary<long, int>(n * 2);
            for (int i = 0; i < n; i++)
                for (int k = 0; k < 3; k++)
                {
                    int a = _vid[3 * i + k];
                    int b = _vid[3 * i + (k + 1) % 3];
                    long key = a < b ? ((long)a << 32) | (uint)b : ((long)b << 32) | (uint)a;

                    int other;
                    if (edges.TryGetValue(key, out other))
                    {
                        _adj[3 * i + k] = other / 3;
                        _adj[other] = i;
                        edges.Remove(key);                 // Kante ist voll
                    }
                    else edges[key] = 3 * i + k;
                }
        }

        private static int Id(Dictionary<VKey, int> ids, Vec3 p)
        {
            VKey k = new VKey(p);
            int id;
            if (ids.TryGetValue(k, out id)) return id;
            id = ids.Count;
            ids[k] = id;
            return id;
        }

        // ------------------------------------------------------------------ Auswahl

        public enum SelectMode { Ersetzen, Hinzufuegen, Entfernen }

        public void ClearSelection()
        {
            Selected = null;
            SelectedCount = 0;
        }

        public void SelectAll()
        {
            Selected = new bool[Tris.Count];
            for (int i = 0; i < Selected.Length; i++) Selected[i] = true;
            SelectedCount = Selected.Length;
        }

        /// <summary>
        /// Von <paramref name="startTri"/> aus über alle Nachbarn wachsen, solange der
        /// Winkel zwischen den Facettennormalen unter dem Knickwinkel bleibt.
        /// Liefert die Anzahl der Dreiecke in der gefundenen Fläche.
        /// </summary>
        public int SelectRegion(int startTri, double breakAngleDeg, SelectMode mode)
        {
            if (startTri < 0 || startTri >= Tris.Count) return 0;
            if (!HasTopology) BuildTopology();

            double cosBreak = Math.Cos(MathUtil.Clamp(breakAngleDeg, 0.1, 179.0) * MathUtil.Deg);

            bool[] region = new bool[Tris.Count];
            var stack = new Stack<int>();
            stack.Push(startTri);
            region[startTri] = true;
            int count = 1;

            while (stack.Count > 0)
            {
                int i = stack.Pop();
                Vec3 ni = Tris[i].N;
                for (int k = 0; k < 3; k++)
                {
                    int j = _adj[3 * i + k];
                    if (j < 0 || region[j]) continue;
                    if (Vec3.Dot(ni, Tris[j].N) < cosBreak) continue;   // echte Kante: hier endet die Flaeche
                    region[j] = true;
                    count++;
                    stack.Push(j);
                }
            }

            if (Selected == null || Selected.Length != Tris.Count)
            {
                Selected = new bool[Tris.Count];
                SelectedCount = 0;
            }

            if (mode == SelectMode.Ersetzen)
            {
                Selected = region;
                SelectedCount = count;
            }
            else
            {
                for (int i = 0; i < region.Length; i++)
                {
                    if (!region[i]) continue;
                    bool want = mode == SelectMode.Hinzufuegen;
                    if (Selected[i] != want) { Selected[i] = want; SelectedCount += want ? 1 : -1; }
                }
            }
            if (SelectedCount <= 0) ClearSelection();
            return count;
        }

        /// <summary>Hüllquader und Flächeninhalt der aktuellen Auswahl.</summary>
        public void SelectionInfo(out Aabb bounds, out double area, out Vec3 centroid)
        {
            bounds = Aabb.Empty;
            area = 0;
            Vec3 acc = Vec3.Zero;
            double wsum = 0;

            for (int i = 0; i < Tris.Count; i++)
            {
                if (!IsSelected(i)) continue;
                Tri t = Tris[i];
                bounds.Add(t.A); bounds.Add(t.B); bounds.Add(t.C);
                double a = Vec3.Cross(t.B - t.A, t.C - t.A).Length * 0.5;
                area += a;
                acc = acc + t.Centroid * a;
                wsum += a;
            }
            centroid = wsum > 1e-12 ? acc / wsum : Vec3.Zero;
        }

        /// <summary>
        /// Ausgleichskugel durch die ausgewählten Dreiecke (kleinste Fehlerquadrate).
        ///
        /// Aus |p − c|² = r² wird durch Ausmultiplizieren die lineare Beziehung
        /// |p|² = 2·c·p + (r² − |c|²), also ein Gleichungssystem in (cx, cy, cz, k)
        /// mit k = r² − |c|². Das lässt sich direkt lösen; anschliessend ist
        /// r = √(k + |c|²).
        ///
        /// <paramref name="residual"/> ist die mittlere Abweichung der Punkte von der
        /// gefundenen Kugel – klein heisst „die Fläche ist wirklich eine Kugel".
        /// </summary>
        public bool FitSphere(out Vec3 center, out double radius, out double residual)
        {
            center = Vec3.Zero; radius = 0; residual = double.MaxValue;

            var pts = new List<Vec3>(Tris.Count);
            for (int i = 0; i < Tris.Count; i++)
            {
                if (!IsSelected(i)) continue;
                Tri t = Tris[i];
                pts.Add(t.A); pts.Add(t.B); pts.Add(t.C);
            }
            if (pts.Count < 16) return false;

            double[,] m = new double[4, 4];
            double[] rhs = new double[4];
            foreach (Vec3 p in pts)
            {
                double[] row = { 2 * p.X, 2 * p.Y, 2 * p.Z, 1.0 };
                double b = p.LengthSq;
                for (int i = 0; i < 4; i++)
                {
                    for (int j = 0; j < 4; j++) m[i, j] += row[i] * row[j];
                    rhs[i] += row[i] * b;
                }
            }

            double[] sol;
            if (!Solve4(m, rhs, out sol)) return false;

            center = new Vec3(sol[0], sol[1], sol[2]);
            double r2 = sol[3] + center.LengthSq;
            if (r2 <= 0) return false;
            radius = Math.Sqrt(r2);

            double err = 0;
            foreach (Vec3 p in pts) err += Math.Abs((p - center).Length - radius);
            residual = err / pts.Count;
            return true;
        }

        /// <summary>Gauss mit Spaltenpivotisierung für das 4x4-System.</summary>
        private static bool Solve4(double[,] a, double[] b, out double[] x)
        {
            x = new double[4];
            double[,] m = (double[,])a.Clone();
            double[] r = (double[])b.Clone();

            for (int col = 0; col < 4; col++)
            {
                int piv = col;
                for (int i = col + 1; i < 4; i++)
                    if (Math.Abs(m[i, col]) > Math.Abs(m[piv, col])) piv = i;
                if (Math.Abs(m[piv, col]) < 1e-12) return false;

                if (piv != col)
                {
                    for (int j = 0; j < 4; j++) { double t = m[col, j]; m[col, j] = m[piv, j]; m[piv, j] = t; }
                    double tr = r[col]; r[col] = r[piv]; r[piv] = tr;
                }

                for (int i = col + 1; i < 4; i++)
                {
                    double f = m[i, col] / m[col, col];
                    if (f == 0) continue;
                    for (int j = col; j < 4; j++) m[i, j] -= f * m[col, j];
                    r[i] -= f * r[col];
                }
            }

            for (int i = 3; i >= 0; i--)
            {
                double s = r[i];
                for (int j = i + 1; j < 4; j++) s -= m[i, j] * x[j];
                x[i] = s / m[i, i];
            }
            return true;
        }
    }
}
