using System;

namespace Mas5ACAM
{
    /// <summary>
    /// Lage des Modells im Werkstück-Koordinatensystem – also: wo X, Y und Z liegen.
    ///
    /// Ein STL bringt sein eigenes Koordinatensystem mit, das selten zur Aufspannung
    /// passt. Hier wird das Modell erst gedreht (Reihenfolge X, dann Y, dann Z) und
    /// danach verschoben. Das Ergebnis ist das Werkstück-Koordinatensystem:
    ///
    ///   * Ursprung (0,0,0) = Werkstück-Nullpunkt auf der Tischoberfläche
    ///   * +Z = Drehachse des C-Tisches, zeigt nach oben zur Spindel
    ///   * +X, +Y = Tischebene bei C = 0
    ///
    /// Weil die Drehachse des Tisches die Werkstück-Z-Achse ist, muss das Teil auf
    /// dieser Achse stehen. Die Schaltflächen im Fenster setzen den Nullpunkt deshalb
    /// immer auf einen mittigen Punkt des Modells oder der gewählten Fläche.
    /// </summary>
    public sealed class Workpiece
    {
        public double RotXDeg, RotYDeg, RotZDeg;
        public Vec3 Offset = Vec3.Zero;

        public bool IsIdentity
        {
            get
            {
                return Math.Abs(RotXDeg) < 1e-12 && Math.Abs(RotYDeg) < 1e-12 && Math.Abs(RotZDeg) < 1e-12
                       && Offset.LengthSq < 1e-24;
            }
        }

        /// <summary>Richtung transformieren (nur drehen).</summary>
        public Vec3 Direction(Vec3 v)
        {
            v = Vec3.RotX(v, RotXDeg * MathUtil.Deg);
            v = Vec3.RotY(v, RotYDeg * MathUtil.Deg);
            v = Vec3.RotZ(v, RotZDeg * MathUtil.Deg);
            return v;
        }

        /// <summary>Punkt transformieren (drehen, dann verschieben).</summary>
        public Vec3 Point(Vec3 p) { return Direction(p) + Offset; }

        /// <summary>Setzt den Nullpunkt so, dass der Rohmodell-Punkt <paramref name="raw"/>
        /// nach der Drehung auf (0,0,0) liegt.</summary>
        public void ZeroAt(Vec3 raw) { Offset = -Direction(raw); }

        /// <summary>Kopie des Netzes im Werkstück-Koordinatensystem. Die Reihenfolge der
        /// Dreiecke bleibt erhalten, damit eine bestehende Flächenauswahl gültig bleibt.</summary>
        public Mesh Apply(Mesh src)
        {
            Mesh m = new Mesh { Name = src.Name };
            for (int i = 0; i < src.Tris.Count; i++)
            {
                Tri t = src.Tris[i];
                m.Add(new Tri(Point(t.A), Point(t.B), Point(t.C)));
            }
            m.RecomputeBounds();
            return m;
        }

        public Workpiece Clone()
        {
            return new Workpiece { RotXDeg = RotXDeg, RotYDeg = RotYDeg, RotZDeg = RotZDeg, Offset = Offset };
        }
    }
}
