using RailReader.Core.Models;

namespace RailReader.Core.Services;

/// <summary>
/// Lazily generates page indices for background analysis, scanning outward
/// from the current page in alternating forward/backward directions.
/// Skips pages already in the analysis cache or in-flight.
/// </summary>
internal sealed class BackgroundAnalysisQueue
{
    private readonly int _pageCount;
    private int _nextForward;
    private int _nextBackward;

    public BackgroundAnalysisQueue(int pageCount)
    {
        _pageCount = pageCount;
        // Both cursors out-of-range until Reset is called.
        _nextForward = pageCount;
        _nextBackward = -1;
    }

    /// <summary>Re-centre the scan origin on the current page.</summary>
    public void Reset(int currentPage)
    {
        // Start forward scan from the current page itself so it's included
        // if it was never analysed (e.g. worker wasn't ready on first load).
        _nextForward = currentPage;
        _nextBackward = currentPage - 1;
    }

    public bool IsExhausted => _nextForward >= _pageCount && _nextBackward < 0;

    /// <summary>
    /// Returns the next page to analyse, or null if all pages are covered.
    /// Tries forward first, then backward, skipping cached/in-flight pages.
    /// </summary>
    public int? TryGetNext(IReadOnlyDictionary<int, PageAnalysis> cache,
        Func<int, bool> isInFlight)
    {
        while (_nextForward < _pageCount)
        {
            int page = _nextForward++;
            if (!cache.ContainsKey(page) && !isInFlight(page))
                return page;
        }

        while (_nextBackward >= 0)
        {
            int page = _nextBackward--;
            if (!cache.ContainsKey(page) && !isInFlight(page))
                return page;
        }

        return null;
    }
}
