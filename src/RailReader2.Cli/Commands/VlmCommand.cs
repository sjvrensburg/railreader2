using System.Text.Json;
using System.Text.RegularExpressions;
using RailReader.Core;
using RailReader.Core.Models;
using RailReader.Core.Services;
using RailReader.Renderer.Skia;

namespace RailReader.Cli.Commands;

public static class VlmCommand
{
    private record Target(int Page, int BlockIdx, LayoutBlock Block, double PageW, double PageH);

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
        var noStructured = Program.HasFlag(args, "no-structured-output");

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

        var wantedClasses = ResolveClasses(classesOpt, all);
        if (wantedClasses.Count == 0)
            return Program.Fail("No classes selected. Use --classes equation,table,figure or --all.");

        var promptStyle = VlmService.PromptStyle.Instruction;
        if (promptStyleStr != null
            && !Enum.TryParse<VlmService.PromptStyle>(promptStyleStr, ignoreCase: true, out promptStyle))
            return Program.Fail($"Invalid --prompt-style: {promptStyleStr} (expected: instruction, ocr)");

        // Resolve default VLM config.
        // API key precedence: --api-key > OPENAI_API_KEY env var > AppConfig.
        var appConfig = AppConfig.Load();
        var baseApiKey = apiKeyOverride
            ?? (string.IsNullOrWhiteSpace(appConfig.VlmApiKey)
                ? Environment.GetEnvironmentVariable("OPENAI_API_KEY")
                : appConfig.VlmApiKey);
        var baseCfg = new VlmEndpointConfig(
            endpointOverride ?? appConfig.VlmEndpoint,
            modelOverride ?? appConfig.VlmModel,
            baseApiKey);

        var eqCfg = Override(baseCfg, eqEndpoint, eqModel, eqApiKey);
        var tblCfg = Override(baseCfg, tblEndpoint, tblModel, tblApiKey);
        var figCfg = Override(baseCfg, figEndpoint, figModel, figApiKey);

        bool anyEndpoint =
            !string.IsNullOrWhiteSpace(eqCfg.Endpoint) ||
            !string.IsNullOrWhiteSpace(tblCfg.Endpoint) ||
            !string.IsNullOrWhiteSpace(figCfg.Endpoint);

        bool dryRun = dumpCrops != null && !anyEndpoint;
        if (dryRun)
            Console.Error.WriteLine("VLM endpoint not configured — running in dry mode (crops only, no API calls).");
        else if (!anyEndpoint)
            return Program.Fail("VLM endpoint not configured. Use --endpoint or set it in Settings, or pass --dump-crops for a dry run.");

        var pdf = factory.CreatePdfService(pdfPath);

        var (targets, buildErr) = BuildTargets(pdf, pageRange, singlePageStr, singleBlockStr,
            fromStructure, wantedClasses, minConfidence);
        if (buildErr != null) return Program.Fail(buildErr);

        if (targets!.Count == 0)
        {
            Console.Error.WriteLine("No blocks matched the selection.");
            WriteJson(new VlmOutput { Source = Path.GetFileName(pdfPath) }, outputPath);
            return 0;
        }

        Console.Error.WriteLine($"Processing {targets.Count} blocks...");

        if (dumpCrops != null)
            Directory.CreateDirectory(dumpCrops);

        // Render crops: one page render per N blocks on that page (PDFium is
        // single-threaded and rasterising at 300 DPI is the expensive step).
        var prepared = new List<(Target Target, byte[]? Png)>(targets.Count);
        foreach (var pageGroup in targets.GroupBy(t => t.Page))
        {
            var blocks = pageGroup.ToList();
            var bboxes = blocks.Select(t => t.Block.BBox).ToList();
            var pageW = blocks[0].PageW;
            var pageH = blocks[0].PageH;

            List<byte[]?> pngs;
            try
            {
                pngs = BlockCropRenderer.RenderBlocksAsPng(pdf, pageGroup.Key, bboxes, pageW, pageH, dpi);
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"  Error rendering page {pageGroup.Key + 1}: {ex.Message}");
                foreach (var t in blocks) prepared.Add((t, null));
                continue;
            }

            for (int i = 0; i < blocks.Count; i++)
            {
                var t = blocks[i];
                var png = pngs[i];
                prepared.Add((t, png));

                if (png != null && dumpCrops != null)
                {
                    var cropName = $"page{t.Page + 1:D4}_block{t.BlockIdx:D2}_{LayoutConstants.LayoutClasses[t.Block.ClassId]}.png";
                    File.WriteAllBytes(Path.Combine(dumpCrops, cropName), png);
                }
            }
        }

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
                        var action = GetAction(p.Target.Block.ClassId);
                        var classCfg = PickConfig(action, eqCfg, tblCfg, figCfg);

                        VlmService.VlmResult result;
                        if (p.Png == null)
                            result = new VlmService.VlmResult(null, "Failed to render crop");
                        else if (string.IsNullOrWhiteSpace(classCfg.Endpoint))
                            result = new VlmService.VlmResult(null, $"No endpoint configured for {action}");
                        else
                            result = await VlmService.DescribeBlockAsync(
                                p.Png, action, classCfg, promptStyle, structuredOutput: !noStructured);

                        if (!noHtmlToMd && action == VlmService.BlockAction.Markdown
                            && result.Text != null && LooksLikeHtmlTable(result.Text))
                        {
                            var md = HtmlTableToMarkdown(result.Text);
                            if (md != null) result = new VlmService.VlmResult(md, result.Error);
                        }

                        var cur = Interlocked.Increment(ref done);
                        Console.Error.WriteLine($"  [{cur}/{total}] page {p.Target.Page + 1} block {p.Target.BlockIdx} ({LayoutConstants.LayoutClasses[p.Target.Block.ClassId]}) — {(result.Error ?? "ok")}");

                        return BuildResult(p.Target, action, result, classCfg.Model);
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
            foreach (var p in prepared)
            {
                var action = GetAction(p.Target.Block.ClassId);
                results.Add(BuildResult(p.Target, action,
                    new VlmService.VlmResult(null, p.Png == null ? "Failed to render crop" : "dry run"),
                    PickConfig(action, eqCfg, tblCfg, figCfg).Model));
            }
        }

        var output = new VlmOutput { Source = Path.GetFileName(pdfPath) };
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

    static (List<Target>? Targets, string? Error) BuildTargets(
        IPdfService pdf, string? pageRange, string? singlePageStr, string? singleBlockStr,
        string? fromStructure, HashSet<int> wantedClasses, float minConfidence)
    {
        int? singlePage = null, singleBlock = null;
        if (singlePageStr != null)
        {
            if (!int.TryParse(singlePageStr, out var p) || p < 1 || p > pdf.PageCount)
                return (null, $"Invalid --page: {singlePageStr}");
            singlePage = p - 1;
        }
        if (singleBlockStr != null)
        {
            if (!int.TryParse(singleBlockStr, out var b) || b < 0)
                return (null, $"Invalid --block: {singleBlockStr}");
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
            if (err != null) return (null, err);
            pages = parsed!;
        }

        var result = new List<Target>();

        // Path 1: reuse existing structure JSON
        if (fromStructure != null)
        {
            StructureOutput? structure;
            try
            {
                var json = File.ReadAllText(fromStructure);
                structure = JsonSerializer.Deserialize<StructureOutput>(json, Shared.JsonOptions);
            }
            catch (FileNotFoundException)
            {
                return (null, $"Structure file not found: {fromStructure}");
            }
            catch (Exception ex)
            {
                return (null, $"Failed to read structure JSON: {ex.Message}");
            }
            if (structure == null) return (null, "Failed to parse structure JSON");

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
                    result.Add(new Target(sp.Page, i, lb, sp.Width, sp.Height));
                }
            }
            return (result, null);
        }

        // Path 2: run inline layout analysis
        using var analyzer = Shared.CreateAnalyzer(true);
        if (analyzer == null)
            return (null, "ONNX model not available — cannot run layout analysis. Use --from-structure or install the model.");

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

                result.Add(new Target(pageIdx, i, b, pw, ph));
            }
        }
        return (result, null);
    }

    static HashSet<int> ResolveClasses(string? classesOpt, bool all)
    {
        var set = new HashSet<int>();
        var tokens = classesOpt?.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(t => t.ToLowerInvariant()).ToHashSet() ?? [];

        if (all || tokens.Contains("equation"))
            set.UnionWith(LayoutConstants.EquationClasses);
        if (all || tokens.Contains("table"))
            set.UnionWith(LayoutConstants.TableClasses);
        if (all || tokens.Contains("figure"))
            set.UnionWith(LayoutConstants.FigureClasses);

        return set;
    }

    static VlmService.BlockAction GetAction(int classId) =>
        VlmService.GetBlockAction(classId) ?? VlmService.BlockAction.LaTeX;

    static VlmBlockResult BuildResult(Target t, VlmService.BlockAction action,
        VlmService.VlmResult result, string? model)
    {
        return new VlmBlockResult
        {
            Page = t.Page,
            Index = t.BlockIdx,
            Class = LayoutConstants.LayoutClasses[t.Block.ClassId],
            ClassId = t.Block.ClassId,
            BBox = new BBoxOutput(t.Block.BBox.X, t.Block.BBox.Y, t.Block.BBox.W, t.Block.BBox.H),
            Confidence = t.Block.Confidence,
            Action = action.ToString(),
            Model = model,
            Text = result.Text,
            Error = result.Error,
        };
    }

    static VlmEndpointConfig Override(VlmEndpointConfig baseCfg, string? endpoint, string? model, string? apiKey) =>
        new(endpoint ?? baseCfg.Endpoint, model ?? baseCfg.Model, apiKey ?? baseCfg.ApiKey);

    static VlmEndpointConfig PickConfig(VlmService.BlockAction action,
        VlmEndpointConfig eq, VlmEndpointConfig tbl, VlmEndpointConfig fig) =>
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
        Console.WriteLine("  --no-structured-output      Disable JSON schema response format (default: on)");
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
