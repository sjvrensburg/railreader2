using System.Text.Json;
using System.Text.RegularExpressions;
using RailReader.Core;
using RailReader.Core.Models;
using RailReader.Core.Services;
using RailReader.Renderer.Skia;

namespace RailReader.Cli.Commands;

public static class VlmCommand
{
    public static int Execute(string[] args, IPdfServiceFactory factory, ILogger logger)
    {
        if (Program.HasFlag(args, "help") || Program.HasFlag(args, "-h"))
        {
            PrintHelp();
            return 0;
        }

        var pdfPath = Program.GetRequiredPdf(args);
        var outputPath = Program.GetOption(args, "output");
        var classesOpt = Program.GetOption(args, "classes");
        var all = Program.HasFlag(args, "all");
        var pageRange = Program.GetOption(args, "pages");
        var singlePageStr = Program.GetOption(args, "page");
        var singleBlockStr = Program.GetOption(args, "block");
        var dpiStr = Program.GetOption(args, "dpi");
        var endpointOverride = Program.GetOption(args, "endpoint");
        var modelOverride = Program.GetOption(args, "model");
        var apiKeyOverride = Program.GetOption(args, "api-key");
        var eqEndpoint = Program.GetOption(args, "equation-endpoint");
        var eqModel = Program.GetOption(args, "equation-model");
        var eqApiKey = Program.GetOption(args, "equation-api-key");
        var tblEndpoint = Program.GetOption(args, "table-endpoint");
        var tblModel = Program.GetOption(args, "table-model");
        var tblApiKey = Program.GetOption(args, "table-api-key");
        var figEndpoint = Program.GetOption(args, "figure-endpoint");
        var figModel = Program.GetOption(args, "figure-model");
        var figApiKey = Program.GetOption(args, "figure-api-key");
        var promptStyleStr = Program.GetOption(args, "prompt-style");
        var concurrencyStr = Program.GetOption(args, "concurrency");
        var dumpCrops = Program.GetOption(args, "dump-crops");
        var minConfStr = Program.GetOption(args, "min-confidence");
        var fromStructure = Program.GetOption(args, "from-structure");
        var noHtmlToMd = Program.HasFlag(args, "no-html-to-md");

        // Parse numeric options
        int dpi = 300;
        if (dpiStr != null && int.TryParse(dpiStr, out var d))
        {
            dpi = Math.Clamp(d, 72, 1200);
            if (d != dpi) Console.Error.WriteLine($"Warning: DPI clamped to {dpi} (valid range: 72-1200)");
        }

        int concurrency = 1;
        if (concurrencyStr != null && int.TryParse(concurrencyStr, out var c))
            concurrency = Math.Max(1, c);

        float minConfidence = 0f;
        if (minConfStr != null && float.TryParse(minConfStr, out var mc))
            minConfidence = Math.Clamp(mc, 0f, 1f);

        // Class filter
        var wantedClasses = ResolveClasses(classesOpt, all);
        if (wantedClasses.Count == 0)
            return Program.Fail("No classes selected. Use --classes equation,table,figure or --all.");

        // Prompt style
        var promptStyle = VlmService.PromptStyle.Instruction;
        if (promptStyleStr != null)
        {
            if (!Enum.TryParse<VlmService.PromptStyle>(promptStyleStr, ignoreCase: true, out promptStyle))
                return Program.Fail($"Invalid --prompt-style: {promptStyleStr} (expected: instruction, ocr)");
        }

        // Load base config + apply default overrides.
        // API key precedence: --api-key > OPENAI_API_KEY env var > AppConfig.
        var baseConfig = AppConfig.Load();
        if (endpointOverride != null) baseConfig.VlmEndpoint = endpointOverride;
        if (modelOverride != null) baseConfig.VlmModel = modelOverride;
        if (apiKeyOverride != null)
            baseConfig.VlmApiKey = apiKeyOverride;
        else if (string.IsNullOrWhiteSpace(baseConfig.VlmApiKey))
        {
            var envKey = Environment.GetEnvironmentVariable("OPENAI_API_KEY");
            if (!string.IsNullOrWhiteSpace(envKey)) baseConfig.VlmApiKey = envKey;
        }

        // Per-class resolved configs
        var eqCfg = MakeClassConfig(baseConfig, eqEndpoint, eqModel, eqApiKey);
        var tblCfg = MakeClassConfig(baseConfig, tblEndpoint, tblModel, tblApiKey);
        var figCfg = MakeClassConfig(baseConfig, figEndpoint, figModel, figApiKey);

        bool anyEndpoint =
            !string.IsNullOrWhiteSpace(eqCfg.VlmEndpoint) ||
            !string.IsNullOrWhiteSpace(tblCfg.VlmEndpoint) ||
            !string.IsNullOrWhiteSpace(figCfg.VlmEndpoint);

        bool dryRun = dumpCrops != null && !anyEndpoint;
        if (dryRun)
            Console.Error.WriteLine("VLM endpoint not configured — running in dry mode (crops only, no API calls).");
        else if (!anyEndpoint)
            return Program.Fail("VLM endpoint not configured. Use --endpoint or set it in Settings, or pass --dump-crops for a dry run.");

        var pdf = factory.CreatePdfService(pdfPath);

        // Build list of (pageIdx, blockIdx, block) tuples to process
        List<(int Page, int BlockIdx, LayoutBlock Block, double PageW, double PageH)> targets;
        try
        {
            targets = BuildTargets(pdf, pageRange, singlePageStr, singleBlockStr,
                fromStructure, wantedClasses, minConfidence);
        }
        catch (Exception ex)
        {
            return Program.Fail(ex.Message);
        }

        if (targets.Count == 0)
        {
            Console.Error.WriteLine("No blocks matched the selection.");
            var empty = new VlmOutput
            {
                Source = Path.GetFileName(pdfPath),
                Model = baseConfig.VlmModel ?? "",
            };
            WriteJson(empty, outputPath);
            return 0;
        }

        Console.Error.WriteLine($"Processing {targets.Count} blocks...");

        if (dumpCrops != null)
            Directory.CreateDirectory(dumpCrops);

        // Render all crops sequentially (PDFium is single-threaded), then fan
        // out the VLM requests concurrently.
        var prepared = new List<(int Page, int BlockIdx, LayoutBlock Block, byte[]? Png)>();
        foreach (var t in targets)
        {
            try
            {
                var png = BlockCropRenderer.RenderBlockAsPng(
                    pdf, t.Page, t.Block.BBox, t.PageW, t.PageH, dpi);
                prepared.Add((t.Page, t.BlockIdx, t.Block, png));

                if (png != null && dumpCrops != null)
                {
                    var cropName = $"page{t.Page + 1:D4}_block{t.BlockIdx:D2}_{LayoutConstants.LayoutClasses[t.Block.ClassId]}.png";
                    File.WriteAllBytes(Path.Combine(dumpCrops, cropName), png);
                }
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"  Error rendering page {t.Page + 1} block {t.BlockIdx}: {ex.Message}");
                prepared.Add((t.Page, t.BlockIdx, t.Block, null));
            }
        }

        // Dispatch VLM calls with bounded concurrency
        var results = new List<VlmBlockResult>(prepared.Count);
        if (!dryRun)
        {
            using var gate = new SemaphoreSlim(concurrency);
            var tasks = new List<Task<VlmBlockResult>>(prepared.Count);
            int done = 0;
            int total = prepared.Count;

            foreach (var p in prepared)
            {
                tasks.Add(Task.Run(async () =>
                {
                    await gate.WaitAsync();
                    try
                    {
                        var action = GetAction(p.Block.ClassId);
                        var classCfg = PickConfig(action, eqCfg, tblCfg, figCfg);

                        VlmService.VlmResult result;
                        if (p.Png == null)
                            result = new VlmService.VlmResult(null, "Failed to render crop");
                        else if (string.IsNullOrWhiteSpace(classCfg.VlmEndpoint))
                            result = new VlmService.VlmResult(null, $"No endpoint configured for {action}");
                        else
                            result = await VlmService.DescribeBlockAsync(p.Png, action, classCfg, promptStyle);

                        // Post-process: HTML tables → Markdown
                        if (!noHtmlToMd && action == VlmService.BlockAction.Markdown
                            && result.Text != null && LooksLikeHtmlTable(result.Text))
                        {
                            var md = HtmlTableToMarkdown(result.Text);
                            if (md != null) result = new VlmService.VlmResult(md, result.Error);
                        }

                        var cur = Interlocked.Increment(ref done);
                        Console.Error.WriteLine($"  [{cur}/{total}] page {p.Page + 1} block {p.BlockIdx} ({LayoutConstants.LayoutClasses[p.Block.ClassId]}) — {(result.Error ?? "ok")}");

                        return BuildResult(p.Page, p.BlockIdx, p.Block, action, result, classCfg.VlmModel);
                    }
                    finally
                    {
                        gate.Release();
                    }
                }));
            }

            Task.WhenAll(tasks).GetAwaiter().GetResult();
            results.AddRange(tasks.Select(t => t.Result));
        }
        else
        {
            // Dry run — record metadata only
            foreach (var p in prepared)
            {
                var action = GetAction(p.Block.ClassId);
                results.Add(BuildResult(p.Page, p.BlockIdx, p.Block, action,
                    new VlmService.VlmResult(null, p.Png == null ? "Failed to render crop" : "dry run"),
                    PickConfig(action, eqCfg, tblCfg, figCfg).VlmModel));
            }
        }

        // Group by page
        var output = new VlmOutput
        {
            Source = Path.GetFileName(pdfPath),
            Model = baseConfig.VlmModel ?? "",
        };
        foreach (var grp in results.GroupBy(r => r.Page).OrderBy(g => g.Key))
        {
            output.Pages.Add(new VlmPage
            {
                Page = grp.Key,
                Blocks = grp.OrderBy(r => r.Index).ToList(),
            });
        }

        WriteJson(output, outputPath);

        int errors = results.Count(r => r.Error != null && r.Error != "dry run");
        return errors > 0 ? 1 : 0;
    }

    static List<(int Page, int BlockIdx, LayoutBlock Block, double PageW, double PageH)> BuildTargets(
        IPdfService pdf, string? pageRange, string? singlePageStr, string? singleBlockStr,
        string? fromStructure, HashSet<int> wantedClasses, float minConfidence)
    {
        var result = new List<(int, int, LayoutBlock, double, double)>();

        // Single block mode: --page N --block M
        int? singlePage = null, singleBlock = null;
        if (singlePageStr != null)
        {
            if (!int.TryParse(singlePageStr, out var p) || p < 1 || p > pdf.PageCount)
                throw new InvalidOperationException($"Invalid --page: {singlePageStr}");
            singlePage = p - 1;
        }
        if (singleBlockStr != null)
        {
            if (!int.TryParse(singleBlockStr, out var b) || b < 0)
                throw new InvalidOperationException($"Invalid --block: {singleBlockStr}");
            singleBlock = b;
        }

        List<int> pages;
        if (singlePage.HasValue)
        {
            pages = [singlePage.Value];
        }
        else
        {
            var (parsed, err) = PageRangeParser.Parse(pageRange, pdf.PageCount);
            if (err != null) throw new InvalidOperationException(err);
            pages = parsed!;
        }

        // Path 1: reuse existing structure JSON
        if (fromStructure != null)
        {
            if (!File.Exists(fromStructure))
                throw new FileNotFoundException($"Structure file not found: {fromStructure}");

            var json = File.ReadAllText(fromStructure);
            var structure = JsonSerializer.Deserialize<StructureOutput>(json, Shared.JsonOptions)
                ?? throw new InvalidOperationException("Failed to parse structure JSON");

            var pageSet = pages.ToHashSet();
            foreach (var sp in structure.Pages)
            {
                if (!pageSet.Contains(sp.Page)) continue;

                for (int i = 0; i < sp.Blocks.Count; i++)
                {
                    var sb = sp.Blocks[i];
                    if (!wantedClasses.Contains(sb.ClassId)) continue;
                    if (sb.Confidence < minConfidence) continue;
                    if (singleBlock.HasValue && i != singleBlock.Value) continue;

                    var lb = new LayoutBlock
                    {
                        BBox = new BBox(sb.BBox.X, sb.BBox.Y, sb.BBox.W, sb.BBox.H),
                        ClassId = sb.ClassId,
                        Confidence = sb.Confidence,
                        Order = sb.ReadingOrder,
                    };
                    result.Add((sp.Page, i, lb, sp.Width, sp.Height));
                }
            }
            return result;
        }

        // Path 2: run inline layout analysis
        using var analyzer = Shared.CreateAnalyzer(true);
        if (analyzer == null)
            throw new InvalidOperationException("ONNX model not available — cannot run layout analysis. Use --from-structure or install the model.");

        foreach (var pageIdx in pages)
        {
            Console.Error.WriteLine($"  Analyzing page {pageIdx + 1}/{pdf.PageCount}...");
            var (pw, ph) = pdf.GetPageSize(pageIdx);
            var (rgbBytes, pxW, pxH) = pdf.RenderPagePixmap(pageIdx, 800);
            var analysis = analyzer.RunAnalysis(rgbBytes, pxW, pxH, pw, ph);

            for (int i = 0; i < analysis.Blocks.Count; i++)
            {
                var b = analysis.Blocks[i];
                if (!wantedClasses.Contains(b.ClassId)) continue;
                if (b.Confidence < minConfidence) continue;
                if (singleBlock.HasValue && i != singleBlock.Value) continue;

                result.Add((pageIdx, i, b, pw, ph));
            }
        }
        return result;
    }

    static HashSet<int> ResolveClasses(string? classesOpt, bool all)
    {
        var set = new HashSet<int>();
        if (all || (classesOpt != null && classesOpt.Contains("equation")))
        {
            set.Add(LayoutConstants.ClassDisplayFormula);
            set.Add(15); // inline_formula
            set.Add(11); // formula_number
            set.Add(1);  // algorithm
        }
        if (all || (classesOpt != null && classesOpt.Contains("table")))
        {
            set.Add(LayoutConstants.ClassTable);
        }
        if (all || (classesOpt != null && classesOpt.Contains("figure")))
        {
            set.Add(LayoutConstants.ClassImage);
            set.Add(LayoutConstants.ClassChart);
            set.Add(LayoutConstants.ClassFooterImage);
            set.Add(LayoutConstants.ClassHeaderImage);
        }
        return set;
    }

    static VlmService.BlockAction GetAction(int classId)
    {
        if (classId == LayoutConstants.ClassTable)
            return VlmService.BlockAction.Markdown;
        if (LayoutConstants.FigureClasses.Contains(classId))
            return VlmService.BlockAction.Description;
        return VlmService.BlockAction.LaTeX;
    }

    static VlmBlockResult BuildResult(int page, int blockIdx, LayoutBlock block,
        VlmService.BlockAction action, VlmService.VlmResult result, string? model)
    {
        return new VlmBlockResult
        {
            Page = page,
            Index = blockIdx,
            Class = LayoutConstants.LayoutClasses[block.ClassId],
            ClassId = block.ClassId,
            BBox = new BBoxOutput(block.BBox.X, block.BBox.Y, block.BBox.W, block.BBox.H),
            Confidence = block.Confidence,
            Action = action.ToString(),
            Model = model,
            Text = result.Text,
            Error = result.Error,
        };
    }

    static AppConfig MakeClassConfig(AppConfig baseCfg, string? endpoint, string? model, string? apiKey)
    {
        return new AppConfig
        {
            VlmEndpoint = endpoint ?? baseCfg.VlmEndpoint,
            VlmModel = model ?? baseCfg.VlmModel,
            VlmApiKey = apiKey ?? baseCfg.VlmApiKey,
        };
    }

    static AppConfig PickConfig(VlmService.BlockAction action, AppConfig eq, AppConfig tbl, AppConfig fig) =>
        action switch
        {
            VlmService.BlockAction.Markdown => tbl,
            VlmService.BlockAction.Description => fig,
            _ => eq,
        };

    static bool LooksLikeHtmlTable(string text)
    {
        var t = text.TrimStart();
        return t.StartsWith("<table", StringComparison.OrdinalIgnoreCase)
            || t.StartsWith("<tr", StringComparison.OrdinalIgnoreCase)
            || (t.Contains("<tr", StringComparison.OrdinalIgnoreCase)
                && t.Contains("<td", StringComparison.OrdinalIgnoreCase));
    }

    private static readonly Regex TrRegex = new(@"<tr\b[^>]*>(?<body>.*?)</tr\s*>",
        RegexOptions.IgnoreCase | RegexOptions.Singleline);
    private static readonly Regex CellRegex = new(@"<(?<tag>th|td)\b[^>]*>(?<body>.*?)</\k<tag>\s*>",
        RegexOptions.IgnoreCase | RegexOptions.Singleline);
    private static readonly Regex TagStripRegex = new(@"<[^>]+>", RegexOptions.Singleline);
    private static readonly Regex WhitespaceRegex = new(@"\s+", RegexOptions.Singleline);

    /// <summary>
    /// Best-effort HTML table → Markdown pipe table conversion. Drops rowspan/colspan
    /// semantics; flattens all cells in each row. Returns null if no rows found.
    /// </summary>
    static string? HtmlTableToMarkdown(string html)
    {
        var rows = new List<List<string>>();
        foreach (Match trMatch in TrRegex.Matches(html))
        {
            var cells = new List<string>();
            foreach (Match cellMatch in CellRegex.Matches(trMatch.Groups["body"].Value))
            {
                var raw = cellMatch.Groups["body"].Value;
                var stripped = TagStripRegex.Replace(raw, " ");
                stripped = System.Net.WebUtility.HtmlDecode(stripped);
                stripped = WhitespaceRegex.Replace(stripped, " ").Trim();
                stripped = stripped.Replace("|", "\\|");
                cells.Add(stripped);
            }
            if (cells.Count > 0) rows.Add(cells);
        }
        if (rows.Count == 0) return null;

        int width = rows.Max(r => r.Count);
        var sb = new System.Text.StringBuilder();
        for (int i = 0; i < rows.Count; i++)
        {
            while (rows[i].Count < width) rows[i].Add("");
            sb.Append("| ").Append(string.Join(" | ", rows[i])).AppendLine(" |");
            if (i == 0)
            {
                sb.Append('|');
                for (int c = 0; c < width; c++) sb.Append(" --- |");
                sb.AppendLine();
            }
        }
        return sb.ToString().TrimEnd();
    }

    static void WriteJson(VlmOutput output, string? outputPath)
    {
        var json = JsonSerializer.Serialize(output, Shared.JsonOptions);
        if (outputPath != null)
        {
            var dir = Path.GetDirectoryName(outputPath);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            File.WriteAllText(outputPath, json);
            Console.Error.WriteLine($"VLM results written to {Path.GetFullPath(outputPath)}");
        }
        else
        {
            Console.WriteLine(json);
        }
    }

    static void PrintHelp()
    {
        Console.WriteLine("railreader2-cli vlm — Transcribe layout blocks via a vision LLM");
        Console.WriteLine();
        Console.WriteLine("Usage: railreader2-cli vlm <pdf> [options]");
        Console.WriteLine();
        Console.WriteLine("Selection:");
        Console.WriteLine("  --classes <list>      Comma-separated: equation,table,figure");
        Console.WriteLine("  --all                 Process all three classes");
        Console.WriteLine("  --pages <range>       Page range (e.g. 1,3,5-10)");
        Console.WriteLine("  --page <n>            Single page (1-based)");
        Console.WriteLine("  --block <i>           Single block index within the page (0-based)");
        Console.WriteLine("  --min-confidence <f>  Skip blocks below this detection confidence (0-1)");
        Console.WriteLine();
        Console.WriteLine("Pipeline:");
        Console.WriteLine("  --from-structure <p>  Reuse an existing structure JSON (skip ONNX pass)");
        Console.WriteLine("  --dpi <n>             Crop render DPI (default 300)");
        Console.WriteLine("  --concurrency <n>     Parallel VLM requests (default 1)");
        Console.WriteLine("  --dump-crops <dir>    Write PNG crops to disk");
        Console.WriteLine();
        Console.WriteLine("VLM config (override AppConfig):");
        Console.WriteLine("  --endpoint <url>            OpenAI-compatible endpoint (default for all classes)");
        Console.WriteLine("  --model <name>              Model identifier (default for all classes)");
        Console.WriteLine("  --api-key <key>             API key (else $OPENAI_API_KEY, else AppConfig)");
        Console.WriteLine("  --prompt-style <style>      instruction (default) | ocr");
        Console.WriteLine();
        Console.WriteLine("Per-class overrides (fall back to defaults above):");
        Console.WriteLine("  --equation-endpoint <url>   --equation-model <name>   --equation-api-key <key>");
        Console.WriteLine("  --table-endpoint <url>      --table-model <name>      --table-api-key <key>");
        Console.WriteLine("  --figure-endpoint <url>     --figure-model <name>     --figure-api-key <key>");
        Console.WriteLine();
        Console.WriteLine("Post-processing:");
        Console.WriteLine("  --no-html-to-md             Keep HTML tables as-is (default: convert to Markdown)");
        Console.WriteLine();
        Console.WriteLine("Output:");
        Console.WriteLine("  --output <path>             JSON output file (default: stdout)");
        Console.WriteLine();
        Console.WriteLine("Class mapping:");
        Console.WriteLine("  equation → display_formula, inline_formula, formula_number, algorithm → LaTeX");
        Console.WriteLine("  table    → table                                                  → Markdown");
        Console.WriteLine("  figure   → image, chart, footer_image, header_image               → Description");
    }
}

public class VlmOutput
{
    public string Source { get; set; } = "";
    public string Model { get; set; } = "";
    public List<VlmPage> Pages { get; set; } = [];
}

public class VlmPage
{
    public int Page { get; set; }
    public List<VlmBlockResult> Blocks { get; set; } = [];
}

public class VlmBlockResult
{
    public int Page { get; set; }
    public int Index { get; set; }
    public string Class { get; set; } = "";
    public int ClassId { get; set; }
    public BBoxOutput BBox { get; set; } = new();
    public float Confidence { get; set; }
    public string Action { get; set; } = "";
    public string? Model { get; set; }
    public string? Text { get; set; }
    public string? Error { get; set; }
}
