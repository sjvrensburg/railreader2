using Microsoft.Extensions.AI;
using OpenAI;
using RailReader.Core;
using RailReader.Core.Commands;
using RailReader.Core.Models;
using RailReader.Core.Services;
using RailReader.Agent;

PdfiumResolver.Initialize();

// --- Check for --capture-screenshots mode ---
if (args.Length >= 1 && args[0] == "--capture-screenshots")
{
    string outputDir = args.Length >= 2 ? args[1] : "docs/img";
    return CaptureScreenshots(outputDir);
}

// --- Configuration ---
var apiKey = Environment.GetEnvironmentVariable("OPENAI_API_KEY")
    ?? Environment.GetEnvironmentVariable("ANTHROPIC_API_KEY")
    ?? throw new InvalidOperationException(
        "Set OPENAI_API_KEY or ANTHROPIC_API_KEY environment variable");

var modelId = Environment.GetEnvironmentVariable("RAILREADER_MODEL") ?? "gpt-4o";
var baseUrl = Environment.GetEnvironmentVariable("RAILREADER_BASE_URL");

// --- Setup ---
var config = AppConfig.Load();
var controller = new DocumentController(config, new SynchronousThreadMarshaller());
controller.SetViewportSize(1200, 900); // virtual viewport for headless use

// Try to initialize the ONNX worker (optional for agent use)
try { controller.InitializeWorker(); }
catch (FileNotFoundException ex)
{
    Console.Error.WriteLine($"Warning: {ex.Message}");
    Console.Error.WriteLine("Layout analysis will not be available.");
}

var tools = new RailReaderTools(controller);

// --- Build AI tools ---
var aiTools = new List<AITool>
{
    AIFunctionFactory.Create(tools.OpenDocument),
    AIFunctionFactory.Create(tools.ListDocuments),
    AIFunctionFactory.Create(tools.GetActiveDocument),
    AIFunctionFactory.Create(tools.GoToPage),
    AIFunctionFactory.Create(tools.NextPage),
    AIFunctionFactory.Create(tools.PrevPage),
    AIFunctionFactory.Create(tools.GetPageText),
    AIFunctionFactory.Create(tools.GetLayoutInfo),
    AIFunctionFactory.Create(tools.Search),
    AIFunctionFactory.Create(tools.CloseDocument),
    AIFunctionFactory.Create(tools.SetZoom),
    AIFunctionFactory.Create(tools.AddHighlight),
    AIFunctionFactory.Create(tools.AddTextAnnotation),
    AIFunctionFactory.Create(tools.ExportPdf),
    AIFunctionFactory.Create(tools.ExportPageImage),
    AIFunctionFactory.Create(tools.WaitForAnalysis),
    AIFunctionFactory.Create(tools.SetRailPosition),
    AIFunctionFactory.Create(tools.SetColourEffect),
};

// --- Build chat client ---
var openAiClient = baseUrl is not null
    ? new OpenAIClient(new System.ClientModel.ApiKeyCredential(apiKey),
        new OpenAIClientOptions { Endpoint = new Uri(baseUrl) })
    : new OpenAIClient(apiKey);

IChatClient client = openAiClient.GetChatClient(modelId)
    .AsIChatClient()
    .AsBuilder()
    .UseFunctionInvocation()
    .Build();

// --- Get task from args or stdin ---
string task;
if (args.Length > 0)
    task = string.Join(" ", args);
else
{
    Console.Write("Enter task: ");
    task = Console.ReadLine() ?? "";
}

if (string.IsNullOrWhiteSpace(task))
{
    Console.Error.WriteLine("No task provided.");
    return 1;
}

// --- Run agent loop ---
var messages = new List<ChatMessage>
{
    new(ChatRole.System, """
        You are a PDF reading assistant with access to RailReader tools.
        You can open PDFs, navigate pages, extract text, search, annotate, and export.
        Use the tools to accomplish the user's task, then report your findings.
        """),
    new(ChatRole.User, task),
};

var options = new ChatOptions { Tools = aiTools };

var response = await client.GetResponseAsync(messages, options);
Console.WriteLine(response.Text);

// --- Cleanup ---
foreach (var doc in controller.Documents.ToList())
    doc.Dispose();

return 0;

// ============================================================
// Deterministic screenshot capture (no LLM required)
// ============================================================
static int CaptureScreenshots(string outputDir)
{
    const string attentionPath = "/home/stefan/Downloads/PDFs/NIPS-2017-attention-is-all-you-need-Paper.pdf";
    const string islrPath = "/home/stefan/Downloads/PDFs/ISLRv2_corrected_June_2023.pdf";

    if (!File.Exists(attentionPath))
    {
        Console.Error.WriteLine($"PDF not found: {attentionPath}");
        return 1;
    }
    if (!File.Exists(islrPath))
    {
        Console.Error.WriteLine($"PDF not found: {islrPath}");
        return 1;
    }

    Directory.CreateDirectory(outputDir);

    var config = AppConfig.Load();
    var controller = new DocumentController(config, new SynchronousThreadMarshaller());
    controller.SetViewportSize(1200, 900);

    try { controller.InitializeWorker(); }
    catch (FileNotFoundException ex)
    {
        Console.Error.WriteLine($"Warning: {ex.Message}");
        Console.Error.WriteLine("Layout analysis will not be available. Some screenshots will lack overlays.");
    }

    var tools = new RailReaderTools(controller);
    int captured = 0;

    // Settle the snap animation so the camera is at the target position.
    // Snap animations use wall-clock time (Stopwatch), not simulated dt,
    // so we must wait real time for the animation to complete.
    void SettleCamera()
    {
        var d = controller.ActiveDocument;
        if (d is null) return;
        // Wait for snap animation to finish (default 300ms duration)
        Thread.Sleep(400);
        // Tick to apply the final animation position
        for (int i = 0; i < 5; i++)
            controller.Tick(0.016);
        var (ww, wh) = controller.GetViewportSize();
        d.ClampCamera(ww, wh);
    }

    void Capture(string name, ScreenshotOptions opts)
    {
        var doc = controller.ActiveDocument!;
        using var bitmap = ScreenshotCompositor.RenderPage(doc, controller, opts);
        var path = Path.Combine(outputDir, name);
        ScreenshotCompositor.SavePng(bitmap, path);
        var fi = new FileInfo(path);
        Console.WriteLine($"  {name}: {bitmap.Width}x{bitmap.Height}, {fi.Length / 1024} KB");
        captured++;
    }

    // Common options for viewport-cropped rail mode screenshots.
    // Use DPI proportional to zoom so text is sharp after cropping.
    ScreenshotOptions RailScreenshotOptions(
        bool lineFocusBlur = false,
        float lineFocusBlurIntensity = 0.5f) => new()
    {
        Dpi = 450, // ~150 * 3.5 zoom, rounded to DPI tier
        RailOverlay = true,
        DebugOverlay = false,
        Annotations = false,
        SearchHighlights = false,
        LineFocusBlur = lineFocusBlur,
        LineFocusBlurIntensity = lineFocusBlurIntensity,
        SimulateViewport = true,
        ViewportWidth = 1200,
        ViewportHeight = 900,
    };

    Console.WriteLine("Capturing screenshots...");
    Console.WriteLine();

    // --- attention.pdf screenshots ---
    Console.WriteLine($"Opening {Path.GetFileName(attentionPath)}...");
    tools.OpenDocument(attentionPath);

    // 1. Full page view (no analysis)
    Console.Write("[1/8] ");
    Capture("full_page_view_no_analysis.png", new ScreenshotOptions
    {
        Dpi = 150, RailOverlay = false, DebugOverlay = false, Annotations = false, SearchHighlights = false,
    });

    // Wait for analysis
    Console.WriteLine("  Waiting for analysis...");
    tools.WaitForAnalysis(15000);

    // 2. Layout analysis debug overlay
    Console.Write("[2/8] ");
    Capture("full_page_view_with_analysis.png", new ScreenshotOptions
    {
        Dpi = 150, RailOverlay = false, DebugOverlay = true, Annotations = false, SearchHighlights = false,
    });

    // 3. Rail mode (page 1, zoomed in) — viewport-cropped
    tools.GoToPage(1);
    tools.WaitForAnalysis(15000);
    tools.SetZoom(3.5);

    var doc = controller.ActiveDocument!;
    if (doc.Rail.Active && doc.Rail.HasAnalysis)
    {
        tools.SetRailPosition(0, 2); // block 0, line 2 for visual interest
        SettleCamera();
    }

    Console.Write("[3/8] ");
    Capture("rail_mode.png", RailScreenshotOptions());

    // 4. Amber colour effect (page 0, full page)
    tools.GoToPage(0);
    tools.SetZoom(1.0);
    tools.SetColourEffect("Amber", 1.0f);
    Console.Write("[4/8] ");
    Capture("colour_effects.png", new ScreenshotOptions
    {
        Dpi = 150, RailOverlay = false, DebugOverlay = false, Annotations = false, SearchHighlights = false,
    });

    // 5. High contrast + rail mode — viewport-cropped
    tools.GoToPage(1);
    tools.WaitForAnalysis(15000);
    tools.SetZoom(3.5);
    tools.SetColourEffect("HighContrast", 1.0f);

    doc = controller.ActiveDocument!;
    if (doc.Rail.Active && doc.Rail.HasAnalysis)
    {
        tools.SetRailPosition(0, 1);
        SettleCamera();
    }

    Console.Write("[5/8] ");
    Capture("colour_effect_high_contrast.png", RailScreenshotOptions());

    // Reset colour effect and capture line focus blur while still on page 1 at zoom 3.5
    tools.SetColourEffect("None", 1.0f);

    // 8. Line focus blur — same page/zoom, just enable blur
    doc = controller.ActiveDocument!;
    if (doc.Rail.Active && doc.Rail.HasAnalysis)
    {
        tools.SetRailPosition(0, 2);
        SettleCamera();
    }

    Console.Write("[8/8] ");
    Capture("line_focus_blur.png", RailScreenshotOptions(
        lineFocusBlur: true, lineFocusBlurIntensity: 0.7f));

    // 7. Annotations — use character-level text extraction for precise placement
    tools.GoToPage(0);
    tools.WaitForAnalysis(15000);
    tools.SetZoom(1.0);

    doc = controller.ActiveDocument!;
    // Reset annotations to avoid leftovers from previous runs
    doc.Annotations = new RailReader.Core.Models.AnnotationFile
    {
        SourcePdf = Path.GetFileName(attentionPath),
    };

    // Use PdfTextService to find specific phrases and their character bounding boxes
    var pageText = PdfTextService.ExtractPageText(doc.Pdf.PdfBytes, 0);

    // Helper: find a phrase in the text and return a highlight rect from char boxes
    void HighlightPhrase(string phrase, string? noteText = null)
    {
        int idx = pageText.Text.IndexOf(phrase, StringComparison.OrdinalIgnoreCase);
        if (idx < 0 || idx + phrase.Length > pageText.CharBoxes.Count) return;

        // Get bounding box spanning all chars in the phrase
        float minX = float.MaxValue, minY = float.MaxValue;
        float maxX = float.MinValue, maxY = float.MinValue;
        for (int i = idx; i < idx + phrase.Length; i++)
        {
            var cb = pageText.CharBoxes[i];
            if (cb.Left == 0 && cb.Top == 0 && cb.Right == 0 && cb.Bottom == 0) continue;
            minX = Math.Min(minX, cb.Left);
            minY = Math.Min(minY, cb.Top);
            maxX = Math.Max(maxX, cb.Right);
            maxY = Math.Max(maxY, cb.Bottom);
        }
        if (minX >= maxX) return;

        tools.AddHighlight(0, minX, minY, maxX - minX, maxY - minY);
        if (noteText is not null)
            tools.AddTextAnnotation(0, maxX + 4, minY, noteText);
    }

    // Highlight the key sentence about the Transformer architecture
    HighlightPhrase(
        "We propose a new simple network architecture, the Transformer",
        "Introduces Transformer");

    // Highlight the key result
    HighlightPhrase(
        "achieving 28.4 BLEU on the WMT 2014 English-to-German",
        "SOTA result");

    Console.Write("[7/8] ");
    Capture("annotations.png", new ScreenshotOptions
    {
        Dpi = 150, RailOverlay = false, DebugOverlay = false, Annotations = true, SearchHighlights = false,
    });

    // Close attention.pdf
    tools.CloseDocument();

    // --- ISLRv2 screenshots ---
    try
    {
        Console.WriteLine();
        Console.WriteLine($"Opening {Path.GetFileName(islrPath)}...");
        tools.OpenDocument(islrPath);

        // 6. Search highlights
        tools.Search("regression", caseSensitive: false, regex: false);
        Console.Write("[6/8] ");
        Capture("search_highlights.png", new ScreenshotOptions
        {
            Dpi = 150, RailOverlay = false, DebugOverlay = false, Annotations = false, SearchHighlights = true,
        });

        tools.CloseDocument();
    }
    catch (Exception ex)
    {
        Console.Error.WriteLine($"  Warning: ISLRv2 screenshot failed: {ex.Message}");
        Console.Error.WriteLine("  Skipping search_highlights.png");
    }

    Console.WriteLine();
    Console.WriteLine($"Done! {captured} screenshots saved to {Path.GetFullPath(outputDir)}");

    return 0;
}
