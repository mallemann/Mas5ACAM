using System;
using System.Collections.Generic;
using System.Globalization;

namespace Mas5ACAM
{
    /// <summary>
    /// Erzeugt aus einem Dreiecksnetz eine simultane 5-Achs-Bahn für einen Kugelfräser.
    ///
    /// Strategie: Kugelkoordinaten-Projektion. Aus einem Zentrumspunkt heraus wird die
    /// Fläche in Richtung (theta, phi) abgetastet. Weil beim Kugelfräser der
    /// Kugelmittelpunkt immer auf der um R versetzten Fläche liegt
    /// (Mittelpunkt = Berührpunkt + R * Normale), ist die Bahn unabhängig von der
    /// Werkzeugneigung schnittgenau; die Neigung bestimmt nur Erreichbarkeit und Freigang.
    /// </summary>
    public static partial class ToolpathGenerator
    {
        internal struct Sample
        {
            public bool Valid;
            public Vec3 P;          // Berührpunkt (bereits um das Aufmass versetzt)
            public Vec3 N;          // Flächennormale
            public double R;        // Abstand vom Projektionszentrum
            public double Theta;    // Grad
            public bool OnPlane;    // Werkzeug sitzt auf einer Z-Ebene, nicht auf der Flaeche
        }

        public static Toolpath Generate(Mesh mesh, TriGrid grid, CamParameters cp)
        {
            Toolpath tp = new Toolpath();
            if (mesh == null || mesh.Count == 0)
            {
                tp.Log.Add("Kein Modell geladen.");
                return tp;
            }

            double rMax = ProbeRadius(mesh, cp.Center);
            tp.Log.Add(string.Format(CultureInfo.InvariantCulture,
                "Modell: {0} Dreiecke, Huelle {1} .. {2}", mesh.Count, mesh.Bounds.Min, mesh.Bounds.Max));
            tp.Log.Add(string.Format(CultureInfo.InvariantCulture,
                "Projektionszentrum {0}, Suchradius {1:0.0} mm", cp.Center, rMax));
            tp.Log.Add(mesh.HasSelection
                ? string.Format(CultureInfo.InvariantCulture,
                    "Bearbeitete Flaeche: {0} von {1} Dreiecken ausgewaehlt", mesh.SelectedCount, mesh.Count)
                : "Bearbeitete Flaeche: keine Auswahl - das ganze Modell gilt als bearbeitbar");

            List<List<Sample>> raw;
            switch (cp.Strategy)
            {
                case Strategy.ParallelBahnen: raw = SampleRaster(grid, cp, tp); break;
                case Strategy.Breitenkreise:  raw = SampleRings(grid, cp, rMax, tp); break;
                default:                      raw = SampleSpiral(grid, cp, rMax, tp); break;
            }

            BuildPasses(raw, grid, cp, tp);
            ComputeFeeds(cp, tp);
            Summarize(cp, tp);
            return tp;
        }

        // ------------------------------------------------------------------ Abtastung

        /// <summary>Suchradius: so gross, dass der Strahl garantiert ausserhalb des Modells startet.</summary>
        private static double ProbeRadius(Mesh mesh, Vec3 c)
        {
            double r = 0;
            foreach (Vec3 p in Corners(mesh.Bounds)) r = Math.Max(r, (p - c).Length);
            return r * 1.05 + 1.0;
        }

        private static Vec3[] Corners(Aabb b)
        {
            Vec3 lo = b.Min, hi = b.Max;
            return new[]
            {
                new Vec3(lo.X, lo.Y, lo.Z), new Vec3(hi.X, lo.Y, lo.Z),
                new Vec3(lo.X, hi.Y, lo.Z), new Vec3(hi.X, hi.Y, lo.Z),
                new Vec3(lo.X, lo.Y, hi.Z), new Vec3(hi.X, lo.Y, hi.Z),
                new Vec3(lo.X, hi.Y, hi.Z), new Vec3(hi.X, hi.Y, hi.Z)
            };
        }

        /// <summary>Ein Strahl von aussen nach innen auf das Zentrum zu; der erste Treffer
        /// ist der Punkt der Aussenhaut in dieser Richtung.</summary>
        private static Sample Probe(TriGrid grid, CamParameters cp, double rMax, double thetaRad, double phiRad)
        {
            Sample s = new Sample();
            s.Theta = thetaRad * MathUtil.Rad;

            Vec3 d = new Vec3(Math.Sin(thetaRad) * Math.Cos(phiRad),
                              Math.Sin(thetaRad) * Math.Sin(phiRad),
                              Math.Cos(thetaRad));
            Vec3 orig = cp.Center + d * rMax;

            double t; int tri;
            if (!grid.RayFirstHit(orig, -d, out t, out tri)) return s;

            // Nur die gewaehlte Flaeche wird bearbeitet. Trifft der Strahl zuerst etwas
            // anderes, ist die Flaeche dort verdeckt - dann gibt es hier keinen Punkt.
            if (!grid.Mesh.IsSelected(tri)) return s;

            Vec3 hit = orig - d * t;
            double r = (hit - cp.Center).Length;
            if (cp.UseRadiusBand && Math.Abs(r - cp.BandRadius) > cp.BandTolerance) return s;

            Vec3 n = grid.Mesh.NormalAt(tri, hit);
            if (Vec3.Dot(n, d) < 0) n = -n;                    // Normale nach aussen orientieren

            Vec3 contact = hit + n * cp.Stock;
            if (!AboveZMin(cp, contact.Z)) return s;            // unterhalb der Bearbeitungsgrenze

            s.Valid = true;
            s.R = r;
            s.N = n;
            s.P = contact;
            return s;
        }

        /// <summary>
        /// Liegt dieser Berührpunkt über der unteren Bearbeitungsgrenze?
        ///
        /// Geprüft wird der <b>Berührpunkt</b>, nicht das Werkzeug: Zmin begrenzt die
        /// bearbeitete Fläche, nicht die Werkzeuglage. Dass die Fräserkugel an einer Flanke
        /// unter Zmin hängt, ist zulässig und unvermeidbar.
        /// </summary>
        internal static bool AboveZMin(CamParameters cp, double contactZ)
        {
            return !cp.UseZMin || contactZ >= cp.ZMin - 1e-6;
        }

        /// <summary>Zustellung aus der gewünschten Restmaterialhöhe (konvexe Fläche).</summary>
        private static double Stepover(CamParameters cp, double surfaceRadius)
        {
            if (cp.UseFixedStepover) return MathUtil.Clamp(cp.FixedStepover, 0.005, 2.0 * cp.Tool.Radius);

            double R = cp.Tool.Radius;
            double r = Math.Max(surfaceRadius, 1e-6);
            double reff = R * r / (R + r);                      // Fräser und Fläche beide konvex
            double s = Math.Sqrt(8.0 * reff * Math.Max(cp.ScallopHeight, 1e-5));
            return MathUtil.Clamp(s, 0.005, 2.0 * R);
        }

        /// <summary>Winkelschritt entlang der Bahn aus der Sehnentoleranz.</summary>
        private static double PhiStep(CamParameters cp, double rho)
        {
            double maxStep = cp.MaxAngStepDeg * MathUtil.Deg;
            double minStep = cp.MinAngStepDeg * MathUtil.Deg;
            if (rho < 1e-6) return maxStep;
            double c = 1.0 - cp.ChordTolerance / rho;
            if (c <= -1.0) return maxStep;
            return MathUtil.Clamp(2.0 * Math.Acos(MathUtil.Clamp(c, -1.0, 1.0)), minStep, maxStep);
        }

        private static List<List<Sample>> SampleSpiral(TriGrid grid, CamParameters cp, double rMax, Toolpath tp)
        {
            var all = new List<List<Sample>>();
            var cur = new List<Sample>();

            double th0 = cp.ThetaStartDeg * MathUtil.Deg;
            double th1 = cp.ThetaEndDeg * MathUtil.Deg;
            double dir = cp.ClockwiseC ? -1.0 : 1.0;

            double theta = th0, phi = 0.0, lastR = Math.Max(cp.BandRadius, 0.001);
            int guard = 4000000;

            while (theta <= th1 + 1e-12 && guard-- > 0)
            {
                Sample s = Probe(grid, cp, rMax, theta, phi);
                if (s.Valid) { lastR = s.R; cur.Add(s); }
                else
                {
                    tp.MissedRays++;
                    if (cur.Count > 1) { all.Add(cur); cur = new List<Sample>(); }
                    else cur.Clear();
                }

                double r = lastR;
                double dPhi = PhiStep(cp, Math.Max(r * Math.Sin(theta), 1e-6));
                double dThetaPerRev = Stepover(cp, r) / r;
                phi += dir * dPhi;
                theta += dThetaPerRev * dPhi / (2.0 * Math.PI);
            }
            if (cur.Count > 1) all.Add(cur);
            return all;
        }

        private static List<List<Sample>> SampleRings(TriGrid grid, CamParameters cp, double rMax, Toolpath tp)
        {
            var all = new List<List<Sample>>();
            double th0 = cp.ThetaStartDeg * MathUtil.Deg;
            double th1 = cp.ThetaEndDeg * MathUtil.Deg;
            double dir = cp.ClockwiseC ? -1.0 : 1.0;

            double theta = Math.Max(th0, 0.5 * MathUtil.Deg);
            double lastR = Math.Max(cp.BandRadius, 0.001);
            int guard = 100000;

            while (theta <= th1 + 1e-12 && guard-- > 0)
            {
                var cur = new List<Sample>();
                double phi = 0.0, turned = 0.0;
                while (turned <= 2.0 * Math.PI + 1e-9)
                {
                    Sample s = Probe(grid, cp, rMax, theta, phi);
                    if (s.Valid) { lastR = s.R; cur.Add(s); }
                    else
                    {
                        tp.MissedRays++;
                        if (cur.Count > 1) { all.Add(cur); cur = new List<Sample>(); }
                        else cur.Clear();
                    }
                    double dPhi = PhiStep(cp, Math.Max(lastR * Math.Sin(theta), 1e-6));
                    phi += dir * dPhi;
                    turned += dPhi;
                }
                if (cur.Count > 1) all.Add(cur);
                theta += Stepover(cp, lastR) / lastR;
            }
            return all;
        }

        // ------------------------------------------------------- Werkzeuglage und Kinematik

        private static void BuildPasses(List<List<Sample>> raw, TriGrid grid, CamParameters cp, Toolpath tp)
        {
            double R = cp.Tool.Radius;
            double lead = cp.LeadAngleDeg * MathUtil.Deg;
            double tilt = cp.TiltAngleDeg * MathUtil.Deg;
            ToolCollision checker = new ToolCollision(grid, cp);

            double prevA = 0.0, prevC = 0.0;
            bool first = true;

            foreach (List<Sample> seq in raw)
            {
                Pass pass = new Pass();
                for (int i = 0; i < seq.Count; i++)
                {
                    Sample s = seq[i];

                    // Bahnrichtung aus den Nachbarpunkten, auf die Tangentialebene projiziert
                    Vec3 fwd = seq[Math.Min(i + 1, seq.Count - 1)].P - seq[Math.Max(i - 1, 0)].P;
                    Vec3 u = (fwd - s.N * Vec3.Dot(s.N, fwd)).Normalized;
                    if (u.LengthSq < 0.5) u = MathUtil.AnyPerpendicular(s.N);

                    Vec3 axis;
                    if (cp.AxisMode == ToolAxisMode.Senkrecht)
                    {
                        axis = Vec3.UnitZ;                 // A und C bleiben stehen
                    }
                    else
                    {
                        axis = (s.N * Math.Cos(lead) + u * Math.Sin(lead)).Normalized;
                        if (Math.Abs(tilt) > 1e-9) axis = Vec3.Rotate(axis, u, tilt).Normalized;
                    }

                    ClPoint p = new ClPoint();
                    p.OnPlane = s.OnPlane;
                    p.Contact = s.P;
                    p.Normal = s.N;
                    p.Center = s.P + s.N * R;
                    p.Theta = s.Theta;
                    p.Tangent = u;
                    p.Type = MoveType.Feed;

                    double a, c;
                    bool reachable = cp.Machine.ChooseAC(axis, first ? 0.0 : prevA, first ? 0.0 : prevC, out a, out c);
                    if (!reachable)
                    {
                        // A-Grenze erreicht: Werkzeugachse auf die zulässige Stellung abknicken.
                        // Der Kugelmittelpunkt bleibt unverändert, deshalb bleibt der Berührpunkt exakt.
                        axis = Machine5Axis.ToolAxisFromAC(a, c);
                        p.AxisClamped = true;
                        tp.ClampedCount++;
                    }

                    p.Axis = axis;
                    p.Tip = p.Center - axis * R;
                    p.A = a; p.C = c;
                    p.Machine = cp.Machine.Forward(p.Tip, a, c);
                    if (p.Contact.Z < tp.MinContactZ) tp.MinContactZ = p.Contact.Z;
                    if (p.Contact.Z > tp.MaxContactZ) tp.MaxContactZ = p.Contact.Z;

                    if (cp.CheckCollision && checker.Collides(p.Center, axis))
                    {
                        tp.CollisionSkipped++;
                        if (pass.Count > 1) tp.Passes.Add(pass);
                        pass = new Pass();
                        continue;
                    }

                    prevA = a; prevC = c; first = false;
                    tp.Track(p);
                    pass.Points.Add(p);
                }
                if (pass.Count > 1) tp.Passes.Add(pass);
            }
        }

        // ---------------------------------------------------------------------- Vorschub

        private static void ComputeFeeds(CamParameters cp, Toolpath tp)
        {
            double total = 0, minutes = 0;
            int limited = 0, rotary = 0;
            double maxAdps = 0, maxCdps = 0;

            foreach (Pass pass in tp.Passes)
            {
                for (int i = 0; i < pass.Points.Count; i++)
                {
                    ClPoint p = pass.Points[i];
                    if (i == 0) { p.Feed = cp.PlungeFeed; pass.Points[i] = p; continue; }

                    ClPoint q = pass.Points[i - 1];
                    double dsPart = (p.Contact - q.Contact).Length;      // Weg am Werkstueck
                    double dsMach = (p.Machine - q.Machine).Length;      // Weg der Linearachsen
                    double dA = Math.Abs(p.A - q.A);
                    double dC = Math.Abs(p.C - q.C);
                    total += dsPart;

                    // Blockzeit: der langsamste der drei Ansprueche bestimmt sie. Ohne diese
                    // Begrenzung wuerden die Drehachsen in Polnaehe unrealistisch schnell laufen,
                    // weil dort der Weg am Werkstueck fast null, die C-Drehung aber gross ist.
                    double tCut = dsPart / Math.Max(cp.Feed, 1e-6);
                    double tA = dA / Math.Max(cp.Machine.MaxAFeed, 1e-6);
                    double tC = dC / Math.Max(cp.Machine.MaxCFeed, 1e-6);
                    double tBlock = Math.Max(tCut, Math.Max(tA, tC));
                    if (tBlock > tCut * 1.001) limited++;
                    if (dsMach < 0.25 * dsPart) rotary++;
                    if (tBlock > 1e-12)
                    {
                        maxAdps = Math.Max(maxAdps, dA / tBlock);
                        maxCdps = Math.Max(maxCdps, dC / tBlock);
                    }
                    minutes += tBlock;

                    double f;
                    if (cp.FeedMode == FeedMode.G93Inverszeit)
                    {
                        // G93: F = 1 / Blockzeit in Minuten
                        f = tBlock > 1e-9 ? 1.0 / tBlock : 99999.0;
                        f = MathUtil.Clamp(f, 0.01, 99999.0);
                    }
                    else
                    {
                        // G94: F so gewaehlt, dass die Linearachsen ihren Weg in der Blockzeit schaffen
                        f = tBlock > 1e-9 ? dsMach / tBlock : cp.Feed;
                        f = MathUtil.Clamp(f, cp.MinFeedOut, cp.MaxFeedOut);
                    }
                    p.Feed = f;
                    pass.Points[i] = p;
                }
            }
            tp.CutLength = total;
            tp.EstimatedMinutes = minutes;
            tp.FeedLimitedBlocks = limited;
            tp.RotaryDominatedBlocks = rotary;
            tp.MaxADegPerMin = maxAdps;
            tp.MaxCDegPerMin = maxCdps;
        }

        /// <summary>Klartext, wie der Bahnabstand zustande kommt - der Wert soll nicht
        /// unsichtbar in der Rechnung stecken.</summary>
        internal static string StepoverNote(CamParameters cp)
        {
            double R = cp.Tool.Radius;
            if (cp.UseFixedStepover)
                return string.Format(CultureInfo.InvariantCulture,
                    "Bahnabstand: {0:0.####} mm von Hand vorgegeben (Scallop wird nicht verwendet)",
                    MathUtil.Clamp(cp.FixedStepover, 0.005, 2.0 * R));

            double flat = FlatStepover(cp);
            return string.Format(CultureInfo.InvariantCulture,
                "Bahnabstand aus Scallop {0:0.####} mm bei R{1:0.###}: {2:0.####} mm auf ebener " +
                "Flaeche (s = 2*Wurzel(2*R*h - h^2)); auf gewoelbter Flaeche entsprechend enger",
                cp.ScallopHeight, R, flat);
        }

        private static void Summarize(CamParameters cp, Toolpath tp)
        {
            tp.Log.Add(StepoverNote(cp));
            tp.Log.Add(string.Format(CultureInfo.InvariantCulture,
                "Bahn: {0} Schnitte, {1} Punkte, Schnittlaenge {2:0.0} mm",
                tp.Passes.Count, tp.PointCount, tp.CutLength));
            if (tp.PointCount == 0) { tp.Log.Add("ACHTUNG: keine erreichbaren Bahnpunkte gefunden."); return; }

            tp.Log.Add(string.Format(CultureInfo.InvariantCulture,
                "A-Achse {0:0.000} .. {1:0.000} Grad (Grenze {2:0.#} .. {3:0.#})",
                tp.MinA, tp.MaxA, cp.Machine.AMinDeg, cp.Machine.AMaxDeg));
            double turns = (tp.MaxC - tp.MinC) / 360.0;
            tp.Log.Add(string.Format(CultureInfo.InvariantCulture,
                "C-Achse {0:0.0} .. {1:0.0} Grad = {2:0.0} Umdrehungen (endlos)",
                tp.MinC, tp.MaxC, turns));
            if (cp.AxisMode == ToolAxisMode.Flaechennormale && turns > 5 && Math.Abs(tp.MaxA) < 50)
                tp.Log.Add(string.Format(CultureInfo.InvariantCulture,
                    "HINWEIS: Die Flaeche ist mit hoechstens {0:0.0} Grad flach, trotzdem dreht C {1:0.0} mal. " +
                    "Das kommt von der Normalenausrichtung. Fuer so eine Flaeche ist die Werkzeugachse " +
                    "\"Senkrecht\" meist die ruhigere und schnellere Wahl.", Math.Abs(tp.MaxA), turns));
            tp.Log.Add(string.Format(CultureInfo.InvariantCulture,
                "Maschine X {0:0.000}..{1:0.000}  Y {2:0.000}..{3:0.000}  Z {4:0.000}..{5:0.000}",
                tp.MachineMin.X, tp.MachineMax.X, tp.MachineMin.Y, tp.MachineMax.Y,
                tp.MachineMin.Z, tp.MachineMax.Z));

            if (tp.ClampedCount > 0)
                tp.Log.Add(string.Format(CultureInfo.InvariantCulture,
                    "{0} Punkte mit an der A-Grenze abgeknickter Werkzeugachse - unterhalb des Aequators " +
                    "unvermeidbar, der Beruehrpunkt bleibt trotzdem exakt.", tp.ClampedCount));
            if (tp.CollisionSkipped > 0)
                tp.Log.Add(string.Format(CultureInfo.InvariantCulture,
                    "{0} Punkte wegen Kollision Fraeser/Schaft/Halter verworfen.", tp.CollisionSkipped));
            tp.Log.Add(string.Format(CultureInfo.InvariantCulture,
                "Bearbeitet wurde Z {0:0.###} .. {1:0.###} mm am Beruehrpunkt{2}",
                tp.MinContactZ, tp.MaxContactZ,
                cp.UseZMin ? " (Grenze Zmin " + cp.ZMin.ToString("0.###", CultureInfo.InvariantCulture) + " mm)"
                           : " (keine Zmin-Grenze gesetzt)"));
            if (tp.MissedRays > 0)
                tp.Log.Add(string.Format(CultureInfo.InvariantCulture,
                    "{0} Abtaststrahlen ohne Treffer auf der gewaehlten Flaeche.", tp.MissedRays));
            tp.Log.Add(string.Format(CultureInfo.InvariantCulture,
                "Hoechste Drehachsdrehzahl A {0:0} Grad/min, C {1:0} Grad/min (Grenze {2:0} / {3:0})",
                tp.MaxADegPerMin, tp.MaxCDegPerMin, cp.Machine.MaxAFeed, cp.Machine.MaxCFeed));
            if (cp.FeedMode == FeedMode.G94Kompensiert && tp.RotaryDominatedBlocks > tp.PointCount / 4)
                tp.Log.Add(string.Format(CultureInfo.InvariantCulture,
                    "HINWEIS: In {0} von {1} Saetzen bewegen sich die Linearachsen kaum - die Bewegung " +
                    "kommt fast nur aus der C-Drehung. Der F-Wert unter G94 ist dort wenig aussagekraeftig. " +
                    "Fuer ein rotationssymmetrisch aufgespanntes Teil ist G93 Inverszeit die genauere Wahl.",
                    tp.RotaryDominatedBlocks, tp.PointCount));
            if (tp.FeedLimitedBlocks > 0)
                tp.Log.Add(string.Format(CultureInfo.InvariantCulture,
                    "{0} Saetze wurden wegen der Drehachsgrenze langsamer gefahren als F{1:0}.",
                    tp.FeedLimitedBlocks, cp.Feed));
            tp.Log.Add(string.Format(CultureInfo.InvariantCulture,
                "Geschaetzte Schnittzeit {0:0.0} min (Sollvorschub F{1:0})", tp.EstimatedMinutes, cp.Feed));
        }
    }
}
