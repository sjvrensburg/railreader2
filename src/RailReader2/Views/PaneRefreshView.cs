using Avalonia.Controls;
using Avalonia.Layout;

namespace RailReader2.Views;

/// <summary>
/// Base for accordion pane views with lazily-rebuilt content. The accordion keeps all five panes
/// realised at once, so a collapsed/hidden pane must not run its (sometimes expensive) refresh on
/// every navigation. Subclasses call <see cref="RefreshIfVisible"/> from their update handlers and
/// implement <see cref="Refresh"/>; the work is deferred while the pane is hidden and flushed when
/// it becomes effectively visible.
/// </summary>
public abstract class PaneRefreshView : UserControl
{
    private bool _refreshPending;

    protected PaneRefreshView()
    {
        EffectiveViewportChanged += OnViewportChanged;
    }

    /// <summary>Rebuild now if the pane is visible, otherwise defer until it is.</summary>
    protected void RefreshIfVisible()
    {
        if (IsEffectivelyVisible) { Refresh(); _refreshPending = false; }
        else _refreshPending = true;
    }

    /// <summary>Mark a refresh as needed without running it now (for callers that gate themselves).</summary>
    protected void DeferRefresh() => _refreshPending = true;

    /// <summary>Rebuild the pane's content. Called when visible, or on becoming visible if deferred.</summary>
    protected abstract void Refresh();

    // Fires when the pane's visible region changes (panel shown/hidden, section expand/collapse);
    // flush a deferred refresh once it becomes visible.
    private void OnViewportChanged(object? sender, EffectiveViewportChangedEventArgs e)
    {
        if (IsEffectivelyVisible && _refreshPending)
        {
            _refreshPending = false;
            Refresh();
        }
    }
}
