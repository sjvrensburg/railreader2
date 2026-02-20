using System.Runtime.InteropServices;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using RailReader2.Models;

namespace RailReader2.Services;

public sealed class LayoutAnalyzer : IDisposable
{
    private readonly InferenceSession _session;
    private bool _loggedOutputShapes;

    static LayoutAnalyzer()
    {
        // Pre-load the OnnxRuntime native library before OnnxRuntime's own static
        // initializer runs. NativeLibrary.TryLoad caches the handle so the subsequent
        // P/Invoke inside OnnxRuntime finds it already loaded — no resolver conflict.
        // (SetDllImportResolver can only be called once per assembly and OnnxRuntime
        // registers its own, so we must not use it.)
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Linux)) return;

        string[] candidates =
        [
            Path.Combine(AppContext.BaseDirectory,
                "runtimes", RuntimeInformation.RuntimeIdentifier, "native", "libonnxruntime.so"),
            Path.Combine(AppContext.BaseDirectory,
                "runtimes", "linux-x64", "native", "libonnxruntime.so"),
            Path.Combine(AppContext.BaseDirectory, "libonnxruntime.so"),
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

        Console.Error.WriteLine($"[ONNX] Input names: {string.Join(", ", _session.InputNames)}");
        Console.Error.WriteLine($"[ONNX] Output names: {string.Join(", ", _session.OutputNames)}");
    }

    public PageAnalysis RunAnalysis(byte[] rgbBytes, int pxW, int pxH, double pageW, double pageH)
    {
        int target = LayoutConstants.InputSize;
        float scaleH = (float)target / pxH;
        float scaleW = (float)target / pxW;

        var chwData = PreprocessImage(rgbBytes, pxW, pxH, target);

        var imShape = new DenseTensor<float>(new float[] { target, target }, new[] { 1, 2 });
        var image = new DenseTensor<float>(chwData, new[] { 1, 3, target, target });
        var scaleFactor = new DenseTensor<float>(new float[] { scaleH, scaleW }, new[] { 1, 2 });

        var inputs = new List<NamedOnnxValue>
        {
            NamedOnnxValue.CreateFromTensor("im_shape", imShape),
            NamedOnnxValue.CreateFromTensor("image", image),
            NamedOnnxValue.CreateFromTensor("scale_factor", scaleFactor),
        };

        using var results = _session.Run(inputs);

        // Single pass: log output shapes (first call only) and find detection tensor.
        // Copy tensor data immediately since the results collection owns the memory.
        float[]? detectionData = null;
        int detRows = 0, detCols = 0;
        foreach (var r in results)
        {
            Tensor<float>? t = null;
            try { t = r.AsTensor<float>(); } catch { }

            if (!_loggedOutputShapes && t is not null)
            {
                Console.Error.WriteLine($"[ONNX] Output '{r.Name}': dims=[{string.Join(",", t.Dimensions.ToArray())}]");
                var flat = t.ToArray();
                var preview = string.Join(", ", flat.Take(Math.Min(14, flat.Length)).Select(v => v.ToString("F2")));
                Console.Error.WriteLine($"[ONNX]   First values: [{preview}]");
            }

            if (detectionData is null && t is not null && t.Dimensions.Length == 2 && t.Dimensions[1] >= 6)
            {
                detRows = t.Dimensions[0];
                detCols = t.Dimensions[1];
                detectionData = t.ToArray(); // copy before results are consumed
            }
        }
        _loggedOutputShapes = true;

        if (detectionData is null)
        {
            // No suitable detection tensor found; return empty analysis
            return new PageAnalysis { Blocks = [], PageWidth = pageW, PageHeight = pageH };
        }

        float mapScaleX = (float)(pageW / pxW);
        float mapScaleY = (float)(pageH / pxH);
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

        Nms(rawBlocks, LayoutConstants.NmsIouThreshold);

        rawBlocks.Sort((a, b) =>
        {
            int cmp = a.Order.CompareTo(b.Order);
            return cmp != 0 ? cmp : a.BBox.Y.CompareTo(b.BBox.Y);
        });
        for (int i = 0; i < rawBlocks.Count; i++)
            rawBlocks[i].Order = i;

        DetectLinesForBlocks(rawBlocks, rgbBytes, pxW, pxH, mapScaleX, mapScaleY);

        return new PageAnalysis
        {
            Blocks = rawBlocks,
            PageWidth = pageW,
            PageHeight = pageH,
        };
    }

    private static float[] PreprocessImage(byte[] rgbBytes, int origW, int origH, int target)
    {
        // PP-DocLayoutV3 uses mean=[0,0,0] std=[1,1,1] (no ImageNet normalization)
        // Just scale pixels to [0, 1] and convert to CHW layout
        float scaleH = (float)target / origH;
        float scaleW = (float)target / origW;
        int pixelCount = target * target;
        var chwData = new float[3 * pixelCount];

        for (int y = 0; y < target; y++)
        {
            for (int x = 0; x < target; x++)
            {
                int srcY = Math.Min((int)(y / scaleH), origH - 1);
                int srcX = Math.Min((int)(x / scaleW), origW - 1);
                int srcIdx = (srcY * origW + srcX) * 3;
                int dstIdx = y * target + x;
                for (int c = 0; c < 3; c++)
                {
                    chwData[c * pixelCount + dstIdx] = rgbBytes[srcIdx + c] / 255.0f;
                }
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

        for (int i = blocks.Count - 1; i >= 0; i--)
        {
            if (!keep[i]) blocks.RemoveAt(i);
        }
    }

    private static float[] ComputeRowDensities(byte[] rgbBytes, int imgW, int cropX, int cropY, int cropW, int cropH)
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

    private static List<(int Start, int Height)> FindLineRuns(float[] densities)
    {
        var nonZero = densities.Where(v => v > 0.005f).ToArray();
        float threshold = nonZero.Length == 0
            ? 0.005f
            : Math.Max(nonZero.Average() * LayoutConstants.DensityThresholdFraction, 0.005f);

        var runs = new List<(int, int)>();
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
