using System;
using System.Collections.Generic;

namespace Mas5ACAM
{
    /// <summary>
    /// Kugel fallen lassen: Wie tief darf der Kugelmittelpunkt eines Kugelfräsers auf
    /// einer senkrechten Linie über (x, y) sinken, ohne in die Fläche zu greifen?
    ///
    /// <para>Es genügt <b>nicht</b>, nur den Flächenpunkt senkrecht unter dem Werkzeug zu
    /// betrachten. Die Kugel hat Radius R, also kann sie seitlich in eine ansteigende
    /// Fläche schneiden, obwohl direkt unter ihr noch Luft ist. Genau das passiert beim
    /// Schruppen in Ebenen an jeder Flanke.</para>
    ///
    /// <para>Richtig ist die Bedingung „Abstand Mittelpunkt zur Fläche ≥ R". Für ein
    /// einzelnes Dreieck lässt sich der tiefste zulässige Mittelpunkt geschlossen
    /// ausrechnen – getrennt für die drei Fälle, in denen die Kugel auf der Dreiecksfläche,
    /// auf einer Kante oder auf einer Ecke zu liegen kommt. Über alle Dreiecke in
    /// Reichweite wird das Maximum genommen.</para>
    /// </summary>
    internal static class BallDrop
    {
        /// <summary>Tiefster zulässiger Kugelmittelpunkt über (qx, qy) für dieses Dreieck.
        /// false, wenn die Kugel dieses Dreieck auf dieser Linie gar nicht berührt.</summary>
        public static bool OnTriangle(double qx, double qy, double r, in Tri t, out double z)
        {
            z = double.NegativeInfinity;
            bool any = false;

            // --- Fall 1: die Kugel liegt auf der Dreiecksfläche ---------------------------
            Vec3 n = t.N;
            if (n.Z > 1e-9)
            {
                // Mittelpunkt auf der um r versetzten Ebene: n·(c − A) = r
                double zf = (r + Vec3.Dot(n, t.A) - n.X * qx - n.Y * qy) / n.Z;
                Vec3 c = new Vec3(qx, qy, zf);
                Vec3 touch = c - n * r;
                if ((touch - TriMath.ClosestPointOnTriangle(touch, t)).LengthSq < 1e-12)
                {
                    z = zf; any = true;
                }
            }

            // --- Fall 2: die Kugel liegt auf einer Kante ---------------------------------
            any |= Edge(qx, qy, r, t.A, t.B, ref z);
            any |= Edge(qx, qy, r, t.B, t.C, ref z);
            any |= Edge(qx, qy, r, t.C, t.A, ref z);

            // --- Fall 3: die Kugel liegt auf einer Ecke ----------------------------------
            any |= Vertex(qx, qy, r, t.A, ref z);
            any |= Vertex(qx, qy, r, t.B, ref z);
            any |= Vertex(qx, qy, r, t.C, ref z);

            return any;
        }

        /// <summary>
        /// Kugel auf einer Kante. Mit c = (qx, qy, z) und w = c − P ist der Abstand zur
        /// Geraden |w|² − (w·ê)². Einsetzen von w = w0 + z·k liefert eine quadratische
        /// Gleichung in z; gesucht ist die obere Lösung, und die Berührung muss innerhalb
        /// der Strecke liegen.
        /// </summary>
        private static bool Edge(double qx, double qy, double r, Vec3 p, Vec3 q, ref double z)
        {
            Vec3 e = q - p;
            double len = e.Length;
            if (len < 1e-12) return false;
            Vec3 eu = e / len;

            Vec3 w0 = new Vec3(qx - p.X, qy - p.Y, -p.Z);
            double w0e = Vec3.Dot(w0, eu);
            double kz = eu.Z;

            double a = 1.0 - kz * kz;
            if (a < 1e-12) return false;                       // senkrechte Kante
            double b = 2.0 * w0.Z - 2.0 * w0e * kz;
            double c0 = w0.LengthSq - w0e * w0e - r * r;

            double disc = b * b - 4.0 * a * c0;
            if (disc < 0) return false;

            double zc = (-b + Math.Sqrt(disc)) / (2.0 * a);
            double s = (w0e + zc * kz) / len;                  // Lage der Beruehrung auf der Strecke
            if (s < 0.0 || s > 1.0) return false;

            if (zc > z) { z = zc; return true; }
            return false;
        }

        /// <summary>Kugel auf einer Ecke: |c − V| = r mit c senkrecht über (qx, qy).</summary>
        private static bool Vertex(double qx, double qy, double r, Vec3 v, ref double z)
        {
            double dx = qx - v.X, dy = qy - v.Y;
            double d2 = dx * dx + dy * dy;
            if (d2 > r * r) return false;

            double zc = v.Z + Math.Sqrt(r * r - d2);
            if (zc > z) { z = zc; return true; }
            return false;
        }

        /// <summary>
        /// Tiefster zulässiger Kugelmittelpunkt über (qx, qy) für das ganze Netz –
        /// betrachtet werden nur ausgewählte Dreiecke. <paramref name="hitTri"/> liefert
        /// das Dreieck, das die Kugel trägt (−1, wenn keines in Reichweite ist).
        /// </summary>
        public static double OnMesh(TriGrid grid, double qx, double qy, double r,
                                    List<int> cand, HashSet<int> scratch, out int hitTri)
        {
            Mesh m = grid.Mesh;
            Vec3 lo = new Vec3(qx - r, qy - r, m.Bounds.Min.Z - r - 1);
            Vec3 hi = new Vec3(qx + r, qy + r, m.Bounds.Max.Z + r + 1);
            grid.QueryBox(lo, hi, cand, scratch);

            double best = double.NegativeInfinity;
            hitTri = -1;

            for (int i = 0; i < cand.Count; i++)
            {
                int ti = cand[i];
                if (!m.IsSelected(ti)) continue;
                double z;
                if (!OnTriangle(qx, qy, r, m.Tris[ti], out z)) continue;
                if (z > best) { best = z; hitTri = ti; }
            }
            return best;
        }
    }
}
