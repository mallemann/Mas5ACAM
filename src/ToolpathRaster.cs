using System;
using System.Collections.Generic;
using System.Globalization;

namespace Mas5ACAM
{
    /// <summary>
    /// Strategie „Parallelbahnen" – die richtige Wahl für eine Freiformfläche auf einem
    /// Block. Die Fläche wird von oben in Z abgetastet, die Bahnen laufen als paralleles
    /// Raster darüber.
    ///
    /// <para><b>Z-Zustelltiefe.</b> Steht über der Fläche mehr Material, als das Werkzeug
    /// in einem Schnitt abtragen darf, wird in Ebenen geschruppt: von der Rohteil-Oberkante
    /// abwärts in Schritten von höchstens <see cref="CamParameters.MaxZStep"/>. In jeder
    /// Ebene fährt das Werkzeug die Fläche ab, wird aber nach unten von der Ebene begrenzt –
    /// wo die Fläche tiefer liegt, sitzt der Fräser auf der Ebene. Die letzte Bahn ist die
    /// Fläche selbst (Schlichten).</para>
    ///
    /// <para>Auch hier gilt: Kugelmittelpunkt = Berührpunkt + R · Normale. Deshalb ist die
    /// Bahn unabhängig von der Werkzeugneigung schnittgenau, und der tiefste Punkt der
    /// Fräserkugel liegt immer senkrecht unter dem Kugelmittelpunkt – ein Ebenenschnitt
    /// bleibt also auch bei angestelltem Werkzeug exakt auf der Ebene.</para>
    /// </summary>
    public static partial class ToolpathGenerator
    {
        private static List<List<Sample>> SampleRaster(TriGrid grid, CamParameters cp, Toolpath tp)
        {
            var all = new List<List<Sample>>();
            Mesh mesh = grid.Mesh;

            Aabb sel; double area; Vec3 cen;
            mesh.SelectionInfo(out sel, out area, out cen);
            if (sel.IsEmpty)
            {
                tp.Log.Add("Keine Flaeche zum Abtasten gefunden.");
                return all;
            }

            double ang = cp.RasterAngleDeg * MathUtil.Deg;
            Vec3 uDir = new Vec3(Math.Cos(ang), Math.Sin(ang), 0);
            Vec3 vDir = new Vec3(-Math.Sin(ang), Math.Cos(ang), 0);

            double uMin, uMax, vMin, vMax;
            Extent(sel, uDir, out uMin, out uMax);
            Extent(sel, vDir, out vMin, out vMax);

            double R = cp.Tool.Radius;
            double zTop = cp.AutoStockTop ? sel.Max.Z : cp.StockTop;
            double zBot = cp.UseZMin ? Math.Max(cp.ZMin, sel.Min.Z) : sel.Min.Z;
            if (zTop < zBot) zTop = zBot;                    // unsinnige Eingabe abfangen
            double rayZ = Math.Max(zTop, sel.Max.Z) + 10.0;

            // Ebenen von Zmax abwaerts bis Zmin. Die erste liegt auf Zmax, die letzte auf
            // Zmin - tiefer kommt das Werkzeug nie. Bei automatischen Grenzen ist die
            // letzte Bahn stattdessen die freie Schlichtbahn auf der Flaeche.
            // Schruppebenen von Zmax abwaerts, dann immer eine freie Schlichtbahn.
            //
            // Die tiefste Schruppebene liegt eine Zustellung ueber Zmin, nicht auf Zmin:
            // eine Ebene genau auf Zmin wuerde dort, wo die Flaeche tiefer liegt, einen
            // flachen Boden fraesen. Zmin soll aber nur begrenzen, welcher Teil der
            // Flaeche bearbeitet wird - nicht selbst Material abtragen. Was unterhalb
            // liegt, bleibt unberuehrt; die Schlichtbahn wird dort schlicht ausgelassen.
            var planes = new List<double>();
            double span = zTop - zBot;
            int n = (cp.MaxZStep > 1e-6 && span > cp.MaxZStep + 1e-9)
                  ? (int)Math.Ceiling(span / cp.MaxZStep) : 1;
            double step = span / n;
            for (int k = 0; k < n; k++) planes.Add(zTop - k * step);

            // Liegt die Rohteil-Oberkante nicht ueber der Flaeche, steht dort auch kein
            // Material - die oberste Ebene waere ein Leerschnitt und faellt weg.
            if (zTop <= sel.Max.Z + 1e-9 && planes.Count > 0) planes.RemoveAt(0);
            planes.Add(double.NegativeInfinity);

            tp.Log.Add(string.Format(CultureInfo.InvariantCulture,
                "Rohteil-Oberkante {0:0.###} mm ({1}), abgetragen bis {2:0.###} mm = {3:0.###} mm Hoehe{4}",
                zTop, cp.AutoStockTop ? "automatisch = hoechster Flaechenpunkt" : "von Hand gesetzt",
                zBot, span,
                zTop > sel.Max.Z + 1e-9
                    ? " - davon " + (zTop - sel.Max.Z).ToString("0.###") + " mm Rohmaterial ueber der Flaeche"
                    : ""));
            tp.Log.Add(string.Format(CultureInfo.InvariantCulture,
                "Z-Zustellung: {0} Schruppebenen mit je {1:0.###} mm (Grenze {2:0.###} mm) " +
                "plus Schlichtbahn auf der Flaeche", planes.Count - 1, step, cp.MaxZStep));
            tp.Log.Add(string.Format(CultureInfo.InvariantCulture,
                "Bahnabstand quer: Schlichten {0:0.####} mm, Schruppen {1:0.###} mm ({2:0.##} x D{3:0.###})",
                FlatStepover(cp), Math.Max(0.05, cp.RoughStepoverFactor * cp.Tool.Diameter),
                cp.RoughStepoverFactor, cp.Tool.Diameter));

            tp.ZLevels.Clear();
            foreach (double z in planes) tp.ZLevels.Add(double.IsNegativeInfinity(z) ? zBot : z);

            var cand = new List<int>(512);
            var seen = new HashSet<int>();

            double finishOver = FlatStepover(cp);
            double roughOver = Math.Max(0.05, cp.RoughStepoverFactor * cp.Tool.Diameter);

            for (int pi = 0; pi < planes.Count; pi++)
            {
                bool finish = double.IsNegativeInfinity(planes[pi]);
                double over = finish ? finishOver : roughOver;
                double duStep = MathUtil.Clamp(over * 0.5, 0.05, 1.0);

                int lines = Math.Max(1, (int)Math.Ceiling((vMax - vMin) / over));
                double dv = lines > 0 ? (vMax - vMin) / lines : over;

                int clamped = 0;                          // Punkte, die wirklich auf der Ebene sitzen
                int firstPassOfLevel = all.Count;

                List<Sample> open = null;                 // Bahn, an die angehaengt werden darf
                for (int li = 0; li <= lines; li++)
                {
                    double v = vMin + li * dv;
                    bool back = cp.ZigZag && (li % 2 == 1);

                    List<List<Sample>> lineParts = ScanLine(grid, cp, uDir, vDir, v,
                                                            uMin, uMax, duStep, rayZ, planes[pi], back,
                                                            tp, cand, seen);

                    foreach (List<Sample> part in lineParts)
                    {
                        foreach (Sample sm in part) if (sm.OnPlane) clamped++;

                        List<Sample> thin = Decimate(part, cp);
                        if (thin.Count < 2) continue;

                        // Zickzack: aufeinanderfolgende Bahnen direkt verbinden, solange der
                        // Sprung nicht groesser als zwei Zustellungen ist. Das spart bei einem
                        // Raster mit hunderten Zeilen sehr viel Leerweg.
                        if (open != null && cp.ZigZag &&
                            (open[open.Count - 1].P - thin[0].P).Length <= 2.0 * over)
                        {
                            open.AddRange(thin);
                        }
                        else
                        {
                            open = new List<Sample>(thin);
                            all.Add(open);
                        }
                    }
                    if (lineParts.Count == 0) open = null;   // Luecke: Verbindung abreissen
                }

                // Eine Schruppebene, die nirgends greift, waere nur eine Kopie der
                // Schlichtbahn - die spart sich die App.
                if (!finish && clamped == 0)
                {
                    all.RemoveRange(firstPassOfLevel, all.Count - firstPassOfLevel);
                    tp.Log.Add(string.Format(CultureInfo.InvariantCulture,
                        "Ebene Z {0:0.###} entfaellt: dort steht kein Material an", planes[pi]));
                }
            }
            return all;
        }

        /// <summary>Eine Rasterzeile abtasten. Liefert die zusammenhängenden Stücke –
        /// Löcher entstehen dort, wo die gewählte Fläche nicht getroffen wird.</summary>
        private static List<List<Sample>> ScanLine(TriGrid grid, CamParameters cp,
                                                   Vec3 uDir, Vec3 vDir, double v,
                                                   double uMin, double uMax, double du,
                                                   double rayZ, double plane, bool reverse,
                                                   Toolpath tp, List<int> cand, HashSet<int> seen)
        {
            var parts = new List<List<Sample>>();
            var cur = new List<Sample>();
            int n = Math.Max(1, (int)Math.Ceiling((uMax - uMin) / du));

            for (int i = 0; i <= n; i++)
            {
                double u = reverse ? uMax - i * (uMax - uMin) / n : uMin + i * (uMax - uMin) / n;
                Vec3 xy = uDir * u + vDir * v;

                Sample s = ProbeDown(grid, cp, xy.X, xy.Y, rayZ, plane, cand, seen);
                if (s.Valid) cur.Add(s);
                else
                {
                    tp.MissedRays++;
                    if (cur.Count > 1) { parts.Add(cur); cur = new List<Sample>(); }
                    else cur.Clear();
                }
            }
            if (cur.Count > 1) parts.Add(cur);
            return parts;
        }

        /// <summary>
        /// Werkzeuglage über einer Rasterstelle bestimmen.
        ///
        /// Zuerst wird geprüft, ob dort überhaupt gewählte Fläche liegt (senkrechter
        /// Strahl). Dann wird die Fräserkugel <b>fallen gelassen</b>: der Mittelpunkt sinkt
        /// so weit, bis die Kugel die Fläche gerade noch berührt – siehe <see cref="BallDrop"/>.
        /// Das ist der entscheidende Unterschied zu „Flächenpunkt plus R mal Normale": nur
        /// so kann die Kugel nicht seitlich in eine ansteigende Flanke schneiden.
        /// Zuletzt begrenzt die Schruppebene den Mittelpunkt nach unten.
        /// </summary>
        private static Sample ProbeDown(TriGrid grid, CamParameters cp, double qx, double qy,
                                        double rayZ, double plane, List<int> cand, HashSet<int> seen)
        {
            Sample s = new Sample();

            double t; int tri;
            if (!grid.RayFirstHit(new Vec3(qx, qy, rayZ), new Vec3(0, 0, -1), out t, out tri)) return s;
            if (!grid.Mesh.IsSelected(tri)) return s;

            double R = cp.Tool.Radius;
            double dropR = R + Math.Max(0.0, cp.Stock);      // Aufmass: Kugel bleibt weiter weg

            int carrier;
            double cz = BallDrop.OnMesh(grid, qx, qy, dropR, cand, seen, out carrier);
            double planeZ = double.IsNegativeInfinity(plane) ? double.NegativeInfinity : plane + R;

            Vec3 center;
            Vec3 n;

            if (carrier >= 0 && cz >= planeZ)
            {
                s.OnPlane = false;
                center = new Vec3(qx, qy, cz);
                Vec3 touch = TriMath.ClosestPointOnTriangle(center, grid.Mesh.Tris[carrier]);
                Vec3 nGeo = (center - touch).Normalized;

                // Für die Werkzeugachse ist die geglättete Flächennormale die bessere Wahl -
                // aber nur, wenn die Kugel wirklich auf der Fläche liegt und nicht auf einer
                // Kante. Sonst zeigt die geometrische Richtung die Wahrheit.
                Vec3 nSm = grid.Mesh.NormalAt(carrier, touch);
                if (nSm.Z < 0) nSm = -nSm;
                n = (nGeo.LengthSq > 0.5 && Vec3.Dot(nSm, nGeo) > 0.7) ? nSm : nGeo;
                if (n.LengthSq < 0.5 || n.Z < 1e-6) n = Vec3.UnitZ;
            }
            else if (!double.IsNegativeInfinity(planeZ))
            {
                center = new Vec3(qx, qy, planeZ);          // sitzt flach auf der Ebene
                n = Vec3.UnitZ;
                s.OnPlane = true;
            }
            else return s;

            Vec3 contact = center - n * R;                   // BuildPasses rechnet Center = P + R*N
            if (!AboveZMin(cp, contact.Z)) return s;          // unterhalb der Bearbeitungsgrenze

            s.Valid = true;
            s.P = contact;
            s.N = n;
            s.R = 0;
            s.Theta = Math.Acos(MathUtil.Clamp(n.Z, -1, 1)) * MathUtil.Rad;   // Flankenwinkel
            return s;
        }

        /// <summary>Zustellung für eine ebene Fläche aus der Restmaterialhöhe:
        /// h = R − √(R² − (s/2)²), also s = 2·√(2·R·h − h²).</summary>
        internal static double FlatStepover(CamParameters cp)
        {
            if (cp.UseFixedStepover) return MathUtil.Clamp(cp.FixedStepover, 0.01, 1.8 * cp.Tool.Radius);

            double R = cp.Tool.Radius;
            double h = MathUtil.Clamp(cp.ScallopHeight, 1e-5, R * 0.9);
            return MathUtil.Clamp(2.0 * Math.Sqrt(2.0 * R * h - h * h), 0.01, 1.8 * R);
        }

        /// <summary>
        /// Dicht abgetastete Bahn ausdünnen. Ein Punkt darf entfallen, solange die Sehne
        /// nirgends weiter als die Sehnentoleranz von der Bahn abweicht und sich die
        /// Flächennormale um weniger als den erlaubten Winkel dreht – Letzteres begrenzt
        /// die Drehachsbewegung je Satz.
        /// </summary>
        private static List<Sample> Decimate(List<Sample> src, CamParameters cp)
        {
            var outp = new List<Sample>();
            if (src.Count == 0) return outp;

            double tol = Math.Max(cp.ChordTolerance, 1e-6);
            double cosMax = Math.Cos(MathUtil.Clamp(cp.MaxAngStepDeg, 0.1, 30.0) * MathUtil.Deg);
            const int window = 64;

            outp.Add(src[0]);
            int anchor = 0;
            while (anchor < src.Count - 1)
            {
                int best = anchor + 1;
                int limit = Math.Min(src.Count - 1, anchor + window);

                for (int j = anchor + 2; j <= limit; j++)
                {
                    if (Vec3.Dot(src[anchor].N, src[j].N) < cosMax) break;

                    bool ok = true;
                    for (int k = anchor + 1; k < j; k++)
                        if (PointSegDistance(src[k].P, src[anchor].P, src[j].P) > tol) { ok = false; break; }

                    if (!ok) break;
                    best = j;
                }
                outp.Add(src[best]);
                anchor = best;
            }
            return outp;
        }

        private static double PointSegDistance(Vec3 p, Vec3 a, Vec3 b)
        {
            Vec3 ab = b - a;
            double len2 = ab.LengthSq;
            if (len2 < 1e-18) return (p - a).Length;
            double t = MathUtil.Clamp(Vec3.Dot(p - a, ab) / len2, 0, 1);
            return (p - (a + ab * t)).Length;
        }

        /// <summary>Ausdehnung des Hüllquaders in einer waagrechten Richtung.</summary>
        private static void Extent(Aabb b, Vec3 dir, out double lo, out double hi)
        {
            lo = double.MaxValue; hi = double.MinValue;
            double[] xs = { b.Min.X, b.Max.X };
            double[] ys = { b.Min.Y, b.Max.Y };
            foreach (double x in xs)
                foreach (double y in ys)
                {
                    double d = x * dir.X + y * dir.Y;
                    if (d < lo) lo = d;
                    if (d > hi) hi = d;
                }
        }
    }
}
