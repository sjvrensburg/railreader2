namespace RailReader.Core.Services;

public static class LayoutConstants
{
    public const int InputSize = 640;
    public const float ConfidenceThreshold = 0.25f;
    public const float NmsIouThreshold = 0.6f;
    public const float DarkLuminanceThreshold = 160.0f;
    public const float DensityThresholdFraction = 0.15f;
    public const int MinLineHeightPx = 3;

    public const byte LetterboxPadValue = 114;

    // YOLO-DLA runtime class table — the label per output index of the ONNX model.
    //
    // IMPORTANT: The runtime indices below do NOT match the dataset YAML
    // (AIBox-IMU/DLA YOLO-DLA dataset/doc_data.yaml). The YAML lists 16 classes
    // [t, t1, t2, t3, t4, paragraph, author, keyword, abstract, reference, graph,
    //  note, other, formula, table, footnote] but training id 4 (`t4`) had ZERO
    // instances and id 16 was also missing, so ONNX export collapsed the schema
    // and remapped output indices. The model's own metadata says:
    //
    //   names: {0:'class0', 1:'class1', 2:'class2', 3:'class3', 4:'class5',
    //           5:'class6', 6:'class7', 7:'class8', 8:'class9', 9:'class10',
    //           10:'class11', 11:'class12', 12:'class13', 13:'class14',
    //           14:'class15', 15:'class17'}
    //
    // i.e. runtime idx 4 maps to dataset id 5 (`paragraph`), and everything
    // from idx 4 upward shifts one position. Verified visually against the
    // training set: see /tmp/dla-samples/ during integration.
    //
    // Semantic meanings below were derived by rendering training annotations:
    public static readonly string[] LayoutClasses =
    [
        "t",          //  0  — rare (0.2% of training); semantics unclear
        "t1",         //  1  — top-level section heading ("4 Research Framework")
        "t2",         //  2  — subsection heading ("4.1 Technology acceptance model")
        "t3",         //  3  — sub-subsection (29 instances total; practically unused)
        "paragraph",  //  4  — BODY TEXT (51% of training — primary reading class)
        "author",     //  5  — author byline
        "keyword",    //  6  — keywords list
        "abstract",   //  7  — abstract block
        "reference",  //  8  — bibliography entry
        "graph",      //  9  — figure / image
        "note",       // 10  — figure or table CAPTION (not page furniture!)
        "other",      // 11  — page furniture: running header, page number, URL footer
        "formula",    // 12  — display equation
        "table",      // 13  — table
        "footnote",   // 14  — footnote, legal/copyright bottom block
        "class17",    // 15  — unknown (5 stray training instances; treat as noise)
    ];

    // Frequently referenced class indices (must match LayoutClasses order above).
    public const int ClassDocTitle = 1;         // t1   — top section heading
    public const int ClassParagraphTitle = 2;   // t2   — subsection heading
    public const int ClassParagraph = 4;        // paragraph — body text
    public const int ClassImage = 9;            // graph — figure
    public const int ClassNote = 10;            // caption (figure/table)
    public const int ClassOther = 11;           // page furniture
    public const int ClassFormula = 12;
    public const int ClassTable = 13;
    public const int ClassFootnote = 14;

    // Alias kept for code paths still using PP-DocLayoutV3 naming.
    public const int ClassDisplayFormula = ClassFormula;

    public static HashSet<int> DefaultNavigableClasses() =>
    [
        1,  // t1        (top heading)
        2,  // t2        (subsection heading)
        3,  // t3        (rare deeper heading)
        4,  // paragraph (BODY TEXT — primary reading class)
        5,  // author
        6,  // keyword
        7,  // abstract
        8,  // reference
        12, // formula
        14, // footnote
    ];

    /// <summary>
    /// Block types that are horizontally centered when narrower than the viewport.
    /// Excludes heading types (t1/t2/t3) which look better left-aligned, and
    /// excludes page furniture and captions.
    /// </summary>
    public static HashSet<int> DefaultCenteringClasses() =>
    [
        4,  // paragraph
        5,  // author
        6,  // keyword
        7,  // abstract
        8,  // reference
        12, // formula
        14, // footnote
    ];

    public static readonly HashSet<int> FigureClasses = [ClassImage];

    public static readonly HashSet<int> TableClasses = [ClassTable];

    public static readonly HashSet<int> EquationClasses = [ClassFormula];

    public static int? ClassNameToIndex(string name) =>
        Array.IndexOf(LayoutClasses, name) is var idx and >= 0 ? idx : null;

    public static string GetClassName(int classId) =>
        classId >= 0 && classId < LayoutClasses.Length ? LayoutClasses[classId] : "unknown";
}
