using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Globalization;
using System.Windows.Forms;

namespace Mas5ACAM
{
    /// <summary>
    /// 3D-Vorschau auf GDI+ – eigener Software-Renderer, keine externen Pakete.
    /// Zwei Ansichten:
    ///   * Werkstückansicht: das Teil steht still, das Werkzeug kippt (CAM-Sicht).
    ///   * Maschinenansicht: das Werkzeug steht senkrecht, das Teil dreht sich mit A und C
    ///     – so, wie es die Maschine wirklich macht. Das ist die Kontrolle für den GCode.
    /// </summary>
    public sealed class Viewport3D : Control
    {
        // ---------------------------------------------------------------- Szene
        public Mesh Model;
        public Toolpath Path;
        public CamParameters Cp = new CamParameters();

        public bool MachineView;
        public bool ShowModel = true;
        public bool ShowWireframe;
        public bool ShowPath = true;

        /// <summary>Eilgaenge und Verbindungswege (orange gestrichelt). Bei einem
        /// komplexen Modell legen sie sich als Netz ueber das Teil und verdecken die
        /// Schnittbahn - dann schaltet man sie aus.</summary>
        public bool ShowLinks = true;
        public bool ShowToolAxes = true;
        public bool ShowTool = true;
        public int AxisEvery = 30;

        /// <summary>Wird bei Strg+Klick auf eine Fläche ausgelöst.</summary>
        public event Action<int, Mesh.SelectMode> FacePicked;

        public bool ShowSelection = true;

        /// <summary>Zmin/Zmax als Ebenen einblenden. Nur in der Werkstueckansicht sinnvoll:
        /// in der Maschinenansicht ist das Teil gedreht, eine waagrechte Z-Ebene waere dort
        /// schief und wuerde mehr verwirren als helfen.</summary>
        public bool ShowZLimits = true;

        // Die Animation laeuft ueber ALLE Saetze des Programms - Eilgaenge, Anfahren,
        // Schnitt, Abheben, G53. Die Schnittbahn auf der Flaeche wird daneben getrennt
        // gefuehrt, damit sie weiterhin am Beruehrpunkt gezeichnet werden kann.
        private readonly List<ClPoint> _moves = new List<ClPoint>();
        private readonly List<ClPoint> _cut = new List<ClPoint>();
        private readonly List<int> _passStart = new List<int>();
        private readonly List<int> _cutAt = new List<int>();     // Schnittpunkte bis Satz i
        private int _anim;

        public int PointCount { get { return _moves.Count; } }

        public int AnimIndex
        {
            get { return _anim; }
            set { _anim = MathUtil.Clamp(value, 0, Math.Max(0, _moves.Count - 1)); Invalidate(); }
        }

        public ClPoint? Current
        {
            get { return _moves.Count == 0 ? (ClPoint?)null : _moves[MathUtil.Clamp(_anim, 0, _moves.Count - 1)]; }
        }

        // ---------------------------------------------------------------- Kamera
        private double _yaw = -0.9, _pitch = 0.42, _dist = 140;
        private Vec3 _target = new Vec3(0, 0, 20);
        private Point _lastMouse;
        private MouseButtons _drag = MouseButtons.None;

        public Viewport3D()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint |
                     ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            BackColor = Color.FromArgb(26, 28, 32);
        }

        public void SetToolpath(Toolpath tp)
        {
            Path = tp;
            _moves.Clear(); _cut.Clear(); _passStart.Clear(); _cutAt.Clear();
            if (tp != null)
            {
                foreach (Pass p in tp.Passes)
                {
                    _passStart.Add(_cut.Count);
                    _cut.AddRange(p.Points);
                }

                // Ohne Postprozessorlauf gibt es noch keine Satzliste - dann wenigstens
                // die Schnittpunkte animieren.
                if (tp.Moves.Count > 0) _moves.AddRange(tp.Moves);
                else _moves.AddRange(_cut);

                int cut = 0;
                foreach (ClPoint m in _moves)
                {
                    if (m.Type == MoveType.Feed) cut++;
                    _cutAt.Add(cut);
                }
            }
            _anim = 0;
            Invalidate();
        }

        /// <summary>Kamera so setzen, dass das Modell formatfüllend sichtbar ist.</summary>
        public void ZoomToFit()
        {
            if (Model != null && !Model.Bounds.IsEmpty)
            {
                Aabb b = Model.Bounds;
                // Eingeblendete Grenzebenen gehoeren mit ins Bild - sonst liegt eine
                // ueber dem Modell gesetzte Zmax-Ebene ausserhalb der Ansicht.
                if (ShowZLimits && !MachineView && Cp != null)
                {
                    if (Cp.UseZMin) b.Add(new Vec3(b.Center.X, b.Center.Y, Cp.ZMin));
                    if (!Cp.AutoStockTop) b.Add(new Vec3(b.Center.X, b.Center.Y, Cp.StockTop));
                }
                // Die A-Achse liegt unter dem Werkstueck - sie soll mit ins Bild
                if (Cp != null && Cp.Machine.TableAboveA > 1e-9)
                    b.Add(new Vec3(b.Center.X, b.Center.Y, -Cp.Machine.TableAboveA));

                _target = b.Center;
                // Abstand aus dem halben Durchmesser und dem Oeffnungswinkel, nicht als
                // Faustformel - sonst liegt ein flaches, breites Teil viel zu weit weg.
                double radius = b.Size.Length * 0.5;
                _dist = MathUtil.Clamp(radius / Math.Tan(28.0 * MathUtil.Deg) * 1.15, 5, 5000);
            }
            Invalidate();
        }

        // ---------------------------------------------------------------- Maus
        protected override void OnMouseDown(MouseEventArgs e)
        {
            Focus();
            // Strg+Klick waehlt eine Flaeche, statt die Ansicht zu drehen.
            if (e.Button == MouseButtons.Left && (ModifierKeys & Keys.Control) == Keys.Control)
            {
                Mesh.SelectMode mode = Mesh.SelectMode.Ersetzen;
                if ((ModifierKeys & Keys.Shift) == Keys.Shift) mode = Mesh.SelectMode.Hinzufuegen;
                else if ((ModifierKeys & Keys.Alt) == Keys.Alt) mode = Mesh.SelectMode.Entfernen;

                int tri = PickTriangle(e.Location);
                if (tri >= 0 && FacePicked != null) FacePicked(tri, mode);
                base.OnMouseDown(e);
                return;
            }
            _drag = e.Button; _lastMouse = e.Location;
            base.OnMouseDown(e);
        }

        /// <summary>
        /// Dreieck unter dem Mauszeiger suchen. Aus der Bildschirmposition wird der
        /// Sichtstrahl zurueckgerechnet; in der Maschinenansicht wird er zusaetzlich in
        /// das Werkstueck-System zurueckgedreht, damit auch dort gewaehlt werden kann.
        /// </summary>
        public int PickTriangle(Point screen)
        {
            if (Model == null || Model.Count == 0) return -1;
            PrepareCamera();

            Vec3 dir = (_r * ((screen.X - _cx) / _focal)
                      + _u * ((_cy - screen.Y) / _focal)
                      + _f).Normalized;
            Vec3 orig = _eye;

            if (MachineView)
            {
                ClPoint? cur = Current;
                double a = cur.HasValue ? cur.Value.A : 0.0;
                double c = cur.HasValue ? cur.Value.C : 0.0;
                orig = Cp.Machine.Inverse(orig, a, c);
                dir = Vec3.RotZ(Vec3.RotX(dir, -a * MathUtil.Deg), -c * MathUtil.Deg).Normalized;
            }

            double best = double.MaxValue;
            int bestTri = -1;
            for (int i = 0; i < Model.Tris.Count; i++)
            {
                double t;
                if (TriMath.RayHit(orig, dir, Model.Tris[i], out t) && t < best) { best = t; bestTri = i; }
            }
            return bestTri;
        }
        protected override void OnMouseUp(MouseEventArgs e)   { _drag = MouseButtons.None; base.OnMouseUp(e); }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            int dx = e.X - _lastMouse.X, dy = e.Y - _lastMouse.Y;
            _lastMouse = e.Location;
            if (_drag == MouseButtons.Left)
            {
                _yaw += dx * 0.008;
                _pitch = MathUtil.Clamp(_pitch + dy * 0.008, -1.53, 1.53);
                Invalidate();
            }
            else if (_drag == MouseButtons.Right || _drag == MouseButtons.Middle)
            {
                Vec3 r, u, f;
                Basis(out r, out u, out f);
                double k = _dist * 0.0016;
                _target = _target - r * (dx * k) + u * (dy * k);
                Invalidate();
            }
            base.OnMouseMove(e);
        }

        protected override void OnMouseWheel(MouseEventArgs e)
        {
            _dist = MathUtil.Clamp(_dist * (e.Delta > 0 ? 0.88 : 1.136), 5, 5000);
            Invalidate();
            base.OnMouseWheel(e);
        }

        // ---------------------------------------------------------------- Projektion
        private void Basis(out Vec3 right, out Vec3 up, out Vec3 fwd)
        {
            double cp = Math.Cos(_pitch), sp = Math.Sin(_pitch);
            fwd = new Vec3(Math.Cos(_yaw) * cp, Math.Sin(_yaw) * cp, -sp).Normalized;
            right = Vec3.Cross(fwd, Vec3.UnitZ).Normalized;
            if (right.LengthSq < 0.5) right = Vec3.UnitX;
            up = Vec3.Cross(right, fwd).Normalized;
        }

        private Vec3 _eye, _r, _u, _f;
        private double _focal, _cx, _cy;

        private void PrepareCamera()
        {
            Basis(out _r, out _u, out _f);
            _eye = _target - _f * _dist;
            _cx = Width * 0.5; _cy = Height * 0.5;
            _focal = (Height * 0.5) / Math.Tan(28.0 * MathUtil.Deg);
        }

        /// <summary>Weltpunkt (Werkstueck-KS) -> Bildschirmpunkt. Fuer die Rundprobe der
        /// Klick-Rueckrechnung: was hier hinprojiziert wird, muss PickTriangle dort finden.</summary>
        public bool TryProject(Vec3 p, out PointF screen)
        {
            PrepareCamera();
            double a = 0, c = 0;
            ClPoint? cur = Current;
            if (MachineView && cur.HasValue) { a = cur.Value.A; c = cur.Value.C; }
            double z;
            return Project(Xf(p, a, c), out screen, out z);
        }

        /// <summary>Weltpunkt -> Bildschirm. z ist die Tiefe (Kameraabstand).</summary>
        private bool Project(Vec3 p, out PointF s, out double z)
        {
            Vec3 d = p - _eye;
            z = Vec3.Dot(d, _f);
            s = PointF.Empty;
            if (z < 0.5) return false;
            double k = _focal / z;
            s = new PointF((float)(_cx + Vec3.Dot(d, _r) * k), (float)(_cy - Vec3.Dot(d, _u) * k));
            return true;
        }

        /// <summary>Transformation Werkstück -> dargestelltes Koordinatensystem.</summary>
        private Vec3 Xf(Vec3 p, double a, double c)
        {
            return MachineView ? Cp.Machine.Forward(p, a, c) : p;
        }

        private Vec3 XfDir(Vec3 v, double a, double c)
        {
            return MachineView ? Cp.Machine.ForwardDir(v, a, c) : v;
        }

        // ---------------------------------------------------------------- Zeichnen
        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.Clear(BackColor);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            if (Width < 20 || Height < 20) return;

            PrepareCamera();

            double a = 0, c = 0;
            ClPoint? cur = Current;
            if (MachineView && cur.HasValue) { a = cur.Value.A; c = cur.Value.C; }

            DrawTable(g, a, c);
            if (ShowModel && Model != null) DrawModel(g, a, c);
            if (ShowPath) DrawPath(g, a, c);
            if (ShowToolAxes) DrawToolAxes(g, a, c);
            if (ShowZLimits && !MachineView && Cp != null) DrawZLimits(g);
            if (ShowTool && cur.HasValue) DrawTool(g, cur.Value, a, c);
            DrawHud(g, cur);
        }

        /// <summary>Tisch bzw. Drehachsen als Orientierungshilfe.</summary>
        private void DrawTable(Graphics g, double a, double c)
        {
            using (Pen grid = new Pen(Color.FromArgb(52, 56, 64)))
            using (Pen axisX = new Pen(Color.FromArgb(200, 90, 90), 1.6f))
            using (Pen axisY = new Pen(Color.FromArgb(110, 190, 110), 1.6f))
            using (Pen axisZ = new Pen(Color.FromArgb(110, 150, 230), 1.6f))
            {
                // Achsenlaenge an das Modell anpassen, damit die Beschriftung nicht im
                // Werkstueck verschwindet.
                double ax = 30, az = 30, r = 45;
                if (Model != null && !Model.Bounds.IsEmpty)
                {
                    Vec3 sz = Model.Bounds.Size;
                    ax = Math.Max(20.0, Math.Max(sz.X, sz.Y) * 0.9);
                    az = Math.Max(20.0, Model.Bounds.Max.Z * 1.25 + 6.0);
                    r = Math.Max(30.0, Math.Max(sz.X, sz.Y) * 2.2);
                }
                for (int i = 0; i <= 12; i++)
                {
                    double t = -r + 2 * r * i / 12.0;
                    Seg(g, grid, Xf(new Vec3(t, -r, 0), a, c), Xf(new Vec3(t, r, 0), a, c));
                    Seg(g, grid, Xf(new Vec3(-r, t, 0), a, c), Xf(new Vec3(r, t, 0), a, c));
                }
                Seg(g, axisX, Xf(Vec3.Zero, a, c), Xf(new Vec3(ax, 0, 0), a, c));
                Seg(g, axisY, Xf(Vec3.Zero, a, c), Xf(new Vec3(0, ax, 0), a, c));
                Seg(g, axisZ, Xf(Vec3.Zero, a, c), Xf(new Vec3(0, 0, az), a, c));

                // Werkstueck-Nullpunkt und Achsen beschriften - das ist die Antwort auf
                // die Frage, wo X, Y und Z liegen.
                AxisLabel(g, new Vec3(ax + 3, 0, 0), a, c, "X", Color.FromArgb(235, 130, 130));
                AxisLabel(g, new Vec3(0, ax + 3, 0), a, c, "Y", Color.FromArgb(140, 220, 140));
                AxisLabel(g, new Vec3(0, 0, az + 3), a, c, "Z (C-Achse)", Color.FromArgb(150, 185, 250));
                AxisLabel(g, new Vec3(0, 0, 0), a, c, "0", Color.FromArgb(225, 225, 235));
            }

            DrawAAxis(g, a, c);
        }

        /// <summary>
        /// Die A-Achse als Schwenkachse der Maschine.
        ///
        /// Sie ist <b>maschinenfest</b> und wird deshalb bewusst <i>nicht</i> durch
        /// <see cref="Xf"/> geschickt: sie liegt immer waagrecht in X, um den Tischversatz
        /// unter dem Werkstück-Nullpunkt. Damit lässt sich in der Maschinenansicht direkt
        /// sehen, ob das Werkstück wirklich um diese Achse schwenkt und nicht um die
        /// X-Achse durch den Nullpunkt.
        ///
        /// Die dünne Linie vom Nullpunkt zur Achse ist der Schwenkradius – sie zeigt beim
        /// Abspielen, wie der Nullpunkt um die A-Achse ausholt.
        /// </summary>
        private void DrawAAxis(Graphics g, double a, double c)
        {
            double h = Cp != null ? Cp.Machine.TableAboveA : 0.0;
            double len = 40;
            if (Model != null && !Model.Bounds.IsEmpty)
                len = Math.Max(30.0, Math.Max(Model.Bounds.Size.X, Model.Bounds.Size.Y) * 1.3);

            Color col = Color.FromArgb(210, 140, 235);
            Vec3 p0 = new Vec3(-len, 0, -h), p1 = new Vec3(len, 0, -h);

            using (Pen pen = new Pen(col, 1.8f) { DashStyle = DashStyle.Dash })
                Seg(g, pen, p0, p1);

            // Schwenkradius: vom aktuellen Werkstueck-Nullpunkt senkrecht auf die A-Achse
            if (h > 1e-9)
            {
                Vec3 zero = Xf(Vec3.Zero, a, c);
                using (Pen thin = new Pen(Color.FromArgb(130, col), 1f) { DashStyle = DashStyle.Dot })
                    Seg(g, thin, zero, new Vec3(zero.X, 0, -h));

                PointF sm;
                double zz;
                if (Project(new Vec3(zero.X, 0, -h), out sm, out zz))
                    using (Font f = new Font("Segoe UI", 8f))
                    using (SolidBrush br = new SolidBrush(Color.FromArgb(180, col)))
                        g.DrawString(h.ToString("0.###", CultureInfo.InvariantCulture) + " mm",
                                     f, br, sm.X + 4, sm.Y + 2);
            }

            PointF s;
            double z;
            if (Project(p1, out s, out z))
                using (Font f = new Font("Segoe UI", 8.5f, FontStyle.Bold))
                using (SolidBrush br = new SolidBrush(col))
                    g.DrawString("A-Achse (Schwenkachse)", f, br, s.X + 4, s.Y - 6);
        }

        private void AxisLabel(Graphics g, Vec3 p, double a, double c, string text, Color col)
        {
            PointF s; double z;
            if (!Project(Xf(p, a, c), out s, out z)) return;
            using (Font f = new Font("Segoe UI", 8f, FontStyle.Bold))
            using (SolidBrush b = new SolidBrush(col))
                g.DrawString(text, f, b, s.X + 3, s.Y - 6);
        }

        private struct Facet { public PointF[] Pts; public double Z; public Color Col; }

        private void DrawModel(Graphics g, double a, double c)
        {
            List<Facet> list = new List<Facet>(Model.Count / 2 + 16);
            Vec3 light = new Vec3(0.35, -0.55, 0.76).Normalized;

            for (int i = 0; i < Model.Tris.Count; i++)
            {
                Tri t = Model.Tris[i];
                Vec3 pa = Xf(t.A, a, c), pb = Xf(t.B, a, c), pc = Xf(t.C, a, c);
                Vec3 n = XfDir(t.N, a, c);

                // Rückseiten weglassen
                if (Vec3.Dot(n, (pa - _eye)) > 0) continue;

                PointF s0, s1, s2; double z0, z1, z2;
                if (!Project(pa, out s0, out z0) || !Project(pb, out s1, out z1) || !Project(pc, out s2, out z2)) continue;

                double sh = MathUtil.Clamp(Math.Abs(Vec3.Dot(n, light)), 0, 1);
                int v = (int)(48 + 165 * (0.25 + 0.75 * sh));
                bool sel = ShowSelection && Model.HasSelection && Model.Selected[i];
                Facet f = new Facet
                {
                    Pts = new[] { s0, s1, s2 },
                    Z = (z0 + z1 + z2) / 3.0,
                    // Gewaehlte Flaeche warm, Aufspannung neutral grau
                    Col = sel ? Color.FromArgb((int)(v * 0.55), (int)(v * 0.86), v)
                              : Color.FromArgb(v, (int)(v * 0.94), (int)(v * 0.86))
                };
                list.Add(f);
            }

            list.Sort((p, q) => q.Z.CompareTo(p.Z));         // hinten zuerst
            foreach (Facet f in list)
                using (SolidBrush b = new SolidBrush(f.Col))
                    g.FillPolygon(b, f.Pts);

            if (ShowWireframe)
                using (Pen p = new Pen(Color.FromArgb(70, 0, 0, 0)))
                    foreach (Facet f in list) g.DrawPolygon(p, f.Pts);
        }

        private void DrawPath(Graphics g, double a, double c)
        {
            int reached = _cutAt.Count > 0 ? _cutAt[MathUtil.Clamp(_anim, 0, _cutAt.Count - 1)] : 0;

            // Schnittbahn am Beruehrpunkt
            if (_cut.Count >= 2)
                using (Pen done = new Pen(Color.FromArgb(255, 90, 210, 255), 1.7f))
                using (Pen todo = new Pen(Color.FromArgb(120, 70, 120, 150), 1.0f))
                    for (int pi = 0; pi < _passStart.Count; pi++)
                    {
                        int from = _passStart[pi];
                        int to = pi + 1 < _passStart.Count ? _passStart[pi + 1] : _cut.Count;
                        DrawPolyline(g, from, to, a, c, done, todo, reached);
                    }

            // Verbindungen und Eilgaenge an der Werkzeugspitze - gestrichelt, damit sie
            // sich von der Schnittbahn unterscheiden.
            if (ShowLinks && _moves.Count >= 2 && Path != null && Path.Moves.Count > 0)
                using (Pen rapid = new Pen(Color.FromArgb(200, 255, 170, 70), 1.2f) { DashStyle = DashStyle.Dash })
                {
                    PointF prev = PointF.Empty; bool have = false;
                    for (int i = 0; i < _moves.Count; i++)
                    {
                        PointF sp; double z;
                        if (!Project(Xf(_moves[i].Tip, a, c), out sp, out z)) { have = false; continue; }
                        if (have && _moves[i].Type != MoveType.Feed) g.DrawLine(rapid, prev, sp);
                        prev = sp; have = true;
                    }
                }
        }

        private void DrawPolyline(Graphics g, int from, int to, double a, double c,
                                  Pen done, Pen todo, int reached)
        {
            PointF prev = PointF.Empty; bool have = false;
            for (int i = from; i < to; i++)
            {
                Vec3 p = Xf(_cut[i].Contact, a, c);
                PointF s; double z;
                if (!Project(p, out s, out z)) { have = false; continue; }
                if (have) g.DrawLine(i <= reached ? done : todo, prev, s);
                prev = s; have = true;
            }
        }

        private void DrawToolAxes(Graphics g, double a, double c)
        {
            if (_cut.Count == 0) return;
// Pfeillaenge und Dichte an das Modell anpassen: bei 15000 Bahnpunkten
            // ergaeben feste Werte nur noch einen Rasen.
            double len = 14.0;
            if (Model != null && !Model.Bounds.IsEmpty)
                len = MathUtil.Clamp(Model.Bounds.Size.Length * 0.10, 3.0, 40.0);
            int step = Math.Max(AxisEvery, _cut.Count / 220);
            using (Pen pen = new Pen(Color.FromArgb(150, 255, 176, 70), 1.0f))
            using (Pen clamped = new Pen(Color.FromArgb(190, 255, 96, 96), 1.2f))
            {
                for (int i = 0; i < _cut.Count; i += step)
                {
                    ClPoint p = _cut[i];
                    Vec3 p0 = Xf(p.Contact, a, c);
                    Vec3 p1 = Xf(p.Contact + p.Axis * len, a, c);
                    Seg(g, p.AxisClamped ? clamped : pen, p0, p1);
                }
            }
        }

        /// <summary>
        /// Die beiden Grenzebenen in der Ausgangsposition. Sie zeigen, welcher Hoehenbereich
        /// der Flaeche bearbeitet wird - zum Nachpruefen der eingegebenen Werte.
        /// </summary>
        private void DrawZLimits(Graphics g)
        {
            if (Model == null || Model.Bounds.IsEmpty) return;

            Aabb b = Model.Bounds;
            double m = Math.Max(b.Size.X, b.Size.Y) * 0.12 + 4.0;
            double x0 = b.Min.X - m, x1 = b.Max.X + m;
            double y0 = b.Min.Y - m, y1 = b.Max.Y + m;

            // Rohteil-Oberkante nur zeigen, wo sie etwas bedeutet: bei den Parallelbahnen
            // und wenn sie von Hand ueber die Flaeche gesetzt wurde.
            if (!Cp.AutoStockTop && Cp.Strategy == Strategy.ParallelBahnen)
                Plane(g, x0, x1, y0, y1, Cp.StockTop, Color.FromArgb(120, 210, 190),
                      "Rohteil-Oberkante " + Cp.StockTop.ToString("0.###", CultureInfo.InvariantCulture));

            if (Cp.UseZMin)
                Plane(g, x0, x1, y0, y1, Cp.ZMin, Color.FromArgb(255, 150, 90),
                      "Zmin " + Cp.ZMin.ToString("0.###", CultureInfo.InvariantCulture));
        }

        private void Plane(Graphics g, double x0, double x1, double y0, double y1, double z,
                           Color col, string text)
        {
            Vec3[] corners =
            {
                new Vec3(x0, y0, z), new Vec3(x1, y0, z), new Vec3(x1, y1, z), new Vec3(x0, y1, z)
            };
            PointF[] pts = new PointF[4];
            for (int i = 0; i < 4; i++)
            {
                double zz;
                if (!Project(corners[i], out pts[i], out zz)) return;
            }

            using (SolidBrush fill = new SolidBrush(Color.FromArgb(38, col)))
            using (Pen edge = new Pen(Color.FromArgb(220, col), 1.6f))
            {
                g.FillPolygon(fill, pts);
                g.DrawPolygon(edge, pts);
            }

            // Gitterlinien, damit die Ebene auch von der Seite als Flaeche lesbar bleibt
            using (Pen thin = new Pen(Color.FromArgb(90, col), 1f))
                for (int i = 1; i < 6; i++)
                {
                    double t = i / 6.0;
                    Seg(g, thin, new Vec3(x0 + (x1 - x0) * t, y0, z), new Vec3(x0 + (x1 - x0) * t, y1, z));
                    Seg(g, thin, new Vec3(x0, y0 + (y1 - y0) * t, z), new Vec3(x1, y0 + (y1 - y0) * t, z));
                }

            using (Font f = new Font("Segoe UI", 9f, FontStyle.Bold))
            using (SolidBrush br = new SolidBrush(col))
                g.DrawString(text, f, br, pts[1].X + 6, pts[1].Y - 8);
        }

        private void DrawTool(Graphics g, ClPoint p, double a, double c)
        {
            double R = Cp.Tool.Radius, L = Cp.Tool.FreeLength;
            Vec3 center = Xf(p.Center, a, c);
            Vec3 axis = XfDir(p.Axis, a, c);
            Vec3 top = Xf(p.Center + p.Axis * L, a, c);

            PointF sc, st; double zc, zt;
            if (!Project(center, out sc, out zc) || !Project(top, out st, out zt)) return;

            // Bildschirmradius aus einem senkrecht zur Achse versetzten Punkt ableiten
            Vec3 perp = Vec3.Cross(axis, _f).Normalized;
            if (perp.LengthSq < 0.5) perp = _r;
            PointF se; double ze;
            if (!Project(center + perp * R, out se, out ze)) return;
            float rp = (float)Math.Max(1.5, Math.Sqrt((se.X - sc.X) * (se.X - sc.X) + (se.Y - sc.Y) * (se.Y - sc.Y)));

            float ux = st.X - sc.X, uy = st.Y - sc.Y;
            float ul = (float)Math.Sqrt(ux * ux + uy * uy);
            if (ul > 0.001f) { ux /= ul; uy /= ul; } else { ux = 0; uy = -1; }
            float nx = -uy, ny = ux;
            float rt = rp * (float)(Cp.Tool.ShankRadius / Math.Max(R, 1e-6));

            using (SolidBrush shank = new SolidBrush(Color.FromArgb(215, 205, 210, 220)))
            using (SolidBrush ball = new SolidBrush(Color.FromArgb(235, 250, 205, 110)))
            using (Pen edge = new Pen(Color.FromArgb(200, 40, 44, 52), 1f))
            {
                PointF[] body =
                {
                    new PointF(sc.X + nx * rp, sc.Y + ny * rp),
                    new PointF(st.X + nx * rt, st.Y + ny * rt),
                    new PointF(st.X - nx * rt, st.Y - ny * rt),
                    new PointF(sc.X - nx * rp, sc.Y - ny * rp)
                };
                g.FillPolygon(shank, body);
                g.DrawPolygon(edge, body);
                g.FillEllipse(ball, sc.X - rp, sc.Y - rp, rp * 2, rp * 2);
                g.DrawEllipse(edge, sc.X - rp, sc.Y - rp, rp * 2, rp * 2);
            }

            // Berührpunkt markieren
            PointF sk; double zk;
            if (Project(Xf(p.Contact, a, c), out sk, out zk))
                using (SolidBrush b = new SolidBrush(Color.FromArgb(255, 255, 80, 80)))
                    g.FillEllipse(b, sk.X - 2.5f, sk.Y - 2.5f, 5, 5);
        }

        private void Seg(Graphics g, Pen pen, Vec3 a, Vec3 b)
        {
            PointF s0, s1; double z0, z1;
            if (Project(a, out s0, out z0) && Project(b, out s1, out z1)) g.DrawLine(pen, s0, s1);
        }

        private void DrawHud(Graphics g, ClPoint? cur)
        {
            string mode = MachineView ? "Maschinenansicht (Teil dreht, Werkzeug senkrecht)"
                                      : "Werkstueckansicht (Teil steht, Werkzeug kippt)";
            var lines = new List<string> { mode };

            if (cur.HasValue)
            {
                ClPoint p = cur.Value;
                string art = p.Type == MoveType.Feed ? "Schnitt"
                           : p.Type == MoveType.Rapid ? "Eilgang"
                           : p.Type == MoveType.Plunge ? "Eintauchen" : "Rueckzug";
                if (p.ZUnknown) art += " G53 (Hoehe im Werkstuecksystem unbekannt)";
                lines.Add(string.Format(CultureInfo.InvariantCulture,
                    "Satz {0}/{1}   {2}", _anim + 1, _moves.Count, art));
                lines.Add(string.Format(CultureInfo.InvariantCulture,
                    "Maschine  X {0,8:0.000}  Y {1,8:0.000}  Z {2,8:0.000}", p.Machine.X, p.Machine.Y, p.Machine.Z));
                lines.Add(string.Format(CultureInfo.InvariantCulture,
                    "          A {0,8:0.000}  C {1,8:0.000}{2}", p.A, p.C, p.AxisClamped ? "   [A-Grenze]" : ""));
                lines.Add(string.Format(CultureInfo.InvariantCulture,
                    "Beruehrpunkt {0}   F {1:0}", p.Contact, p.Feed));
            }
            else lines.Add("Noch keine Bahn berechnet.");

            using (Font f = new Font("Consolas", 8.5f))
            using (SolidBrush bg = new SolidBrush(Color.FromArgb(150, 0, 0, 0)))
            using (SolidBrush fg = new SolidBrush(Color.FromArgb(230, 235, 240)))
            {
                float y = 8, h = f.GetHeight(g) + 1;
                g.FillRectangle(bg, 4, 4, 430, h * lines.Count + 8);
                foreach (string s in lines) { g.DrawString(s, f, fg, 10, y); y += h; }
            }

            // Legende
            using (Font f = new Font("Segoe UI", 8f))
            using (SolidBrush fg = new SolidBrush(Color.FromArgb(170, 180, 195)))
                g.DrawString("Links ziehen = drehen | rechts ziehen = schieben | Rad = zoomen     " +
                             "Strg+Klick = Flaeche waehlen | +Umschalt = dazu | +Alt = weg",
                             f, fg, 10, Height - 20);
        }
    }
}
