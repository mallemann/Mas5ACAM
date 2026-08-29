using System;

namespace Mas5ACAM
{
    public enum Strategy
    {
        /// <summary>Durchgehende Spirale vom Pol zum Äquator (echte 5-Achs-Simultanbahn).</summary>
        Spirale,
        /// <summary>Einzelne Breitenkreise mit Zustellung dazwischen.</summary>
        Breitenkreise,
        /// <summary>Paralleles Raster, von oben in Z auf die Fläche projiziert.
        /// Die Wahl für Freiformflächen auf einem Block. Nur diese Strategie kennt
        /// die Z-Zustelltiefe und damit das Schruppen in Ebenen.</summary>
        ParallelBahnen
    }

    public enum ToolAxisMode
    {
        /// <summary>Werkzeugachse folgt der Flächennormale (echtes 5-Achs-Simultanfräsen).</summary>
        Flaechennormale,
        /// <summary>Werkzeug bleibt senkrecht, A und C stehen still. Für flache Flächen,
        /// die keine Anstellung brauchen – spart der Maschine viel Drehbewegung.</summary>
        Senkrecht
    }

    public enum FeedMode
    {
        /// <summary>G94 mm/min, F pro Satz so umgerechnet, dass am Werkstück der Sollvorschub ankommt.</summary>
        G94Kompensiert,
        /// <summary>G93 Inverszeit – die Steuerung braucht keine Kinematikkenntnis.</summary>
        G93Inverszeit
    }

    /// <summary>Werkzeugbeschreibung: Kugelfräser mit Schaft und Halter.</summary>
    public sealed class Tool
    {
        public string Name = "Kugelfraeser D6";
        public int Number = 1;
        public double Diameter = 6.0;        // Kugeldurchmesser
        public double ShankDiameter = 6.0;   // Schaftdurchmesser
        public double FreeLength = 30.0;     // freie Länge ab Kugelmittelpunkt
        public double HolderDiameter = 25.0;
        public double HolderLength = 40.0;

        public double Radius { get { return Diameter * 0.5; } }
        public double ShankRadius { get { return ShankDiameter * 0.5; } }
        public double HolderRadius { get { return HolderDiameter * 0.5; } }
    }

    /// <summary>Alle Eingabewerte der Bahnberechnung.</summary>
    public sealed class CamParameters
    {
        public Tool Tool = new Tool();
        public Machine5Axis Machine = new Machine5Axis();
        public Strategy Strategy = Strategy.Spirale;

        // --- Strategie: Kugelkoordinaten-Projektion um einen Zentrumspunkt -------------
        public Vec3 Center = new Vec3(0, 0, 30);   // Projektionszentrum = Kugelmittelpunkt
        public double ThetaStartDeg = 0.0;         // 0° = Nordpol
        public double ThetaEndDeg = 150.0;         // >90° = unterhalb des Äquators
        public bool ClockwiseC = false;            // Drehsinn der Spirale

        // --- Parallelbahnen (Z-Projektion) ------------------------------------------------
        public double RasterAngleDeg = 0.0;        // 0 = Bahnen entlang X, 90 = entlang Y
        public bool ZigZag = true;                 // abwechselnde Richtung, mit direkter Verbindung

        /// <summary>Maximale Z-Zustelltiefe je Schnitt. Liegt mehr Material über der Fläche,
        /// wird in so vielen Ebenen geschruppt, dass keine tiefer als dieser Wert zustellt.
        /// 0 schaltet das Schruppen ab (nur Schlichtbahn).</summary>
        public double MaxZStep = 2.0;

        /// <summary>
        /// Untere Bearbeitungsgrenze, gemessen in der <b>Ausgangsposition</b> des Werkstücks.
        ///
        /// <para>Ist sie eingeschaltet, werden nur Berührpunkte mit z ≥ Zmin angefahren.
        /// Beispiel: Kugel als Fläche gewählt, Zmin auf Kugelmitte – dann wird nur die obere
        /// Halbkugel gefräst, die untere bleibt unberührt.</para>
        ///
        /// <para>Es ist ausdrücklich <b>keine</b> Schranke für das Werkzeug: an einer steilen
        /// Flanke hängt die Fräserkugel zwangsläufig unter ihrem Berührpunkt. Sie darf das
        /// auch, sonst liesse sich die Flanke gar nicht schlichten.</para>
        ///
        /// <para>Gilt für alle Bahnformen.</para>
        /// </summary>
        public bool UseZMin = false;
        public double ZMin = 0.0;

        /// <summary>
        /// Rohteil-Oberkante – nur für die Parallelbahnen.
        ///
        /// <para>Sie sagt, ab welcher Höhe Material ansteht. Automatisch ist das der höchste
        /// Punkt der gewählten Fläche; dann gibt es nichts abzutragen, was über der Fläche
        /// liegt. Trägt man einen höheren Wert ein, steht dort Rohmaterial – daraus entstehen
        /// zusätzliche Schruppebenen, und die Bearbeitung beginnt entsprechend höher.</para>
        ///
        /// <para>Eine <i>obere</i> Bearbeitungsgrenze gibt es bewusst nicht: sie wäre beim
        /// Fräsen von oben ohne Nutzen. Was oben nicht bearbeitet werden soll, gehört nicht
        /// in die gewählte Fläche.</para>
        /// </summary>
        public bool AutoStockTop = true;
        public double StockTop = 0.0;

        /// <summary>Seitliche Zustellung beim Schruppen als Anteil des Werkzeugdurchmessers.
        /// Geschlichtet wird dagegen nach der Restmaterialhöhe.</summary>
        public double RoughStepoverFactor = 0.4;

        // --- Genauigkeit ---------------------------------------------------------------
        public double ScallopHeight = 0.01;        // Restmaterialhöhe zwischen den Bahnen, mm

        /// <summary>
        /// Bahnabstand beim Schlichten direkt vorgeben, statt ihn aus der Restmaterialhöhe
        /// zu rechnen.
        ///
        /// <para>Normalerweise ergibt sich der Abstand aus dem Scallop: auf ebener Fläche
        /// gilt h = R − √(R² − (s/2)²), also s = 2·√(2·R·h − h²). Bei R3 und h = 0,01 mm
        /// sind das 0,489 mm. Wer den Abstand aus anderen Gründen festlegen will
        /// (Oberflächenbild, Taktzeit), schaltet hier um.</para>
        /// </summary>
        public bool UseFixedStepover = false;
        public double FixedStepover = 0.3;
        public double ChordTolerance = 0.01;       // Sehnenfehler entlang der Bahn, mm
        public double MinAngStepDeg = 0.10;
        public double MaxAngStepDeg = 4.0;
        public double Stock = 0.0;                 // Aufmass auf der Fläche, mm

        // --- Flächenfilter --------------------------------------------------------------
        // Die eigentliche Eingrenzung macht die Flächenauswahl am Modell. Das Radiusband
        // ist nur noch ein zusätzliches Sieb für Fälle ohne Auswahl.
        public bool UseRadiusBand = false;         // nur Treffer im erwarteten Radiusband verwenden
        public double BandRadius = 10.0;
        public double BandTolerance = 1.5;

        // --- Werkzeuganstellung ----------------------------------------------------------
        /// <summary>Wie die Werkzeugachse ausgerichtet wird.</summary>
        public ToolAxisMode AxisMode = ToolAxisMode.Flaechennormale;

        /// <summary>Knickwinkel, an dem die Flächenauswahl aufhört zu wachsen.</summary>
        public double BreakAngleDeg = 35.0;

        public double LeadAngleDeg = 12.0;         // Voreilung in Bahnrichtung (weg vom toten Zentrum)
        public double TiltAngleDeg = 0.0;          // seitliche Neigung

        // --- Kollision ---------------------------------------------------------------------
        public bool CheckCollision = true;
        public double GougeTolerance = 0.02;       // erlaubte Unterschreitung am Fräser, mm
        public double HolderClearance = 0.50;      // Mindestluft Halter/Schaft zum Modell, mm
        public bool CheckHolder = true;

        // --- Technologie ----------------------------------------------------------------------
        public double Feed = 1200;                 // Sollvorschub am Werkstück, mm/min
        public double PlungeFeed = 300;
        public double RapidFeed = 5000;            // nur zur Zeitschätzung
        public double Spindle = 12000;
        public double MaxFeedOut = 8000;           // Begrenzung des ausgegebenen F-Werts
        public double MinFeedOut = 5;

        // --- Sicherheit / An- und Abfahren ------------------------------------------------------
        public double SafeZ = 120.0;               // Rückzugsebene in Maschinen-Z, mm
        public double Clearance = 3.0;             // Abhebeweg entlang der Werkzeugachse, mm
        public double ApproachLength = 2.0;        // davon im Vorschub angefahren, mm

        // --- Postprozessor ------------------------------------------------------------------------
        // G93 ist die Voreinstellung: Bei einem rotationssymmetrisch auf dem C-Tisch
        // aufgespannten Teil bewegen sich die Linearachsen kaum, die Bewegung kommt aus
        // der C-Drehung. Ein F-Wert in mm/min beschreibt das nicht - die Inverszeit schon.
        public FeedMode FeedMode = FeedMode.G93Inverszeit;
        public string ProgramName = "5ACAM";
        public int ProgramNumber = 1001;
        /// <summary>
        /// Rückzug in Maschinenkoordinaten am Programmanfang und -ende.
        ///
        /// <para><c>G53 G0 Z-1</c> fährt die Z-Achse unabhängig von jedem Nullpunkt an eine
        /// feste Stelle im Maschinenraum. So beginnt und endet das Programm immer an
        /// derselben sicheren Höhe – auch wenn der Werkstücknullpunkt beim letzten Lauf
        /// woanders lag.</para>
        ///
        /// <para>G53 ist satzweise wirksam. Danach weiss der Postprozessor nicht mehr, wo
        /// die Z-Achse im Werkstücksystem steht, und schreibt den nächsten Z-Wert wieder
        /// ausdrücklich aus.</para>
        /// </summary>
        public bool UseG53Retract = true;
        public double G53RetractZ = -1.0;

        /// <summary>
        /// Wie weit Maschinen-Z0 über dem Werkstück-Nullpunkt liegt (mm).
        ///
        /// <para>Wird nur für die <b>Anzeige</b> gebraucht: ohne diesen Wert weiss die App
        /// nicht, wo <c>G53 Z-1</c> im Werkstück-Koordinatensystem liegt, und kann den
        /// Rückzug in der Animation nicht an die richtige Stelle setzen. 0 heisst
        /// „unbekannt" – dann zeigt die Animation stattdessen die Rückzugsebene und sagt
        /// das auch. Auf den erzeugten GCode hat der Wert keinen Einfluss.</para>
        /// </summary>
        public double MachineZeroAboveWork = 0.0;

        public bool WithToolChange = true;
        public bool WithCoolant = true;
        public int Decimals = 3;
        public int AngleDecimals = 3;
        public bool LineNumbers = true;

        /// <summary>Vollstaendige Saetze: alle Achsen und der Vorschub stehen in jeder Zeile,
        /// auch wenn sich ein Wert nicht geaendert hat. Macht den GCode Zeile fuer Zeile
        /// lesbar. Aus = modal, nur geaenderte Worte (kuerzere Datei).</summary>
        public bool FullBlocks = true;

        /// <summary>Achsworte auf feste Spaltenbreiten auffuellen, damit die Zahlen
        /// untereinander stehen. Aufgefuellt wird hinter der Zahl, nie zwischen
        /// Adressbuchstabe und Zahl.</summary>
        public bool AlignColumns = true;
    }
}
