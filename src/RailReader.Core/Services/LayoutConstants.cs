namespace RailReader.Core.Services;

public static class LayoutConstants
{
    public const int InputSize = 800;
    public const float ConfidenceThreshold = 0.4f;
    public const float NmsIouThreshold = 0.5f;
    public const float DarkLuminanceThreshold = 160.0f;
    public const float DensityThresholdFraction = 0.15f;
    public const int MinLineHeightPx = 3;

    // Frequently referenced class indices (must match LayoutClasses order below)
    public const int ClassChart = 3;
    public const int ClassDisplayFormula = 5;
    public const int ClassDocTitle = 6;
    public const int ClassFooterImage = 9;
    public const int ClassHeaderImage = 13;
    public const int ClassImage = 14;
    public const int ClassParagraphTitle = 17;
    public const int ClassTable = 21;

    // PP-DocLayoutV3 official label list (25 classes, alphabetical order)
    // Source: https://huggingface.co/PaddlePaddle/PP-DocLayoutV3 inference.yml
    public static readonly string[] LayoutClasses =
    [
        "abstract",          // 0
        "algorithm",         // 1
        "aside_text",        // 2
        "chart",             // 3
        "content",           // 4
        "display_formula",   // 5
        "doc_title",         // 6
        "figure_title",      // 7
        "footer",            // 8
        "footer_image",      // 9
        "footnote",          // 10
        "formula_number",    // 11
        "header",            // 12
        "header_image",      // 13
        "image",             // 14
        "inline_formula",    // 15
        "number",            // 16
        "paragraph_title",   // 17
        "reference",         // 18
        "reference_content", // 19
        "seal",              // 20
        "table",             // 21
        "text",              // 22
        "vertical_text",     // 23
        "vision_footnote",   // 24
    ];

    public static HashSet<int> DefaultNavigableClasses() =>
    [
        0,  // abstract
        1,  // algorithm
        5,  // display_formula
        10, // footnote
        17, // paragraph_title
        22, // text
    ];

    /// <summary>
    /// Block types that are horizontally centered when narrower than the viewport.
    /// Excludes heading types (paragraph_title, doc_title) which look better left-aligned.
    /// </summary>
    public static HashSet<int> DefaultCenteringClasses() =>
    [
        0,  // abstract
        1,  // algorithm
        5,  // display_formula
        10, // footnote
        22, // text
    ];

    public static readonly HashSet<int> FigureClasses =
    [ClassChart, ClassFooterImage, ClassHeaderImage, ClassImage];

    public static readonly HashSet<int> TableClasses = [ClassTable];

    public static int? ClassNameToIndex(string name) =>
        Array.IndexOf(LayoutClasses, name) is var idx and >= 0 ? idx : null;
}
