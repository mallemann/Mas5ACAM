using System;
using System.Globalization;

namespace Mas5ACAM
{
    /// <summary>Punkt bzw. Vektor im Raum (mm).</summary>
    public struct Vec3
    {
        public double X, Y, Z;

        public Vec3(double x, double y, double z) { X = x; Y = y; Z = z; }

        public static readonly Vec3 Zero  = new Vec3(0, 0, 0);
        public static readonly Vec3 UnitX = new Vec3(1, 0, 0);
        public static readonly Vec3 UnitY = new Vec3(0, 1, 0);
        public static readonly Vec3 UnitZ = new Vec3(0, 0, 1);

        public static Vec3 operator +(Vec3 a, Vec3 b) { return new Vec3(a.X + b.X, a.Y + b.Y, a.Z + b.Z); }
        public static Vec3 operator -(Vec3 a, Vec3 b) { return new Vec3(a.X - b.X, a.Y - b.Y, a.Z - b.Z); }
        public static Vec3 operator -(Vec3 a)         { return new Vec3(-a.X, -a.Y, -a.Z); }
        public static Vec3 operator *(Vec3 a, double s) { return new Vec3(a.X * s, a.Y * s, a.Z * s); }
        public static Vec3 operator *(double s, Vec3 a) { return a * s; }
        public static Vec3 operator /(Vec3 a, double s) { return new Vec3(a.X / s, a.Y / s, a.Z / s); }

        public double LengthSq { get { return X * X + Y * Y + Z * Z; } }
        public double Length   { get { return Math.Sqrt(X * X + Y * Y + Z * Z); } }

        /// <summary>Auf Länge 1 normiert. Der Nullvektor bleibt der Nullvektor.</summary>
        public Vec3 Normalized
        {
            get
            {
                double l = Length;
                return l > 1e-15 ? new Vec3(X / l, Y / l, Z / l) : Zero;
            }
        }

        public static double Dot(Vec3 a, Vec3 b) { return a.X * b.X + a.Y * b.Y + a.Z * b.Z; }

        public static Vec3 Cross(Vec3 a, Vec3 b)
        {
            return new Vec3(a.Y * b.Z - a.Z * b.Y,
                            a.Z * b.X - a.X * b.Z,
                            a.X * b.Y - a.Y * b.X);
        }

        public static double Distance(Vec3 a, Vec3 b) { return (a - b).Length; }

        public static Vec3 Min(Vec3 a, Vec3 b) { return new Vec3(Math.Min(a.X, b.X), Math.Min(a.Y, b.Y), Math.Min(a.Z, b.Z)); }
        public static Vec3 Max(Vec3 a, Vec3 b) { return new Vec3(Math.Max(a.X, b.X), Math.Max(a.Y, b.Y), Math.Max(a.Z, b.Z)); }

        /// <summary>Linear interpoliert zwischen a und b.</summary>
        public static Vec3 Lerp(Vec3 a, Vec3 b, double t) { return a + (b - a) * t; }

        /// <summary>Dreht den Vektor um eine (normierte) Achse, Rodrigues-Formel.</summary>
        public static Vec3 Rotate(Vec3 v, Vec3 axis, double angleRad)
        {
            Vec3 k = axis.Normalized;
            double c = Math.Cos(angleRad), s = Math.Sin(angleRad);
            return v * c + Cross(k, v) * s + k * (Dot(k, v) * (1.0 - c));
        }

        /// <summary>Dreht um die X-Achse (entspricht der A-Achse der Maschine).</summary>
        public static Vec3 RotX(Vec3 v, double a)
        {
            double c = Math.Cos(a), s = Math.Sin(a);
            return new Vec3(v.X, v.Y * c - v.Z * s, v.Y * s + v.Z * c);
        }

        /// <summary>Dreht um die Y-Achse.</summary>
        public static Vec3 RotY(Vec3 v, double a)
        {
            double c = Math.Cos(a), s = Math.Sin(a);
            return new Vec3(v.X * c + v.Z * s, v.Y, -v.X * s + v.Z * c);
        }

        /// <summary>Dreht um die Z-Achse (entspricht der C-Achse der Maschine).</summary>
        public static Vec3 RotZ(Vec3 v, double a)
        {
            double c = Math.Cos(a), s = Math.Sin(a);
            return new Vec3(v.X * c - v.Y * s, v.X * s + v.Y * c, v.Z);
        }

        public override string ToString()
        {
            return string.Format(CultureInfo.InvariantCulture, "({0:0.###}, {1:0.###}, {2:0.###})", X, Y, Z);
        }
    }

    /// <summary>Achsparalleler Hüllquader.</summary>
    public struct Aabb
    {
        public Vec3 Min, Max;
        public bool IsEmpty;

        public static Aabb Empty
        {
            get { return new Aabb { Min = new Vec3(double.MaxValue, double.MaxValue, double.MaxValue), Max = new Vec3(double.MinValue, double.MinValue, double.MinValue), IsEmpty = true }; }
        }

        public void Add(Vec3 p)
        {
            Min = IsEmpty ? p : Vec3.Min(Min, p);
            Max = IsEmpty ? p : Vec3.Max(Max, p);
            IsEmpty = false;
        }

        public Vec3 Size   { get { return IsEmpty ? Vec3.Zero : Max - Min; } }
        public Vec3 Center { get { return IsEmpty ? Vec3.Zero : (Min + Max) * 0.5; } }

        public Aabb Expanded(double d)
        {
            if (IsEmpty) return this;
            Vec3 e = new Vec3(d, d, d);
            return new Aabb { Min = Min - e, Max = Max + e, IsEmpty = false };
        }
    }

    public static class MathUtil
    {
        public const double Deg = Math.PI / 180.0;
        public const double Rad = 180.0 / Math.PI;

        public static double Clamp(double v, double lo, double hi) { return v < lo ? lo : (v > hi ? hi : v); }
        public static int    Clamp(int v, int lo, int hi)          { return v < lo ? lo : (v > hi ? hi : v); }

        /// <summary>Winkeldifferenz b-a, ausgewickelt auf (-180°, +180°] – in Grad.</summary>
        public static double WrapDeg180(double d)
        {
            while (d > 180.0)  d -= 360.0;
            while (d <= -180.0) d += 360.0;
            return d;
        }

        /// <summary>Liefert zu <paramref name="target"/> den Wert, der <paramref name="reference"/>
        /// am nächsten liegt und sich nur um Vielfache von 360° unterscheidet (endlose C-Achse).</summary>
        public static double Unwrap(double reference, double target)
        {
            return reference + WrapDeg180(target - reference);
        }

        /// <summary>Ein beliebiger Vektor, der senkrecht auf v steht.</summary>
        public static Vec3 AnyPerpendicular(Vec3 v)
        {
            Vec3 a = Math.Abs(v.X) < 0.9 ? Vec3.UnitX : Vec3.UnitY;
            return Vec3.Cross(v, a).Normalized;
        }
    }
}
