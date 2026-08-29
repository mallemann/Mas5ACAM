using System;

namespace Mas5ACAM
{
    /// <summary>
    /// Erzeugt das Beispielmodell: Kugel auf Zylinder, aufgespannt auf dem C-Tisch.
    /// Werkstück-Nullpunkt = Schnittpunkt C-Achse / Tischoberfläche, Z zeigt nach oben.
    /// </summary>
    public static class ModelGenerator
    {
        /// <summary>Beispiel nach Vorgabe: Zylinder D10 x L20 ab Z=0, Kugel D20 tangential
        /// aufgesetzt (Mittelpunkt Z=30).</summary>
        public static Mesh BallOnPost(double sphereDia = 20.0, double sphereCz = 30.0,
                                      double postDia = 10.0, double postLen = 20.0,
                                      int nu = 96, int nv = 48, int nPost = 64)
        {
            Mesh m = new Mesh();
            m.Name = "Kugel-auf-Zylinder";
            AddCylinder(m, postDia * 0.5, 0.0, postLen, nPost);

            // Ab hier kommt die Kugel. Sie ist die zu bearbeitende Flaeche und wird
            // gleich vorgewaehlt - der Zylinder ist Aufspannung und bleibt unberuehrt.
            int firstSphereTri = m.Count;
            AddSphere(m, new Vec3(0, 0, sphereCz), sphereDia * 0.5, nu, nv);
            m.RecomputeBounds();

            m.Selected = new bool[m.Count];
            for (int i = firstSphereTri; i < m.Count; i++) m.Selected[i] = true;
            m.SelectedCount = m.Count - firstSphereTri;
            return m;
        }

        /// <summary>
        /// Zweites Beispiel: Block mit welliger Freiformfläche obenauf – der Fall, für den
        /// die Strategie „Parallelbahnen" mit Z-Zustellung gedacht ist. Der Block ist auf
        /// der C-Achse zentriert; die Wellenfläche oben wird vorgewählt, die Seiten und der
        /// Boden bleiben Aufspannung.
        /// </summary>
        public static Mesh WavyBlock(double lx = 120, double ly = 80, double zBase = 18,
                                     double amp = 7, int nx = 140, int ny = 96)
        {
            Mesh m = new Mesh();
            m.Name = "Freiformflaeche-auf-Block";

            Func<double, double, double> h = (x, y) =>
                zBase + amp * Math.Sin(2 * Math.PI * (x / lx + 0.18))
                      + amp * 0.45 * Math.Cos(2 * Math.PI * (y / ly + 0.10))
                      + amp * 0.25 * Math.Sin(2 * Math.PI * (x / lx + y / ly));

            Func<int, int, Vec3> top = (i, j) =>
            {
                double x = -lx / 2 + lx * i / nx;
                double y = -ly / 2 + ly * j / ny;
                return new Vec3(x, y, h(x, y));
            };

            // Seiten und Boden zuerst - sie sind Aufspannung und bleiben unmarkiert.
            for (int i = 0; i < nx; i++)
            {
                Vec3 a = top(i, 0), b = top(i + 1, 0);
                m.AddQuad(new Vec3(a.X, a.Y, 0), new Vec3(b.X, b.Y, 0), b, a);
                Vec3 c = top(i, ny), d = top(i + 1, ny);
                m.AddQuad(c, d, new Vec3(d.X, d.Y, 0), new Vec3(c.X, c.Y, 0));
            }
            for (int j = 0; j < ny; j++)
            {
                Vec3 a = top(0, j), b = top(0, j + 1);
                m.AddQuad(a, b, new Vec3(b.X, b.Y, 0), new Vec3(a.X, a.Y, 0));
                Vec3 c = top(nx, j), d = top(nx, j + 1);
                m.AddQuad(new Vec3(c.X, c.Y, 0), new Vec3(d.X, d.Y, 0), d, c);
            }
            m.AddQuad(new Vec3(-lx / 2, -ly / 2, 0), new Vec3(-lx / 2, ly / 2, 0),
                      new Vec3(lx / 2, ly / 2, 0), new Vec3(lx / 2, -ly / 2, 0));

            int firstTop = m.Count;
            for (int i = 0; i < nx; i++)
                for (int j = 0; j < ny; j++)
                    m.AddQuad(top(i, j), top(i + 1, j), top(i + 1, j + 1), top(i, j + 1));

            m.RecomputeBounds();
            m.Selected = new bool[m.Count];
            for (int i = firstTop; i < m.Count; i++) m.Selected[i] = true;
            m.SelectedCount = m.Count - firstTop;
            return m;
        }

        /// <summary>Zylindermantel plus Boden- und Deckelscheibe.</summary>
        public static void AddCylinder(Mesh m, double r, double z0, double z1, int n)
        {
            for (int i = 0; i < n; i++)
            {
                double a0 = 2 * Math.PI * i / n, a1 = 2 * Math.PI * (i + 1) / n;
                Vec3 p0 = new Vec3(r * Math.Cos(a0), r * Math.Sin(a0), z0);
                Vec3 p1 = new Vec3(r * Math.Cos(a1), r * Math.Sin(a1), z0);
                Vec3 p2 = new Vec3(r * Math.Cos(a1), r * Math.Sin(a1), z1);
                Vec3 p3 = new Vec3(r * Math.Cos(a0), r * Math.Sin(a0), z1);
                m.AddQuad(p0, p1, p2, p3);                                  // Mantel, Normale nach aussen

                m.Add(new Tri(new Vec3(0, 0, z1), p3, p2));                 // Deckel, Normale +Z
                m.Add(new Tri(new Vec3(0, 0, z0), p1, p0));                 // Boden, Normale -Z
            }
        }

        /// <summary>Kugel in Kugelkoordinaten, Normalen nach aussen.</summary>
        public static void AddSphere(Mesh m, Vec3 c, double r, int nu, int nv)
        {
            for (int j = 0; j < nv; j++)
            {
                double t0 = Math.PI * j / nv, t1 = Math.PI * (j + 1) / nv;   // Polarwinkel ab +Z
                for (int i = 0; i < nu; i++)
                {
                    double f0 = 2 * Math.PI * i / nu, f1 = 2 * Math.PI * (i + 1) / nu;
                    Vec3 a = Sph(c, r, t0, f0), b = Sph(c, r, t0, f1);
                    Vec3 d = Sph(c, r, t1, f0), e = Sph(c, r, t1, f1);
                    if (j == 0)         m.Add(new Tri(a, d, e));             // Kappe am Nordpol
                    else if (j == nv-1) m.Add(new Tri(a, d, b));             // Kappe am Südpol
                    else                m.AddQuad(a, d, e, b);
                }
            }
        }

        private static Vec3 Sph(Vec3 c, double r, double theta, double phi)
        {
            return new Vec3(c.X + r * Math.Sin(theta) * Math.Cos(phi),
                            c.Y + r * Math.Sin(theta) * Math.Sin(phi),
                            c.Z + r * Math.Cos(theta));
        }
    }
}
