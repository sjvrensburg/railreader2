using RailReader.Core.Models;
using RailReader.Core.Services;

namespace RailReader.Core;

/// <summary>
/// Owns all annotation tool state and interaction logic.
/// Methods receive DocumentState as a parameter rather than reading ActiveDocument from a controller.
/// </summary>
public sealed class AnnotationInteractionHandler
{
    // Annotation tool state
    public AnnotationTool ActiveTool { get; set; } = AnnotationTool.None;
    public bool IsAnnotating => ActiveTool != AnnotationTool.None;
    public Annotation? SelectedAnnotation { get; set; }
    public Annotation? PreviewAnnotation { get; set; }
    public string ActiveAnnotationColor { get; set; } = "#FFFF00";
    public float ActiveAnnotationOpacity { get; set; } = 0.4f;
    public float ActiveStrokeWidth { get; set; } = 2f;

    // Colour palettes for tools with colour options
    public static readonly (string Color, float Opacity)[] HighlightColors =
    [
        ("#FFFF00", 0.35f),  // Yellow
        ("#66CC66", 0.35f),  // Green
        ("#FF8FA0", 0.35f),  // Pink
    ];

    public static readonly (string Color, float Opacity)[] PenColors =
    [
        ("#FF0000", 0.8f),   // Red
        ("#0000FF", 0.8f),   // Blue
        ("#000000", 0.9f),   // Black
    ];

    public static readonly (string Color, float Opacity)[] RectColors =
    [
        ("#0066FF", 0.5f),   // Blue
        ("#FF0000", 0.5f),   // Red
        ("#000000", 0.6f),   // Black
    ];

    /// <summary>Stroke width presets: thin, normal, thick.</summary>
    public static readonly float[] ThicknessPresets = [1f, 2f, 4f];

    private int _highlightColorIndex;
    private int _penColorIndex;
    private int _rectColorIndex;
    private int _penThicknessIndex = 1;  // default: normal (2f)
    private int _rectThicknessIndex = 1;

    // In-progress annotation building state
    private List<PointF>? _freehandPoints;
    private float _rectStartX, _rectStartY;
    private int _highlightCharStart = -1;

    // Browse-mode drag state (for moving/resizing annotations)
    private Annotation? _dragAnnotation;
    private float _dragStartPageX, _dragStartPageY;
    private PositionSnapshot? _dragOriginalPosition;
    private ResizeHandle _resizeHandle = ResizeHandle.None;
    private RectF _resizeStartBounds;
    private List<PointF>? _resizeOriginalPoints;

    // Text selection state
    public string? SelectedText { get; set; }
    public List<HighlightRect>? TextSelectionRects { get; set; }
    private int _textSelectCharStart = -1;

    // Clipboard callback (set by UI)
    public Action<string>? CopyToClipboard { get; set; }

    public void SetAnnotationTool(AnnotationTool tool)
    {
        ActiveTool = tool;
        SelectedAnnotation = null;
        PreviewAnnotation = null;
        _freehandPoints = null;
        _highlightCharStart = -1;

        if (tool != AnnotationTool.TextSelect)
        {
            SelectedText = null;
            TextSelectionRects = null;
            _textSelectCharStart = -1;
        }

        switch (tool)
        {
            case AnnotationTool.Highlight:
                var hc = HighlightColors[_highlightColorIndex];
                ActiveAnnotationColor = hc.Color;
                ActiveAnnotationOpacity = hc.Opacity;
                break;
            case AnnotationTool.Pen:
                var pc = PenColors[_penColorIndex];
                ActiveAnnotationColor = pc.Color;
                ActiveAnnotationOpacity = pc.Opacity;
                ActiveStrokeWidth = ThicknessPresets[_penThicknessIndex];
                break;
            case AnnotationTool.Rectangle:
                var rc = RectColors[_rectColorIndex];
                ActiveAnnotationColor = rc.Color;
                ActiveAnnotationOpacity = rc.Opacity;
                ActiveStrokeWidth = ThicknessPresets[_rectThicknessIndex];
                break;
            case AnnotationTool.TextNote:
                ActiveAnnotationColor = "#FFCC00";
                ActiveAnnotationOpacity = 0.9f;
                break;
            case AnnotationTool.Eraser:
            case AnnotationTool.None:
                break;
        }
    }

    public void SetAnnotationColorIndex(AnnotationTool tool, int index)
    {
        switch (tool)
        {
            case AnnotationTool.Highlight:
                _highlightColorIndex = Math.Clamp(index, 0, HighlightColors.Length - 1);
                break;
            case AnnotationTool.Pen:
                _penColorIndex = Math.Clamp(index, 0, PenColors.Length - 1);
                break;
            case AnnotationTool.Rectangle:
                _rectColorIndex = Math.Clamp(index, 0, RectColors.Length - 1);
                break;
        }
    }

    public int GetAnnotationColorIndex(AnnotationTool tool) => tool switch
    {
        AnnotationTool.Highlight => _highlightColorIndex,
        AnnotationTool.Pen => _penColorIndex,
        AnnotationTool.Rectangle => _rectColorIndex,
        _ => 0,
    };

    public void SetThicknessIndex(AnnotationTool tool, int index)
    {
        int clamped = Math.Clamp(index, 0, ThicknessPresets.Length - 1);
        switch (tool)
        {
            case AnnotationTool.Pen: _penThicknessIndex = clamped; break;
            case AnnotationTool.Rectangle: _rectThicknessIndex = clamped; break;
        }
        ActiveStrokeWidth = ThicknessPresets[clamped];
    }

    public int GetThicknessIndex(AnnotationTool tool) => tool switch
    {
        AnnotationTool.Pen => _penThicknessIndex,
        AnnotationTool.Rectangle => _rectThicknessIndex,
        _ => 1,
    };

    public void CancelAnnotationTool()
    {
        SetAnnotationTool(AnnotationTool.None);
    }

    /// <summary>
    /// Handle pointer down in annotation mode. Returns true if a text note dialog
    /// is needed (caller should show dialog and call CompleteTextNote/CompleteTextNoteEdit).
    /// </summary>
    public (bool NeedsTextNoteDialog, bool IsEdit, TextNoteAnnotation? ExistingNote, float PageX, float PageY)
        HandleAnnotationPointerDown(DocumentState? doc, double pageX, double pageY)
    {
        if (doc is null) return default;

        switch (ActiveTool)
        {
            case AnnotationTool.TextSelect:
                _textSelectCharStart = FindNearestCharIndex(doc, (float)pageX, (float)pageY);
                SelectedText = null;
                TextSelectionRects = null;
                break;
            case AnnotationTool.Highlight:
                _highlightCharStart = FindNearestCharIndex(doc, (float)pageX, (float)pageY);
                break;
            case AnnotationTool.Pen:
                _freehandPoints = [new PointF((float)pageX, (float)pageY)];
                break;
            case AnnotationTool.Rectangle:
                _rectStartX = (float)pageX;
                _rectStartY = (float)pageY;
                break;
            case AnnotationTool.TextNote:
                var hitNote = FindTextNoteAtPoint(doc, (float)pageX, (float)pageY);
                return (true, hitNote is not null, hitNote, (float)pageX, (float)pageY);
            case AnnotationTool.Eraser:
                EraseAtPoint(doc, (float)pageX, (float)pageY);
                break;
        }
        return default;
    }

    /// <summary>
    /// Complete a text note creation after dialog returns.
    /// </summary>
    public void CompleteTextNote(DocumentState? doc, float pageX, float pageY, string text)
    {
        if (doc is null || string.IsNullOrEmpty(text)) return;
        var note = new TextNoteAnnotation
        {
            X = pageX,
            Y = pageY,
            Color = ActiveAnnotationColor,
            Opacity = ActiveAnnotationOpacity,
            Text = text,
        };
        doc.AddAnnotation(doc.CurrentPage, note);
    }

    /// <summary>
    /// Complete a text note edit after dialog returns.
    /// </summary>
    public void CompleteTextNoteEdit(DocumentState? doc, TextNoteAnnotation note, string newText)
    {
        if (doc is null) return;
        doc.UpdateAnnotationText(doc.CurrentPage, note, newText);
    }

    public bool HandleAnnotationPointerMove(DocumentState? doc, double pageX, double pageY,
        bool shiftHeld = false)
    {
        if (doc is null) return false;
        bool changed = false;

        switch (ActiveTool)
        {
            case AnnotationTool.TextSelect when _textSelectCharStart >= 0:
                int tsEnd = FindNearestCharIndex(doc, (float)pageX, (float)pageY);
                if (tsEnd >= 0)
                {
                    int tsStart = Math.Min(_textSelectCharStart, tsEnd);
                    int tsLen = Math.Max(_textSelectCharStart, tsEnd) - tsStart + 1;
                    var pageText = doc.GetOrExtractText(doc.CurrentPage);
                    TextSelectionRects = BuildHighlightRects(pageText, tsStart, tsLen);
                    int textEnd = Math.Min(tsStart + tsLen, pageText.Text.Length);
                    SelectedText = tsStart < pageText.Text.Length
                        ? pageText.Text[tsStart..textEnd]
                        : null;
                    changed = true;
                }
                break;
            case AnnotationTool.Highlight when _highlightCharStart >= 0:
                int endChar = FindNearestCharIndex(doc, (float)pageX, (float)pageY);
                if (endChar >= 0)
                {
                    int start = Math.Min(_highlightCharStart, endChar);
                    int end = Math.Max(_highlightCharStart, endChar);
                    var pageText = doc.GetOrExtractText(doc.CurrentPage);
                    var rects = BuildHighlightRects(pageText, start, end - start + 1);
                    PreviewAnnotation = new HighlightAnnotation
                    {
                        Rects = rects,
                        Color = ActiveAnnotationColor,
                        Opacity = ActiveAnnotationOpacity,
                    };
                    changed = true;
                }
                break;
            case AnnotationTool.Pen when _freehandPoints is not null:
                _freehandPoints.Add(new PointF((float)pageX, (float)pageY));
                List<PointF> penPoints = shiftHeld
                    ? [_freehandPoints[0], new PointF((float)pageX, (float)pageY)]
                    : [.. _freehandPoints];
                PreviewAnnotation = new FreehandAnnotation
                {
                    Points = penPoints,
                    Color = ActiveAnnotationColor,
                    Opacity = ActiveAnnotationOpacity,
                    StrokeWidth = ActiveStrokeWidth,
                };
                changed = true;
                break;
            case AnnotationTool.Rectangle:
                float rx = Math.Min(_rectStartX, (float)pageX);
                float ry = Math.Min(_rectStartY, (float)pageY);
                float rw = Math.Abs((float)pageX - _rectStartX);
                float rh = Math.Abs((float)pageY - _rectStartY);
                PreviewAnnotation = new RectAnnotation
                {
                    X = rx, Y = ry, W = rw, H = rh,
                    Color = ActiveAnnotationColor,
                    Opacity = ActiveAnnotationOpacity,
                    StrokeWidth = ActiveStrokeWidth,
                };
                changed = true;
                break;
        }
        return changed;
    }

    public bool HandleAnnotationPointerUp(DocumentState? doc, double pageX, double pageY)
    {
        if (doc is null) return false;
        bool changed = false;

        switch (ActiveTool)
        {
            case AnnotationTool.TextSelect:
                _textSelectCharStart = -1;
                break;
            case AnnotationTool.Highlight when PreviewAnnotation is HighlightAnnotation h:
                doc.AddAnnotation(doc.CurrentPage, h);
                PreviewAnnotation = null;
                _highlightCharStart = -1;
                changed = true;
                break;
            case AnnotationTool.Pen when PreviewAnnotation is FreehandAnnotation f:
                doc.AddAnnotation(doc.CurrentPage, f);
                PreviewAnnotation = null;
                _freehandPoints = null;
                changed = true;
                break;
            case AnnotationTool.Rectangle when PreviewAnnotation is RectAnnotation r:
                if (r.W > 1 && r.H > 1)
                    doc.AddAnnotation(doc.CurrentPage, r);
                PreviewAnnotation = null;
                changed = true;
                break;
        }
        return changed;
    }

    private void EraseAtPoint(DocumentState doc, float pageX, float pageY)
    {
        var list = GetCurrentPageAnnotations(doc);
        if (list is null) return;
        for (int i = list.Count - 1; i >= 0; i--)
        {
            if (AnnotationGeometry.HitTest(list[i], pageX, pageY))
            {
                doc.RemoveAnnotation(doc.CurrentPage, list[i]);
                return;
            }
        }
    }

    public void CopySelectedText()
    {
        if (SelectedText is not null)
            CopyToClipboard?.Invoke(SelectedText);
    }

    public void UndoAnnotation(DocumentState? doc) => doc?.Undo();

    public void RedoAnnotation(DocumentState? doc) => doc?.Redo();

    /// <summary>
    /// Delete the currently selected annotation (if any) in browse mode.
    /// Returns true if an annotation was deleted.
    /// </summary>
    public bool DeleteSelectedAnnotation(DocumentState? doc)
    {
        if (doc is null || SelectedAnnotation is null) return false;
        doc.RemoveAnnotation(doc.CurrentPage, SelectedAnnotation);
        SelectedAnnotation = null;
        return true;
    }

    // --- Browse-mode interaction (select, move, resize) ---

    /// <summary>
    /// Handle pointer down in browse mode. Returns true if an annotation was hit
    /// (caller should not start camera pan).
    /// </summary>
    public bool HandleBrowsePointerDown(DocumentState? doc, float pageX, float pageY)
    {
        if (doc is null) return false;
        var list = GetCurrentPageAnnotations(doc);

        // First check resize handles on selected freehand
        if (SelectedAnnotation is FreehandAnnotation selectedFreehand && list is not null)
        {
            var handle = AnnotationGeometry.HitTestResizeHandle(selectedFreehand, pageX, pageY);
            if (handle != ResizeHandle.None)
            {
                _resizeHandle = handle;
                _dragStartPageX = pageX;
                _dragStartPageY = pageY;
                var bounds = AnnotationGeometry.GetAnnotationBounds(selectedFreehand);
                _resizeStartBounds = bounds ?? RectF.Empty;
                _resizeOriginalPoints = [.. selectedFreehand.Points];
                return true;
            }
        }

        // Hit-test annotations (top to bottom)
        if (list is not null)
        {
            for (int i = list.Count - 1; i >= 0; i--)
            {
                if (AnnotationGeometry.HitTest(list[i], pageX, pageY))
                {
                    SelectedAnnotation = list[i];
                    _dragAnnotation = list[i];
                    _dragStartPageX = pageX;
                    _dragStartPageY = pageY;
                    _dragOriginalPosition = PositionSnapshot.Capture(list[i]);
                    _resizeHandle = ResizeHandle.None;
                    return true;
                }
            }
        }

        // Clicked empty space — deselect
        SelectedAnnotation = null;
        _dragAnnotation = null;
        _resizeHandle = ResizeHandle.None;
        return false;
    }

    /// <summary>
    /// Handle pointer move in browse mode (dragging annotation or resizing).
    /// Returns true if annotations changed.
    /// </summary>
    public bool HandleBrowsePointerMove(float pageX, float pageY)
    {
        if (_resizeHandle != ResizeHandle.None && _resizeOriginalPoints is not null
            && SelectedAnnotation is FreehandAnnotation freehand)
        {
            ResizeFreehand(freehand, pageX, pageY);
            return true;
        }

        if (_dragAnnotation is null) return false;

        float dx = pageX - _dragStartPageX;
        float dy = pageY - _dragStartPageY;

        MoveAnnotation(_dragAnnotation, dx, dy, _dragOriginalPosition!);
        return true;
    }

    /// <summary>
    /// Handle pointer up in browse mode. Creates undo action if moved/resized.
    /// Returns true if annotations changed.
    /// </summary>
    public bool HandleBrowsePointerUp(DocumentState? doc, float pageX, float pageY)
    {
        if (doc is null) return false;

        if (_resizeHandle != ResizeHandle.None && _resizeOriginalPoints is not null
            && SelectedAnnotation is FreehandAnnotation freehand)
        {
            List<PointF> newPoints = [.. freehand.Points];
            if (!PointsEqual(_resizeOriginalPoints, newPoints))
                doc.PushUndoAction(new ResizeFreehandAction(freehand, _resizeOriginalPoints, newPoints));
            _resizeHandle = ResizeHandle.None;
            _resizeOriginalPoints = null;
            return true;
        }

        if (_dragAnnotation is not null && _dragOriginalPosition is not null)
        {
            var newPosition = PositionSnapshot.Capture(_dragAnnotation);
            float dx = pageX - _dragStartPageX;
            float dy = pageY - _dragStartPageY;
            if (Math.Abs(dx) > 0.5f || Math.Abs(dy) > 0.5f)
                doc.PushUndoAction(new MoveAnnotationAction(_dragAnnotation, _dragOriginalPosition, newPosition));
            _dragAnnotation = null;
            _dragOriginalPosition = null;
            return true;
        }

        return false;
    }

    /// <summary>
    /// Handle click in browse mode on text notes.
    /// Single click: toggle expand/collapse. Double click: open edit dialog.
    /// </summary>
    public (bool Handled, TextNoteAnnotation? EditNote) HandleBrowseClick(DocumentState? doc, float pageX, float pageY, bool isDoubleClick = false)
    {
        if (doc is null) return (false, null);
        var list = GetCurrentPageAnnotations(doc);
        if (list is null) return (false, null);

        for (int i = list.Count - 1; i >= 0; i--)
        {
            if (list[i] is TextNoteAnnotation tn && AnnotationGeometry.HitTest(tn, pageX, pageY))
            {
                SelectedAnnotation = tn;
                if (isDoubleClick)
                {
                    tn.IsExpanded = true;
                    return (true, tn);
                }
                tn.IsExpanded = !tn.IsExpanded;
                return (true, null);
            }
        }
        return (false, null);
    }

    private static void MoveAnnotation(Annotation annotation, float dx, float dy, PositionSnapshot original)
    {
        switch (annotation)
        {
            case TextNoteAnnotation tn:
                tn.X = original.X + dx;
                tn.Y = original.Y + dy;
                break;
            case FreehandAnnotation f when original.Points is not null:
                for (int i = 0; i < f.Points.Count && i < original.Points.Count; i++)
                    f.Points[i] = new PointF(original.Points[i].X + dx, original.Points[i].Y + dy);
                break;
            case HighlightAnnotation h when original.Rects is not null:
                for (int i = 0; i < h.Rects.Count && i < original.Rects.Count; i++)
                {
                    var or = original.Rects[i];
                    h.Rects[i] = new HighlightRect(or.X + dx, or.Y + dy, or.W, or.H);
                }
                break;
            case RectAnnotation r:
                r.X = original.X + dx;
                r.Y = original.Y + dy;
                break;
        }
    }

    private void ResizeFreehand(FreehandAnnotation freehand, float pageX, float pageY)
    {
        if (_resizeOriginalPoints is null) return;
        var oldBounds = _resizeStartBounds;
        if (oldBounds.Width < 1 || oldBounds.Height < 1) return;

        var newBounds = AnnotationGeometry.ComputeNewBounds(oldBounds, _resizeHandle, pageX, pageY, _dragStartPageX, _dragStartPageY);

        // Minimum size constraint
        if (newBounds.Width < 10 || newBounds.Height < 10) return;

        // Scale all points proportionally
        for (int i = 0; i < freehand.Points.Count && i < _resizeOriginalPoints.Count; i++)
        {
            var op = _resizeOriginalPoints[i];
            float nx = (op.X - oldBounds.Left) / oldBounds.Width;
            float ny = (op.Y - oldBounds.Top) / oldBounds.Height;
            freehand.Points[i] = new PointF(newBounds.Left + nx * newBounds.Width, newBounds.Top + ny * newBounds.Height);
        }
    }

    private static bool PointsEqual(List<PointF> a, List<PointF> b)
    {
        if (a.Count != b.Count) return false;
        for (int i = 0; i < a.Count; i++)
            if (Math.Abs(a[i].X - b[i].X) > 0.1f || Math.Abs(a[i].Y - b[i].Y) > 0.1f)
                return false;
        return true;
    }

    public static List<Annotation>? GetCurrentPageAnnotations(DocumentState doc)
    {
        return doc.Annotations.Pages.TryGetValue(doc.CurrentPage, out var list) ? list : null;
    }

    public static TextNoteAnnotation? FindTextNoteAtPoint(DocumentState doc, float pageX, float pageY)
    {
        if (GetCurrentPageAnnotations(doc) is not { } list) return null;
        for (int i = list.Count - 1; i >= 0; i--)
        {
            if (list[i] is TextNoteAnnotation tn && AnnotationGeometry.HitTest(tn, pageX, pageY))
                return tn;
        }
        return null;
    }

    private static int FindNearestCharIndex(DocumentState doc, float pageX, float pageY)
    {
        var pageText = doc.GetOrExtractText(doc.CurrentPage);
        if (pageText.CharBoxes.Count == 0) return -1;

        float bestDist = float.MaxValue;
        int bestIdx = -1;
        for (int i = 0; i < pageText.CharBoxes.Count; i++)
        {
            var cb = pageText.CharBoxes[i];
            if (cb.Left == 0 && cb.Right == 0 && cb.Top == 0 && cb.Bottom == 0) continue;
            float cx = (cb.Left + cb.Right) / 2;
            float cy = (cb.Top + cb.Bottom) / 2;
            float dist = (cx - pageX) * (cx - pageX) + (cy - pageY) * (cy - pageY);
            if (dist < bestDist) { bestDist = dist; bestIdx = i; }
        }
        return bestIdx;
    }

    public static List<HighlightRect> BuildHighlightRects(PageText pageText, int charStart, int charLength)
    {
        var rects = new List<HighlightRect>();
        foreach (var (l, t, r, b) in MergeCharBoxesIntoLines(pageText, charStart, charLength))
            rects.Add(new HighlightRect(l - 1, t, r - l + 2, b - t));
        return rects;
    }

    private static IEnumerable<(float Left, float Top, float Right, float Bottom)> MergeCharBoxesIntoLines(
        PageText pageText, int charStart, int charLength)
    {
        if (pageText.CharBoxes.Count == 0) yield break;

        int end = Math.Min(charStart + charLength, pageText.CharBoxes.Count);
        float curLeft = 0, curTop = 0, curRight = 0, curBottom = 0;
        bool hasRect = false;
        const float lineThreshold = 4f;

        for (int i = charStart; i < end; i++)
        {
            var cb = pageText.CharBoxes[i];
            if (cb.Left == 0 && cb.Right == 0 && cb.Top == 0 && cb.Bottom == 0) continue;

            if (!hasRect)
            {
                curLeft = cb.Left; curTop = cb.Top; curRight = cb.Right; curBottom = cb.Bottom;
                hasRect = true;
            }
            else if (Math.Abs(cb.Top - curTop) < lineThreshold)
            {
                curLeft = Math.Min(curLeft, cb.Left);
                curRight = Math.Max(curRight, cb.Right);
                curTop = Math.Min(curTop, cb.Top);
                curBottom = Math.Max(curBottom, cb.Bottom);
            }
            else
            {
                yield return (curLeft, curTop, curRight, curBottom);
                curLeft = cb.Left; curTop = cb.Top; curRight = cb.Right; curBottom = cb.Bottom;
            }
        }

        if (hasRect)
            yield return (curLeft, curTop, curRight, curBottom);
    }
}
