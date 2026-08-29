using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using Mas5ACAM;

namespace Mas5ACAM.Selftest
{
    /// <summary>
    /// Prüft den Rechenkern ohne Oberfläche: Kinematik, Bahngeometrie und Postprozessor.
    /// Aufruf:  dotnet run --project selftest [Ausgabeordner]
    /// </summary>
    internal static class Selftest
    {
        private static int _fail;

        private static void Check(bool ok, string what)
        {
            Console.WriteLine((ok ? "  OK   " : "  FEHL ") + what);
            if (!ok) _fail++;
        }

        private static int Main(string[] args)
        {
            string outDir = args.Length > 0 ? args[0] : Directory.GetCurrentDirectory();
            Directory.CreateDirectory(outDir);
            CultureInfo.DefaultThreadCurrentCulture = CultureInfo.InvariantCulture;

            Console.WriteLine("== 1. Kinematik ==");
            TestKinematics();

            Console.WriteLine();
            Console.WriteLine("== 2. Modell ==");
            Mesh mesh = ModelGenerator.BallOnPost();
            mesh.BuildSmoothNormals();
            TriGrid grid = new TriGrid(mesh);
            Console.WriteLine("  Dreiecke: " + mesh.Count);
            Console.WriteLine("  Huelle:   " + mesh.Bounds.Min + " .. " + mesh.Bounds.Max);
            Check(Math.Abs(mesh.Bounds.Max.Z - 40.0) < 1e-6, "Kugelscheitel liegt bei Z = 40");
            Check(Math.Abs(mesh.Bounds.Min.Z) < 1e-6, "Zylinderfuss liegt bei Z = 0");
            TestRaycast(grid);

            Console.WriteLine();
            Console.WriteLine("== 3. Werkzeugweg ==");
            CamParameters cp = new CamParameters();
            DateTime t0 = DateTime.Now;
            Toolpath tp = ToolpathGenerator.Generate(mesh, grid, cp);
            double secs = (DateTime.Now - t0).TotalSeconds;
            foreach (string s in tp.Log) Console.WriteLine("  " + s);
            Console.WriteLine("  Rechenzeit " + secs.ToString("0.00") + " s");
            TestPath(tp, cp);

            if (Environment.GetEnvironmentVariable("DUMP") != null) DumpPoints(tp);
            if (Environment.GetEnvironmentVariable("NDIAG") != null) DiagNormals(mesh, grid);

            Console.WriteLine();
            Console.WriteLine("== 4. Postprozessor ==");
            string nc = new PostProcessor(cp).Build(tp, mesh.Name);
            string ncPath = Path.Combine(outDir, "kugel_5achs.nc");
            File.WriteAllText(ncPath, nc);
            string[] lines = nc.Replace("\r", "").Split('\n');
            Console.WriteLine("  " + lines.Length + " Zeilen -> " + ncPath);
            TestGCode(lines, cp);

            TestMoveList(tp, cp);

            Console.WriteLine();
            Console.WriteLine("== 5. G94-Variante ==");
            Check(nc.Contains("G93"), "Voreinstellung ist G93 Inverszeit");
            Check(nc.TrimEnd().EndsWith("%"), "Programm ist vollstaendig");

            cp.FeedMode = FeedMode.G94Kompensiert;
            Toolpath tp94 = ToolpathGenerator.Generate(mesh, grid, cp);
            string nc94 = new PostProcessor(cp).Build(tp94, mesh.Name);
            File.WriteAllText(Path.Combine(outDir, "kugel_5achs_g94.nc"), nc94);
            Check(!nc94.Contains("G93"), "G94-Variante schaltet kein G93 ein");
            Check(nc94.Contains(" F"), "G94-Variante gibt Vorschubwerte aus");
            Check(tp94.RotaryDominatedBlocks > tp94.PointCount / 2,
                  "In " + tp94.RotaryDominatedBlocks + " von " + tp94.PointCount +
                  " Saetzen dominiert die C-Drehung - deshalb ist G93 die Voreinstellung");
            cp.FeedMode = FeedMode.G93Inverszeit;

            Console.WriteLine();
            Console.WriteLine("== 6. STL-Export/Import ==");
            string stl = Path.Combine(outDir, "kugel_auf_zylinder.stl");
            StlIo.SaveBinary(mesh, stl);
            Mesh re = StlIo.Load(stl);
            Check(re.Count == mesh.Count, "STL-Roundtrip: " + re.Count + " Dreiecke");
            Check(Math.Abs(re.Bounds.Max.Z - 40.0) < 1e-3, "STL-Roundtrip: Huelle stimmt");

            Console.WriteLine();
            Console.WriteLine("== 7. Flaechenauswahl ==");
            TestSelection(mesh);

            Console.WriteLine();
            Console.WriteLine("== 8. Werkstueck-Koordinatensystem ==");
            TestWorkpiece();

            Console.WriteLine();
            Console.WriteLine("== 9. Freiformflaeche: Parallelbahnen mit Z-Zustellung ==");
            TestRaster(outDir);

            Console.WriteLine();
            Console.WriteLine("== 10. Z-Fenster: welcher Hoehenbereich bearbeitet wird ==");
            TestZWindow();

            Console.WriteLine();
            Console.WriteLine(_fail == 0 ? "ALLE PRUEFUNGEN BESTANDEN" : (_fail + " PRUEFUNG(EN) FEHLGESCHLAGEN"));
            Console.WriteLine();
            Console.WriteLine("--- Erste 30 GCode-Zeilen ---");
            for (int i = 0; i < Math.Min(30, lines.Length); i++) Console.WriteLine(lines[i]);
            Console.WriteLine("--- Letzte 8 Zeilen ---");
            for (int i = Math.Max(0, lines.Length - 9); i < lines.Length; i++) Console.WriteLine(lines[i]);

            return _fail == 0 ? 0 : 1;
        }

        // ---------------------------------------------------------------- 1. Kinematik

        private static void TestKinematics()
        {
            Machine5Axis m = new Machine5Axis();
            Random rnd = new Random(12345);
            double worstAxis = 0, worstPoint = 0;

            for (int i = 0; i < 20000; i++)
            {
                // zufällige Werkzeugachse mit k >= 0 (das ist der von A = +/-90 Grad erreichbare Halbraum)
                Vec3 t = new Vec3(rnd.NextDouble() * 2 - 1, rnd.NextDouble() * 2 - 1, rnd.NextDouble()).Normalized;
                if (t.LengthSq < 0.5) continue;

                double a, c;
                bool ok = m.ChooseAC(t, 0, 0, out a, out c);
                if (!ok) { Console.WriteLine("  unerwartet unerreichbar: " + t); _fail++; continue; }

                // Die Achse muss im Maschinenraum senkrecht stehen
                Vec3 inMachine = m.ForwardDir(t, a, c);
                worstAxis = Math.Max(worstAxis, (inMachine - Vec3.UnitZ).Length);

                // Rueckrechnung der Achse aus der Achsstellung
                Vec3 back = Machine5Axis.ToolAxisFromAC(a, c);
                worstPoint = Math.Max(worstPoint, (back - t).Length);
            }
            Check(worstAxis < 1e-9, "Werkzeugachse steht nach Forward() exakt in Maschinen-Z (max. Fehler " + worstAxis.ToString("0.0e+0") + ")");
            Check(worstPoint < 1e-9, "ToolAxisFromAC ist die exakte Umkehrung (max. Fehler " + worstPoint.ToString("0.0e+0") + ")");

            // A-Grenze: eine Achse unterhalb des Aequators ist auf dieser Maschine nicht erreichbar
            double a2, c2;
            bool reach = m.ChooseAC(new Vec3(0.6, 0, -0.8), 0, 0, out a2, out c2);
            Check(!reach, "Werkzeugachse mit negativer Z-Komponente ist korrekt als unerreichbar erkannt");
            Check(Math.Abs(a2 - 90.0) < 1e-9, "A wird dabei auf die Grenze 90 Grad begrenzt (A = " + a2.ToString("0.000") + ")");

            // Tischversatz: ein Punkt auf der C-Achse bleibt bei C-Drehung stehen
            Machine5Axis m2 = new Machine5Axis { TableAboveA = 25.0 };
            Vec3 p1 = m2.Forward(new Vec3(0, 0, 10), 0, 0);
            Vec3 p2 = m2.Forward(new Vec3(0, 0, 10), 0, 137.0);
            Check((p1 - p2).Length < 1e-12, "Punkt auf der C-Achse bleibt bei C-Drehung ortsfest");

            // Der Tischversatz darf die ausgegebenen Werte NICHT anheben: der Nullpunkt
            // wird auf dem Tisch angetastet, nicht auf der A-Achse.
            Check(p1.Length < 1e-12 || Math.Abs(p1.Z - 10.0) < 1e-12,
                  "Tisch 25 mm ueber der A-Achse: ein Punkt 10 mm ueber dem Tisch steht bei " +
                  "A=C=0 auf Z" + p1.Z.ToString("0.###") + " (nicht Z35)");
            Check(m2.Forward(Vec3.Zero, 0, 0).Length < 1e-12,
                  "Der Werkstuecknullpunkt selbst liegt bei A=C=0 auf X0 Y0 Z0");

            // Beim Schwenken holt der Nullpunkt dagegen aus - das ist die Kinematik.
            Vec3 pk = m2.Forward(Vec3.Zero, 90, 0);
            Check(Math.Abs(pk.X) < 1e-12 && Math.Abs(pk.Y + 25.0) < 1e-12 && Math.Abs(pk.Z + 25.0) < 1e-12,
                  "Bei A=90 wandert der Nullpunkt auf " + pk + " - Tischversatz wirkt in der Kinematik");
            Check(Math.Abs(new Machine5Axis { TableAboveA = 0 }.Forward(Vec3.Zero, 90, 0).Length) < 1e-12,
                  "Ohne Tischversatz bleibt er beim Schwenken im Ursprung");

            // Ueber eine ganze Bahn: der Unterschied zwischen Tischversatz 0 und 25 mm darf
            // ausschliesslich das Ausholen beim Schwenken sein, RotX(d,A) - d. Bei A = 0
            // muss er null sein - sonst waere das Programm um den Versatz verschoben.
            Machine5Axis mv = new Machine5Axis { TableAboveA = 25.0 };
            Machine5Axis m0 = new Machine5Axis();
            Vec3 dv = new Vec3(0, 0, 25);
            double worstDiff = 0, worstAtZeroA = 0;
            for (int i = 0; i < 4000; i++)
            {
                Vec3 p = new Vec3(rnd.NextDouble() * 40 - 20, rnd.NextDouble() * 40 - 20, rnd.NextDouble() * 40);
                double aa = rnd.NextDouble() * 180 - 90, cc = rnd.NextDouble() * 720 - 360;
                Vec3 soll = Vec3.RotX(dv, aa * MathUtil.Deg) - dv;
                worstDiff = Math.Max(worstDiff, ((mv.Forward(p, aa, cc) - m0.Forward(p, aa, cc)) - soll).Length);
                worstAtZeroA = Math.Max(worstAtZeroA, (mv.Forward(p, 0, cc) - m0.Forward(p, 0, cc)).Length);
            }
            Check(worstDiff < 1e-12,
                  "Der Tischversatz wirkt genau als Schwenkausholung (max. Abweichung " +
                  worstDiff.ToString("0.0e+0") + " mm)");
            Check(worstAtZeroA < 1e-12,
                  "Bei A = 0 aendert der Tischversatz keine einzige Koordinate (max. " +
                  worstAtZeroA.ToString("0.0e+0") + " mm)");

            // Das Werkstueck muss um die A-Achse kreisen, nicht um die X-Achse durch den
            // Nullpunkt. Pruefbar am Nullpunkt selbst: sein Abstand zur A-Achse (waagrechte
            // Gerade in X auf Hoehe -25) bleibt konstant, der zur X-Achse nicht.
            double dAmin = double.MaxValue, dAmax = double.MinValue;
            double dXmin = double.MaxValue, dXmax = double.MinValue;
            for (int i = 0; i <= 180; i++)
            {
                double aa = -90.0 + i;
                Vec3 z0 = mv.Forward(Vec3.Zero, aa, 37.0);
                double dA = Math.Sqrt(z0.Y * z0.Y + (z0.Z + 25.0) * (z0.Z + 25.0));
                double dX = Math.Sqrt(z0.Y * z0.Y + z0.Z * z0.Z);
                dAmin = Math.Min(dAmin, dA); dAmax = Math.Max(dAmax, dA);
                dXmin = Math.Min(dXmin, dX); dXmax = Math.Max(dXmax, dX);
            }
            Check(Math.Abs(dAmax - 25.0) < 1e-9 && Math.Abs(dAmin - 25.0) < 1e-9,
                  "Der Nullpunkt kreist um die A-Achse mit konstantem Radius " +
                  dAmin.ToString("0.0000") + " .. " + dAmax.ToString("0.0000") + " mm");
            Check(dXmax - dXmin > 10.0,
                  "Zur X-Achse schwankt der Abstand dagegen (" + dXmin.ToString("0.000") + " .. " +
                  dXmax.ToString("0.000") + " mm) - das Teil dreht also nicht um X");

            // A-Drehung um 90 Grad kippt Werkstueck-Z in Maschinen-Y
            Vec3 p3 = new Machine5Axis().Forward(new Vec3(0, 0, 10), 90, 0);
            // Rechte-Hand-Regel um +X: +Z wandert nach -Y
            Check(Math.Abs(p3.Y + 10.0) < 1e-9 && Math.Abs(p3.Z) < 1e-9,
                  "A = 90 Grad kippt Werkstueck-Z nach Maschinen -Y: " + p3);

            // Vorwaerts/Rueckwaerts muessen sich exakt aufheben
            Machine5Axis m3 = new Machine5Axis { TableAboveA = 17.5 };
            double worstInv = 0;
            for (int i = 0; i < 5000; i++)
            {
                Vec3 p = new Vec3(rnd.NextDouble() * 60 - 30, rnd.NextDouble() * 60 - 30, rnd.NextDouble() * 60);
                double aa = rnd.NextDouble() * 180 - 90, cc = rnd.NextDouble() * 720 - 360;
                worstInv = Math.Max(worstInv, (m3.Inverse(m3.Forward(p, aa, cc), aa, cc) - p).Length);
            }
            Check(worstInv < 1e-9, "Inverse(Forward(p)) = p (max. Fehler " + worstInv.ToString("0.0e+0") + ")");
        }

        // ---------------------------------------------------------------- 2. Strahlschnitt

        private static void TestRaycast(TriGrid grid)
        {
            Vec3 c = new Vec3(0, 0, 30);
            Random rnd = new Random(7);
            double worst = 0; int hits = 0;

            for (int i = 0; i < 4000; i++)
            {
                double th = rnd.NextDouble() * 140.0 * MathUtil.Deg;      // 0..140 Grad, dort ist nur die Kugel
                double ph = rnd.NextDouble() * 2 * Math.PI;
                Vec3 d = new Vec3(Math.Sin(th) * Math.Cos(ph), Math.Sin(th) * Math.Sin(ph), Math.Cos(th));
                double t; int tri;
                if (!grid.RayFirstHit(c + d * 200.0, -d, out t, out tri)) continue;
                Vec3 hit = c + d * 200.0 - d * t;
                worst = Math.Max(worst, Math.Abs((hit - c).Length - 10.0));
                hits++;
            }
            Check(hits > 3900, "Strahlschnitt trifft die Kugel in " + hits + " von 4000 Richtungen");
            Check(worst < 0.02, "Radius der Treffer weicht max. " + worst.ToString("0.0000") + " mm von 10 mm ab (Facettenfehler)");
        }

        // ---------------------------------------------------------------- 3. Bahn

        private static void TestPath(Toolpath tp, CamParameters cp)
        {
            Check(tp.PointCount > 1000, "Bahn enthaelt " + tp.PointCount + " Punkte");
            if (tp.PointCount == 0) return;

            Vec3 sphere = new Vec3(0, 0, 30);
            double R = cp.Tool.Radius;
            double worstCenter = 0, worstAxis = 0, worstTip = 0, maxDA = 0, maxDC = 0;
            double minTheta = 999, maxTheta = -999;
            bool aInRange = true;

            ClPoint prev = default(ClPoint);
            bool first = true;

            foreach (Pass pass in tp.Passes)
            {
                first = true;
                foreach (ClPoint p in pass.Points)
                {
                    // (a) Der Kugelmittelpunkt des Fraesers muss exakt R ueber der Kugelflaeche liegen.
                    //     Fuer die analytische Kugel heisst das: Abstand = 10 + R.
                    worstCenter = Math.Max(worstCenter, Math.Abs((p.Center - sphere).Length - (10.0 + R)));

                    // (b) Die Werkzeugachse muss im Maschinenraum senkrecht stehen.
                    Vec3 inMachine = cp.Machine.ForwardDir(p.Axis, p.A, p.C);
                    worstAxis = Math.Max(worstAxis, (inMachine - Vec3.UnitZ).Length);

                    // (c) Die ausgegebene Maschinenposition muss die Werkzeugspitze sein.
                    Vec3 tip = cp.Machine.Forward(p.Tip, p.A, p.C);
                    worstTip = Math.Max(worstTip, (tip - p.Machine).Length);

                    // (d) A muss in den Grenzen liegen, C darf keine Spruenge machen.
                    if (p.A < cp.Machine.AMinDeg - 1e-6 || p.A > cp.Machine.AMaxDeg + 1e-6) aInRange = false;
                    if (!first)
                    {
                        maxDA = Math.Max(maxDA, Math.Abs(p.A - prev.A));
                        maxDC = Math.Max(maxDC, Math.Abs(p.C - prev.C));
                    }
                    minTheta = Math.Min(minTheta, p.Theta);
                    maxTheta = Math.Max(maxTheta, p.Theta);
                    prev = p; first = false;
                }
            }

            Check(worstCenter < 0.02, "Fraesermitte liegt ueberall " + (10.0 + R).ToString("0.###") +
                                      " mm vom Kugelzentrum (max. Abweichung " + worstCenter.ToString("0.0000") + " mm)");
            Check(worstAxis < 1e-9, "Werkzeugachse steht in jeder Achsstellung senkrecht (max. " + worstAxis.ToString("0.0e+0") + ")");
            Check(worstTip < 1e-9, "Ausgegebene Maschinenposition ist die Werkzeugspitze (max. " + worstTip.ToString("0.0e+0") + ")");
            Check(aInRange, "A bleibt in " + cp.Machine.AMinDeg + " .. " + cp.Machine.AMaxDeg + " Grad");
            Check(maxDA < 2.0, "Groesster A-Sprung zwischen zwei Saetzen: " + maxDA.ToString("0.000") + " Grad");
            Check(maxDC < 15.0, "Groesster C-Sprung zwischen zwei Saetzen: " + maxDC.ToString("0.000") + " Grad");
            TestInterpolationError(tp, cp);
            Console.WriteLine("  Theta ueberdeckt " + minTheta.ToString("0.0") + " .. " + maxTheta.ToString("0.0") + " Grad");
            Check(maxTheta > 95.0, "Es wird auch unterhalb des Aequators gefraest (bis Theta " + maxTheta.ToString("0.0") + " Grad)");
        }

        private static void DumpPoints(Toolpath tp)
        {
            int i = 0;
            foreach (Pass pass in tp.Passes)
            {
                foreach (ClPoint p in pass.Points)
                {
                    if (i >= 180 && i <= 260)
                        Console.WriteLine(string.Format(CultureInfo.InvariantCulture,
                            "{0,5} th={1,7:0.000} P={2} |P-Z|={3:0.0000} n={4} u={5} axis={6} A={7,8:0.000} C={8,10:0.000}",
                            i, p.Theta, p.Contact, (p.Contact - new Vec3(0,0,30)).Length, p.Normal, p.Tangent, p.Axis, p.A, p.C));
                    i++;
                }
            }
        }

        /// <summary>Vergleicht die interpolierte Normale mit der analytischen Kugelnormale.</summary>
        private static void DiagNormals(Mesh mesh, TriGrid grid)
        {
            Vec3 c = new Vec3(0, 0, 30);
            foreach (double thDeg in new[] { 1.0, 3.0, 5.5, 12.0, 30.0, 60.0, 95.0 })
            {
                double worst = 0; int worstI = -1; Vec3 worstN = Vec3.Zero, worstD = Vec3.Zero;
                for (int i = 0; i < 360; i++)
                {
                    double th = thDeg * MathUtil.Deg, ph = i * MathUtil.Deg;
                    Vec3 d = new Vec3(Math.Sin(th) * Math.Cos(ph), Math.Sin(th) * Math.Sin(ph), Math.Cos(th));
                    double t; int tri;
                    if (!grid.RayFirstHit(c + d * 200, -d, out t, out tri)) continue;
                    Vec3 hit = c + d * 200 - d * t;
                    Vec3 n = mesh.NormalAt(tri, hit);
                    double err = Math.Acos(MathUtil.Clamp(Vec3.Dot(n, d), -1, 1)) * MathUtil.Rad;
                    if (err > worst) { worst = err; worstI = tri; worstN = n; worstD = d; }
                }
                if (worstI >= 0)
                {
                    Tri wt = mesh.Tris[worstI];
                    double bu, bv, bw;
                    Vec3 hp = c + worstD * (worstD.Length > 0 ? 0 : 0);
                    Console.WriteLine("     Dreieck " + worstI + " A=" + wt.A + " B=" + wt.B + " C=" + wt.C);
                    Console.WriteLine("     nA=" + mesh.VertexNormal(worstI, 0) + " nB=" + mesh.VertexNormal(worstI, 1) + " nC=" + mesh.VertexNormal(worstI, 2) + "  n=" + worstN);
                }
                Vec3 fn = worstI >= 0 ? mesh.Tris[worstI].N : Vec3.Zero;
                double ferr = worstI >= 0 ? Math.Acos(MathUtil.Clamp(Vec3.Dot(fn, worstD), -1, 1)) * MathUtil.Rad : 0;
                Console.WriteLine(string.Format(CultureInfo.InvariantCulture,
                    "  theta={0,6:0.0}  max Normalenfehler geglaettet {1,7:0.000} Grad | Facette an derselben Stelle {2,7:0.000} Grad",
                    thDeg, worst, ferr));
            }
        }

        /// <summary>
        /// Der entscheidende Genauigkeitstest: die Steuerung interpoliert zwischen zwei
        /// Saetzen alle fuenf Achsen linear. Hier wird die Satzmitte so nachgerechnet, wie
        /// die Maschine sie wirklich anfaehrt, und geprueft, wie weit der Fraeser dort von
        /// der Sollflaeche abweicht.
        /// </summary>
        private static void TestInterpolationError(Toolpath tp, CamParameters cp)
        {
            Vec3 sphere = new Vec3(0, 0, 30);
            double R = cp.Tool.Radius, soll = 10.0 + R;
            double worst = 0; int worstIdx = -1, idx = 0;
            var hist = new SortedDictionary<double, int>();

            foreach (Pass pass in tp.Passes)
            {
                for (int i = 1; i < pass.Count; i++)
                {
                    ClPoint p = pass.Points[i - 1], q = pass.Points[i];
                    for (int k = 1; k <= 3; k++)
                    {
                        double t = k / 4.0;
                        double a = p.A + (q.A - p.A) * t;
                        double c = p.C + (q.C - p.C) * t;
                        Vec3 m = Vec3.Lerp(p.Machine, q.Machine, t);

                        Vec3 tipW = cp.Machine.Inverse(m, a, c);
                        Vec3 axisW = Machine5Axis.ToolAxisFromAC(a, c);
                        Vec3 centerW = tipW + axisW * R;
                        double err = Math.Abs((centerW - sphere).Length - soll);
                        if (err > worst) { worst = err; worstIdx = idx; }
                    }
                    idx++;
                }
            }
            Console.WriteLine("  Groesste Flaechenabweichung bei linearer Satzinterpolation: " +
                              worst.ToString("0.0000") + " mm (Satz " + worstIdx + ")");
            Check(worst < 0.02, "Interpolationsfehler bleibt unter 0.02 mm");
        }

        // ---------------------------------------------------------------- 7. Auswahl

        private static void TestSelection(Mesh mesh)
        {
            // Das Beispielmodell bringt die Kugel bereits als Auswahl mit.
            Check(mesh.HasSelection, "Beispielmodell hat die Kugel vorgewaehlt (" + mesh.SelectedCount + " Dreiecke)");
            int sphereTris = mesh.SelectedCount;

            Vec3 c0; double r0, res0;
            Check(mesh.FitSphere(out c0, out r0, out res0), "Ausgleichskugel laesst sich bestimmen");
            Check((c0 - new Vec3(0, 0, 30)).Length < 0.01,
                  "Kugelmittelpunkt der Auswahl = " + c0 + " (Soll 0,0,30)");
            Check(Math.Abs(r0 - 10.0) < 0.01, "Kugelradius der Auswahl = " + r0.ToString("0.0000") + " mm (Soll 10)");
            Check(res0 < 0.01, "Mittlere Abweichung von der Ausgleichskugel " + res0.ToString("0.00000") + " mm");

            mesh.BuildTopology();
            Check(mesh.HasTopology, "Kantennachbarschaft aufgebaut");

            // Ein Dreieck der Kugel anklicken: das Flaechenwachstum muss genau die Kugel
            // finden und am Zylinder nicht weitermachen - die beiden haengen nicht zusammen.
            int sphereTri = mesh.Count - 1;
            int grown = mesh.SelectRegion(sphereTri, 35.0, Mesh.SelectMode.Ersetzen);
            Check(grown == sphereTris,
                  "Klick auf die Kugel waehlt genau " + grown + " Dreiecke (erwartet " + sphereTris + ")");

            bool cylinderClean = true;
            for (int i = 0; i < mesh.Count - sphereTris; i++) if (mesh.Selected[i]) cylinderClean = false;
            Check(cylinderClean, "Kein einziges Zylinderdreieck ist mitgewaehlt");

            // Ein Dreieck am Zylindermantel: das Wachstum muss an den 90-Grad-Kanten zu
            // Deckel und Boden aufhoeren.
            int mantle = mesh.SelectRegion(0, 35.0, Mesh.SelectMode.Ersetzen);
            Check(mantle == 128,
                  "Klick auf den Zylindermantel waehlt " + mantle + " Dreiecke - Deckel und Boden bleiben aussen (erwartet 128)");

            // Ein zu grosser Knickwinkel laesst die Auswahl ueber die Kante laufen
            int all = mesh.SelectRegion(0, 120.0, Mesh.SelectMode.Ersetzen);
            Check(all == 256, "Mit 120 Grad Knickwinkel wird der ganze Zylinder gewaehlt (" + all + " von 256)");

            // Auswahl wiederherstellen
            mesh.SelectRegion(sphereTri, 35.0, Mesh.SelectMode.Ersetzen);
            Check(mesh.SelectedCount == sphereTris, "Auswahl wiederhergestellt");
        }

        // ---------------------------------------------------------------- 8. Werkstueck-KS

        private static void TestWorkpiece()
        {
            Mesh raw = ModelGenerator.BallOnPost();

            // Drehung um X um 90 Grad: Werkstueck-Z wandert nach -Y
            Workpiece wp = new Workpiece { RotXDeg = 90 };
            Mesh rot = wp.Apply(raw);
            Check(Math.Abs(rot.Bounds.Min.Y + 40.0) < 1e-9 && Math.Abs(rot.Bounds.Max.Y) < 1e-9,
                  "90 Grad um X: aus Z 0..40 wird Y -40..0 (" + rot.Bounds.Min.Y.ToString("0.###") +
                  " .. " + rot.Bounds.Max.Y.ToString("0.###") + ")");
            Check(Math.Abs(rot.Bounds.Min.Z + 10.0) < 1e-9 && Math.Abs(rot.Bounds.Max.Z - 10.0) < 1e-9,
                  "90 Grad um X: aus Y -10..10 wird Z -10..10");

            // Nullpunkt auf den Kugelmittelpunkt legen
            wp.ZeroAt(new Vec3(0, 0, 30));
            Mesh moved = wp.Apply(raw);
            moved.CopySelectionFrom(raw);
            Vec3 c; double r, res;
            Check(moved.FitSphere(out c, out r, out res) && c.Length < 1e-6,
                  "Nach ZeroAt liegt der Kugelmittelpunkt im Ursprung: " + c);
            Check(moved.SelectedCount == raw.SelectedCount, "Flaechenauswahl hat die Transformation ueberlebt");

            // Vollstaendiger Durchlauf in einem schraeg gestellten Koordinatensystem:
            // die Bahn muss weiterhin exakt auf der Kugel liegen.
            Workpiece tilt = new Workpiece { RotXDeg = 11, RotYDeg = -7, RotZDeg = 23 };
            tilt.Offset = new Vec3(3, -4, 5);
            Mesh m2 = tilt.Apply(raw);
            m2.CopySelectionFrom(raw);
            m2.BuildSmoothNormals();
            m2.BuildTopology();
            TriGrid g2 = new TriGrid(m2);

            Vec3 c2; double r2, res2;
            m2.FitSphere(out c2, out r2, out res2);

            CamParameters cp2 = new CamParameters { Center = c2, ThetaEndDeg = 120 };
            Toolpath tp2 = ToolpathGenerator.Generate(m2, g2, cp2);
            Check(tp2.PointCount > 500, "Bahn im schraegen Koordinatensystem: " + tp2.PointCount + " Punkte");

            double worst = 0;
            double soll = r2 + cp2.Tool.Radius;
            foreach (Pass pass in tp2.Passes)
                foreach (ClPoint pt in pass.Points)
                    worst = Math.Max(worst, Math.Abs((pt.Center - c2).Length - soll));
            Check(worst < 0.02,
                  "Fraesermitte bleibt auch dort " + soll.ToString("0.###") + " mm vom Kugelzentrum (max. " +
                  worst.ToString("0.0000") + " mm)");
        }

        // ------------------------------------------------ 9. Parallelbahnen / Z-Zustellung

        private static void TestRaster(string outDir)
        {
            Mesh m = ModelGenerator.WavyBlock();
            m.BuildSmoothNormals();
            m.BuildTopology();
            TriGrid g = new TriGrid(m);

            Console.WriteLine("  Modell: " + m.Count + " Dreiecke, Huelle " + m.Bounds.Min + " .. " + m.Bounds.Max);
            Check(m.HasSelection, "Wellenflaeche ist vorgewaehlt (" + m.SelectedCount + " von " + m.Count + " Dreiecken)");

            // Klick auf die Wellenflaeche muss genau sie treffen und nicht ueber die Kante laufen
            int topTri = m.Count - 1;
            int grown = m.SelectRegion(topTri, 35.0, Mesh.SelectMode.Ersetzen);
            Check(grown == m.SelectedCount && grown > m.Count / 2,
                  "Klick auf die Wellenflaeche waehlt " + grown + " Dreiecke - Seiten und Boden bleiben aussen");

            Aabb sel; double area; Vec3 cen;
            m.SelectionInfo(out sel, out area, out cen);
            double material = sel.Max.Z - sel.Min.Z;
            Console.WriteLine("  Gewaehlte Flaeche: Z " + sel.Min.Z.ToString("0.###") + " .. " +
                              sel.Max.Z.ToString("0.###") + " = " + material.ToString("0.###") + " mm Hoehenunterschied");

            CamParameters cp = new CamParameters
            {
                Strategy = Strategy.ParallelBahnen,
                MaxZStep = 3.0,
                ScallopHeight = 0.02,
                CheckCollision = false          // hier interessiert die Bahngeometrie
            };

            DateTime t0 = DateTime.Now;
            Toolpath tp = ToolpathGenerator.Generate(m, g, cp);
            double secs = (DateTime.Now - t0).TotalSeconds;
            foreach (string line in tp.Log) Console.WriteLine("  " + line);
            Console.WriteLine("  Rechenzeit " + secs.ToString("0.00") + " s");

            Check(tp.PointCount > 2000, "Bahn enthaelt " + tp.PointCount + " Punkte");

            // --- Z-Zustelltiefe: keine Ebene darf tiefer zustellen als erlaubt
            Check(tp.ZLevels.Count >= 2, tp.ZLevels.Count + " Z-Ebenen (Schruppebenen plus Schlichtbahn)");
            double worstStep = 0;
            for (int i = 1; i < tp.ZLevels.Count; i++)
                worstStep = Math.Max(worstStep, tp.ZLevels[i - 1] - tp.ZLevels[i]);
            Check(worstStep <= cp.MaxZStep + 1e-9,
                  "Groesste Z-Zustellung " + worstStep.ToString("0.0000") + " mm (Grenze " +
                  cp.MaxZStep.ToString("0.###") + " mm)");
            Check(Math.Abs(tp.ZLevels[0] - (sel.Max.Z - worstStep)) < 1e-6,
                  "Ohne Rohmaterial: erste Schruppebene eine Zustellung unter dem hoechsten " +
                  "Flaechenpunkt (die Oberkante selbst waere ein Leerschnitt)");
            Check(Math.Abs(tp.ZLevels[tp.ZLevels.Count - 1] - sel.Min.Z) < 1e-6,
                  "Die Schlichtbahn geht bis zum tiefsten Flaechenpunkt");

            // --- Kein Eingriff unter die Sollflaeche: der Fraeser darf sie nur beruehren
            var cand = new List<int>();
            var seen = new HashSet<int>();
            double R = cp.Tool.Radius;
            double worstGouge = 0, closestFinish = double.MaxValue;
            int checkedPts = 0;
            int step = Math.Max(1, tp.PointCount / 4000);

            int idx = 0;
            Pass last = tp.Passes[tp.Passes.Count - 1];
            foreach (Pass pass in tp.Passes)
                foreach (ClPoint pt in pass.Points)
                {
                    if (idx++ % step != 0) continue;
                    checkedPts++;
                    Vec3 e = new Vec3(R + 1, R + 1, R + 1);
                    g.QueryBox(pt.Center - e, pt.Center + e, cand, seen);
                    double best = double.MaxValue;
                    for (int i = 0; i < cand.Count; i++)
                        if (m.IsSelected(cand[i]))
                            best = Math.Min(best, TriMath.PointTriangleDistance(pt.Center, m.Tris[cand[i]]));
                    if (best == double.MaxValue) continue;
                    worstGouge = Math.Max(worstGouge, R - best);
                    if (ReferenceEquals(pass, last)) closestFinish = Math.Min(closestFinish, Math.Abs(best - R));
                }

            Check(worstGouge < 0.02,
                  checkedPts + " Punkte geprueft: tiefster Eingriff unter die Sollflaeche " +
                  worstGouge.ToString("0.0000") + " mm");
            Check(closestFinish < 0.05,
                  "Die Schlichtbahn liegt wirklich auf der Flaeche (Abweichung " +
                  closestFinish.ToString("0.0000") + " mm von R)");

            // --- Werkzeugachse und Achsstellung
            double maxA = 0, maxNormalTilt = 0;
            foreach (Pass pass in tp.Passes)
                foreach (ClPoint pt in pass.Points)
                {
                    maxA = Math.Max(maxA, Math.Abs(pt.A));
                    maxNormalTilt = Math.Max(maxNormalTilt,
                        Math.Acos(MathUtil.Clamp(pt.Normal.Z, -1, 1)) * MathUtil.Rad);
                }
            Check(maxA <= 90.0 + 1e-9, "A bleibt in den Grenzen (groesster Wert " + maxA.ToString("0.000") + " Grad)");
            Console.WriteLine("  Steilste Flaechenstelle " + maxNormalTilt.ToString("0.0") +
                              " Grad, groesste A-Stellung " + maxA.ToString("0.0") + " Grad");

            // --- Werkzeugachse senkrecht: A und C duerfen sich nicht bewegen
            cp.AxisMode = ToolAxisMode.Senkrecht;
            Toolpath tv = ToolpathGenerator.Generate(m, g, cp);
            double maxAv = 0, spanC = 0, minC = double.MaxValue, maxC = double.MinValue;
            foreach (Pass pass in tv.Passes)
                foreach (ClPoint pt in pass.Points)
                {
                    maxAv = Math.Max(maxAv, Math.Abs(pt.A));
                    minC = Math.Min(minC, pt.C); maxC = Math.Max(maxC, pt.C);
                }
            spanC = maxC - minC;
            Check(maxAv < 1e-9 && spanC < 1e-9,
                  "Werkzeugachse senkrecht: A bleibt " + maxAv.ToString("0.###") +
                  " Grad, C bewegt sich " + spanC.ToString("0.###") + " Grad");
            Check(tv.PointCount > 1000, "Auch senkrecht entsteht eine vollstaendige Bahn (" + tv.PointCount + " Punkte)");
            cp.AxisMode = ToolAxisMode.Flaechennormale;

            string nc = new PostProcessor(cp).Build(tp, m.Name);
            File.WriteAllText(Path.Combine(outDir, "freiform_5achs.nc"), nc);
            StlIo.SaveBinary(m, Path.Combine(outDir, "freiform_block.stl"));
            Console.WriteLine("  GCode geschrieben: freiform_5achs.nc");
        }

        // ------------------------------------------------------- 10. Z-Fenster

        private static void TestZWindow()
        {
            // --- Kugel: nur die obere Halbkugel bearbeiten -------------------------------
            Mesh k = ModelGenerator.BallOnPost();
            k.BuildSmoothNormals();
            k.BuildTopology();
            TriGrid gk = new TriGrid(k);

            CamParameters frei = new CamParameters();          // ohne Begrenzung
            Toolpath tFrei = ToolpathGenerator.Generate(k, gk, frei);
            Console.WriteLine("  Ohne Begrenzung: Beruehrpunkte Z " +
                              tFrei.MinContactZ.ToString("0.###") + " .. " + tFrei.MaxContactZ.ToString("0.###"));
            Check(tFrei.MinContactZ < 25.0,
                  "Ohne Begrenzung wird bis unter den Aequator gefraest (Z bis " +
                  tFrei.MinContactZ.ToString("0.###") + " mm)");

            CamParameters halb = new CamParameters
            {
                UseZMin = true,
                ZMin = 30.0                                    // Kugelmitte
            };
            Toolpath tHalb = ToolpathGenerator.Generate(k, gk, halb);
            foreach (string line in tHalb.Log) Console.WriteLine("  " + line);

            double lowest = double.MaxValue, highest = double.MinValue, maxTheta = 0;
            double lowestBall = double.MaxValue;
            foreach (Pass pass in tHalb.Passes)
                foreach (ClPoint pt in pass.Points)
                {
                    lowest = Math.Min(lowest, pt.Contact.Z);
                    highest = Math.Max(highest, pt.Contact.Z);
                    maxTheta = Math.Max(maxTheta, pt.Theta);
                    lowestBall = Math.Min(lowestBall, pt.Center.Z - halb.Tool.Radius);
                }

            Check(tHalb.PointCount > 500, "Obere Halbkugel: " + tHalb.PointCount + " Bahnpunkte");
            Check(lowest >= 30.0 - 1e-6,
                  "Kein Beruehrpunkt unter Zmin = 30 mm (tiefster " + lowest.ToString("0.####") + " mm)");
            Check(highest > 39.5,
                  "Nach oben wird nicht begrenzt - bearbeitet bis zum Scheitel (hoechster " +
                  "Beruehrpunkt " + highest.ToString("0.###") + " mm)");
            Check(maxTheta <= 90.5,
                  "Die Bahn endet am Aequator (groesstes Theta " + maxTheta.ToString("0.0") + " Grad)");
            Check(tHalb.PointCount < tFrei.PointCount,
                  "Die begrenzte Bahn ist kuerzer als die freie (" + tHalb.PointCount +
                  " statt " + tFrei.PointCount + " Punkte)");

            // Das Werkzeug DARF unter Zmin haengen - sonst waere der Aequator nicht schlichtbar
            Console.WriteLine("  Tiefster Punkt der Fraeserkugel: " + lowestBall.ToString("0.###") +
                              " mm - liegt erlaubterweise unter Zmin, sonst waere der Aequator " +
                              "nicht zu schlichten");
            Check(lowestBall < 30.0,
                  "Die Fraeserkugel haengt am Aequator unter Zmin, der Beruehrpunkt bleibt aber darueber");

            // --- Freiform: Zmax ueber dem Modell heisst Rohmaterial ----------------------
            Mesh w = ModelGenerator.WavyBlock();
            w.BuildSmoothNormals();
            w.BuildTopology();
            TriGrid gw = new TriGrid(w);

            Aabb sel; double area; Vec3 cen;
            w.SelectionInfo(out sel, out area, out cen);

            CamParameters autoW = new CamParameters
            {
                Strategy = Strategy.ParallelBahnen, MaxZStep = 3.0,
                ScallopHeight = 0.05, CheckCollision = false
            };
            Toolpath tAuto = ToolpathGenerator.Generate(w, gw, autoW);

            CamParameters rohW = new CamParameters
            {
                Strategy = Strategy.ParallelBahnen, MaxZStep = 3.0,
                ScallopHeight = 0.05, CheckCollision = false,
                AutoStockTop = false,
                StockTop = sel.Max.Z + 6.0                     // 6 mm Rohmaterial obendrauf
            };
            Toolpath tRoh = ToolpathGenerator.Generate(w, gw, rohW);
            foreach (string line in tRoh.Log) Console.WriteLine("  " + line);

            Check(Math.Abs(tRoh.ZLevels[0] - (sel.Max.Z + 6.0)) < 1e-9,
                  "Rohteil-Oberkante ueber dem Modell: erste Ebene liegt bei " +
                  tRoh.ZLevels[0].ToString("0.###") + " mm, also im Rohmaterial");
            Check(tRoh.ZLevels.Count > tAuto.ZLevels.Count,
                  "Dadurch entstehen mehr Ebenen: " + tRoh.ZLevels.Count + " statt " + tAuto.ZLevels.Count);
            Check(tRoh.PointCount > tAuto.PointCount,
                  "Und mehr Bahnpunkte: " + tRoh.PointCount + " statt " + tAuto.PointCount);

            // Die Schlichtbahn darf bei manuellen Grenzen nicht verlorengehen
            Pass lastAuto = tAuto.Passes[tAuto.Passes.Count - 1];
            Pass lastRoh = tRoh.Passes[tRoh.Passes.Count - 1];
            int flatAuto = 0, flatRoh = 0;
            foreach (ClPoint q in lastAuto.Points) if (q.OnPlane) flatAuto++;
            foreach (ClPoint q in lastRoh.Points) if (q.OnPlane) flatRoh++;
            Check(flatAuto == 0 && flatRoh == 0,
                  "Die letzte Bahn ist in beiden Faellen die freie Schlichtbahn auf der Flaeche, " +
                  "keine Ebene (" + flatAuto + " / " + flatRoh + " Ebenenpunkte)");
            Check(lastRoh.Count > 500,
                  "Auch mit Zmax im Rohmaterial bleibt die Schlichtbahn vollstaendig (" +
                  lastRoh.Count + " Punkte)");
        }

        /// <summary>
        /// Die Satzliste, aus der die Animation laeuft. Sie entsteht beim Schreiben des
        /// GCodes - also aus derselben Quelle - und muss deshalb Satz fuer Satz dazu
        /// passen, einschliesslich der Rueckzuege am Anfang und am Ende.
        /// </summary>
        private static void TestMoveList(Toolpath tp, CamParameters cp)
        {
            Check(tp.Moves.Count > tp.PointCount,
                  "Satzliste enthaelt auch die Verbindungen: " + tp.Moves.Count +
                  " Saetze zu " + tp.PointCount + " Schnittpunkten");

            int feed = 0, rapid = 0, retract = 0;
            foreach (ClPoint m in tp.Moves)
            {
                if (m.Type == MoveType.Feed) feed++;
                else if (m.Type == MoveType.Rapid) rapid++;
                else retract++;
            }
            Check(feed == tp.PointCount,
                  "Jeder Schnittpunkt steht genau einmal in der Liste (" + feed + ")");
            Check(rapid > 0, rapid + " Eilgaenge und " + retract + " Rueckzuege sind mit drin");

            ClPoint last = tp.Moves[tp.Moves.Count - 1];
            ClPoint beforeLast = tp.Moves[tp.Moves.Count - 2];
            // C ist endlos: am Ende zaehlt die Stellung, nicht der Zahlenwert. Angefahren
            // wird das naechste Vielfache von 360 Grad - gleiche Ausrichtung wie C0, aber
            // ohne das Teil dutzende Male um die eigene Achse zu kurbeln.
            double turns = last.C / 360.0;
            double weg = Math.Abs(last.C - beforeLast.C);
            Check(last.Type == MoveType.Rapid && Math.Abs(last.A) < 1e-9,
                  "Der letzte Satz stellt A auf 0 zurueck");
            Check(Math.Abs(turns - Math.Round(turns)) < 1e-9,
                  "C endet auf " + last.C.ToString("0.###") + " Grad = " +
                  Math.Round(turns).ToString("0") + " volle Umdrehungen - dieselbe Stellung wie C0");
            Check(weg <= 180.0 + 1e-6,
                  "Dafuer dreht C nur " + weg.ToString("0.#") + " Grad statt " +
                  Math.Abs(beforeLast.C).ToString("0.#") + " Grad");
            Check(beforeLast.Type == MoveType.Retract && beforeLast.ZUnknown,
                  "Davor steht der G53-Rueckzug - ohne Maschinen-Z0 ist seine Hoehe im " +
                  "Werkstuecksystem unbekannt");
            Check(tp.Moves[tp.Moves.Count - 3].Type == MoveType.Rapid,
                  "Und davor der Rueckzug auf die Rueckzugsebene");

            // Mit bekanntem Maschinen-Z0 muss die Hoehe stimmen
            CamParameters cz = new CamParameters { MachineZeroAboveWork = 400.0 };
            Toolpath t2 = ToolpathGenerator.Generate(
                ModelGeneratorHelper(), GridHelper(), cz);
            new PostProcessor(cz).Build(t2, "test");
            ClPoint l2 = t2.Moves[t2.Moves.Count - 2];
            Check(!l2.ZUnknown && Math.Abs(l2.Machine.Z - (cz.G53RetractZ + 400.0)) < 1e-9,
                  "Mit Maschinen-Z0 = 400 liegt der Rueckzug auf Z " +
                  l2.Machine.Z.ToString("0.###") + " im Werkstuecksystem (G53 Z-1)");

            Console.WriteLine("  " + ToolpathGenerator.StepoverNote(cp));
        }

        private static Mesh _helperMesh;
        private static TriGrid _helperGrid;

        private static Mesh ModelGeneratorHelper()
        {
            if (_helperMesh == null)
            {
                _helperMesh = ModelGenerator.BallOnPost();
                _helperMesh.BuildSmoothNormals();
                _helperGrid = new TriGrid(_helperMesh);
            }
            return _helperMesh;
        }

        private static TriGrid GridHelper() { ModelGeneratorHelper(); return _helperGrid; }

        // ---------------------------------------------------------------- 4. GCode

        private static void TestGCode(string[] lines, CamParameters cp)
        {
            int g1 = 0, withA = 0, withC = 0, simultan = 0;
            double minC = double.MaxValue, maxC = double.MinValue;

            foreach (string raw in lines)
            {
                string l = raw.Trim();
                if (l.Length == 0 || l.StartsWith("(") || l == "%") continue;
                if (!l.Contains("G1") && !(g1 > 0 && (l.Contains("X") || l.Contains("A")))) { /* modal */ }

                bool hasX = HasWord(l, 'X'), hasY = HasWord(l, 'Y'), hasZ = HasWord(l, 'Z');
                bool hasA = HasWord(l, 'A'), hasC = HasWord(l, 'C');
                if (l.Contains("G0 ")) continue;

                if (hasA) withA++;
                if (hasC) { withC++; double v = Word(l, 'C'); minC = Math.Min(minC, v); maxC = Math.Max(maxC, v); }
                if ((hasX || hasY || hasZ) && hasA && hasC) simultan++;
                if (hasX || hasY || hasZ || hasA || hasC) g1++;
            }

            Check(g1 > 1000, g1 + " Bewegungssaetze im Programm");
            Check(simultan > 1000, simultan + " Saetze bewegen Linear- UND beide Drehachsen gleichzeitig (echte Simultanbahn)");
            Check(maxC - minC > 360.0, "C laeuft ueber " + ((maxC - minC) / 360.0).ToString("0.0") + " Umdrehungen durch (endlose Achse genutzt)");
            Check(lines[0] == "%" && Array.IndexOf(lines, "%") == 0, "Programm beginnt mit %");

            bool m30 = false, g21 = false;
            foreach (string l in lines)
            {
                if (l.Contains("M30")) m30 = true;
                if (l.Contains("G21")) g21 = true;
            }
            Check(g21, "G21 (metrisch) im Kopf");
            Check(m30, "M30 am Programmende");

            // --- G53-Rueckzug am Anfang und am Ende
            int g53Count = 0, firstG53 = -1, lastG53 = -1, toolChange = -1, m5 = -1, m30Line = -1;
            for (int i = 0; i < lines.Length; i++)
            {
                if (lines[i].Contains("G53")) { g53Count++; if (firstG53 < 0) firstG53 = i; lastG53 = i; }
                if (lines[i].Contains("M6") && toolChange < 0) toolChange = i;
                if (lines[i].Contains("M5") && !lines[i].Contains("M50")) m5 = i;
                if (lines[i].Contains("M30")) m30Line = i;
            }
            Check(g53Count == 2, "Genau zwei G53-Saetze im Programm (" + g53Count + ")");
            Check(firstG53 >= 0 && lines[firstG53].Contains("G53 G0 Z-1.000"),
                  "Der erste lautet: " + (firstG53 >= 0 ? lines[firstG53].Trim() : "fehlt"));
            Check(firstG53 >= 0 && toolChange > firstG53,
                  "Er steht vor dem Werkzeugwechsel - erst hochfahren, dann wechseln");
            Check(lastG53 > m5 && m30Line > lastG53,
                  "Der zweite steht nach M5 und vor M30 - Ende immer an derselben Stelle");

            // Nach G53 ist die Z-Lage im Werkstuecksystem unbekannt: der naechste
            // Bewegungssatz muss Z wieder ausdruecklich schreiben.
            int nextMove = -1;
            for (int i = firstG53 + 1; i < lines.Length && nextMove < 0; i++)
                if (HasWord(lines[i], 'X') || HasWord(lines[i], 'Y') || HasWord(lines[i], 'Z')) nextMove = i;
            Check(nextMove > 0 && HasWord(lines[nextMove], 'Z'),
                  "Der erste Bewegungssatz nach G53 schreibt Z ausdruecklich: " +
                  (nextMove > 0 ? lines[nextMove].Trim() : "fehlt"));
        }

        private static bool HasWord(string line, char w)
        {
            int i = line.IndexOf(w);
            return i >= 0 && i + 1 < line.Length && (char.IsDigit(line[i + 1]) || line[i + 1] == '-' || line[i + 1] == '.');
        }

        private static double Word(string line, char w)
        {
            int i = line.IndexOf(w);
            if (i < 0) return 0;
            int j = i + 1;
            while (j < line.Length && (char.IsDigit(line[j]) || line[j] == '-' || line[j] == '.' || line[j] == '+')) j++;
            double v;
            double.TryParse(line.Substring(i + 1, j - i - 1), NumberStyles.Float, CultureInfo.InvariantCulture, out v);
            return v;
        }
    }
}
