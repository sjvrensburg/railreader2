using Avalonia;
using Avalonia.Controls;
using Avalonia.Rendering.Composition;

namespace RailReader2.Views;

/// <summary>
/// Generic base class for controls that host a <see cref="CompositionCustomVisual"/>
/// backed by a typed handler. Manages the visual lifecycle (create on attach,
/// dispose on detach, sync size) and provides <see cref="UpdateState"/> to send
/// immutable state snapshots to the composition thread.
/// </summary>
internal class CompositionLayerControl<THandler> : Control
    where THandler : CompositionCustomVisualHandler, new()
{
    private CompositionCustomVisual? _visual;

    protected CompositionLayerControl()
    {
        IsHitTestVisible = false;
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        var compositor = ElementComposition.GetElementVisual(this)?.Compositor;
        if (compositor is not null)
        {
            _visual = compositor.CreateCustomVisual(new THandler());
            _visual.Size = new Vector(Bounds.Width, Bounds.Height);
            ElementComposition.SetElementChildVisual(this, _visual);
        }
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        ElementComposition.SetElementChildVisual(this, null);
        _visual = null;
        base.OnDetachedFromVisualTree(e);
    }

    protected override void OnSizeChanged(SizeChangedEventArgs e)
    {
        base.OnSizeChanged(e);
        if (_visual is not null)
            _visual.Size = new Vector(e.NewSize.Width, e.NewSize.Height);
    }

    /// <summary>
    /// Sends a state snapshot to the composition thread handler.
    /// The handler calls <see cref="CompositionCustomVisualHandler.Invalidate()"/>
    /// to schedule a re-render on the next compositor frame.
    /// </summary>
    internal void UpdateState(object state) =>
        _visual?.SendHandlerMessage(state);

    /// <summary>
    /// Sends a message to the handler, returning false if the visual is detached
    /// (message would be silently dropped). Used for lifecycle-sensitive messages
    /// like <see cref="RetireImage"/> that require fallback disposal.
    /// </summary>
    internal bool TrySendMessage(object message)
    {
        if (_visual is null) return false;
        _visual.SendHandlerMessage(message);
        return true;
    }
}
