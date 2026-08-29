using System;

namespace Mas5ACAM
{
    /// <summary>
    /// Kinematik einer 5-Achs-Fräse in Tisch-Tisch-Konfiguration (X,Y,Z,A,C).
    ///
    ///   * Die Spindel steht fest und zeigt in Maschinen-Z nach unten;
    ///     die Werkzeugachse ist im Maschinenraum immer (0,0,1).
    ///   * A dreht um X, trägt den C-Tisch (Schwenkbrücke).  Grenzen ±90°.
    ///   * C dreht um Z und trägt das Werkstück.  Endlos.
    ///   * Drehpunkt = Schnittpunkt A-Achse / C-Achse.  Die Tischoberfläche
    ///     (= Werkstück-Nullpunkt) liegt um <see cref="TableAboveA"/> darüber.
    ///
    /// <para><b>Bezug der ausgegebenen Koordinaten.</b> Der Programm-Nullpunkt ist der
    /// <b>Werkstück-Nullpunkt</b>: Mitte C-Tisch auf der Tischoberfläche, in der
    /// Ausgangsstellung A = C = 0. Genau dort tastet man an. Der Abstand zur A-Achse geht
    /// nur in die Kinematik ein – er verschiebt die ausgegebenen Werte nicht. Ein Punkt
    /// 10 mm über dem Tisch steht bei A = C = 0 auf Z10, unabhängig davon, ob der Tisch
    /// 0 oder 25 mm über der A-Achse liegt.</para>
    ///
    /// Vorwärts:   p_m = R_A(A) * ( d + R_C(C) * p_w ) − d ,  d = (0,0,TableAboveA)
    /// Rückwärts:  aus der gewünschten Werkzeugachse t_w folgt
    ///             C = atan2(i, j)   und   A = atan2(sqrt(i²+j²), k).
    /// </summary>
    public sealed class Machine5Axis
    {
        /// <summary>
        /// Höhe der Tischoberfläche (und damit des Werkstück-Nullpunkts) über der A-Achse
        /// in mm. Geht ausschliesslich in die Kinematik ein: sie beschreibt, wie weit der
        /// Nullpunkt beim Schwenken von A ausholt. Die ausgegebenen Koordinaten verschiebt
        /// sie nicht.
        /// </summary>
        public double TableAboveA = 0.0;

        public double AMinDeg = -90.0;
        public double AMaxDeg =  90.0;

        /// <summary>C ist endlos; hier nur zur Kennzeichnung.</summary>
        public bool CEndless = true;

        /// <summary>
        /// Vorzeichen der ausgegebenen Achswerte. Intern wird durchgehend mit der
        /// Rechte-Hand-Regel gerechnet (A dreht +Z nach -Y, C dreht +X nach +Y).
        /// Dreht die Maschine eine Achse andersherum, kehrt das Vorzeichen nur die
        /// Beschriftung im GCode um - die Bewegung selbst bleibt dieselbe.
        /// </summary>
        public double ASign = 1.0;
        public double CSign = 1.0;

        /// <summary>Maximaler Achsvorschub der Drehachsen in Grad/min (Vorschubbegrenzung).</summary>
        public double MaxAFeed = 3600.0;
        public double MaxCFeed = 7200.0;

        /// <summary>Verfahrwege für die Plausibilitätsprüfung, mm.</summary>
        public double TravelX = 500, TravelY = 400, TravelZ = 400;

        /// <summary>
        /// Werkstückpunkt -> ausgegebene Koordinaten bei gegebener Achsstellung (Grad).
        ///
        /// Gedreht wird um den Drehpunkt, gemessen wird aber ab dem Werkstück-Nullpunkt –
        /// deshalb wird der Tischversatz am Ende wieder abgezogen. Sonst hätte allein die
        /// Angabe „Tisch liegt 25 mm über der A-Achse" alle Z-Werte um 25 mm angehoben,
        /// obwohl der angetastete Nullpunkt unverändert auf dem Tisch liegt.
        /// </summary>
        public Vec3 Forward(Vec3 pw, double aDeg, double cDeg)
        {
            Vec3 d = new Vec3(0, 0, TableAboveA);
            Vec3 inA = d + Vec3.RotZ(pw, cDeg * MathUtil.Deg);
            return Vec3.RotX(inA, aDeg * MathUtil.Deg) - d;
        }

        /// <summary>Ausgegebene Koordinaten -> Werkstückpunkt (Umkehrung von <see cref="Forward"/>).</summary>
        public Vec3 Inverse(Vec3 pm, double aDeg, double cDeg)
        {
            Vec3 d = new Vec3(0, 0, TableAboveA);
            Vec3 inA = Vec3.RotX(pm + d, -aDeg * MathUtil.Deg);
            return Vec3.RotZ(inA - d, -cDeg * MathUtil.Deg);
        }

        /// <summary>Richtungsvektor (kein Punkt) vom Werkstück- ins Maschinensystem.</summary>
        public Vec3 ForwardDir(Vec3 vw, double aDeg, double cDeg)
        {
            return Vec3.RotX(Vec3.RotZ(vw, cDeg * MathUtil.Deg), aDeg * MathUtil.Deg);
        }

        /// <summary>
        /// Rückwärtskinematik der Drehachsen: Welche Stellung (A,C) richtet die im Werkstück
        /// gewünschte Werkzeugachse t_w parallel zur Spindel (Maschinen-Z) aus?
        /// Es gibt immer zwei Lösungen: (A, C) und (-A, C+180°).
        /// </summary>
        public static void SolveAC(Vec3 toolAxisW, out double aDeg, out double cDeg,
                                   out double aAltDeg, out double cAltDeg)
        {
            Vec3 t = toolAxisW.Normalized;
            double rho = Math.Sqrt(t.X * t.X + t.Y * t.Y);

            if (rho < 1e-12)                       // Werkzeugachse parallel zu Werkstück-Z
            {
                aDeg = t.Z >= 0 ? 0.0 : 180.0;
                cDeg = 0.0;
            }
            else
            {
                cDeg = Math.Atan2(t.X, t.Y) * MathUtil.Rad;
                aDeg = Math.Atan2(rho, t.Z) * MathUtil.Rad;      // 0° .. 180°
            }
            aAltDeg = -aDeg;
            cAltDeg = cDeg + 180.0;
        }

        /// <summary>
        /// Wählt aus den beiden Lösungen die maschinell zulässige und – bei Gleichstand –
        /// die mit dem kürzeren Weg zur vorherigen Stellung. C wird dabei stetig
        /// ausgewickelt (endlose Achse, C darf über ±360° hinauslaufen).
        /// </summary>
        public bool ChooseAC(Vec3 toolAxisW, double prevA, double prevC,
                             out double aDeg, out double cDeg)
        {
            // Senkrechte Werkzeugachse: C ist unbestimmt. Dann stehen lassen, statt auf 0
            // zu springen - sonst dreht der Tisch ohne jeden Grund.
            Vec3 tn = toolAxisW.Normalized;
            if (Math.Sqrt(tn.X * tn.X + tn.Y * tn.Y) < 1e-9)
            {
                aDeg = tn.Z >= 0 ? 0.0 : 180.0;
                cDeg = prevC;
                if (aDeg > AMaxDeg) { aDeg = MathUtil.Clamp(aDeg, AMinDeg, AMaxDeg); return false; }
                return true;
            }

            double a1, c1, a2, c2;
            SolveAC(toolAxisW, out a1, out c1, out a2, out c2);

            c1 = MathUtil.Unwrap(prevC, c1);
            c2 = MathUtil.Unwrap(prevC, c2);

            bool ok1 = a1 >= AMinDeg - 1e-9 && a1 <= AMaxDeg + 1e-9;
            bool ok2 = a2 >= AMinDeg - 1e-9 && a2 <= AMaxDeg + 1e-9;

            if (ok1 && ok2)
            {
                double cost1 = Math.Abs(a1 - prevA) + Math.Abs(c1 - prevC);
                double cost2 = Math.Abs(a2 - prevA) + Math.Abs(c2 - prevC);
                if (cost2 < cost1) { aDeg = a2; cDeg = c2; } else { aDeg = a1; cDeg = c1; }
                return true;
            }
            if (ok1) { aDeg = a1; cDeg = c1; return true; }
            if (ok2) { aDeg = a2; cDeg = c2; return true; }

            aDeg = MathUtil.Clamp(a1, AMinDeg, AMaxDeg);
            cDeg = c1;
            return false;                          // nicht erreichbar – Aufrufer muss begrenzen
        }

        /// <summary>Die Werkzeugachse, die sich bei der Achsstellung (A,C) im Werkstück ergibt.
        /// Umkehrung von <see cref="SolveAC"/>: t_w = R_C(-C) * R_A(-A) * (0,0,1).</summary>
        public static Vec3 ToolAxisFromAC(double aDeg, double cDeg)
        {
            Vec3 t = Vec3.RotX(Vec3.UnitZ, -aDeg * MathUtil.Deg);
            return Vec3.RotZ(t, -cDeg * MathUtil.Deg);
        }
    }
}
