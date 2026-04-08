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
    private bool _forwardExhausted;
    private bool _backwardExhausted;

    public BackgroundAnalysisQueue(int pageCount)
    {
        _pageCount = pageCount;
        _nextForward = 0;
        _nextBackward = -1;
        _forwardExhausted = true;
        _backwardExhausted = true;
    }

    /// <summary>Re-centre the scan origin on the current page.</summary>
    public void Reset(int currentPage)
    {
        // Start forward scan from the current page itself so it's included
        // if it was never analysed (e.g. worker wasn't ready on first load).
        _nextForward = currentPage;
        _nextBackward = currentPage - 1;
        _forwardExhausted = _nextForward >= _pageCount;
        _backwardExhausted = _nextBackward < 0;
    }

    public bool IsExhausted => _forwardExhausted && _backwardExhausted;

    /// <summary>
    /// Returns the next page to analyse, or null if all pages are covered.
    /// Tries forward first, then backward, skipping cached/in-flight pages.
    /// </summary>
    public int? TryGetNext(IReadOnlyDictionary<int, PageAnalysis> cache,
        Func<int, bool> isInFlight)
    {
        if (!_forwardExhausted)
        {
            while (_nextForward < _pageCount)
            {
                int page = _nextForward++;
                if (_nextForward >= _pageCount)
                    _forwardExhausted = true;
                if (!cache.ContainsKey(page) && !isInFlight(page))
                    return page;
            }
            _forwardExhausted = true;
        }

        if (!_backwardExhausted)
        {
            while (_nextBackward >= 0)
            {
                int page = _nextBackward--;
                if (_nextBackward < 0)
                    _backwardExhausted = true;
                if (!cache.ContainsKey(page) && !isInFlight(page))
                    return page;
            }
            _backwardExhausted = true;
        }

        return null;
    }
}
