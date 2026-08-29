using System;
using System.Collections.Generic;

namespace Mas5ACAM
{
    public enum MoveType { Rapid, Feed, Plunge, Retract }

    /// <summary>Ein Bahnpunkt: Kontaktpunkt auf der Fläche plus die daraus abgeleitete
    /// Werkzeuglage und die fertige Achsstellung der Maschine.</summary>
    public struct ClPoint
    {
        public Vec3 Contact;        // Berührpunkt Werkzeug/Fläche, Werkstück-KS
        public Vec3 Normal;         // Flächennormale im Berührpunkt
        public Vec3 Center;         // Kugelmittelpunkt des Fräsers, Werkstück-KS
        public Vec3 Axis;           // Werkzeugachse (Richtung Spindel), Werkstück-KS
        public Vec3 Tangent;        // Bahnrichtung im Berührpunkt (für Voreilwinkel und Diagnose)
        public Vec3 Tip;            // Werkzeugspitze, Werkstück-KS
        public double A, C;         // Achsstellung in Grad
        public Vec3 Machine;        // X/Y/Z der Werkzeugspitze in Maschinenkoordinaten
        public MoveType Type;
        public bool OnPlane;        // Werkzeug sitzt flach auf einer Z-Ebene
        public bool ZUnknown;       // G53-Satz: Hoehe im Werkstuecksystem nicht bekannt
        public double Feed;         // mm/min bzw. Inverszeit-F-Wert
        public bool AxisClamped;    // Werkzeugachse musste wegen A-Grenze abgeknickt werden
        public double Theta;        // Polarwinkel der Strategie, Grad (nur Info)
    }

    /// <summary>Ein zusammenhängender Schnitt. Zwischen Schnitten wird abgehoben.</summary>
    public sealed class Pass
    {
        public readonly List<ClPoint> Points = new List<ClPoint>();
        public int Count { get { return Points.Count; } }
    }

    /// <summary>Das Ergebnis der Bahnberechnung.</summary>
    public sealed class Toolpath
    {
        public readonly List<Pass> Passes = new List<Pass>();
        public readonly List<string> Log = new List<string>();

        /// <summary>Die Z-Ebenen der Strategie Parallelbahnen, von oben nach unten.
        /// Der letzte Eintrag ist die Flaeche selbst (Schlichtbahn).</summary>
        public readonly List<double> ZLevels = new List<double>();

        /// <summary>
        /// Jede Bewegung des fertigen Programms in der Reihenfolge, in der sie im GCode
        /// steht – Eilgänge, Anfahren, Schnitt, Abheben und die G53-Rückzüge.
        ///
        /// <para>Die Liste füllt der <see cref="PostProcessor"/> beim Schreiben, Satz für
        /// Satz. Damit zeigt die Animation genau das, was auch im Programm steht; sie kann
        /// nicht davon abweichen, weil es dieselbe Quelle ist.</para>
        /// </summary>
        public readonly List<ClPoint> Moves = new List<ClPoint>();

        public int ClampedCount;
        public int CollisionSkipped;
        public int MissedRays;
        public double MinContactZ = double.MaxValue;   // Hoehenbereich der bearbeiteten Flaeche
        public double MaxContactZ = double.MinValue;
        public double MinA = double.MaxValue, MaxA = double.MinValue;
        public double MinC = double.MaxValue, MaxC = double.MinValue;
        public Vec3 MachineMin = new Vec3(double.MaxValue, double.MaxValue, double.MaxValue);
        public Vec3 MachineMax = new Vec3(double.MinValue, double.MinValue, double.MinValue);
        public double CutLength;          // Bahnlänge am Kontaktpunkt, mm
        public double EstimatedMinutes;
        public int RotaryDominatedBlocks; // Saetze, in denen sich die Linearachsen kaum bewegen
        public int FeedLimitedBlocks;     // Saetze, die die Drehachsgrenze ausgebremst hat
        public double MaxADegPerMin, MaxCDegPerMin;

        public int PointCount
        {
            get { int n = 0; foreach (Pass p in Passes) n += p.Count; return n; }
        }

        public void Track(ClPoint p)
        {
            if (p.A < MinA) MinA = p.A;
            if (p.A > MaxA) MaxA = p.A;
            if (p.C < MinC) MinC = p.C;
            if (p.C > MaxC) MaxC = p.C;
            MachineMin = Vec3.Min(MachineMin, p.Machine);
            MachineMax = Vec3.Max(MachineMax, p.Machine);
        }
    }
}
