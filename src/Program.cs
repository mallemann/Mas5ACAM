using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.Globalization;
using System.IO;
using System.Windows.Forms;

namespace Mas5ACAM
{
    internal static class Program
    {
        [STAThread]
        private static void Main(string[] args)
        {
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);

            // Diagnosemodus: Fenster aufbauen, rechnen, als PNG ablegen, beenden.
            //   Mas5ACAM.exe --shot bild.png [modell.stl]
            int shot = Array.IndexOf(args, "--shot");
            if (shot >= 0 && shot + 1 < args.Length)
            {
                string[] rest = new string[Math.Max(0, args.Length - shot - 2)];
                Array.Copy(args, shot + 2, rest, 0, rest.Length);
                try { Screenshot(args[shot + 1], rest); }
                catch (Exception ex) { File.WriteAllText(args[shot + 1] + ".error.txt", ex.ToString()); }
                return;
            }

            Application.Run(new MainForm(args));
        }

        private static void Save(Form f, string path)
        {
            using (Bitmap bmp = new Bitmap(f.Width, f.Height))
            {
                f.DrawToBitmap(bmp, new Rectangle(0, 0, f.Width, f.Height));
                bmp.Save(path, ImageFormat.Png);
            }
        }

        private static void Screenshot(string path, string[] rest)
        {
            bool wavy = Array.IndexOf(rest, "--wavy") >= 0;
            using (MainForm f = new MainForm(wavy ? new string[0] : rest))
            {
                if (wavy) f.LoadWavyExample();

                int ti = Array.IndexOf(rest, "--tisch");
                if (ti >= 0 && ti + 1 < rest.Length)
                    f.SetTableOffset(double.Parse(rest[ti + 1], CultureInfo.InvariantCulture));

                int zi = Array.IndexOf(rest, "--zwin");
                if (zi >= 0 && zi + 2 < rest.Length)
                    f.SetZWindow(double.Parse(rest[zi + 1], CultureInfo.InvariantCulture),
                                 double.Parse(rest[zi + 2], CultureInfo.InvariantCulture));
                f.WindowState = FormWindowState.Normal;
                f.Size = new Size(1600, 1000);
                f.Show();
                Application.DoEvents();
                f.RunCalculation();

                // Rundprobe auf der Schlichtbahn - dort liegt der Beruehrpunkt wirklich
                // auf der Flaeche, bei den Schruppebenen liegt er im Material.
                f.SetView(false, 0.97);
                File.WriteAllText(path + ".diag.txt", f.DiagnosePick() + Environment.NewLine + f.DiagnoseAAxis() + Environment.NewLine + f.DiagnoseUi());
                f.SetView(false, 0.0);
                Application.DoEvents();

                Save(f, path);

                // Zweites Bild: Maschinenansicht unterhalb des Aequators, wo A an der
                // Grenze steht - dort zeigt sich, ob die Kinematik plausibel ist.
                f.SetView(true, 0.97);
                Application.DoEvents();
                File.AppendAllText(path + ".diag.txt", Environment.NewLine + "Maschinenansicht: " + f.DiagnosePick());
                Save(f, Path.Combine(Path.GetDirectoryName(Path.GetFullPath(path)) ?? ".",
                                     Path.GetFileNameWithoutExtension(path) + "_maschine.png"));

                // Drittes Bild: das Programmende - dort muss das Werkzeug zurueckgezogen sein.
                f.SetView(false, 1.0);
                Application.DoEvents();
                Save(f, Path.Combine(Path.GetDirectoryName(Path.GetFullPath(path)) ?? ".",
                                     Path.GetFileNameWithoutExtension(path) + "_ende.png"));

                // Viertes Bild: der Parameterbereich, bis zur Strategie gescrollt.
                f.SetView(false, 0.0);
                f.ScrollParameters(1120);
                Application.DoEvents();
                Save(f, Path.Combine(Path.GetDirectoryName(Path.GetFullPath(path)) ?? ".",
                                     Path.GetFileNameWithoutExtension(path) + "_parameter.png"));

                // Zum Schluss die Sofortdrehung pruefen - sie verwirft den Werkzeugweg,
                // deshalb erst, wenn alle Bilder abgelegt sind.
                File.AppendAllText(path + ".diag.txt", Environment.NewLine + f.DiagnoseLinks());

                // Zwei Bilder derselben Stelle: mit und ohne Eilgaenge.
                f.SetView(false, 0.6);
                Save(f, Path.Combine(Path.GetDirectoryName(Path.GetFullPath(path)) ?? ".",
                                     Path.GetFileNameWithoutExtension(path) + "_mit_eilgaengen.png"));
                f.SetShowLinks(false);
                Save(f, Path.Combine(Path.GetDirectoryName(Path.GetFullPath(path)) ?? ".",
                                     Path.GetFileNameWithoutExtension(path) + "_ohne_eilgaenge.png"));
                f.SetShowLinks(true);
                File.AppendAllText(path + ".diag.txt", Environment.NewLine + f.DiagnoseWcsLive());

                // Fuenftes Bild: das Modell nur durch eine Eingabe im Feld gekippt.
                f.TypeRotX("30");
                Save(f, Path.Combine(Path.GetDirectoryName(Path.GetFullPath(path)) ?? ".",
                                     Path.GetFileNameWithoutExtension(path) + "_gedreht.png"));
            }
        }
    }
}
