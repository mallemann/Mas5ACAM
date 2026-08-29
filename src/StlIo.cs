using System;
using System.Globalization;
using System.IO;
using System.Text;

namespace Mas5ACAM
{
    /// <summary>Lesen und Schreiben von STL-Dateien (binär und ASCII).</summary>
    public static class StlIo
    {
        public static Mesh Load(string path)
        {
            byte[] data = File.ReadAllBytes(path);
            Mesh m = IsAscii(data) ? ParseAscii(data) : ParseBinary(data);
            m.Name = Path.GetFileNameWithoutExtension(path);
            m.RecomputeBounds();
            return m;
        }

        /// <summary>Eine binäre STL-Datei ist genau 84 + 50*n Bytes gross – das ist das
        /// verlässlichste Kriterium. "solid" am Anfang haben auch manche Binärdateien.</summary>
        private static bool IsAscii(byte[] d)
        {
            if (d.Length < 84) return true;
            uint n = BitConverter.ToUInt32(d, 80);
            long expected = 84L + 50L * n;
            if (expected == d.Length) return false;

            string head = Encoding.ASCII.GetString(d, 0, Math.Min(80, d.Length)).TrimStart();
            return head.StartsWith("solid", StringComparison.OrdinalIgnoreCase);
        }

        private static Mesh ParseBinary(byte[] d)
        {
            Mesh m = new Mesh();
            uint n = BitConverter.ToUInt32(d, 80);
            int p = 84;
            for (uint i = 0; i < n && p + 50 <= d.Length; i++, p += 50)
            {
                Vec3 a = ReadVec(d, p + 12);
                Vec3 b = ReadVec(d, p + 24);
                Vec3 c = ReadVec(d, p + 36);
                Tri t = new Tri(a, b, c);
                if (t.N.LengthSq < 0.5)                    // entartetes Dreieck: STL-Normale übernehmen
                    t.N = ReadVec(d, p).Normalized;
                m.Add(t);
            }
            return m;
        }

        private static Vec3 ReadVec(byte[] d, int o)
        {
            return new Vec3(BitConverter.ToSingle(d, o), BitConverter.ToSingle(d, o + 4), BitConverter.ToSingle(d, o + 8));
        }

        private static Mesh ParseAscii(byte[] d)
        {
            Mesh m = new Mesh();
            string text = Encoding.UTF8.GetString(d);
            string[] lines = text.Split('\n');
            Vec3[] v = new Vec3[3];
            int vi = 0;
            foreach (string raw in lines)
            {
                string line = raw.Trim();
                if (line.StartsWith("vertex", StringComparison.OrdinalIgnoreCase))
                {
                    string[] p = line.Split(new[] { ' ', '\t', '\r' }, StringSplitOptions.RemoveEmptyEntries);
                    if (p.Length < 4) continue;
                    v[vi % 3] = new Vec3(Num(p[1]), Num(p[2]), Num(p[3]));
                    vi++;
                    if (vi % 3 == 0) m.Add(new Tri(v[0], v[1], v[2]));
                }
            }
            return m;
        }

        private static double Num(string s)
        {
            double x;
            double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out x);
            return x;
        }

        public static void SaveBinary(Mesh m, string path)
        {
            using (FileStream fs = new FileStream(path, FileMode.Create, FileAccess.Write))
            using (BinaryWriter w = new BinaryWriter(fs))
            {
                byte[] header = new byte[80];
                byte[] txt = Encoding.ASCII.GetBytes("Mas5ACAM - " + m.Name);
                Array.Copy(txt, header, Math.Min(txt.Length, 79));
                w.Write(header);
                w.Write((uint)m.Tris.Count);
                foreach (Tri t in m.Tris)
                {
                    WriteVec(w, t.N); WriteVec(w, t.A); WriteVec(w, t.B); WriteVec(w, t.C);
                    w.Write((ushort)0);
                }
            }
        }

        private static void WriteVec(BinaryWriter w, Vec3 v)
        {
            w.Write((float)v.X); w.Write((float)v.Y); w.Write((float)v.Z);
        }
    }
}
