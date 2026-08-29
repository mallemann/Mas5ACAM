using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace Mas5ACAM
{
    /// <summary>Hauptfenster: links die Parameter, rechts die 3D-Vorschau.</summary>
    public sealed class MainForm : Form
    {
        private readonly CamParameters _cp = new CamParameters();
        private readonly Workpiece _wp = new Workpiece();
        private Mesh _raw;                 // Modell wie geladen
        private Mesh _mesh;                // dasselbe Modell im Werkstueck-Koordinatensystem
        private TriGrid _grid;
        private Toolpath _path;
        private string _gcode = "";

        private Viewport3D _view;
        private TextBox _log;
        private Label _modelInfo, _wcsInfo, _faceInfo, _machineInfo;

        /// <summary>Diese Felder beschreiben die Maschine und werden gespeichert.</summary>
        private static readonly string[] MachineNumFields =
            { "aMin", "aMax", "tableZ", "mz0", "aFeed", "cFeed" };
        private static readonly string[] MachineFlagFields = { "aInv", "cInv" };
        private Panel _scroll;
        private TrackBar _slider;
        private Timer _timer;

        // Drehung und Nullpunkt wirken sofort. Damit nicht bei jedem Tastendruck das
        // ganze Netz neu gedreht wird, sammelt ein kurzer Nachlauf die Eingabe ein.
        private Timer _wcsTimer;
        private bool _wcsGuard;
        private Button _btnPlay, _btnCalc, _btnSave, _btnShow;
        private CheckBox _cbAxes, _cbLinks;
        private RadioButton _rbMachineView, _rbPartView;
        private Label _headSphere, _headRaster;
        private NumericUpDown _speed;

        private readonly Dictionary<string, TextBox> _f = new Dictionary<string, TextBox>();
        private readonly Dictionary<string, CheckBox> _b = new Dictionary<string, CheckBox>();
        private ComboBox _cbStrategy, _cbFeedMode, _cbAxisMode;

        public MainForm(string[] args)
        {
            Text = "Mas5ACAM - 5-Achsen CAM (X,Y,Z,A,C Tisch-Tisch)";
            Width = 1480; Height = 940;
            StartPosition = FormStartPosition.CenterScreen;
            MinimumSize = new Size(1100, 700);
            BuildUi();

            LoadMachineSettings();
            LoadExample();
            UpdateStrategyFields();
            UpdateLivePreview();
            if (args != null && args.Length > 0 && File.Exists(args[0])) LoadStl(args[0]);

            // Beim Start gleich einmal rechnen, damit sofort etwas zu sehen ist.
            Shown += async (s, e) => { if (!_started) { _started = true; await CalculateAsync(); } };
        }

        private bool _started;

        /// <summary>Bahnberechnung von aussen anstossen (Diagnosemodus).</summary>
        public void RunCalculation()
        {
            _started = true;
            CalculateSync();
        }

        /// <summary>
        /// Rundprobe der Klick-Rueckrechnung: Ein bekannter Punkt der Flaeche wird auf den
        /// Bildschirm projiziert, und an genau dieser Bildschirmposition muss PickTriangle
        /// wieder ein Dreieck derselben Flaeche und desselben Ortes finden.
        /// </summary>
        public string DiagnosePick()
        {
            if (_mesh == null) return "kein Modell";
            ClPoint? cur = _view.Current;
            if (!cur.HasValue) return Report("Klick-Rundprobe: keine Bahn vorhanden");

            Vec3 target = cur.Value.Contact;
            System.Drawing.PointF s;
            if (!_view.TryProject(target, out s)) return Report("Klick-Rundprobe: Punkt nicht im Bild");

            int tri = _view.PickTriangle(System.Drawing.Point.Round(s));
            if (tri < 0) return Report("Klick-Rundprobe FEHLGESCHLAGEN: kein Treffer");

            Vec3 hit = TriMath.ClosestPointOnTriangle(target, _mesh.Tris[tri]);
            double d = (hit - target).Length;
            // Ein grosser Abstand heisst nicht, dass die Rueckrechnung falsch ist: der
            // Punkt kann von dieser Kameraposition aus schlicht hinter dem Modell liegen.
            string urteil = d < 0.3
                ? "Treffer stimmt"
                : "Punkt liegt von hier aus hinter dem Modell - der Klick trifft die Vorderseite";

            return Report(string.Format(CultureInfo.InvariantCulture,
                "Klick-Rundprobe: Beruehrpunkt {0} -> Bildpunkt {1:0}/{2:0} -> Dreieck {3}, Abstand {4:0.000} mm, " +
                "Flaeche {5}, {6}", target, s.X, s.Y, tri, d,
                _mesh.IsSelected(tri) ? "gewaehlt" : "NICHT gewaehlt", urteil));
        }

        private string Report(string s) { Log(s); return s; }

        /// <summary>Wartet, bis der Nachlauf der Eingabefelder abgelaufen ist -
        /// also genau so, wie es bei einer echten Eingabe von Hand ablaeuft.</summary>
        private void PumpMs(int ms)
        {
            for (int i = 0; i < ms; i += 20)
            {
                System.Threading.Thread.Sleep(20);
                Application.DoEvents();
            }
        }

        /// <summary>
        /// Rundprobe zur Sofortdrehung: Es wird in das Feld "Modell drehen um X"
        /// geschrieben wie beim Tippen. Ohne einen Knopf zu druecken muss sich das
        /// Modell drehen, eine halb getippte Zahl darf es nicht auf 0 zuruecksetzen.
        /// </summary>
        /// <summary>Haken "Eilgaenge" von aussen setzen (Dokumentationsbilder).</summary>
        public void SetShowLinks(bool on) { _cbLinks.Checked = on; Application.DoEvents(); }

        /// <summary>Das 3D-Fenster so abzeichnen, wie es gerade steht.</summary>
        private Bitmap Snapshot()
        {
            Application.DoEvents();
            Bitmap bmp = new Bitmap(_view.Width, _view.Height);
            _view.DrawToBitmap(bmp, new Rectangle(0, 0, _view.Width, _view.Height));
            return bmp;
        }

        /// <summary>
        /// Rundprobe zum Haken "Eilgaenge". Statt Farben zu deuten werden zwei Abzuege
        /// verglichen: mit und ohne Haken. Was verschwindet, sind die Eilgaenge; die
        /// blaue Schnittbahn muss Bildpunkt fuer Bildpunkt gleich bleiben.
        /// </summary>
        public string DiagnoseLinks()
        {
            bool keepAxes = _cbAxes.Checked, keepLinks = _cbLinks.Checked;
            bool keepTool = _view.ShowTool;
            _cbAxes.Checked = false;      // Werkzeugachsen sind ebenfalls orange
            _view.ShowTool = false;       // die Fraeserkugel wandert nicht, stoert aber

            _cbLinks.Checked = true;
            Bitmap mit = Snapshot();
            _cbLinks.Checked = false;
            Bitmap ohne = Snapshot();

            int anders = 0, blauMit = 0, blauOhne = 0;
            for (int y = 0; y < mit.Height; y++)
                for (int x = 0; x < mit.Width; x++)
                {
                    Color a = mit.GetPixel(x, y), b = ohne.GetPixel(x, y);
                    if (a.ToArgb() != b.ToArgb()) anders++;
                    if (a.B > 180 && a.B - a.R > 80) blauMit++;
                    if (b.B > 180 && b.B - b.R > 80) blauOhne++;
                }
            mit.Dispose(); ohne.Dispose();

            _cbAxes.Checked = keepAxes; _cbLinks.Checked = keepLinks;
            _view.ShowTool = keepTool;
            _view.Invalidate();
            Application.DoEvents();

            return Report(string.Format(CultureInfo.InvariantCulture,
                "Haken Eilgaenge: das Ausschalten aendert {0} Bildpunkte - {1}. " +
                "Blaue Schnittbahn {2} Bildpunkte mit, {3} ohne Eilgaenge - {4}",
                anders, anders > 200 ? "Eilgaenge verschwinden" : "FALSCH, es aendert sich nichts",
                blauMit, blauOhne,
                blauOhne > 50 && blauOhne > blauMit * 0.99
                    ? "unveraendert, der Rest ist Kantenglaettung" : "FALSCH, Bahn geht verloren"));
        }

        /// <summary>Schreibt in das Drehfeld wie beim Tippen und wartet den Nachlauf ab.</summary>
        public void TypeRotX(string text) { _f["wrx"].Text = text; PumpMs(600); }

        public string DiagnoseWcsLive()
        {
            var sb = new StringBuilder();
            string keep = _f["wrx"].Text;
            bool hadPath = _path != null;

            Aabb b0 = _mesh.Bounds;
            double zHoch0 = b0.Max.Z - b0.Min.Z, yBreit0 = b0.Max.Y - b0.Min.Y;

            _f["wrx"].Text = "90";          // wie getippt, kein Knopfdruck
            PumpMs(600);

            Aabb b1 = _mesh.Bounds;
            double zHoch1 = b1.Max.Z - b1.Min.Z, yBreit1 = b1.Max.Y - b1.Min.Y;
            sb.AppendLine(string.Format(CultureInfo.InvariantCulture,
                "Sofortdrehung: nach Eingabe 90 ohne Knopfdruck steht die Drehung bei {0:0.###} Grad, " +
                "das Modell ist von {1:0.##} auf {2:0.##} mm hoch und von {3:0.##} auf {4:0.##} mm tief - {5}",
                _wp.RotXDeg, zHoch0, zHoch1, yBreit0, yBreit1,
                Math.Abs(_wp.RotXDeg - 90) < 1e-9 && Math.Abs(zHoch1 - yBreit0) < 1e-6
                    && Math.Abs(yBreit1 - zHoch0) < 1e-6 ? "richtig gekippt" : "FALSCH"));

            sb.AppendLine("Werkzeugweg beim Drehen: " +
                (!hadPath ? "war keiner vorhanden"
                 : _path == null ? "verworfen, richtig" : "FALSCH, alter Weg steht noch"));

            _f["wrx"].Text = "-";           // halb getippt
            PumpMs(600);
            sb.AppendLine(string.Format(CultureInfo.InvariantCulture,
                "Halbe Eingabe \"-\": Drehung bleibt bei {0:0.###} Grad - {1}",
                _wp.RotXDeg, Math.Abs(_wp.RotXDeg - 90) < 1e-9 ? "kein Rueckfall auf 0" : "FALSCH"));

            _f["wrx"].Text = "-30";
            PumpMs(600);
            sb.AppendLine(string.Format(CultureInfo.InvariantCulture,
                "Weitergetippt auf -30: Drehung {0:0.###} Grad - {1}",
                _wp.RotXDeg, Math.Abs(_wp.RotXDeg + 30) < 1e-9 ? "richtig" : "FALSCH"));

            _f["wrx"].Text = keep;          // Ausgangszustand wiederherstellen
            PumpMs(600);
            sb.Append(string.Format(CultureInfo.InvariantCulture,
                "Zurueckgesetzt auf {0}: Drehung {1:0.###} Grad, Modell wieder {2:0.##} mm hoch - {3}",
                keep, _wp.RotXDeg, _mesh.Bounds.Max.Z - _mesh.Bounds.Min.Z,
                Math.Abs(_mesh.Bounds.Max.Z - _mesh.Bounds.Min.Z - zHoch0) < 1e-6 ? "wie zuvor" : "FALSCH"));

            Log(sb.ToString());
            return sb.ToString();
        }

        /// <summary>
        /// Prueft fuer jede Bahnform, ob der Haken bedienbar ist und ob er Zmax und Zmin
        /// wirklich freischaltet. Genau hier war die App vorher blockiert: bei einer
        /// anderen Bahnform als Parallelbahnen liess sich der Haken nicht mehr anklicken.
        /// </summary>
        public string DiagnoseUi()
        {
            var sb = new StringBuilder();
            Func<string, string> fld = k => !_f.ContainsKey(k) ? "fehlt"
                                          : (_f[k].Enabled ? "aktiv" : "ausgegraut");

            // Die Probe schaltet Felder um. Alles merken und am Ende exakt zuruecksetzen -
            // sonst steht die Oberflaeche danach anders da als vorher.
            int keep = _cbStrategy.SelectedIndex;
            bool keepUseZ = _b["usezmin"].Checked, keepAutoTop = _b["autotop"].Checked;
            string keepZmin = _f["zmin"].Text, keepTop = _f["stocktop"].Text, keepTable = _f["tableZ"].Text;

            for (int i = 0; i < _cbStrategy.Items.Count; i++)
            {
                _cbStrategy.SelectedIndex = i;
                Application.DoEvents();

                CheckBox box = _b["usezmin"];
                sb.Append(_cbStrategy.Items[i]).Append(": Haken ")
                  .Append(box.Enabled ? "bedienbar" : "GESPERRT");

                box.Checked = true;
                Application.DoEvents();
                sb.Append(" | mit Haken Zmin ").Append(fld("zmin"));

                box.Checked = false;
                Application.DoEvents();
                sb.Append(" | ohne Haken Zmin ").Append(fld("zmin")).AppendLine();
            }

            _cbStrategy.SelectedIndex = keep;
            Application.DoEvents();

            // Live-Vorschau: alles muss beim Tippen mitgehen, nicht erst beim Rechnen.
            _b["usezmin"].Checked = true;
            _f["zmin"].Text = "33.5";
            _f["tableZ"].Text = "25";
            Application.DoEvents();
            sb.AppendLine("Live-Vorschau nach Eingabe (ohne Neuberechnung): Ansicht sieht Zmin " +
                          _view.Cp.ZMin.ToString("0.###", CultureInfo.InvariantCulture) +
                          " (Ebene " + (_view.Cp.UseZMin && _view.ShowZLimits ? "sichtbar" : "AUS") +
                          "), A-Achse bei -" +
                          _view.Cp.Machine.TableAboveA.ToString("0.###", CultureInfo.InvariantCulture) + " mm");

            // Unvollstaendige Eingabe darf die Ebene nicht auf 0 springen lassen
            _f["zmin"].Text = "-";
            _f["tableZ"].Text = "";
            Application.DoEvents();
            sb.AppendLine("Nach unvollstaendiger Eingabe: Zmin bleibt " +
                          _view.Cp.ZMin.ToString("0.###", CultureInfo.InvariantCulture) +
                          ", A-Achse bleibt bei -" +
                          _view.Cp.Machine.TableAboveA.ToString("0.###", CultureInfo.InvariantCulture) + " mm");

            // Maschinendaten: speichern, veraendern, wieder laden - der Wert muss zurueckkommen.
            // Die echte Konfigurationsdatei wird dabei vorher gesichert und danach wieder
            // hergestellt; eine Diagnose darf die eingefahrenen Maschinendaten nicht anfassen.
            string cfg = MachineSettings.FilePath;
            bool hadCfg = System.IO.File.Exists(cfg);
            string cfgBackup = hadCfg ? System.IO.File.ReadAllText(cfg) : null;

            string keepAmin = _f["aMin"].Text;
            bool keepInv = _b["cInv"].Checked;
            _f["aMin"].Text = "-88.5"; _b["cInv"].Checked = true;
            SaveMachineSettings();
            _f["aMin"].Text = "-1"; _b["cInv"].Checked = false;
            LoadMachineSettings();
            sb.AppendLine("Maschinendaten: nach Speichern/Aendern/Laden steht A minimal auf " +
                          _f["aMin"].Text + ", C-Drehrichtung umgekehrt = " + _b["cInv"].Checked +
                          "  (Datei " + cfg + ")");

            _f["aMin"].Text = keepAmin; _b["cInv"].Checked = keepInv;
            if (hadCfg) System.IO.File.WriteAllText(cfg, cfgBackup);
            else System.IO.File.Delete(cfg);
            sb.AppendLine("Konfigurationsdatei wieder im Ausgangszustand: " +
                          (System.IO.File.Exists(cfg) ? "vorhanden wie zuvor" : "nicht vorhanden wie zuvor"));

            _cbStrategy.SelectedIndex = keep;
            _b["usezmin"].Checked = keepUseZ;
            _b["autotop"].Checked = keepAutoTop;
            _f["zmin"].Text = keepZmin;
            _f["stocktop"].Text = keepTop;
            _f["tableZ"].Text = keepTable;
            Application.DoEvents();

            sb.AppendLine("Ausgangszustand wiederhergestellt: Zmin-Grenze " +
                          (_b["usezmin"].Checked ? "ein" : "aus") + " bei " + _f["zmin"].Text +
                          ", C-Tisch ueber A-Achse " + _f["tableZ"].Text);
            return sb.ToString();
        }

        /// <summary>
        /// Prueft, was die eingeblendete A-Achse zeigen soll: dass das Werkstueck beim
        /// Schwenken wirklich um sie kreist und nicht um die X-Achse durch den Nullpunkt.
        ///
        /// Gemessen wird am Werkstueck-Nullpunkt: sein Abstand zur A-Achse muss ueber die
        /// ganze Bahn konstant gleich dem Tischversatz sein. Zum Vergleich der Abstand zur
        /// X-Achse - der schwankt, weil das Teil eben nicht um sie dreht.
        /// </summary>
        public string DiagnoseAAxis()
        {
            double h = _cp.Machine.TableAboveA;
            if (_path == null || _path.PointCount == 0) return Report("A-Achsen-Probe: keine Bahn");

            double aMin = double.MaxValue, aMax = double.MinValue;
            double xMin = double.MaxValue, xMax = double.MinValue;
            foreach (Pass pass in _path.Passes)
                foreach (ClPoint pt in pass.Points)
                {
                    // Werkstueck-Nullpunkt in der aktuellen Achsstellung
                    Vec3 z0 = _cp.Machine.Forward(Vec3.Zero, pt.A, pt.C);
                    // Abstand zur A-Achse: waagrechte Gerade in X auf Hoehe -h
                    double dA = Math.Sqrt(z0.Y * z0.Y + (z0.Z + h) * (z0.Z + h));
                    // Abstand zur X-Achse durch den Nullpunkt
                    double dX = Math.Sqrt(z0.Y * z0.Y + z0.Z * z0.Z);
                    aMin = Math.Min(aMin, dA); aMax = Math.Max(aMax, dA);
                    xMin = Math.Min(xMin, dX); xMax = Math.Max(xMax, dX);
                }

            return Report(string.Format(CultureInfo.InvariantCulture,
                "A-Achsen-Probe: Tischversatz {0:0.###} mm. Abstand Nullpunkt zur A-Achse " +
                "{1:0.0000} .. {2:0.0000} mm (muss konstant {0:0.###} sein), zur X-Achse " +
                "{3:0.0000} .. {4:0.0000} mm (schwankt) -> das Teil dreht um die A-Achse",
                h, aMin, aMax, xMin, xMax));
        }

        /// <summary>Tischversatz von aussen setzen (Diagnose- und Dokumentationsmodus).</summary>
        public void SetTableOffset(double mm)
        {
            SetField("tableZ", mm);
            ReadParameters();
            _view.Cp = _cp;
            _view.ZoomToFit();
            _view.Invalidate();
        }

        /// <summary>Z-Fenster von aussen setzen (Diagnose- und Dokumentationsmodus).</summary>
        public void SetZWindow(double zmin, double stockTop)
        {
            _b["usezmin"].Checked = true;
            SetField("zmin", zmin);
            if (stockTop > 0) { _b["autotop"].Checked = false; SetField("stocktop", stockTop); }
            UpdateStrategyFields();
            ReadParameters();
            _view.Cp = _cp;
            _view.ZoomToFit();
            _view.Invalidate();
        }

        // ---------------------------------------------------------------- Maschinendaten

        private void SaveMachineSettings()
        {
            var v = new Dictionary<string, string>();
            foreach (string k in MachineNumFields) if (_f.ContainsKey(k)) v[k] = _f[k].Text.Trim();
            foreach (string k in MachineFlagFields) if (_b.ContainsKey(k)) v[k] = _b[k].Checked ? "1" : "0";

            try
            {
                MachineSettings.Save(v);
                Log("Maschinendaten gespeichert: " + MachineSettings.FilePath);
                UpdateMachineInfo();
            }
            catch (Exception ex)
            {
                Log("Maschinendaten konnten nicht gespeichert werden: " + ex.Message);
            }
        }

        private void LoadMachineSettings()
        {
            try
            {
                var v = MachineSettings.Load();
                if (v.Count == 0) { UpdateMachineInfo(); return; }

                foreach (var kv in v)
                {
                    if (_f.ContainsKey(kv.Key)) _f[kv.Key].Text = kv.Value;
                    else if (_b.ContainsKey(kv.Key)) _b[kv.Key].Checked = kv.Value == "1";
                }
                Log("Maschinendaten geladen: " + MachineSettings.FilePath);
            }
            catch (Exception ex)
            {
                Log("Maschinendaten konnten nicht geladen werden: " + ex.Message);
            }
            UpdateMachineInfo();
        }

        private void UpdateMachineInfo()
        {
            if (_machineInfo == null) return;
            _machineInfo.Text = MachineSettings.Exists
                ? "Gespeichert in " + MachineSettings.FilePath + "\nwird beim Start geladen"
                : "Noch nicht gespeichert. Ablage waere\n" + MachineSettings.FilePath;
        }

        /// <summary>Parameterbereich scrollen (Diagnose- und Dokumentationsmodus).</summary>
        public void ScrollParameters(int y)
        {
            if (_scroll == null) return;
            _scroll.AutoScrollPosition = new Point(0, Math.Max(0, y));
            _scroll.Refresh();
        }

        /// <summary>Ansicht von aussen umschalten (Diagnosemodus).</summary>
        public void SetView(bool machineView, double fractionOfPath)
        {
            _view.MachineView = machineView;
            if (_rbMachineView != null) _rbMachineView.Checked = machineView;
            if (_rbPartView != null) _rbPartView.Checked = !machineView;
            SetAnim((int)Math.Round(MathUtil.Clamp(fractionOfPath, 0, 1) * Math.Max(0, _view.PointCount - 1)));
            _view.Refresh();
        }

        // ================================================================= Oberfläche

        private void BuildUi()
        {
            SplitContainer split = new SplitContainer
            {
                Dock = DockStyle.Fill,
                Orientation = Orientation.Vertical,
                SplitterWidth = 6,
                FixedPanel = FixedPanel.Panel1
            };
            Controls.Add(split);
            split.Panel1MinSize = 400;
            split.SplitterDistance = 430;

            // ---------------- linke Seite: Parameter (scrollbar) oben, Protokoll unten
            Panel left = new Panel { Dock = DockStyle.Fill };
            Panel scroll = new Panel { Dock = DockStyle.Fill, AutoScroll = true, Padding = new Padding(8) };
            _scroll = scroll;
            FlowLayoutPanel flow = new FlowLayoutPanel
            {
                Dock = DockStyle.Top, FlowDirection = FlowDirection.TopDown,
                WrapContents = false, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, Width = 396
            };
            scroll.Controls.Add(flow);

            flow.Controls.Add(GroupModel());
            flow.Controls.Add(GroupWcs());
            flow.Controls.Add(GroupFace());
            flow.Controls.Add(GroupTool());
            flow.Controls.Add(GroupMachine());
            flow.Controls.Add(GroupStrategy());
            flow.Controls.Add(GroupCollision());
            flow.Controls.Add(GroupTech());
            flow.Controls.Add(GroupPost());
            flow.Controls.Add(GroupActions());

            _log = new TextBox
            {
                Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical,
                Dock = DockStyle.Bottom, Height = 200, Font = new Font("Consolas", 8.5f),
                BackColor = Color.FromArgb(250, 250, 250)
            };
            Splitter sp = new Splitter { Dock = DockStyle.Bottom, Height = 5, BackColor = SystemColors.ControlDark };
            left.Controls.Add(scroll);          // Fill zuerst: WinForms dockt vom hoechsten Index abwaerts
            left.Controls.Add(sp);
            left.Controls.Add(_log);
            split.Panel1.Controls.Add(left);

            // ---------------- rechte Seite: 3D + Animation
            _view = new Viewport3D { Dock = DockStyle.Fill, Cp = _cp };
            _view.FacePicked += OnFacePicked;
            Panel bottom = BuildAnimationBar();
            split.Panel2.Controls.Add(_view);
            split.Panel2.Controls.Add(bottom);

            _wcsTimer = new Timer { Interval = 250 };
            _wcsTimer.Tick += (s, e) => { _wcsTimer.Stop(); ApplyWcsIfChanged(); };

            _timer = new Timer { Interval = 30 };
            _timer.Tick += (s, e) =>
            {
                if (_view.PointCount == 0) { _timer.Stop(); return; }
                int next = _view.AnimIndex + (int)_speed.Value;
                if (next >= _view.PointCount - 1) { next = _view.PointCount - 1; _timer.Stop(); _btnPlay.Text = "Start"; }
                SetAnim(next);
            };
        }

        private Panel BuildAnimationBar()
        {
            Panel p = new Panel { Dock = DockStyle.Bottom, Height = 112, Padding = new Padding(8, 6, 8, 8) };

            _slider = new TrackBar { Dock = DockStyle.Top, Minimum = 0, Maximum = 1, TickStyle = TickStyle.None, Height = 30 };
            _slider.Scroll += (s, e) => { _view.AnimIndex = _slider.Value; };

            FlowLayoutPanel row = new FlowLayoutPanel
            {
                Dock = DockStyle.Fill, FlowDirection = FlowDirection.LeftToRight,
                AutoSize = false, WrapContents = true
            };

            _btnPlay = new Button { Text = "Start", Width = 80 };
            _btnPlay.Click += (s, e) =>
            {
                if (_timer.Enabled) { _timer.Stop(); _btnPlay.Text = "Start"; }
                else
                {
                    if (_view.AnimIndex >= _view.PointCount - 1) SetAnim(0);
                    _timer.Start(); _btnPlay.Text = "Pause";
                }
            };
            Button btnReset = new Button { Text = "Anfang", Width = 70 };
            btnReset.Click += (s, e) => { _timer.Stop(); _btnPlay.Text = "Start"; SetAnim(0); };
            Button btnEnd = new Button { Text = "Ende", Width = 60 };
            btnEnd.Click += (s, e) => { _timer.Stop(); _btnPlay.Text = "Start"; SetAnim(Math.Max(0, _view.PointCount - 1)); };

            _speed = new NumericUpDown { Minimum = 1, Maximum = 500, Value = 6, Width = 60 };

            // Die Umschaltung zwischen den beiden Ansichten ist die wichtigste Bedienung
            // im 3D-Fenster - deshalb beschriftet und als Auswahl, nicht als Haken.
            Label viewLabel = new Label
            {
                Text = "Ansicht:", AutoSize = true, Padding = new Padding(16, 6, 0, 0),
                Font = new Font("Segoe UI", 9f, FontStyle.Bold)
            };
            _rbPartView = new RadioButton
            {
                Text = "Werkstueck", Checked = true, AutoSize = true, Padding = new Padding(6, 4, 0, 0)
            };
            _rbMachineView = new RadioButton
            {
                Text = "Maschine", AutoSize = true, Padding = new Padding(6, 4, 0, 0)
            };
            _rbMachineView.CheckedChanged += (s, e) =>
            {
                _view.MachineView = _rbMachineView.Checked;
                _view.Invalidate();
            };

            CheckBox showModel = new CheckBox { Text = "Modell", Checked = true, AutoSize = true, Padding = new Padding(10, 4, 0, 0) };
            showModel.CheckedChanged += (s, e) => { _view.ShowModel = showModel.Checked; _view.Invalidate(); };

            _cbAxes = new CheckBox { Text = "Werkzeugachsen", Checked = true, AutoSize = true, Padding = new Padding(10, 4, 0, 0) };
            _cbAxes.CheckedChanged += (s, e) => { _view.ShowToolAxes = _cbAxes.Checked; _view.Invalidate(); };

            CheckBox showPath = new CheckBox { Text = "Schnittbahn", Checked = true, AutoSize = true, Padding = new Padding(10, 4, 0, 0) };
            showPath.CheckedChanged += (s, e) => { _view.ShowPath = showPath.Checked; _view.Invalidate(); };

            _cbLinks = new CheckBox { Text = "Eilgaenge", Checked = true, AutoSize = true, Padding = new Padding(10, 4, 0, 0) };
            _cbLinks.CheckedChanged += (s, e) => { _view.ShowLinks = _cbLinks.Checked; _view.Invalidate(); };

            CheckBox showZ = new CheckBox { Text = "Z-Grenzen", Checked = true, AutoSize = true, Padding = new Padding(10, 4, 0, 0) };
            showZ.CheckedChanged += (s, e) => { _view.ShowZLimits = showZ.Checked; _view.Invalidate(); };

            Button btnFit = new Button { Text = "Ansicht anpassen", Width = 130 };
            btnFit.Click += (s, e) => _view.ZoomToFit();

            row.Controls.Add(_btnPlay); row.Controls.Add(btnReset); row.Controls.Add(btnEnd);
            row.Controls.Add(new Label { Text = "Tempo", AutoSize = true, Padding = new Padding(10, 6, 0, 0) });
            row.Controls.Add(_speed);
            row.Controls.Add(viewLabel); row.Controls.Add(_rbPartView); row.Controls.Add(_rbMachineView);
            row.Controls.Add(new Label { Text = "|", AutoSize = true, Padding = new Padding(10, 6, 0, 0),
                                         ForeColor = SystemColors.ControlDark });
            row.Controls.Add(showModel); row.Controls.Add(_cbAxes); row.Controls.Add(showPath);
            row.Controls.Add(_cbLinks); row.Controls.Add(showZ);
            row.Controls.Add(btnFit);

            p.Controls.Add(row);
            p.Controls.Add(_slider);
            return p;
        }

        // ---------------------------------------------------------------- Baugruppen

        private GroupBox GroupModel()
        {
            TableLayoutPanel t = Table();
            _modelInfo = new Label { AutoSize = false, Height = 36, Width = 350, ForeColor = Color.FromArgb(60, 60, 60) };

            Button gen = new Button { Text = "Beispiel Kugel", Width = 168 };
            gen.Click += (s, e) => LoadExample();
            Button gen2 = new Button { Text = "Beispiel Freiformflaeche", Width = 168 };
            gen2.Click += (s, e) => LoadExampleWavy();
            Button open = new Button { Text = "STL laden ...", Width = 168 };
            open.Click += (s, e) =>
            {
                using (OpenFileDialog d = new OpenFileDialog { Filter = "STL-Dateien (*.stl)|*.stl|Alle Dateien|*.*" })
                    if (d.ShowDialog(this) == DialogResult.OK) LoadStl(d.FileName);
            };
            Button save = new Button { Text = "Modell als STL speichern ...", Width = 344 };
            save.Click += (s, e) =>
            {
                if (_mesh == null) return;
                using (SaveFileDialog d = new SaveFileDialog { Filter = "STL-Dateien (*.stl)|*.stl", FileName = _mesh.Name + ".stl" })
                    if (d.ShowDialog(this) == DialogResult.OK) { StlIo.SaveBinary(_mesh, d.FileName); Log("Modell gespeichert: " + d.FileName); }
            };

            FlowLayoutPanel row = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.LeftToRight, Margin = new Padding(0) };
            row.Controls.Add(gen); row.Controls.Add(gen2);
            FlowLayoutPanel row2 = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.LeftToRight, Margin = new Padding(0) };
            row2.Controls.Add(open);

            t.Controls.Add(row, 0, 0); t.SetColumnSpan(row, 2);
            t.Controls.Add(row2, 0, 1); t.SetColumnSpan(row2, 2);
            t.Controls.Add(save, 0, 2); t.SetColumnSpan(save, 2);
            t.Controls.Add(_modelInfo, 0, 3); t.SetColumnSpan(_modelInfo, 2);
            return Group("Modell", t);
        }

        /// <summary>Wo X, Y und Z liegen: Modell drehen und Nullpunkt setzen.</summary>
        private GroupBox GroupWcs()
        {
            TableLayoutPanel t = Table();
            Num(t, 0, "wrx", "Modell drehen um X (Grad)", 0);
            Num(t, 1, "wry", "Modell drehen um Y (Grad)", 0);
            Num(t, 2, "wrz", "Modell drehen um Z (Grad)", 0);
            Num(t, 3, "wox", "Nullpunkt verschieben X (mm)", 0);
            Num(t, 4, "woy", "Nullpunkt verschieben Y (mm)", 0);
            Num(t, 5, "woz", "Nullpunkt verschieben Z (mm)", 0);

            foreach (string k in WcsFields)
                _f[k].TextChanged += (s2, e) =>
                {
                    if (_wcsGuard) return;
                    _wcsTimer.Stop();
                    _wcsTimer.Start();
                };

            Button apply = new Button { Text = "Jetzt uebernehmen", Width = 344 };
            apply.Click += (s2, e) => { _wcsTimer.Stop(); ApplyWcsFromFields(); };
            t.Controls.Add(apply, 0, 6); t.SetColumnSpan(apply, 2);

            FlowLayoutPanel row1 = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.LeftToRight, Margin = new Padding(0) };
            row1.Controls.Add(ZeroButton("Null = Boden Mitte", "unten"));
            row1.Controls.Add(ZeroButton("Null = Modellmitte", "mitte"));
            t.Controls.Add(row1, 0, 7); t.SetColumnSpan(row1, 2);

            FlowLayoutPanel row2 = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.LeftToRight, Margin = new Padding(0) };
            row2.Controls.Add(ZeroButton("Null = Oberkante", "oben"));
            row2.Controls.Add(ZeroButton("Null = Flaechenmitte", "flaeche"));
            t.Controls.Add(row2, 0, 8); t.SetColumnSpan(row2, 2);

            _wcsInfo = new Label { AutoSize = false, Height = 34, Width = 350, ForeColor = Color.FromArgb(60, 60, 60) };
            t.Controls.Add(_wcsInfo, 0, 9); t.SetColumnSpan(_wcsInfo, 2);

            Label hint = new Label
            {
                Text = "Gedreht wird in der Reihenfolge X, Y, Z, danach verschoben.\n" +
                       "+Z ist die Drehachse des C-Tisches - das Teil muss darauf stehen.\n" +
                       "Eingaben wirken sofort; ein Werkzeugweg wird dabei verworfen.",
                AutoSize = true, ForeColor = Color.FromArgb(90, 90, 90)
            };
            t.Controls.Add(hint, 0, 10); t.SetColumnSpan(hint, 2);
            return Group("Werkstueck-Koordinatensystem", t);
        }

        private Button ZeroButton(string text, string where)
        {
            Button b = new Button { Text = text, Width = 168 };
            b.Click += (s2, e) => ZeroTo(where);
            return b;
        }

        /// <summary>Welche Flaeche bearbeitet wird.</summary>
        private GroupBox GroupFace()
        {
            TableLayoutPanel t = Table();

            Label hint = new Label
            {
                Text = "Strg+Klick im 3D-Fenster waehlt die Flaeche unter dem Zeiger.\n" +
                       "Zusaetzlich Umschalt = dazunehmen, Alt = wegnehmen.\n" +
                       "Die gewaehlte Flaeche wird blau dargestellt.",
                AutoSize = true, ForeColor = Color.FromArgb(90, 90, 90)
            };
            t.Controls.Add(hint, 0, 0); t.SetColumnSpan(hint, 2);

            Num(t, 1, "breakang", "Knickwinkel Flaechenerkennung (Grad)", _cp.BreakAngleDeg);

            FlowLayoutPanel row = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.LeftToRight, Margin = new Padding(0) };
            Button all = new Button { Text = "Alles auswaehlen", Width = 168 };
            all.Click += (s2, e) =>
            {
                if (_mesh == null) return;
                _mesh.SelectAll(); UpdateFaceInfo(); FillZLimits(); _view.Invalidate();
                Log("Ganzes Modell ausgewaehlt (" + _mesh.SelectedCount + " Dreiecke)");
            };
            Button none = new Button { Text = "Auswahl loeschen", Width = 168 };
            none.Click += (s2, e) =>
            {
                if (_mesh == null) return;
                _mesh.ClearSelection(); UpdateFaceInfo(); FillZLimits(); _view.Invalidate();
                Log("Auswahl geloescht - das ganze Modell gilt wieder als bearbeitbar");
            };
            row.Controls.Add(all); row.Controls.Add(none);
            t.Controls.Add(row, 0, 2); t.SetColumnSpan(row, 2);

            Button center = new Button { Text = "Projektionszentrum aus Auswahl bestimmen", Width = 344 };
            center.Click += (s2, e) => { AutoCenterFromSelection(true); _view.Invalidate(); };
            t.Controls.Add(center, 0, 3); t.SetColumnSpan(center, 2);

            _faceInfo = new Label { AutoSize = false, Height = 34, Width = 350, ForeColor = Color.FromArgb(60, 60, 60) };
            t.Controls.Add(_faceInfo, 0, 4); t.SetColumnSpan(_faceInfo, 2);

            return Group("Zu bearbeitende Flaeche", t);
        }

        private GroupBox GroupTool()
        {
            TableLayoutPanel t = Table();
            Num(t, 0, "toolNo", "Werkzeugnummer T", _cp.Tool.Number);
            Num(t, 1, "toolD", "Kugelfraeser-Durchmesser (mm)", _cp.Tool.Diameter);
            Num(t, 2, "shankD", "Schaftdurchmesser (mm)", _cp.Tool.ShankDiameter);
            Num(t, 3, "freeL", "Freie Laenge ab Kugelmitte (mm)", _cp.Tool.FreeLength);
            Num(t, 4, "holderD", "Halterdurchmesser (mm)", _cp.Tool.HolderDiameter);
            Num(t, 5, "holderL", "Halterlaenge (mm)", _cp.Tool.HolderLength);
            return Group("Werkzeug", t);
        }

        private GroupBox GroupMachine()
        {
            TableLayoutPanel t = Table();
            Num(t, 0, "aMin", "A minimal (Grad)", _cp.Machine.AMinDeg);
            Num(t, 1, "aMax", "A maximal (Grad)", _cp.Machine.AMaxDeg);
            Num(t, 2, "tableZ", "C-Tisch ueber A-Achse (mm)", _cp.Machine.TableAboveA);
            // Die A-Achse soll sofort an der richtigen Stelle stehen, nicht erst nach dem Rechnen.
            _f["tableZ"].TextChanged += (s2, e) => UpdateLivePreview();
            Num(t, 3, "mz0", "Maschinen-Z0 ueber Nullpunkt (mm, 0 = unbekannt)", _cp.MachineZeroAboveWork);
            Num(t, 4, "aFeed", "Max. Achsvorschub A (Grad/min)", _cp.Machine.MaxAFeed);
            Num(t, 5, "cFeed", "Max. Achsvorschub C (Grad/min)", _cp.Machine.MaxCFeed);
            Chk(t, 6, "aInv", "A-Drehrichtung umkehren", false);
            Chk(t, 7, "cInv", "C-Drehrichtung umkehren", false);
            FlowLayoutPanel row = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.LeftToRight, Margin = new Padding(0) };
            Button save = new Button { Text = "Maschinendaten speichern", Width = 168 };
            save.Click += (s2, e) => SaveMachineSettings();
            Button load = new Button { Text = "Wieder laden", Width = 168 };
            load.Click += (s2, e) => { LoadMachineSettings(); UpdateLivePreview(); };
            row.Controls.Add(save); row.Controls.Add(load);
            t.Controls.Add(row, 0, 8); t.SetColumnSpan(row, 2);

            _machineInfo = new Label { AutoSize = false, Height = 46, Width = 350,
                                       ForeColor = Color.FromArgb(60, 60, 60) };
            t.Controls.Add(_machineInfo, 0, 9); t.SetColumnSpan(_machineInfo, 2);

            Label hint = new Label
            {
                Text = "Nullpunkt des Programms ist der Werkstuecknullpunkt:\n" +
                       "Mitte C-Tisch auf der Tischoberflaeche, angetastet bei A=0 C=0.\n" +
                       "Der Abstand zur A-Achse verschiebt die ausgegebenen Werte NICHT.\n" +
                       "Er sagt nur, wie weit der Nullpunkt beim Schwenken von A ausholt.\n" +
                       "C dreht endlos um Z. Rechte-Hand-Regel: A+ kippt Werkstueck-Z\n" +
                       "nach Maschinen -Y.",
                AutoSize = true, ForeColor = Color.FromArgb(90, 90, 90)
            };
            t.Controls.Add(hint, 0, 10); t.SetColumnSpan(hint, 2);
            return Group("Maschine", t);
        }

        private GroupBox GroupStrategy()
        {
            TableLayoutPanel t = Table();
            int r = 0;

            _cbStrategy = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 180 };
            _cbStrategy.Items.AddRange(new object[] { "Spirale (um ein Zentrum)",
                                                      "Breitenkreise (um ein Zentrum)",
                                                      "Parallelbahnen (Z-Projektion)" });
            _cbStrategy.SelectedIndex = 0;
            _cbStrategy.SelectedIndexChanged += (s2, e) => UpdateStrategyFields();
            t.Controls.Add(new Label { Text = "Bahnform", AutoSize = true, Anchor = AnchorStyles.Left,
                                       Margin = new Padding(3, 6, 3, 3) }, 0, r);
            t.Controls.Add(_cbStrategy, 1, r); r++;

            _headSphere = Head(t, r++, "Nur Spirale / Breitenkreise");
            Num(t, r++, "cx", "Projektionszentrum X (mm)", _cp.Center.X);
            Num(t, r++, "cy", "Projektionszentrum Y (mm)", _cp.Center.Y);
            Num(t, r++, "cz", "Projektionszentrum Z (mm)", _cp.Center.Z);
            Num(t, r++, "th0", "Theta Start (Grad, 0 = Nordpol)", _cp.ThetaStartDeg);
            Num(t, r++, "th1", "Theta Ende (Grad, 90 = Aequator)", _cp.ThetaEndDeg);

            Head(t, r++, "Bearbeitungsgrenze - fuer alle Bahnformen");
            Chk(t, r++, "usezmin", "Nach unten begrenzen (Zmin)", _cp.UseZMin);
            Num(t, r++, "zmin", "Zmin - Bearbeitung bis hinunter (mm)", _cp.ZMin);
            Note(t, r++, "Nur Beruehrpunkte ueber Zmin werden angefahren - gemessen in der\n" +
                         "Ausgangsposition. Kugel mit Zmin auf Kugelmitte: nur die obere\n" +
                         "Halbkugel. Die Fraeserkugel darf dabei unter Zmin haengen, sonst\n" +
                         "waere eine steile Flanke nicht schlichtbar.\n" +
                         "Die Ebene erscheint beim Tippen in der Werkstueckansicht.");

            _headRaster = Head(t, r++, "Nur Parallelbahnen");
            Chk(t, r++, "autotop", "Rohteil-Oberkante = hoechster Flaechenpunkt", _cp.AutoStockTop);
            Num(t, r++, "stocktop", "Rohteil-Oberkante Z (mm)", _cp.StockTop);
            Note(t, r++, "Hoeher als die Flaeche eingetragen heisst: dort steht Rohmaterial,\n" +
                         "das zuerst abgetragen wird. Daraus entstehen zusaetzliche\n" +
                         "Schruppebenen. Eine obere Bearbeitungsgrenze gibt es bewusst nicht -\n" +
                         "was oben nicht bearbeitet werden soll, gehoert nicht in die Auswahl.");
            Num(t, r++, "raster", "Bahnrichtung (Grad, 0 = entlang X)", _cp.RasterAngleDeg);
            Chk(t, r++, "zigzag", "Zickzack mit direkter Verbindung", _cp.ZigZag);
            Num(t, r++, "maxz", "Max. Z-Zustelltiefe je Schnitt (mm)", _cp.MaxZStep);
            Num(t, r++, "roughover", "Schrupp-Zustellung (Anteil von D)", _cp.RoughStepoverFactor);

            // Beim Tippen sofort in die Vorschau uebernehmen, damit sich die Werte am
            // Modell pruefen lassen - nicht erst bei der Bahnberechnung.
            _f["zmin"].TextChanged += (s2, e) => UpdateLivePreview();
            _f["stocktop"].TextChanged += (s2, e) => UpdateLivePreview();

            Head(t, r++, "Fuer alle Bahnformen");
            _cbAxisMode = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 180 };
            _cbAxisMode.Items.AddRange(new object[] { "Flaechennormale (5 Achsen)",
                                                      "Senkrecht (A und C stehen)" });
            _cbAxisMode.SelectedIndex = 0;
            t.Controls.Add(new Label { Text = "Werkzeugachse", AutoSize = true, Anchor = AnchorStyles.Left,
                                       Margin = new Padding(3, 6, 3, 3) }, 0, r);
            t.Controls.Add(_cbAxisMode, 1, r); r++;
            Num(t, r++, "scallop", "Restmaterialhoehe Scallop (mm)", _cp.ScallopHeight);
            Chk(t, r++, "fixover", "Bahnabstand stattdessen direkt vorgeben", _cp.UseFixedStepover);
            Num(t, r++, "overmm", "Bahnabstand beim Schlichten (mm)", _cp.FixedStepover);
            Note(t, r++, "Ohne Haken ergibt sich der Abstand aus dem Scallop:\n" +
                         "s = 2 * Wurzel(2*R*h - h^2). Bei R3 und h = 0.01 mm sind das\n" +
                         "0.489 mm. Der tatsaechliche Wert steht nach dem Rechnen im\n" +
                         "Protokoll.");
            Num(t, r++, "chord", "Sehnentoleranz (mm)", _cp.ChordTolerance);
            Num(t, r++, "stock", "Aufmass (mm)", _cp.Stock);
            Num(t, r++, "lead", "Voreilwinkel (Grad)", _cp.LeadAngleDeg);
            Num(t, r++, "tilt", "Seitliche Neigung (Grad)", _cp.TiltAngleDeg);
            Chk(t, r++, "cw", "Drehsinn / Richtung umkehren", _cp.ClockwiseC);
            Chk(t, r++, "band", "Nur Treffer im Radiusband verwenden", _cp.UseRadiusBand);
            Num(t, r++, "bandR", "Erwarteter Radius (mm)", _cp.BandRadius);
            Num(t, r++, "bandT", "Radiusband-Toleranz (mm)", _cp.BandTolerance);

            return Group("Strategie", t);
        }

        /// <summary>
        /// Zmin und Zmax sofort in die Vorschau uebernehmen - beim Tippen, nicht erst beim
        /// Rechnen. Die Ebenen sollen ja gerade dazu dienen, die Eingabe am Modell zu
        /// pruefen; dafuer muessen sie mitgehen.
        /// </summary>
        private void UpdateLivePreview()
        {
            if (_view == null) return;
            _cp.UseZMin = B("usezmin", _cp.UseZMin);
            _cp.AutoStockTop = B("autotop", _cp.AutoStockTop);
            _cp.Strategy = _cbStrategy != null && _cbStrategy.SelectedIndex == 2
                         ? Strategy.ParallelBahnen : _cp.Strategy;
            // Bei unvollstaendiger Eingabe (leeres Feld, blosses Minus) den bisherigen Wert
            // behalten, statt die Ebene auf 0 springen zu lassen.
            _cp.ZMin = D("zmin", _cp.ZMin);
            _cp.StockTop = D("stocktop", _cp.StockTop);
            _cp.Machine.TableAboveA = D("tableZ", _cp.Machine.TableAboveA);
            _cp.MachineZeroAboveWork = D("mz0", _cp.MachineZeroAboveWork);
            _view.Cp = _cp;
            _view.Invalidate();
        }

        /// <summary>Erklaerender Text unter einer Feldergruppe.</summary>
        private static void Note(TableLayoutPanel t, int row, string text)
        {
            Label l = new Label
            {
                Text = text, AutoSize = true, ForeColor = Color.FromArgb(90, 90, 90),
                Margin = new Padding(3, 2, 3, 6)
            };
            t.Controls.Add(l, 0, row); t.SetColumnSpan(l, 2);
        }

        /// <summary>Zwischenueberschrift in einer Parametertabelle.</summary>
        private static Label Head(TableLayoutPanel t, int row, string text)
        {
            Label l = new Label
            {
                Text = text, AutoSize = true, ForeColor = Color.FromArgb(70, 90, 130),
                Font = new Font("Segoe UI", 8.5f, FontStyle.Bold),
                Margin = new Padding(3, 10, 3, 2)
            };
            t.Controls.Add(l, 0, row); t.SetColumnSpan(l, 2);
            return l;
        }

        /// <summary>
        /// Zeigt an, welcher Teil der Strategie gerade zaehlt.
        ///
        /// Gesperrt wird dabei bewusst nichts: Wer ein Feld nicht bedienen kann, sitzt fest
        /// und sieht nicht warum. Stattdessen wird die Ueberschrift des zustaendigen Blocks
        /// hervorgehoben und die des anderen gedaempft. Einzige Ausnahme ist das Paar
        /// Zmax/Zmin, das ausgegraut wird, solange es automatisch berechnet wird - dort
        /// erklaert der Haken direkt darueber, wie man es freischaltet.
        /// </summary>
        private void UpdateStrategyFields()
        {
            bool raster = _cbStrategy != null && _cbStrategy.SelectedIndex == 2;

            Color on = Color.FromArgb(70, 90, 130);
            Color off = Color.FromArgb(165, 170, 180);
            if (_headSphere != null) _headSphere.ForeColor = raster ? off : on;
            if (_headRaster != null) _headRaster.ForeColor = raster ? on : off;

            if (_f.ContainsKey("zmin"))
                _f["zmin"].Enabled = _b.ContainsKey("usezmin") && _b["usezmin"].Checked;
            if (_f.ContainsKey("stocktop"))
                _f["stocktop"].Enabled = _b.ContainsKey("autotop") && !_b["autotop"].Checked;
        }

        private GroupBox GroupCollision()
        {
            TableLayoutPanel t = Table();
            Chk(t, 0, "coll", "Kollision Werkzeug/Modell pruefen", _cp.CheckCollision);
            Chk(t, 1, "collH", "Halter mitpruefen", _cp.CheckHolder);
            Num(t, 2, "gouge", "Zulaessige Unterschreitung (mm)", _cp.GougeTolerance);
            Num(t, 3, "hclear", "Freigang Schaft/Halter (mm)", _cp.HolderClearance);
            return Group("Kollision", t);
        }

        private GroupBox GroupTech()
        {
            TableLayoutPanel t = Table();
            Num(t, 0, "feed", "Vorschub am Werkstueck (mm/min)", _cp.Feed);
            Num(t, 1, "plunge", "Eintauchvorschub (mm/min)", _cp.PlungeFeed);
            Num(t, 2, "spindle", "Drehzahl S (1/min)", _cp.Spindle);
            Num(t, 3, "safeZ", "Rueckzugsebene Maschinen-Z (mm)", _cp.SafeZ);
            Num(t, 4, "clear", "Abhebeweg (mm)", _cp.Clearance);
            Num(t, 5, "fmax", "Maximaler F-Wert (mm/min)", _cp.MaxFeedOut);
            return Group("Technologie", t);
        }

        private GroupBox GroupPost()
        {
            TableLayoutPanel t = Table();
            _cbFeedMode = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 190 };
            _cbFeedMode.Items.AddRange(new object[] { "G94 mm/min, kompensiert", "G93 Inverszeit" });
            _cbFeedMode.SelectedIndex = _cp.FeedMode == FeedMode.G93Inverszeit ? 1 : 0;
            t.Controls.Add(new Label { Text = "Vorschubart", AutoSize = true, Anchor = AnchorStyles.Left }, 0, 0);
            t.Controls.Add(_cbFeedMode, 1, 0);

            Txt(t, 1, "progName", "Programmname", _cp.ProgramName);
            Num(t, 2, "progNo", "Programmnummer O", _cp.ProgramNumber);
            Chk(t, 3, "nnum", "Satznummern ausgeben", _cp.LineNumbers);
            Chk(t, 4, "full", "Vollstaendige Saetze (alle Achsen je Zeile)", _cp.FullBlocks);
            Chk(t, 5, "align", "Spalten ausrichten", _cp.AlignColumns);
            Chk(t, 6, "tc", "Werkzeugwechsel ausgeben", _cp.WithToolChange);
            Chk(t, 7, "cool", "Kuehlmittel M8/M9", _cp.WithCoolant);
            Chk(t, 8, "g53", "G53-Rueckzug am Anfang und Ende", _cp.UseG53Retract);
            Num(t, 9, "g53z", "G53-Rueckzug Maschinen-Z (mm)", _cp.G53RetractZ);
            Note(t, 10, "G53 G0 Z-1 faehrt die Z-Achse unabhaengig vom Nullpunkt an eine\n" +
                        "feste Stelle im Maschinenraum. Damit startet und endet das\n" +
                        "Programm immer an derselben sicheren Hoehe.");
            return Group("Postprozessor", t);
        }

        private GroupBox GroupActions()
        {
            TableLayoutPanel t = Table();
            _btnCalc = new Button { Text = "Werkzeugweg berechnen", Width = 344, Height = 34, Font = new Font("Segoe UI", 9.5f, FontStyle.Bold) };
            _btnCalc.Click += async (s, e) => await CalculateAsync();

            _btnShow = new Button { Text = "GCode anzeigen", Width = 168, Enabled = false };
            _btnShow.Click += (s, e) => new GCodeForm(_gcode).Show(this);

            _btnSave = new Button { Text = "GCode speichern ...", Width = 168, Enabled = false };
            _btnSave.Click += (s, e) => SaveGCode();

            FlowLayoutPanel row = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.LeftToRight, Margin = new Padding(0) };
            row.Controls.Add(_btnShow); row.Controls.Add(_btnSave);

            t.Controls.Add(_btnCalc, 0, 0); t.SetColumnSpan(_btnCalc, 2);
            t.Controls.Add(row, 0, 1); t.SetColumnSpan(row, 2);
            return Group("Ausgabe", t);
        }

        // ---------------------------------------------------------------- kleine Helfer

        private static TableLayoutPanel Table()
        {
            // Nicht docken: eine GroupBox mit AutoSize kann die Wunschgroesse eines
            // gedockten Kindes nicht ermitteln und klappt dann auf null zusammen.
            return new TableLayoutPanel
            {
                ColumnCount = 2, RowCount = 30,
                AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink,
                Margin = new Padding(0), Padding = new Padding(4),
                ColumnStyles = { new ColumnStyle(SizeType.Absolute, 244), new ColumnStyle(SizeType.Absolute, 112) }
            };
        }

        private static GroupBox Group(string title, Control inner)
        {
            GroupBox g = new GroupBox
            {
                Text = title,
                AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink,
                MinimumSize = new Size(374, 0),
                Padding = new Padding(6, 18, 6, 8),
                Margin = new Padding(0, 0, 0, 8),
                Font = new Font("Segoe UI", 9f, FontStyle.Bold)
            };
            inner.Font = new Font("Segoe UI", 9f, FontStyle.Regular);
            // Ein nicht gedocktes Kind wird vom Padding nicht verschoben - es saesse sonst
            // auf (0,0) und wuerde die Ueberschrift der GroupBox verdecken.
            inner.Location = new Point(8, 18);
            g.Controls.Add(inner);
            return g;
        }

        private void Num(TableLayoutPanel t, int row, string key, string label, double value)
        {
            Txt(t, row, key, label, value.ToString("0.####", CultureInfo.InvariantCulture));
        }

        private void Txt(TableLayoutPanel t, int row, string key, string label, string value)
        {
            t.Controls.Add(new Label { Text = label, AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(3, 6, 3, 3) }, 0, row);
            TextBox b = new TextBox { Text = value, Width = 100, TextAlign = HorizontalAlignment.Right };
            _f[key] = b;
            t.Controls.Add(b, 1, row);
        }

        private void Chk(TableLayoutPanel t, int row, string key, string label, bool value)
        {
            CheckBox c = new CheckBox { Text = label, Checked = value, AutoSize = true };
            if (key == "usezmin" || key == "autotop")
                c.CheckedChanged += (s2, e) => { FillZLimits(); UpdateStrategyFields(); UpdateLivePreview(); };
            _b[key] = c;
            t.Controls.Add(c, 0, row); t.SetColumnSpan(c, 2);
        }

        private double D(string key, double fallback)
        {
            TextBox b;
            if (!_f.TryGetValue(key, out b)) return fallback;
            string s = (b.Text ?? "").Trim().Replace(',', '.');
            double v;
            return double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out v) ? v : fallback;
        }

        private bool B(string key, bool fallback)
        {
            CheckBox c;
            return _b.TryGetValue(key, out c) ? c.Checked : fallback;
        }

        private void SetField(string key, double value)
        {
            TextBox b;
            if (_f.TryGetValue(key, out b)) b.Text = value.ToString("0.####", CultureInfo.InvariantCulture);
        }

        private void Log(string s)
        {
            _log.AppendText(s + Environment.NewLine);
        }

        private void SetAnim(int i)
        {
            _view.AnimIndex = i;
            if (_slider.Maximum >= i) _slider.Value = Math.Max(_slider.Minimum, Math.Min(_slider.Maximum, i));
        }

        // ---------------------------------------------------------------- Modell

        /// <summary>Zweites Beispiel: Freiformflaeche auf einem Block. Dazu wird gleich
        /// die passende Bahnform eingestellt - Parallelbahnen mit Z-Zustellung.</summary>
        public void LoadWavyExample() { LoadExampleWavy(); }

        private void LoadExampleWavy()
        {
            _raw = ModelGenerator.WavyBlock();
            ResetWcsFields();
            _cbStrategy.SelectedIndex = 2;
            SetField("maxz", 3);
            SetField("th1", 150);
            AfterModelLoaded("Beispiel: Block 120 x 80 mm mit welliger Freiformflaeche, "
                             + "die Flaeche ist vorgewaehlt - Bahnform auf Parallelbahnen gestellt");
        }

        private void LoadExample()
        {
            _raw = ModelGenerator.BallOnPost();
            ResetWcsFields();
            _cbStrategy.SelectedIndex = 0;
            AfterModelLoaded("Beispiel: Kugel D20 (Mitte Z=30) auf Zylinder D10 x L20, eingespannt bei X=Y=Z=A=0"
                             + " - die Kugel ist als zu bearbeitende Flaeche vorgewaehlt");
        }

        private void LoadStl(string path)
        {
            try
            {
                _raw = StlIo.Load(path);
                ResetWcsFields();
                AfterModelLoaded("STL geladen: " + path);
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, "STL konnte nicht gelesen werden:\n" + ex.Message, "Fehler",
                                MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        }

        /// <summary>Modell in das Werkstueck-Koordinatensystem bringen und alles neu aufbauen.
        /// Die Dreiecksreihenfolge bleibt dabei erhalten, deshalb ueberlebt die Flaechenauswahl
        /// jede Aenderung von Drehung und Nullpunkt.</summary>
        private void ApplyWorkpiece(bool keepSelection)
        {
            if (_raw == null) return;
            Mesh old = keepSelection ? _mesh : _raw;

            _mesh = _wp.IsIdentity ? _raw : _wp.Apply(_raw);
            if (!ReferenceEquals(_mesh, old)) _mesh.CopySelectionFrom(old);

            _mesh.BuildSmoothNormals();
            _mesh.BuildTopology();
            _grid = new TriGrid(_mesh);
            _view.Model = _mesh;
        }

        /// <summary>Ein berechneter Werkzeugweg gehoert zur alten Lage des Modells und
        /// waere nach einer Drehung falsch. Er wird deshalb verworfen.</summary>
        private void DropToolpath(string why)
        {
            if (_path == null) return;
            _timer.Stop();
            _btnPlay.Text = "Start";
            _path = null; _gcode = "";
            _view.SetToolpath(null);
            _btnSave.Enabled = false; _btnShow.Enabled = false;
            _slider.Maximum = 1; _slider.Value = 0;
            Log("Werkzeugweg verworfen: " + why);
        }

        private void AfterModelLoaded(string what)
        {
            ApplyWorkpiece(false);
            _view.SetToolpath(null);
            _view.ZoomToFit();
            _path = null; _gcode = "";
            _btnSave.Enabled = false; _btnShow.Enabled = false;
            _slider.Maximum = 1; _slider.Value = 0;

            UpdateModelInfo();
            UpdateFaceInfo();
            FillZLimits();
            Log(what);
            Log(_modelInfo.Text.Replace("\n", "  "));
        }

        private void UpdateModelInfo()
        {
            _modelInfo.Text = string.Format(CultureInfo.InvariantCulture,
                "{0}: {1} Dreiecke\nX {2:0.##}..{3:0.##}  Y {4:0.##}..{5:0.##}  Z {6:0.##}..{7:0.##}",
                _mesh.Name, _mesh.Count,
                _mesh.Bounds.Min.X, _mesh.Bounds.Max.X, _mesh.Bounds.Min.Y, _mesh.Bounds.Max.Y,
                _mesh.Bounds.Min.Z, _mesh.Bounds.Max.Z);
            if (_wcsInfo != null)
                _wcsInfo.Text = string.Format(CultureInfo.InvariantCulture,
                    "Modell liegt jetzt bei\nX {0:0.##}..{1:0.##}   Y {2:0.##}..{3:0.##}   Z {4:0.##}..{5:0.##}",
                    _mesh.Bounds.Min.X, _mesh.Bounds.Max.X, _mesh.Bounds.Min.Y, _mesh.Bounds.Max.Y,
                    _mesh.Bounds.Min.Z, _mesh.Bounds.Max.Z);
        }

        // ---------------------------------------------------------------- Werkstueck-KS

        /// <summary>Die sechs Felder, die Lage und Drehung des Modells bestimmen.</summary>
        private static readonly string[] WcsFields = { "wrx", "wry", "wrz", "wox", "woy", "woz" };

        private void ResetWcsFields()
        {
            _wp.RotXDeg = _wp.RotYDeg = _wp.RotZDeg = 0;
            _wp.Offset = Vec3.Zero;
            SetWcsFields(0, 0, 0, Vec3.Zero);
        }

        /// <summary>Felder setzen, ohne den Nachlauf erneut auszuloesen.</summary>
        private void SetWcsFields(double rx, double ry, double rz, Vec3 off)
        {
            bool was = _wcsGuard;
            _wcsGuard = true;
            try
            {
                SetField("wrx", rx); SetField("wry", ry); SetField("wrz", rz);
                SetField("wox", off.X); SetField("woy", off.Y); SetField("woz", off.Z);
            }
            finally { _wcsGuard = was; }
        }

        private void ReadWcs()
        {
            // Halb getippte Eingaben wie "-" duerfen den Wert nicht auf 0 zurueckwerfen;
            // sie behalten den zuletzt gueltigen Stand, bis die Zahl vollstaendig ist.
            _wp.RotXDeg = D("wrx", _wp.RotXDeg);
            _wp.RotYDeg = D("wry", _wp.RotYDeg);
            _wp.RotZDeg = D("wrz", _wp.RotZDeg);
            _wp.Offset = new Vec3(D("wox", _wp.Offset.X), D("woy", _wp.Offset.Y), D("woz", _wp.Offset.Z));
        }

        /// <summary>Nur neu drehen, wenn sich wirklich ein Wert geaendert hat.</summary>
        private void ApplyWcsIfChanged()
        {
            if (_raw == null) return;
            if (D("wrx", _wp.RotXDeg) == _wp.RotXDeg &&
                D("wry", _wp.RotYDeg) == _wp.RotYDeg &&
                D("wrz", _wp.RotZDeg) == _wp.RotZDeg &&
                D("wox", _wp.Offset.X) == _wp.Offset.X &&
                D("woy", _wp.Offset.Y) == _wp.Offset.Y &&
                D("woz", _wp.Offset.Z) == _wp.Offset.Z) return;
            ApplyWcsFromFields();
        }

        private void ApplyWcsFromFields()
        {
            if (_raw == null) return;
            ReadWcs();
            DropToolpath("Modell wurde gedreht oder verschoben");
            ApplyWorkpiece(true);
            UpdateModelInfo();
            UpdateFaceInfo();
            FillZLimits();
            _view.Invalidate();
        }

        /// <summary>Nullpunkt so setzen, dass der genannte Punkt des gedrehten Modells
        /// auf (0,0,0) landet.</summary>
        private void ZeroTo(string where)
        {
            if (_raw == null) return;
            ReadWcs();
            _wp.Offset = Vec3.Zero;
            Mesh rotated = _wp.Apply(_raw);
            rotated.CopySelectionFrom(_mesh);

            Aabb b = rotated.Bounds;
            Vec3 p;
            switch (where)
            {
                case "unten": p = new Vec3(b.Center.X, b.Center.Y, b.Min.Z); break;
                case "mitte": p = b.Center; break;
                case "oben":  p = new Vec3(b.Center.X, b.Center.Y, b.Max.Z); break;
                case "flaeche":
                {
                    Aabb sb; double area; Vec3 cen;
                    rotated.SelectionInfo(out sb, out area, out cen);
                    Vec3 fitC; double fitR, res;
                    p = rotated.FitSphere(out fitC, out fitR, out res) && res < 0.05 * Math.Max(fitR, 1e-9)
                        ? fitC : cen;
                    break;
                }
                default: p = Vec3.Zero; break;
            }

            SetWcsFields(_wp.RotXDeg, _wp.RotYDeg, _wp.RotZDeg, new Vec3(-p.X, -p.Y, -p.Z));
            _wcsTimer.Stop();
            ApplyWcsFromFields();
            Log(string.Format(CultureInfo.InvariantCulture,
                "Nullpunkt {0}: Modell um {1:0.###} / {2:0.###} / {3:0.###} mm verschoben",
                where, -p.X, -p.Y, -p.Z));
        }

        // ---------------------------------------------------------------- Flaechenauswahl

        private void OnFacePicked(int tri, Mesh.SelectMode mode)
        {
            if (_mesh == null) return;
            int n = _mesh.SelectRegion(tri, D("breakang", _cp.BreakAngleDeg), mode);
            Log(string.Format(CultureInfo.InvariantCulture,
                "Flaeche gewaehlt: {0} zusammenhaengende Dreiecke [{1}], Auswahl jetzt {2}",
                n, mode, _mesh.SelectedCount));
            UpdateFaceInfo();
            FillZLimits();
            AutoCenterFromSelection(true);
            _view.Invalidate();
        }

        /// <summary>Projektionszentrum aus der gewaehlten Flaeche ableiten. Ist die Flaeche
        /// kugelig, liefert die Ausgleichskugel Mittelpunkt und Radius; sonst wird nur der
        /// Flaechenschwerpunkt gesetzt und darauf hingewiesen.</summary>
        private void AutoCenterFromSelection(bool verbose)
        {
            if (_mesh == null || !_mesh.HasSelection) return;

            Vec3 c; double r, res;
            if (_mesh.FitSphere(out c, out r, out res) && res < 0.05 * Math.Max(r, 1e-9))
            {
                SetField("cx", c.X); SetField("cy", c.Y); SetField("cz", c.Z);
                SetField("bandR", r);
                if (verbose)
                    Log(string.Format(CultureInfo.InvariantCulture,
                        "Ausgleichskugel: Mittelpunkt {0}, Radius {1:0.###} mm, mittlere Abweichung " +
                        "{2:0.0000} mm - als Projektionszentrum uebernommen", c, r, res));
            }
            else
            {
                Aabb b; double area; Vec3 cen;
                _mesh.SelectionInfo(out b, out area, out cen);
                SetField("cx", cen.X); SetField("cy", cen.Y); SetField("cz", cen.Z);
                if (verbose)
                    Log("Die Flaeche ist keine Kugel - Projektionszentrum auf den Schwerpunkt "
                        + cen + " gesetzt. Bitte pruefen.");
            }
        }

        /// <summary>Zmax und Zmin aus der gewaehlten Flaeche uebernehmen, solange die
        /// Grenzen automatisch bestimmt werden. So stehen dort immer sinnvolle Zahlen,
        /// die sich von Hand weiterverwenden lassen.</summary>
        private void FillZLimits()
        {
            if (_mesh == null) return;
            Aabb b; double area; Vec3 cen;
            _mesh.SelectionInfo(out b, out area, out cen);
            if (b.IsEmpty) return;

            // Solange der jeweilige Wert nicht von Hand gesetzt ist, den sinnvollen
            // Ausgangswert aus der Flaeche eintragen.
            if (!_b.ContainsKey("usezmin") || !_b["usezmin"].Checked) SetField("zmin", b.Min.Z);
            if (!_b.ContainsKey("autotop") || _b["autotop"].Checked) SetField("stocktop", b.Max.Z);
            UpdateLivePreview();
        }

        private void UpdateFaceInfo()
        {
            if (_faceInfo == null || _mesh == null) return;
            if (!_mesh.HasSelection)
            {
                _faceInfo.Text = "Keine Auswahl - das ganze Modell gilt als bearbeitbar.";
                return;
            }
            Aabb b; double area; Vec3 cen;
            _mesh.SelectionInfo(out b, out area, out cen);
            _faceInfo.Text = string.Format(CultureInfo.InvariantCulture,
                "{0} von {1} Dreiecken, {2:0.0} mm2\nZ {3:0.##} .. {4:0.##}",
                _mesh.SelectedCount, _mesh.Count, area, b.Min.Z, b.Max.Z);
        }

        // ---------------------------------------------------------------- Berechnung

        private void ReadParameters()
        {
            _cp.Tool.Number = (int)D("toolNo", 1);
            _cp.Tool.Diameter = D("toolD", 6);
            _cp.Tool.ShankDiameter = D("shankD", 6);
            _cp.Tool.FreeLength = D("freeL", 30);
            _cp.Tool.HolderDiameter = D("holderD", 25);
            _cp.Tool.HolderLength = D("holderL", 40);
            _cp.Tool.Name = string.Format(CultureInfo.InvariantCulture, "Kugelfraeser D{0:0.###}", _cp.Tool.Diameter);

            _cp.Machine.AMinDeg = D("aMin", -90);
            _cp.Machine.AMaxDeg = D("aMax", 90);
            _cp.Machine.TableAboveA = D("tableZ", 0);
            _cp.MachineZeroAboveWork = D("mz0", 0);
            _cp.Machine.MaxAFeed = D("aFeed", 3600);
            _cp.Machine.MaxCFeed = D("cFeed", 7200);
            _cp.Machine.ASign = B("aInv", false) ? -1.0 : 1.0;
            _cp.Machine.CSign = B("cInv", false) ? -1.0 : 1.0;

            _cp.Strategy = _cbStrategy.SelectedIndex == 2 ? Strategy.ParallelBahnen
                         : _cbStrategy.SelectedIndex == 1 ? Strategy.Breitenkreise
                         : Strategy.Spirale;
            _cp.AxisMode = _cbAxisMode.SelectedIndex == 1 ? ToolAxisMode.Senkrecht : ToolAxisMode.Flaechennormale;
            _cp.RasterAngleDeg = D("raster", 0);
            _cp.ZigZag = B("zigzag", true);
            _cp.MaxZStep = D("maxz", 2);
            _cp.UseZMin = B("usezmin", false);
            _cp.ZMin = D("zmin", 0);
            _cp.AutoStockTop = B("autotop", true);
            _cp.StockTop = D("stocktop", 0);
            _cp.RoughStepoverFactor = D("roughover", 0.4);
            _cp.Center = new Vec3(D("cx", 0), D("cy", 0), D("cz", 30));
            _cp.ThetaStartDeg = D("th0", 0);
            _cp.ThetaEndDeg = D("th1", 150);
            _cp.ScallopHeight = D("scallop", 0.01);
            _cp.UseFixedStepover = B("fixover", false);
            _cp.FixedStepover = D("overmm", 0.3);
            _cp.ChordTolerance = D("chord", 0.01);
            _cp.Stock = D("stock", 0);
            _cp.BreakAngleDeg = D("breakang", 35);
            _cp.LeadAngleDeg = D("lead", 12);
            _cp.TiltAngleDeg = D("tilt", 0);
            _cp.UseRadiusBand = B("band", true);
            _cp.BandRadius = D("bandR", 10);
            _cp.BandTolerance = D("bandT", 1.5);
            _cp.ClockwiseC = B("cw", false);

            _cp.CheckCollision = B("coll", true);
            _cp.CheckHolder = B("collH", true);
            _cp.GougeTolerance = D("gouge", 0.02);
            _cp.HolderClearance = D("hclear", 0.5);

            _cp.Feed = D("feed", 1200);
            _cp.PlungeFeed = D("plunge", 300);
            _cp.Spindle = D("spindle", 12000);
            _cp.SafeZ = D("safeZ", 120);
            _cp.Clearance = D("clear", 3);
            _cp.MaxFeedOut = D("fmax", 8000);

            _cp.FeedMode = _cbFeedMode.SelectedIndex == 1 ? FeedMode.G93Inverszeit : FeedMode.G94Kompensiert;
            _cp.ProgramName = _f["progName"].Text;
            _cp.ProgramNumber = (int)D("progNo", 1001);
            _cp.LineNumbers = B("nnum", true);
            _cp.FullBlocks = B("full", true);
            _cp.AlignColumns = B("align", true);
            _cp.WithToolChange = B("tc", true);
            _cp.WithCoolant = B("cool", true);
            _cp.UseG53Retract = B("g53", true);
            _cp.G53RetractZ = D("g53z", -1);
        }

        /// <summary>Rechnen im Hintergrund, damit die Oberflaeche bedienbar bleibt.</summary>
        private async Task CalculateAsync()
        {
            if (_mesh == null || _grid == null) return;
            BeginCalculation();

            Mesh mesh = _mesh; TriGrid grid = _grid; CamParameters cp = _cp;
            DateTime t0 = DateTime.Now;
            Toolpath tp = null; string code = null; string error = null;

            await Task.Run(() => Compute(mesh, grid, cp, out tp, out code, out error));

            EndCalculation(tp, code, error, (DateTime.Now - t0).TotalSeconds);
        }

        /// <summary>Dieselbe Berechnung ohne Hintergrund-Task (Diagnosemodus).</summary>
        private void CalculateSync()
        {
            if (_mesh == null || _grid == null) return;
            BeginCalculation();

            DateTime t0 = DateTime.Now;
            Toolpath tp; string code, error;
            Compute(_mesh, _grid, _cp, out tp, out code, out error);
            EndCalculation(tp, code, error, (DateTime.Now - t0).TotalSeconds);
        }

        private static void Compute(Mesh mesh, TriGrid grid, CamParameters cp,
                                    out Toolpath tp, out string code, out string error)
        {
            tp = null; code = null; error = null;
            try
            {
                tp = ToolpathGenerator.Generate(mesh, grid, cp);
                code = new PostProcessor(cp).Build(tp, mesh.Name);
            }
            catch (Exception ex) { error = ex.ToString(); }
        }

        private void BeginCalculation()
        {
            ReadParameters();
            _btnCalc.Enabled = false;
            _btnCalc.Text = "rechnet ...";
            Cursor = Cursors.WaitCursor;
            Log("");
            Log("--- Berechnung " + DateTime.Now.ToString("HH:mm:ss") + " ---");
        }

        private void EndCalculation(Toolpath tp, string code, string error, double seconds)
        {
            Cursor = Cursors.Default;
            _btnCalc.Enabled = true;
            _btnCalc.Text = "Werkzeugweg berechnen";

            if (error != null) { Log("FEHLER: " + error); return; }

            _path = tp; _gcode = code;
            foreach (string s in tp.Log) Log("  " + s);
            Log(string.Format(CultureInfo.InvariantCulture, "  Rechenzeit {0:0.0} s, GCode {1} Zeilen",
                seconds, CountLines(code)));

            _view.Cp = _cp;
            _view.SetToolpath(tp);
            _slider.Maximum = Math.Max(1, tp.PointCount - 1);
            _slider.Value = 0;
            _btnSave.Enabled = tp.PointCount > 0;
            _btnShow.Enabled = tp.PointCount > 0;
            _view.Invalidate();
        }

        private static int CountLines(string s)
        {
            if (string.IsNullOrEmpty(s)) return 0;
            int n = 0;
            foreach (char c in s) if (c == '\n') n++;
            return n;
        }

        private void SaveGCode()
        {
            using (SaveFileDialog d = new SaveFileDialog
            {
                Filter = "GCode (*.nc)|*.nc|GCode (*.tap)|*.tap|Textdatei (*.txt)|*.txt|Alle Dateien|*.*",
                FileName = _cp.ProgramNumber.ToString(CultureInfo.InvariantCulture) + ".nc"
            })
            {
                if (d.ShowDialog(this) != DialogResult.OK) return;
                File.WriteAllText(d.FileName, _gcode, new UTF8Encoding(false));
                Log("GCode gespeichert: " + d.FileName);
            }
        }
    }

    /// <summary>Einfaches Fenster zur Anzeige des erzeugten GCodes.</summary>
    public sealed class GCodeForm : Form
    {
        public GCodeForm(string code)
        {
            Text = "GCode";
            Width = 760; Height = 800;
            StartPosition = FormStartPosition.CenterParent;
            TextBox t = new TextBox
            {
                Dock = DockStyle.Fill, Multiline = true, ReadOnly = true,
                ScrollBars = ScrollBars.Both, WordWrap = false,
                Font = new Font("Consolas", 9f), Text = code
            };
            Controls.Add(t);
        }
    }
}
