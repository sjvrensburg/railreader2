using System.Runtime.InteropServices;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using RailReader.Core;
using RailReader.Core.Models;

namespace RailReader.Core.Services;

public sealed class LayoutAnalyzer : IDisposable
{
    internal static ILogger Logger { get; set; } = NullLogger.Instance;

    private readonly InferenceSession _session;
#if DEBUG
    private bool _loggedOutputShapes;
#endif
    private float[]? _chwBuffer;

    static LayoutAnalyzer()
    {
        // Pre-load the OnnxRuntime native library before OnnxRuntime's own static
        // initializer runs. NativeLibrary.TryLoad caches the handle so the subsequent
        // P/Invoke inside OnnxRuntime finds it already loaded — no resolver conflict.
        // (SetDllImportResolver can only be called once per assembly and OnnxRuntime
        // registers its own, so we must not use it.)
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows)) return;

        // Platform-specific library name and fallback RIDs
        string ext, fallbackRid;
        if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
        {
            ext = ".dylib";
            fallbackRid = "osx-arm64";
        }
        else
        {
            ext = ".so";
            fallbackRid = "linux-x64";
        }

        string libName = $"libonnxruntime{ext}";
        string[] candidates =
        [
            Path.Combine(AppContext.BaseDirectory,
                "runtimes", RuntimeInformation.RuntimeIdentifier, "native", libName),
            Path.Combine(AppContext.BaseDirectory,
                "runtimes", fallbackRid, "native", libName),
            Path.Combine(AppContext.BaseDirectory, libName),
        ];

        foreach (var path in candidates)
        {
            if (File.Exists(path) && NativeLibrary.TryLoad(path, out _))
                return;
        }
    }

    public LayoutAnalyzer(string modelPath)
    {
        var opts = new SessionOptions();
        opts.GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL;
        _session = new InferenceSession(modelPath, opts);

        Logger.Debug($"[ONNX] Input names: {string.Join(", ", _session.InputNames)}");
        Logger.Debug($"[ONNX] Output names: {string.Join(", ", _session.OutputNames)}");
    }

    public PageAnalysis RunAnalysis(byte[] rgbBytes, int pxW, int pxH, double pageW, double pageH,
        CancellationToken ct = default)
    {
        int target = LayoutConstants.InputSize;

        ct.ThrowIfCancellationRequested();

        // Letterbox: place undistorted image at (0,0) in target×target canvas.
        // FitPageToTarget already ensures max(pxW, pxH) == target, so no resizing
        // is needed — just padding the shorter dimension with black pixels.
        // scale_factor = [1, 1] since the image pixels map 1:1 to canvas pixels.
        var chwData = PreprocessImage(rgbBytes, pxW, pxH, target, ref _chwBuffer);

        ct.ThrowIfCancellationRequested();

        var imShape = new DenseTensor<float>(new float[] { target, target }, new[] { 1, 2 });
        var image = new DenseTensor<float>(chwData, new[] { 1, 3, target, target });
        var scaleFactor = new DenseTensor<float>(new float[] { 1.0f, 1.0f }, new[] { 1, 2 });

        List<NamedOnnxValue> inputs =
        [
            NamedOnnxValue.CreateFromTensor("im_shape", imShape),
            NamedOnnxValue.CreateFromTensor("image", image),
            NamedOnnxValue.CreateFromTensor("scale_factor", scaleFactor),
        ];

        using var results = _session.Run(inputs);

        float mapScaleX = (float)(pageW / pxW);
        float mapScaleY = (float)(pageH / pxH);

        var rawBlocks = ExtractDetections(results, pxW, pxH, mapScaleX, mapScaleY);
        if (rawBlocks is null)
            return new PageAnalysis { Blocks = [], PageWidth = pageW, PageHeight = pageH };
        PostProcessBlocks(rawBlocks, rgbBytes, pxW, pxH, mapScaleX, mapScaleY);

        return new PageAnalysis
        {
            Blocks = rawBlocks,
            PageWidth = pageW,
            PageHeight = pageH,
        };
    }

    private List<LayoutBlock>? ExtractDetections(
        IDisposableReadOnlyCollection<DisposableNamedOnnxValue> results,
        int pxW, int pxH, float mapScaleX, float mapScaleY)
    {
        // Single pass: log output shapes (first call only) and find detection tensor.
        // Copy tensor data immediately since the results collection owns the memory.
        float[]? detectionData = null;
        int detRows = 0, detCols = 0;
        foreach (var r in results)
        {
            if (r.Value is not Tensor<float> t) continue;

            bool isDetection = detectionData is null && t.Dimensions.Length == 2 && t.Dimensions[1] >= 6;

            if (isDetection)
            {
                detRows = t.Dimensions[0];
                detCols = t.Dimensions[1];
                detectionData = t.ToArray();
            }

#if DEBUG
            if (!_loggedOutputShapes)
            {
                Logger.Debug($"[ONNX] Output '{r.Name}': dims=[{string.Join(",", t.Dimensions.ToArray())}]");
                // Reuse detectionData if already copied, otherwise take a snapshot for preview
                var flat = isDetection ? detectionData! : t.ToArray();
                var preview = string.Join(", ", flat.Take(Math.Min(14, flat.Length)).Select(v => v.ToString("F2")));
                Logger.Debug($"[ONNX]   First values: [{preview}]");
            }
#endif
        }
#if DEBUG
        _loggedOutputShapes = true;
#endif

        if (detectionData is null)
            return null;

        bool hasReadingOrder = detCols >= 7;

        var rawBlocks = new List<LayoutBlock>();
        for (int i = 0; i < detRows; i++)
        {
            int off = i * detCols;
            int classId = (int)detectionData[off];
            float confidence = detectionData[off + 1];
            float xmin = detectionData[off + 2];
            float ymin = detectionData[off + 3];
            float xmax = detectionData[off + 4];
            float ymax = detectionData[off + 5];
            int modelOrder = hasReadingOrder ? (int)detectionData[off + 6] : 0;

            if (confidence < LayoutConstants.ConfidenceThreshold) continue;
            if (classId < 0 || classId >= LayoutConstants.LayoutClasses.Length) continue;

            float x = Math.Max(xmin, 0);
            float y = Math.Max(ymin, 0);
            float w = Math.Min(xmax, pxW) - x;
            float h = Math.Min(ymax, pxH) - y;

            if (w < 5 || h < 5) continue;

            rawBlocks.Add(new LayoutBlock
            {
                BBox = new BBox(x * mapScaleX, y * mapScaleY, w * mapScaleX, h * mapScaleY),
                ClassId = classId,
                Confidence = confidence,
                Order = modelOrder,
            });
        }

        return rawBlocks;
    }

    private static void PostProcessBlocks(
        List<LayoutBlock> rawBlocks, byte[] rgbBytes, int pxW, int pxH,
        float mapScaleX, float mapScaleY)
    {
        Nms(rawBlocks, LayoutConstants.NmsIouThreshold);
        SuppressNestedBlocks(rawBlocks);

        rawBlocks.Sort((a, b) =>
        {
            int cmp = a.Order.CompareTo(b.Order);
            return cmp != 0 ? cmp : a.BBox.Y.CompareTo(b.BBox.Y);
        });
        for (int i = 0; i < rawBlocks.Count; i++)
            rawBlocks[i].Order = i;

        ResolveVerticalOverlaps(rawBlocks);
        DetectLinesForBlocks(rawBlocks, rgbBytes, pxW, pxH, mapScaleX, mapScaleY);
    }

    private static float[] PreprocessImage(byte[] rgbBytes, int origW, int origH, int target, ref float[]? buffer)
    {
        // PP-DocLayoutV3 uses mean=[0,0,0] std=[1,1,1] (no ImageNet normalization)
        // Letterbox: place image at (0,0) in target×target canvas with black padding.
        // FitPageToTarget ensures max(origW, origH) == target, so pixels copy 1:1.
        int pixelCount = target * target;
        int needed = 3 * pixelCount;
        if (buffer is null || buffer.Length != needed)
            buffer = new float[needed];
        else
            Array.Clear(buffer);
        var chwData = buffer;

        int copyW = Math.Min(origW, target);
        int copyH = Math.Min(origH, target);

        for (int y = 0; y < copyH; y++)
        {
            int srcRow = y * origW;
            for (int x = 0; x < copyW; x++)
            {
                int srcIdx = (srcRow + x) * 3;
                int dstIdx = y * target + x;
                chwData[dstIdx] = rgbBytes[srcIdx] / 255.0f;                     // R
                chwData[pixelCount + dstIdx] = rgbBytes[srcIdx + 1] / 255.0f;    // G
                chwData[2 * pixelCount + dstIdx] = rgbBytes[srcIdx + 2] / 255.0f; // B
            }
        }
        return chwData;
    }

    private static float Iou(BBox a, BBox b)
    {
        float x1 = Math.Max(a.X, b.X);
        float y1 = Math.Max(a.Y, b.Y);
        float x2 = Math.Min(a.X + a.W, b.X + b.W);
        float y2 = Math.Min(a.Y + a.H, b.Y + b.H);

        float inter = Math.Max(x2 - x1, 0) * Math.Max(y2 - y1, 0);
        float areaA = a.W * a.H;
        float areaB = b.W * b.H;
        float union = areaA + areaB - inter;
        return union <= 0 ? 0 : inter / union;
    }

    private static void Nms(List<LayoutBlock> blocks, float threshold)
    {
        blocks.Sort((a, b) => b.Confidence.CompareTo(a.Confidence));
        var keep = new bool[blocks.Count];
        Array.Fill(keep, true);

        for (int i = 0; i < blocks.Count; i++)
        {
            if (!keep[i]) continue;
            for (int j = i + 1; j < blocks.Count; j++)
            {
                if (!keep[j]) continue;
                if (Iou(blocks[i].BBox, blocks[j].BBox) > threshold)
                    keep[j] = false;
            }
        }

        RemoveFlagged(blocks, keep);
    }

    /// <summary>
    /// Removes blocks that are fully contained within a larger block.
    /// Handles cases like inline_formula detected inside a text block,
    /// which would otherwise create redundant navigation targets.
    /// </summary>
    private static void SuppressNestedBlocks(List<LayoutBlock> blocks)
    {
        const float margin = 2f; // tolerance in page points
        var keep = new bool[blocks.Count];
        Array.Fill(keep, true);

        for (int i = 0; i < blocks.Count; i++)
        {
            if (!keep[i]) continue;
            for (int j = 0; j < blocks.Count; j++)
            {
                if (i == j || !keep[j]) continue;

                var outer = blocks[i].BBox;
                var inner = blocks[j].BBox;

                bool contained = inner.X >= outer.X - margin &&
                    inner.Y >= outer.Y - margin &&
                    inner.X + inner.W <= outer.X + outer.W + margin &&
                    inner.Y + inner.H <= outer.Y + outer.H + margin;

                if (contained && inner.W * inner.H < outer.W * outer.H)
                    keep[j] = false;
            }
        }

        RemoveFlagged(blocks, keep);
    }

    private static void RemoveFlagged(List<LayoutBlock> blocks, bool[] keep)
    {
        for (int i = blocks.Count - 1; i >= 0; i--)
        {
            if (!keep[i]) blocks.RemoveAt(i);
        }
    }

    /// <summary>
    /// Trims vertically overlapping blocks so each block's bounding box covers only
    /// its own content. When two blocks overlap vertically with similar X ranges,
    /// the later block's top is pushed down to the earlier block's bottom.
    /// This prevents line detection from finding text belonging to an adjacent block.
    /// </summary>
    private static void ResolveVerticalOverlaps(List<LayoutBlock> blocks)
    {
        // Blocks are already sorted by reading order / Y position.
        for (int i = 0; i < blocks.Count; i++)
        {
            var a = blocks[i];
            float aBottom = a.BBox.Y + a.BBox.H;

            for (int j = i + 1; j < blocks.Count; j++)
            {
                var b = blocks[j];
                float bBottom = b.BBox.Y + b.BBox.H;

                // Check horizontal overlap: blocks must share significant X range
                float overlapX = Math.Min(a.BBox.X + a.BBox.W, b.BBox.X + b.BBox.W)
                    - Math.Max(a.BBox.X, b.BBox.X);
                float minW = Math.Min(a.BBox.W, b.BBox.W);
                if (overlapX < minW * 0.5f) continue; // not horizontally aligned

                // Check vertical overlap: block A's bottom extends past block B's top
                float overlapY = aBottom - b.BBox.Y;
                if (overlapY <= 0) continue; // no vertical overlap

                // Trim the later block's top to start at the earlier block's bottom
                float newY = aBottom;
                float newH = bBottom - newY;
                if (newH < 5) continue; // don't shrink to nothing

                blocks[j] = new LayoutBlock
                {
                    BBox = new BBox(b.BBox.X, newY, b.BBox.W, newH),
                    ClassId = b.ClassId,
                    Confidence = b.Confidence,
                    Order = b.Order,
                };
            }
        }
    }

    internal static float[] ComputeRowDensities(byte[] rgbBytes, int imgW, int cropX, int cropY, int cropW, int cropH)
    {
        var profile = new float[cropH];
        for (int row = 0; row < cropH; row++)
        {
            int darkCount = 0;
            for (int col = 0; col < cropW; col++)
            {
                int pixelIdx = ((cropY + row) * imgW + (cropX + col)) * 3;
                if (pixelIdx + 2 < rgbBytes.Length)
                {
                    float r = rgbBytes[pixelIdx];
                    float g = rgbBytes[pixelIdx + 1];
                    float b = rgbBytes[pixelIdx + 2];
                    float lum = r * 0.299f + g * 0.587f + b * 0.114f;
                    if (lum < LayoutConstants.DarkLuminanceThreshold)
                        darkCount++;
                }
            }
            profile[row] = (float)darkCount / cropW;
        }

        // Smooth with radius-1 moving average
        var smoothed = new float[cropH];
        for (int r = 0; r < cropH; r++)
        {
            int start = Math.Max(r - 1, 0);
            int end = Math.Min(r + 2, cropH);
            float sum = 0;
            for (int k = start; k < end; k++) sum += profile[k];
            smoothed[r] = sum / (end - start);
        }
        return smoothed;
    }

    /// <summary>
    /// Detects line runs using adaptive density thresholding with recovery for short lines.
    /// The primary pass uses a density-fraction threshold (15% of average) which reliably
    /// segments dense text. A second recovery pass scans any uncovered regions at the top
    /// and bottom of the block with a low absolute threshold to catch short lines (e.g. the
    /// last few words of a paragraph) that fall below the density-fraction threshold.
    /// </summary>
    internal static List<(int Start, int Height)> FindLineRuns(float[] densities)
    {
        // Primary pass: density-fraction threshold — works well for dense text
        var nonZero = densities.Where(v => v > 0.005f).ToArray();
        float threshold = nonZero.Length == 0
            ? 0.005f
            : Math.Max(nonZero.Average() * LayoutConstants.DensityThresholdFraction, 0.005f);

        var runs = FindRunsAboveThreshold(densities, threshold);

        if (runs.Count == 0) return runs;

        // Recovery pass: check for uncovered regions at top and bottom of the block.
        // If the first/last detected line is far from the block edge, re-scan that
        // region with a low absolute threshold to catch short lines.
        int medianHeight = runs.Select(r => r.Height).OrderBy(h => h).ElementAt(runs.Count / 2);
        float recoveryThreshold = 0.005f;

        // Top recovery: region before the first detected line
        int firstLineStart = runs[0].Start;
        if (firstLineStart > medianHeight / 2)
        {
            var topRuns = FindRunsAboveThreshold(densities[..firstLineStart], recoveryThreshold);
            runs.InsertRange(0, topRuns);
        }

        // Bottom recovery: region after the last detected line
        var lastRun = runs[^1];
        int lastLineEnd = lastRun.Start + lastRun.Height;
        int remaining = densities.Length - lastLineEnd;
        if (remaining > medianHeight / 2)
        {
            var bottomRuns = FindRunsAboveThreshold(densities[lastLineEnd..], recoveryThreshold);
            // Offset the recovered runs to block-relative coordinates
            runs.AddRange(bottomRuns.Select(r => (r.Start + lastLineEnd, r.Height)));
        }

        return runs;
    }

    private static List<(int Start, int Height)> FindRunsAboveThreshold(float[] densities, float threshold)
    {
        var runs = new List<(int Start, int Height)>();
        int? runStart = null;

        for (int r = 0; r < densities.Length; r++)
        {
            if (densities[r] > threshold)
            {
                runStart ??= r;
            }
            else if (runStart is not null)
            {
                int runH = r - runStart.Value;
                if (runH >= LayoutConstants.MinLineHeightPx)
                    runs.Add((runStart.Value, runH));
                runStart = null;
            }
        }
        if (runStart is not null)
        {
            int runH = densities.Length - runStart.Value;
            if (runH >= LayoutConstants.MinLineHeightPx)
                runs.Add((runStart.Value, runH));
        }
        return runs;
    }

    private static void DetectLinesForBlocks(
        List<LayoutBlock> blocks, byte[] rgbBytes, int imgW, int imgH, float scaleX, float scaleY)
    {
        foreach (var block in blocks)
        {
            int pxX = Math.Min((int)Math.Round(block.BBox.X / scaleX), imgW - 1);
            int pxY = Math.Min((int)Math.Round(block.BBox.Y / scaleY), imgH - 1);
            int pxW = Math.Min((int)Math.Round(block.BBox.W / scaleX), imgW - pxX);
            int pxH = Math.Min((int)Math.Round(block.BBox.H / scaleY), imgH - pxY);

            if (pxW == 0 || pxH == 0)
            {
                block.Lines.Add(new LineInfo(block.BBox.Y + block.BBox.H / 2, block.BBox.H));
                continue;
            }

            var densities = ComputeRowDensities(rgbBytes, imgW, pxX, pxY, pxW, pxH);
            var runs = FindLineRuns(densities);

            block.Lines = runs
                .Select(run =>
                {
                    float centerYPx = run.Start + run.Height / 2.0f;
                    return new LineInfo(block.BBox.Y + centerYPx * scaleY, run.Height * scaleY);
                })
                .ToList();

            if (block.Lines.Count == 0)
                block.Lines.Add(new LineInfo(block.BBox.Y + block.BBox.H / 2, block.BBox.H));
        }
    }

    public void Dispose()
    {
        _session.Dispose();
    }
}
