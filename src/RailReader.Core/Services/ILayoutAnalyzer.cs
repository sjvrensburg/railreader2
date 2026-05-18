using RailReader.Core.Models;

namespace RailReader.Core.Services;

/// <summary>
/// Page-layout inference. The desktop implementation uses ONNX Runtime
/// with PP-DocLayoutV3; other implementations (e.g. ORT Web for Lite)
/// can replace it.
/// </summary>
public interface ILayoutAnalyzer : IDisposable
{
    PageAnalysis RunAnalysis(byte[] rgbBytes, int pxW, int pxH, double pageW, double pageH,
        IReadOnlyList<CharBox>? charBoxes = null, CancellationToken ct = default);
}
