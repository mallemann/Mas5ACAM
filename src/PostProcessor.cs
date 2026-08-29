using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace Mas5ACAM
{
    /// <summary>
    /// Postprozessor für die Maschine X,Y,Z,A,C (Tisch-Tisch).
    ///
    /// Ausgegeben werden fertige Maschinenkoordinaten: die Kinematik ist im CAM
    /// gerechnet, die Steuerung braucht kein RTCP/TCPM. Der Nullpunkt des Programms ist
    /// der <b>Werkstück-Nullpunkt</b> – Mitte C-Tisch auf der Tischoberfläche, angetastet
    /// in der Ausgangsstellung A = C = 0. Wie weit der Tisch über der A-Achse sitzt, geht
    /// nur in die Kinematik ein und verschiebt die Werte nicht.
    /// </summary>
    public sealed class PostProcessor
    {
        private readonly CamParameters _cp;
        private readonly StringBuilder _sb = new StringBuilder(1 << 20);
        private Toolpath _tp;
        private int _n = 10;

        // Letzte bekannte Lage fuer die Animation. Nach einem G53-Satz ist die Z-Lage im
        // Werkstuecksystem unbekannt und der modale Merker wird geloescht - fuer die
        // Darstellung wird dann hierauf zurueckgegriffen.
        private Vec3 _disp = new Vec3(double.NaN, double.NaN, double.NaN);

        // modale Zustände
        private double _x = double.NaN, _y = double.NaN, _z = double.NaN;
        private double _a = double.NaN, _c = double.NaN, _f = double.NaN;
        private string _motion = "";

        public PostProcessor(CamParameters cp) { _cp = cp; }

        public string Build(Toolpath tp, string modelName)
        {
            _tp = tp;
            tp.Moves.Clear();
            Header(tp, modelName);

            bool firstPass = true;
            foreach (Pass pass in tp.Passes)
            {
                if (pass.Count < 2) continue;
                ClPoint p0 = pass.Points[0];

                Comment(string.Format(CultureInfo.InvariantCulture,
                    "Schnitt ab Theta {0:0.0} Grad, {1} Punkte", p0.Theta, pass.Count));

                // Anfahren: erst hoch, dann drehen, dann positionieren, dann eintauchen
                Rapid(null, null, _cp.SafeZ, null, null);
                Rapid(null, null, null, p0.A, p0.C);
                Rapid(p0.Machine.X, p0.Machine.Y, null, null, null);
                Rapid(null, null, p0.Machine.Z + _cp.Clearance, null, null);
                Line(p0.Machine.X, p0.Machine.Y, p0.Machine.Z, p0.A, p0.C,
                     FeedFor(_cp.PlungeFeed, _cp.Clearance, _cp.Clearance), p0);

                for (int i = 1; i < pass.Count; i++)
                {
                    ClPoint p = pass.Points[i];
                    Line(p.Machine.X, p.Machine.Y, p.Machine.Z, p.A, p.C, p.Feed, p);
                }

                // Abheben entlang der Werkzeugachse = Maschinen-Z, dann auf die Rückzugsebene
                ClPoint last = pass.Points[pass.Count - 1];
                Line(null, null, last.Machine.Z + _cp.Clearance, null, null,
                     FeedFor(_cp.PlungeFeed, _cp.Clearance, _cp.Clearance));
                Rapid(null, null, _cp.SafeZ, null, null);
                firstPass = false;
            }

            if (!firstPass && _cp.FeedMode == FeedMode.G93Inverszeit) Block("G94");
            Footer();
            return _sb.ToString();
        }

        // ------------------------------------------------------------------ Kopf und Fuss

        private void Header(Toolpath tp, string modelName)
        {
            Tool t = _cp.Tool;
            _sb.Append("%\n");
            _sb.Append("O").Append(_cp.ProgramNumber.ToString(CultureInfo.InvariantCulture))
               .Append(" (").Append(Clean(_cp.ProgramName)).Append(")\n");

            Comment("Erzeugt " + DateTime.Now.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture) + " mit Mas5ACAM");
            Comment("Modell: " + Clean(modelName));
            Comment("Maschine: 5 Achsen, Tisch-Tisch. A dreht um X (" +
                    F(_cp.Machine.AMinDeg, 1) + " bis " + F(_cp.Machine.AMaxDeg, 1) + " Grad), C dreht endlos um Z");
            Comment("Ausgabe in MASCHINENKOORDINATEN - Kinematik im CAM gerechnet, kein RTCP/TCPM noetig");
            Comment("Programm-Nullpunkt = Werkstuecknullpunkt = Mitte C-Tisch auf der Tischoberflaeche");
            Comment("Antasten bei A=0 C=0; die A-Achse liegt " + F(_cp.Machine.TableAboveA, 3) +
                    " mm darunter und geht nur in die Kinematik ein");
            Comment("Drehsinn: A" + (_cp.Machine.ASign > 0 ? "+" : "-") + " nach Rechte-Hand-Regel um X, C" +
                    (_cp.Machine.CSign > 0 ? "+" : "-") + " nach Rechte-Hand-Regel um Z");

            Comment(string.Format(CultureInfo.InvariantCulture,
                "Werkzeug T{0} {1}, Kugelfraeser D{2:0.000} (R{3:0.000}), freie Laenge {4:0.0}",
                t.Number, Clean(t.Name), t.Diameter, t.Radius, t.FreeLength));
            Comment(string.Format(CultureInfo.InvariantCulture,
                "Strategie {0}, Scallop {1:0.000} mm, Sehnentoleranz {2:0.000} mm, Voreilwinkel {3:0.0} Grad",
                _cp.Strategy, _cp.ScallopHeight, _cp.ChordTolerance, _cp.LeadAngleDeg));
            Comment(string.Format(CultureInfo.InvariantCulture,
                "Bahn: {0} Schnitte, {1} Punkte, Schnittlaenge {2:0.0} mm, ca. {3:0.0} min",
                tp.Passes.Count, tp.PointCount, tp.CutLength, tp.EstimatedMinutes));
            if (tp.PointCount > 0)
            {
                Comment(string.Format(CultureInfo.InvariantCulture,
                    "A {0:0.000} bis {1:0.000} Grad   C {2:0.0} bis {3:0.0} Grad", tp.MinA, tp.MaxA, tp.MinC, tp.MaxC));
                Comment(string.Format(CultureInfo.InvariantCulture,
                    "X {0:0.000}..{1:0.000}  Y {2:0.000}..{3:0.000}  Z {4:0.000}..{5:0.000}",
                    tp.MachineMin.X, tp.MachineMax.X, tp.MachineMin.Y, tp.MachineMax.Y,
                    tp.MachineMin.Z, tp.MachineMax.Z));
            }
            if (tp.ClampedCount > 0)
                Comment("HINWEIS: " + tp.ClampedCount + " Punkte mit an der A-Grenze abgeknickter Werkzeugachse");
            if (tp.CollisionSkipped > 0)
                Comment("HINWEIS: " + tp.CollisionSkipped + " Punkte wegen Kollision entfernt - Bahn ist dort unterbrochen");

            if (_cp.FullBlocks)
                Comment("Satzaufbau: N | G | X Y Z (mm) | A C (Grad) | F - alle Achsen in jedem Satz");
            else
                Comment("Satzaufbau modal: nur geaenderte Achsworte je Satz");
            Comment("--------------------------------------------------------------------");
            Block("G21 G90 G94 G17 G40 G49 G80");
            G53Retract("Start im Maschinenraum, unabhaengig vom Werkstuecknullpunkt");
            if (_cp.WithToolChange)
            {
                Block("T" + t.Number.ToString(CultureInfo.InvariantCulture) + " M6");
                Comment(Clean(t.Name));
            }
            Block("S" + F(_cp.Spindle, 0) + " M3");
            if (_cp.WithCoolant) Block("M8");
            Rapid(null, null, null, 0, 0);
            if (_cp.FeedMode == FeedMode.G93Inverszeit)
            {
                Comment("G93 Inverszeit-Vorschub: F = 1 / Blockzeit in Minuten");
                Block("G93");
            }
        }

        private void Footer()
        {
            if (_cp.WithCoolant) Block("M9");
            Block("M5");
            G53Retract("Ende im Maschinenraum");

            // C ist endlos und steht am Ende bei mehreren tausend Grad. Auf 0 zurueckzu-
            // drehen hiesse, das Teil dutzende Male um die eigene Achse zu kurbeln - ohne
            // jeden Nutzen, denn jede volle Umdrehung fuehrt zur selben Stellung. Angefahren
            // wird deshalb das naechstgelegene Vielfache von 360 Grad: dieselbe Ausrichtung
            // wie C0, aber hoechstens 180 Grad Weg.
            double cEnd = double.IsNaN(_c) ? 0.0 : Math.Round(_c / 360.0) * 360.0;
            if (Math.Abs(cEnd) > 1e-9)
                Comment(string.Format(CultureInfo.InvariantCulture,
                    "C auf {0:0.###} Grad = {1:0} volle Umdrehungen: dieselbe Stellung wie C0, " +
                    "nur {2:0.#} Grad Weg statt {3:0.#}", cEnd, cEnd / 360.0,
                    Math.Abs(cEnd - _c), Math.Abs(_c)));
            Rapid(null, null, null, 0, cEnd);
            Block("M30");
            _sb.Append("%\n");
        }

        /// <summary>
        /// Rückzug in Maschinenkoordinaten. Danach ist die Z-Lage im Werkstücksystem
        /// unbekannt – deshalb wird der modale Merker gelöscht, damit der nächste Satz
        /// seinen Z-Wert wieder ausdrücklich schreibt.
        /// </summary>
        private void G53Retract(string why)
        {
            if (!_cp.UseG53Retract) return;
            Comment(why);
            Block("G53 G0 Z" + F(_cp.G53RetractZ, _cp.Decimals));

            // Fuer die Animation: wo liegt Maschinen-Z-1 im Werkstuecksystem? Das weiss die
            // App nur, wenn die Hoehe von Maschinen-Z0 ueber dem Nullpunkt eingetragen ist.
            // Sonst wird ersatzweise die Rueckzugsebene gezeigt und das vermerkt.
            bool known = _cp.MachineZeroAboveWork > 1e-9;
            double showZ = known ? _cp.G53RetractZ + _cp.MachineZeroAboveWork : _cp.SafeZ;
            double keepX = double.IsNaN(_x) ? 0 : _x, keepY = double.IsNaN(_y) ? 0 : _y;
            double keepA = double.IsNaN(_a) ? 0 : _a, keepC = double.IsNaN(_c) ? 0 : _c;

            double sx = _x, sy = _y, sz = _z, sa = _a, sc = _c;
            _x = keepX; _y = keepY; _z = showZ; _a = keepA; _c = keepC;
            Record(MoveType.Retract, null);
            if (_tp != null && _tp.Moves.Count > 0)
            {
                ClPoint g = _tp.Moves[_tp.Moves.Count - 1];
                g.ZUnknown = !known;
                _tp.Moves[_tp.Moves.Count - 1] = g;
            }
            _x = sx; _y = sy; _z = sz; _a = sa; _c = sc;

            _x = _y = _z = double.NaN;
            _motion = "G0";
        }

        // ------------------------------------------------------------------ Satzausgabe

        private void Rapid(double? x, double? y, double? z, double? a, double? c)
        {
            string words = Words(x, y, z, a, c, null);
            if (words.Length == 0) return;
            Block(Motion("G0") + words);
            Record(MoveType.Rapid, null);
        }

        private void Line(double? x, double? y, double? z, double? a, double? c, double feed,
                          ClPoint? src = null)
        {
            string words = Words(x, y, z, a, c, feed);
            if (words.Length == 0) return;
            Block(Motion("G1") + words);
            // Ein Satz ohne Quellpunkt ist das Abheben am Bahnende - kein Schnitt.
            Record(src.HasValue ? MoveType.Feed : MoveType.Retract, src);
        }

        /// <summary>
        /// Den soeben geschriebenen Satz auch in die Bewegungsliste legen, aus der die
        /// Animation läuft. Bei Schnittsätzen wird der Originalpunkt übernommen (er kennt
        /// Berührpunkt und Werkzeugachse), bei Verbindungen die Lage aus der Achsstellung
        /// zurückgerechnet.
        /// </summary>
        private void Record(MoveType kind, ClPoint? src)
        {
            if (_tp == null) return;
            Vec3 pos = new Vec3(double.IsNaN(_x) ? _disp.X : _x,
                                double.IsNaN(_y) ? _disp.Y : _y,
                                double.IsNaN(_z) ? _disp.Z : _z);
            if (double.IsNaN(pos.X) || double.IsNaN(pos.Y) || double.IsNaN(pos.Z)) return;
            _disp = pos;

            ClPoint m;
            if (src.HasValue) { m = src.Value; }
            else
            {
                m = new ClPoint();
                Vec3 axis = Machine5Axis.ToolAxisFromAC(_a, _c);
                Vec3 tip = _cp.Machine.Inverse(pos, _a, _c);
                m.Axis = axis;
                m.Tip = tip;
                m.Center = tip + axis * _cp.Tool.Radius;
                m.Contact = tip;
                m.Normal = axis;
            }
            m.Type = kind;
            m.Machine = pos;
            m.A = _a; m.C = _c;
            m.Feed = double.IsNaN(_f) ? 0 : _f;
            _tp.Moves.Add(m);
        }

        /// <summary>Bewegungsart. Bei vollständigen Sätzen steht G0/G1 in jeder Zeile,
        /// sonst nur beim Wechsel (modal).</summary>
        private string Motion(string g)
        {
            bool repeat = _motion != g || _cp.FullBlocks;
            _motion = g;
            return repeat ? Pad(g, 3) : Pad("", 3);
        }

        /// <summary>
        /// Baut die Achsworte eines Satzes.
        ///
        /// <para><b>Vollständige Sätze</b> (Vorgabe): jede Zeile enthält alle bekannten
        /// Achsen und den Vorschub, auch wenn sich ein Wert nicht geändert hat. Der Satz
        /// ist damit für sich allein lesbar – man muss nicht rückwärts suchen, wo X
        /// zuletzt stand.</para>
        ///
        /// <para><b>Modal</b>: nur geänderte Worte. Kürzere Datei, aber Zeile für Zeile
        /// nicht mehr selbsterklärend.</para>
        ///
        /// Ein Satz ohne jede Achsänderung wird in beiden Fällen weggelassen.
        /// </summary>
        private string Words(double? x, double? y, double? z, double? a, double? c, double? f)
        {
            int d = _cp.Decimals, ad = _cp.AngleDecimals;

            bool mx = x.HasValue && Changed(_x, x.Value, d);
            bool my = y.HasValue && Changed(_y, y.Value, d);
            bool mz = z.HasValue && Changed(_z, z.Value, d);
            bool ma = a.HasValue && Changed(_a, a.Value, ad);
            bool mc = c.HasValue && Changed(_c, c.Value, ad);
            if (!(mx || my || mz || ma || mc)) return "";        // keine Bewegung: kein Satz

            if (x.HasValue) _x = x.Value;
            if (y.HasValue) _y = y.Value;
            if (z.HasValue) _z = z.Value;
            if (a.HasValue) _a = a.Value;
            if (c.HasValue) _c = c.Value;

            StringBuilder w = new StringBuilder(96);
            bool full = _cp.FullBlocks;

            Word(w, 'X', _x, d, full || mx, WX);
            Word(w, 'Y', _y, d, full || my, WX);
            Word(w, 'Z', _z, d, full || mz, WX);
            Word(w, 'A', _a * _cp.Machine.ASign, ad, full || ma, WA);
            Word(w, 'C', _c * _cp.Machine.CSign, ad, full || mc, WC);

            if (f.HasValue)
            {
                int fd = _cp.FeedMode == FeedMode.G93Inverszeit ? 3 : 0;
                bool always = full || _cp.FeedMode == FeedMode.G93Inverszeit;
                if (always || Changed(_f, f.Value, fd))
                {
                    _f = f.Value;
                    Word(w, 'F', _f, fd, true, WF);
                }
            }
            return w.ToString().TrimEnd();
        }

        // Spaltenbreiten für die ausgerichtete Ausgabe
        private const int WX = 10, WA = 9, WC = 13, WF = 12;

        /// <summary>Ein Achswort anhängen. Bei Spaltenausrichtung wird hinter der Zahl
        /// aufgefüllt, nie zwischen Adressbuchstabe und Zahl – das verträgt jede Steuerung.</summary>
        private void Word(StringBuilder w, char addr, double value, int decimals, bool emit, int width)
        {
            // Eine noch nie angesprochene Achse bekommt kein Wort - bei Spaltenausrichtung
            // aber Leerraum, damit die folgenden Spalten trotzdem stimmen.
            bool known = !double.IsNaN(value);
            string s = (emit && known) ? addr + F(value, decimals) : "";
            if (s.Length == 0 && !_cp.AlignColumns) return;
            w.Append(_cp.AlignColumns ? Pad(s, width) : s + " ");
        }

        private string Pad(string s, int width)
        {
            if (!_cp.AlignColumns) return s.Length == 0 ? "" : s + " ";
            return s.Length >= width ? s + " " : s.PadRight(width);
        }

        private static bool Changed(double oldV, double newV, int decimals)
        {
            if (double.IsNaN(oldV)) return true;
            double q = Math.Pow(10, -decimals) * 0.5;
            return Math.Abs(oldV - newV) >= q;
        }

        /// <summary>Vorschubwert passend zum Modus (G94 mm/min oder G93 Inverszeit).</summary>
        private double FeedFor(double mmPerMin, double partDistance, double machineDistance)
        {
            if (_cp.FeedMode != FeedMode.G93Inverszeit) return mmPerMin;
            return MathUtil.Clamp(partDistance > 1e-9 ? mmPerMin / partDistance : 99999.0, 0.01, 99999.0);
        }

        private void Block(string s)
        {
            if (_cp.LineNumbers)
            {
                // Satznummer auf feste Breite, damit die Achsspalten ueber das ganze
                // Programm untereinander stehen und nicht mit N10 -> N52130 wandern.
                string n = "N" + _n.ToString(CultureInfo.InvariantCulture);
                _sb.Append(_cp.AlignColumns ? n.PadRight(8) : n + " ");
                _n += 10;
            }
            _sb.Append(s.TrimEnd()).Append('\n');
        }

        private void Comment(string s) { _sb.Append('(').Append(Clean(s)).Append(")\n"); }

        private static string F(double v, int d)
        {
            if (Math.Abs(v) < Math.Pow(10, -d) * 0.5) v = 0.0;      // kein "-0.000"
            return v.ToString("F" + d.ToString(CultureInfo.InvariantCulture), CultureInfo.InvariantCulture);
        }

        private string Dot() { return _cp.AngleDecimals > 0 ? "." + new string('0', _cp.AngleDecimals) : ""; }

        /// <summary>Klammern und Sonderzeichen aus Kommentaren entfernen.</summary>
        private static string Clean(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            StringBuilder b = new StringBuilder(s.Length);
            foreach (char ch in s)
            {
                if (ch == '(') b.Append('[');
                else if (ch == ')') b.Append(']');
                else if (ch == 'ä') b.Append("ae");
                else if (ch == 'ö') b.Append("oe");
                else if (ch == 'ü') b.Append("ue");
                else if (ch == 'Ä') b.Append("Ae");
                else if (ch == 'Ö') b.Append("Oe");
                else if (ch == 'Ü') b.Append("Ue");
                else if (ch == 'ß') b.Append("ss");
                else if (ch >= 32 && ch < 127) b.Append(ch);
            }
            return b.ToString();
        }
    }
}
