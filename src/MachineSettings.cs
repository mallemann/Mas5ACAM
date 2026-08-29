using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;

namespace Mas5ACAM
{
    /// <summary>
    /// Die Maschinendaten überdauern die Sitzung.
    ///
    /// <para>Achsgrenzen, Tischversatz und Drehrichtungen beschreiben die Maschine, nicht
    /// das Werkstück – sie bei jedem Start neu einzutippen wäre nicht nur lästig, sondern
    /// auch eine Fehlerquelle. Sie liegen deshalb in einer schlichten Textdatei unter
    /// <c>%AppData%\Mas5ACAM\maschine.cfg</c>, eine Zeile je Wert.</para>
    ///
    /// <para>Geladen wird beim Start automatisch, geschrieben nur auf Knopfdruck. So
    /// überschreibt ein Versuch mit anderen Werten nicht stillschweigend die eingefahrene
    /// Einstellung.</para>
    /// </summary>
    public static class MachineSettings
    {
        public static string FilePath
        {
            get
            {
                string dir = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Mas5ACAM");
                return Path.Combine(dir, "maschine.cfg");
            }
        }

        public static bool Exists { get { return File.Exists(FilePath); } }

        public static void Save(Dictionary<string, string> values)
        {
            string path = FilePath;
            Directory.CreateDirectory(Path.GetDirectoryName(path));

            StringBuilder b = new StringBuilder();
            b.AppendLine("# Mas5ACAM - Maschinendaten");
            b.AppendLine("# Geschrieben " + DateTime.Now.ToString("yyyy-MM-dd HH:mm",
                                                                  CultureInfo.InvariantCulture));
            b.AppendLine("# Eine Zeile je Wert, Dezimalpunkt.");
            foreach (var kv in values) b.Append(kv.Key).Append('=').AppendLine(kv.Value);

            File.WriteAllText(path, b.ToString(), new UTF8Encoding(false));
        }

        public static Dictionary<string, string> Load()
        {
            var result = new Dictionary<string, string>();
            string path = FilePath;
            if (!File.Exists(path)) return result;

            foreach (string raw in File.ReadAllLines(path))
            {
                string line = raw.Trim();
                if (line.Length == 0 || line.StartsWith("#")) continue;
                int eq = line.IndexOf('=');
                if (eq <= 0) continue;
                result[line.Substring(0, eq).Trim()] = line.Substring(eq + 1).Trim();
            }
            return result;
        }
    }
}
