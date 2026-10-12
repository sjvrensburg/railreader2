using Avalonia.Platform.Storage;
using CommunityToolkit.Mvvm.Input;
using RailReader.Core;
using RailReader.Core.Models;
using RailReader.Core.Services;
using RailReader.Renderer.Skia;
using RailReader2.Services;
using RailReader2.Views;

namespace RailReader2.ViewModels;

// Annotations: mode, tools, markup, pointer handlers, browse mode, undo/redo, review state, export/import
public sealed partial class MainWindowViewModel
{
    public void ToggleAnnotationMode()
    {
        // Core refuses annotation authoring while the view is rotated (stored geometry is the
        // rotation-0 frame — Core 0.47.0), and the shell hides annotation display too. Refuse the
        // mode up front with an explanation instead of offering tools that can't work.
        if (!IsAnnotationMode && IsViewRotated)
        {
            ShowStatusToast("Annotations are unavailable while the view is rotated");
            return;
        }
        IsAnnotationMode = !IsAnnotationMode;
    }

    public void SetAnnotationTool(AnnotationTool tool)
    {
        // As in ToggleAnnotationMode: no annotation tools while the view is rotated.
        if (tool is not AnnotationTool.None and not AnnotationTool.TextSelect && IsViewRotated)
        {
            ShowStatusToast("Annotations are unavailable while the view is rotated");
            return;
        }

        // Picking a real annotation tool implies annotation mode; Browse/TextSelect do not.
        if (tool is not AnnotationTool.None and not AnnotationTool.TextSelect)
            IsAnnotationMode = true;

        _controller.Annotations.SetAnnotationTool(tool);

        if (tool != AnnotationTool.TextSelect)
        {
            OnPropertyChanged(nameof(SelectedText));
            OnPropertyChanged(nameof(TextSelectionRects));
            InvalidateAnnotations();
        }

        OnPropertyChanged(nameof(IsAnnotating));
        OnPropertyChanged(nameof(ActiveTool));
    }

    /// <summary>Sets a comment's review state (Accepted/Rejected/etc.) as an undoable
    /// edit. Core writes /State to the PDF on save; the undo action is railreader2-side.</summary>
    public void SetReviewState(Annotation ann, ReviewState newState)
    {
        if (_controller.FocusedViewport?.Owner is not { } doc) return;
        if (ann.State == newState) return;

        var action = new ChangeReviewStateAction(ann, ann.State, newState);
        ann.State = newState;
        ann.ModifiedUtc = DateTimeOffset.UtcNow;
        doc.PushUndoAction(action);

        OnPropertyChanged(nameof(SelectedAnnotation));
        NotifyAnnotationsMutated();
        InvalidateAnnotations();
    }

    /// <summary>Navigate to a comment: jump to its page, select it, and recenter on it.</summary>
    public void NavigateToAnnotation(int page, Annotation ann)
    {
        if (IsScanAllActive) return;
        GoToPage(page);
        SelectedAnnotation = ann;
        ScrollToAnnotation(ann);
        OnPropertyChanged(nameof(SelectedAnnotation));
        InvalidateAnnotations();
    }

    /// <summary>Center the view on an annotation. In rail mode the rail owns the camera,
    /// so drive the rail to the block and snap horizontally (mirrors search navigation);
    /// otherwise recenter the camera directly.</summary>
    public void ScrollToAnnotation(Annotation ann)
    {
        if (_controller.FocusedViewport?.Owner is not { } doc) return;
        if (AnnotationGeometry.GetAnnotationBounds(ann) is not { } b) return;

        var (ww, wh) = FocusedViewportSize();
        double centerX = (b.Left + b.Right) / 2.0;
        double centerY = (b.Top + b.Bottom) / 2.0;

        if (doc.Rail.Active && doc.Rail.HasAnalysis)
        {
            doc.Rail.FindBlockNearPoint(centerX, centerY);
            doc.Rail.StartSnapToPoint(doc.Camera.OffsetX, doc.Camera.OffsetY, doc.Camera.Zoom, ww, wh, centerX);
            RequestAnimationFrame();
        }
        else
        {
            doc.Camera.OffsetX = ww / 2.0 - centerX * doc.Camera.Zoom;
            doc.Camera.OffsetY = wh / 2.0 - centerY * doc.Camera.Zoom;
            doc.ClampCamera(ww, wh);
        }
        InvalidateCameraAndTab();
    }

    public void CancelAnnotationTool()
    {
        _controller.Annotations.CancelAnnotationTool();
        OnPropertyChanged(nameof(SelectedText));
        OnPropertyChanged(nameof(IsAnnotating));
        OnPropertyChanged(nameof(ActiveTool));
        InvalidateAnnotations();
    }

    public void HandleAnnotationPointerDown(double pageX, double pageY)
    {
        // Belt-and-braces: annotation mode can't be entered while rotated, and rotating exits it,
        // but Core would also refuse the resulting AddAnnotation — don't start a doomed gesture.
        if (IsViewRotated) return;

        // The drag-over-text markup tools (Highlight/Underline/StrikeOut/Squiggly) hit-test against
        // the page's extracted characters and are a silent no-op when there are none (a scanned page
        // with OCR off or in Lines-only mode) — Core has no text to anchor to, so it never builds a
        // preview annotation and the drag produces nothing, with no error. Catch it here so the user
        // gets an explanation instead of a tool that looks broken; Pen/Rectangle/TextNote/FreeText/
        // Eraser don't need a text layer and are unaffected.
        if (_controller.FocusedViewport is { } vp &&
            AnnotationInteractionHandler.IsTextMarkupTool(ActiveTool) &&
            vp.Owner.GetOrExtractText(vp.CurrentPage).CharBoxes.Count == 0)
        {
            ShowStatusToast(
                "No selectable text on this page — try Pen/Rectangle, or",
                "set scanned pages to Read the text",
                () => OpenSettingsTab("Scanned Pages"));
            return;
        }

        var (needsDialog, isEdit, existingNote, px, py) = _controller.Annotations.HandleAnnotationPointerDown(_controller.FocusedViewport, pageX, pageY);

        if (needsDialog)
        {
            if (isEdit && existingNote is not null)
                FireAndForget(EditAnnotationComment(existingNote), nameof(EditAnnotationComment));
            else
                FireAndForget(CreateTextNote(px, py), nameof(CreateTextNote));
        }
        else
        {
            OnPropertyChanged(nameof(SelectedText));
            InvalidateAnnotations();
        }
    }

    public void HandleAnnotationPointerMove(double pageX, double pageY, bool shiftHeld = false)
    {
        if (_controller.Annotations.HandleAnnotationPointerMove(_controller.FocusedViewport, pageX, pageY, shiftHeld))
        {
            OnPropertyChanged(nameof(SelectedText));
            InvalidateAnnotations();
        }
    }

    public void HandleAnnotationPointerUp(double pageX, double pageY)
    {
        // Auto-merge is a preference, but it can't apply where the merge would be undone on reload:
        // on a read-only/signed PDF only RailReader-authored annotations reach the sidecar, so a
        // reviewer's in-PDF highlight merged away here would come back from the PDF next time,
        // beside the merged copy. Re-decide per gesture (the focused document changes).
        _controller.Annotations.AutoMergeTextMarkup =
            AutoMergeTextMarkup && _controller.FocusedViewport is { } vpUp && IsAnnotationStorageInPdf(vpUp.Owner);
        if (_controller.Annotations.HandleAnnotationPointerUp(_controller.FocusedViewport, pageX, pageY))
        {
            InvalidateAnnotations();
            NotifyAnnotationsMutated();
        }

        // FreeText tool: Core finalised a box and is waiting for the UI to supply its text.
        if (_controller.Annotations.PendingFreeText is not null)
            FireAndForget(CompletePendingFreeText(), nameof(CompletePendingFreeText));
    }

    private async Task CompletePendingFreeText()
    {
        if (_window is null) { _controller.Annotations.CancelPendingFreeText(); return; }

        var dialog = new TextNoteDialog { FontSize = CurrentFontSize };
        var result = await dialog.ShowDialog<string?>(_window);
        if (string.IsNullOrEmpty(result))
        {
            _controller.Annotations.CancelPendingFreeText();
        }
        else
        {
            _controller.Annotations.CommitPendingFreeText(_controller.FocusedViewport, result);
            NotifyAnnotationsMutated();
        }
        InvalidateAnnotations();
    }

    private async Task CreateTextNote(float pageX, float pageY)
    {
        if (_window is null) return;
        var dialog = new TextNoteDialog { FontSize = CurrentFontSize };
        var result = await dialog.ShowDialog<string?>(_window);
        if (string.IsNullOrEmpty(result)) return;

        _controller.Annotations.CompleteTextNote(_controller.FocusedViewport, pageX, pageY, result);
        InvalidateAnnotations();
        NotifyAnnotationsMutated();
    }

    /// <summary>Edit the text of a sticky note, or the comment attached to any other annotation
    /// (a highlight's comment is what this feature is for; rectangles and pen strokes take one too).
    /// Same dialog as a new note; Cancel leaves the annotation untouched, an emptied box clears the
    /// comment. One undo step in Core (<c>EditContentsAction</c>).</summary>
    private async Task EditAnnotationComment(Annotation ann)
    {
        if (_window is null) return;
        var dialog = new TextNoteDialog(ann.EffectiveContents ?? "")
        {
            FontSize = CurrentFontSize,
            Title = CommentDialogTitle(ann),
        };
        var result = await dialog.ShowDialog<string?>(_window);
        if (result is null) return;

        _controller.Annotations.CompleteCommentEdit(_controller.FocusedViewport, ann, result);
        InvalidateAnnotations();
        NotifyAnnotationsMutated();
    }

    private static string CommentDialogTitle(Annotation ann) => ann switch
    {
        TextNoteAnnotation => "Text Note",
        FreeTextAnnotation => "Text Box",
        TextMarkupAnnotation m => $"Comment on {MarkupNoun(m)}",
        _ => "Comment",
    };

    /// <summary>What the UI calls a text-markup annotation of this kind, lower case.</summary>
    public static string MarkupNoun(TextMarkupAnnotation m) => m switch
    {
        UnderlineAnnotation => "underline",
        StrikeOutAnnotation => "strikethrough",
        SquigglyAnnotation => "squiggle",
        _ => "highlight",
    };

    /// <summary>True when the annotation's comment can be edited from the menus: everything but a
    /// text box, whose body is its visible text and has no edit path yet.</summary>
    public static bool CanEditComment(Annotation ann) => ann is not FreeTextAnnotation;

    /// <summary>Menu/double-click entry: edit the selected annotation's note or comment.</summary>
    public void EditSelectedAnnotationComment()
    {
        if (IsViewRotated)
        {
            ShowStatusToast("Annotations are unavailable while the view is rotated");
            return;
        }
        if (SelectedAnnotation is not { } ann)
        {
            ShowStatusToast("Select an annotation first — click a highlight or note in browse mode");
            return;
        }
        if (!CanEditComment(ann))
        {
            ShowStatusToast("Text boxes can't take a comment");
            return;
        }
        FireAndForget(EditAnnotationComment(ann), nameof(EditAnnotationComment));
    }

    // --- Browse-mode annotation interaction ---

    /// <summary>
    /// Handle browse-mode pointer down. Returns true if an annotation was hit
    /// (caller should not start camera pan).
    /// </summary>
    public bool HandleBrowsePointerDown(float pageX, float pageY)
    {
        // Annotations are hidden while the view is rotated (their stored geometry is the rotation-0
        // frame) — don't let clicks select/move what isn't drawn.
        if (IsViewRotated) return false;
        bool hit = _controller.Annotations.HandleBrowsePointerDown(_controller.FocusedViewport, pageX, pageY);
        OnPropertyChanged(nameof(SelectedAnnotation));
        InvalidateAnnotations();
        return hit;
    }

    public bool HandleBrowsePointerMove(float pageX, float pageY)
    {
        if (_controller.Annotations.HandleBrowsePointerMove(pageX, pageY))
        {
            InvalidateAnnotations();
            return true;
        }
        return false;
    }

    public bool HandleBrowsePointerUp(float pageX, float pageY)
    {
        if (_controller.Annotations.HandleBrowsePointerUp(_controller.FocusedViewport, pageX, pageY))
        {
            InvalidateAnnotations();
            return true;
        }
        return false;
    }

    /// <summary>Browse-mode click on an annotation: a note toggles open/closed (double-click edits
    /// it); a double-click on text markup selects it and opens its comment (Core 0.64.0). The
    /// returned <c>Edit</c> is the annotation whose dialog was opened, if any.</summary>
    public (bool Handled, Annotation? Edit) HandleBrowseClick(float pageX, float pageY, bool isDoubleClick = false)
    {
        if (IsViewRotated) return (false, null); // annotations are hidden while rotated
        var result = _controller.Annotations.HandleBrowseClick(_controller.FocusedViewport, pageX, pageY, isDoubleClick);
        if (result.Handled)
        {
            OnPropertyChanged(nameof(SelectedAnnotation));
            InvalidateAnnotations();
        }
        if (result.Edit is not null)
            FireAndForget(EditAnnotationComment(result.Edit), nameof(EditAnnotationComment));
        return result;
    }

    public void CopySelectedText()
    {
        _controller.Annotations.CopySelectedText();
    }

    public void DeleteSelectedAnnotation()
    {
        if (_controller.Annotations.DeleteSelectedAnnotation(_controller.FocusedViewport))
        {
            OnPropertyChanged(nameof(SelectedAnnotation));
            InvalidateAnnotations();
            NotifyAnnotationsMutated();
        }
    }

    // --- Copy / cut / paste (Core 0.63.0) ---
    // Notes, text boxes, rectangles and pen strokes are copyable, within and across documents (the
    // clipboard lives in the shared AnnotationInteractionHandler). Text markup is not: it only means
    // something over the text it was made on, so "copying" one copies that text instead.

    public bool HasAnnotationClipboard => _controller.Annotations.HasAnnotationClipboard;

    /// <summary>The topmost annotation under a page point on the focused view's page, or null.
    /// Mirrors Core's browse hit-test priority: a movable annotation wins over text markup, whose
    /// union bounds can cover a note or box drawn earlier.</summary>
    public Annotation? FindAnnotationAt(double pageX, double pageY)
    {
        if (IsViewRotated || _controller.FocusedViewport is not { } vp) return null;
        if (AnnotationInteractionHandler.GetCurrentPageAnnotations(vp) is not { } list) return null;
        Annotation? markup = null;
        for (int i = list.Count - 1; i >= 0; i--)
        {
            if (!AnnotationGeometry.HitTest(list[i], (float)pageX, (float)pageY)) continue;
            if (list[i] is not TextMarkupAnnotation) return list[i];
            markup ??= list[i];
        }
        return markup;
    }

    /// <summary>Select an annotation (e.g. the one under a right-click) so its chrome shows and the
    /// copy/cut/delete actions target it.</summary>
    public void SelectAnnotation(Annotation ann)
    {
        SelectedAnnotation = ann;
        OnPropertyChanged(nameof(SelectedAnnotation));
        InvalidateAnnotations();
    }

    /// <summary>Ctrl+C: selected text first, then the selected annotation.</summary>
    public void CopySelection()
    {
        if (SelectedText is not null)
            CopySelectedText();
        else if (SelectedAnnotation is not null)
            CopySelectedAnnotation();
    }

    public void CopySelectedAnnotation()
    {
        if (IsViewRotated || SelectedAnnotation is not { } ann) return;

        if (ann is TextMarkupAnnotation markup)
        {
            if (MarkupText(markup) is { } text)
            {
                CopyToClipboard?.Invoke(text);
                ShowStatusToast("Copied the highlighted text");
            }
            else
            {
                ShowStatusToast("No text under this highlight to copy");
            }
            return;
        }

        if (!_controller.Annotations.CopySelectedAnnotation(_controller.FocusedViewport))
        {
            ShowStatusToast("This annotation can't be copied");
            return;
        }
        // A note's or text box's body also goes to the system clipboard, so it can be pasted
        // into other apps; Ctrl+V inside railreader2 still pastes the annotation itself.
        if (ann is TextNoteAnnotation or FreeTextAnnotation && !string.IsNullOrWhiteSpace(ann.EffectiveContents))
            CopyToClipboard?.Invoke(ann.EffectiveContents);
        OnPropertyChanged(nameof(HasAnnotationClipboard));
        ShowStatusToast("Annotation copied — Ctrl+V to paste");
    }

    public void CutSelectedAnnotation()
    {
        if (IsViewRotated || SelectedAnnotation is not { } ann) return;
        if (ann is TextMarkupAnnotation)
        {
            ShowStatusToast("Highlights can't be cut — press Delete to remove one");
            return;
        }
        if (!_controller.Annotations.CutSelectedAnnotation(_controller.FocusedViewport))
        {
            ShowStatusToast("This annotation can't be cut");
            return;
        }
        OnPropertyChanged(nameof(HasAnnotationClipboard));
        OnPropertyChanged(nameof(SelectedAnnotation));
        InvalidateAnnotations();
        NotifyAnnotationsMutated();
    }

    /// <summary>Paste the copied annotation onto the focused view's current page — at its original
    /// position (nudged when pasting back onto the source page), or with its top-left at the given
    /// page point (context menu "Paste Here").</summary>
    public void PasteAnnotation(double? pageX = null, double? pageY = null)
    {
        if (!HasAnnotationClipboard || _controller.FocusedViewport is not { } vp) return;
        if (AnnotationInteractionHandler.IsPasteBlockedByRotation(vp))
        {
            ShowStatusToast("Annotations are unavailable while the view is rotated");
            return;
        }
        var pasted = _controller.Annotations.PasteAnnotation(vp, (float?)pageX, (float?)pageY);
        if (pasted is null)
        {
            ShowStatusToast("Couldn't paste the annotation here");
            return;
        }
        OnPropertyChanged(nameof(SelectedAnnotation));
        InvalidateAnnotations();
        NotifyAnnotationsMutated();
    }

    /// <summary>The text under a markup annotation on the focused view's page, line by line
    /// (same per-quad extraction as the Markdown export), or null when there is none.</summary>
    private string? MarkupText(TextMarkupAnnotation markup)
    {
        if (_controller.FocusedViewport is not { } vp) return null;
        var pageText = vp.Owner.GetOrExtractText(vp.CurrentPage);
        var parts = new List<string>();
        foreach (var r in markup.Rects)
            if (pageText.ExtractTextInRect(r.X, r.Y, r.X + r.W, r.Y + r.H) is { Length: > 0 } t)
                parts.Add(t);
        return parts.Count > 0 ? string.Join(" ", parts) : null;
    }

    // --- Merging overlapping text markup (Core 0.64.0) ---
    // Two highlights of the same kind and colour whose line bands overlap become one, keeping the
    // single comment between them (Core refuses when both carry different comments, or when one
    // is part of a reply thread). The result is a fresh RailReader-authored annotation; the sources
    // are deleted from the PDF on save by their /NM — which is exactly what can't happen on a
    // read-only or signed PDF, hence IsAnnotationStorageInPdf below.

    private readonly AnnotationPreferences _annotationPrefs = AnnotationPreferences.Load();

    /// <summary>Merge a new highlight into any same-style highlight it overlaps as it is drawn
    /// (Edit ▸ Merge Overlapping Highlights ▸ Automatically When Drawing). Persisted app-side.</summary>
    public bool AutoMergeTextMarkup
    {
        get => _annotationPrefs.AutoMergeTextMarkup;
        set
        {
            if (_annotationPrefs.AutoMergeTextMarkup == value) return;
            _annotationPrefs.AutoMergeTextMarkup = value;
            _annotationPrefs.Save();
            OnPropertyChanged();
        }
    }

    /// <summary>Full paths of PDFs whose annotation save fell back to the sidecar this session
    /// (read-only or signed), recorded from <c>CompositeAnnotationStore.OnSidecarFallback</c>.</summary>
    private readonly HashSet<string> _sidecarFallbackPaths = new(StringComparer.Ordinal);

    private void RecordSidecarFallback(string pdfPath)
    {
        try { _sidecarFallbackPaths.Add(System.IO.Path.GetFullPath(pdfPath)); }
        catch (Exception) { _sidecarFallbackPaths.Add(pdfPath); }
    }

    /// <summary>Whether this document's annotations are reconciled into the PDF on save, so that
    /// deleting an in-PDF annotation (as a merge does to its sources) sticks. False once the store
    /// has reported a sidecar fallback for it, or when the file isn't writable on disk — the same
    /// test the store makes, minus its signature probe, which Core doesn't expose; a signed-but-
    /// writable PDF is therefore only caught after its first save.</summary>
    private bool IsAnnotationStorageInPdf(DocumentModel doc)
    {
        string path;
        try { path = System.IO.Path.GetFullPath(doc.FilePath); }
        catch (Exception) { path = doc.FilePath; }
        if (_sidecarFallbackPaths.Contains(path)) return false;
        try
        {
            if (!File.Exists(path)) return false;
            if (File.GetAttributes(path).HasFlag(FileAttributes.ReadOnly)) return false;
            using var fs = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite);
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>True when the selected text markup overlaps at least one it could merge with —
    /// gates the context-menu item so it isn't offered for a lone highlight.</summary>
    public bool HasMergePartner(TextMarkupAnnotation markup)
    {
        if (_controller.FocusedViewport is not { } vp) return false;
        if (AnnotationInteractionHandler.GetCurrentPageAnnotations(vp) is not { } list) return false;
        return TextMarkupMerge.FindCluster(list, markup).Count > 1;
    }

    /// <summary>A cluster that includes a reviewer's in-PDF highlight can't be merged on a PDF the
    /// store won't write back into (see <see cref="IsAnnotationStorageInPdf"/>): the sources would
    /// return from the PDF on reload, beside the merged copy.</summary>
    private bool IsMergeBlockedByStorage(Viewport vp, IEnumerable<TextMarkupAnnotation> cluster)
        => cluster.Any(m => m.Source == AnnotationSource.InPdf) && !IsAnnotationStorageInPdf(vp.Owner);

    private const string MergeBlockedMessage =
        "Can't merge a reviewer's highlight here — this PDF is read-only or signed, so it can't be changed";

    /// <summary>Merge the selected highlight with every same-style highlight it overlaps.</summary>
    public void MergeSelectedTextMarkup()
    {
        if (_controller.FocusedViewport is not { } vp) return;
        if (IsViewRotated)
        {
            ShowStatusToast("Annotations are unavailable while the view is rotated");
            return;
        }
        if (SelectedAnnotation is not TextMarkupAnnotation markup)
        {
            ShowStatusToast("Select a highlight first — click it in browse mode");
            return;
        }
        if (AnnotationInteractionHandler.GetCurrentPageAnnotations(vp) is { } list &&
            IsMergeBlockedByStorage(vp, TextMarkupMerge.FindCluster(list, markup)))
        {
            ShowStatusToast(MergeBlockedMessage);
            return;
        }

        var merged = _controller.Annotations.MergeSelectedTextMarkup(vp, out var refusal);
        if (merged is null)
        {
            ShowStatusToast(MergeRefusalMessage(refusal, markup));
            return;
        }
        OnPropertyChanged(nameof(SelectedAnnotation));
        InvalidateAnnotations();
        NotifyAnnotationsMutated();
        ShowStatusToast($"Merged overlapping {MarkupNoun(merged)}s — Ctrl+Z to undo");
    }

    /// <summary>Merge every cluster of overlapping same-style highlights on the focused view's page
    /// (one undo step per cluster; clusters Core refuses are left alone).</summary>
    public void MergePageTextMarkup()
    {
        if (_controller.FocusedViewport is not { } vp) return;
        if (IsViewRotated)
        {
            ShowStatusToast("Annotations are unavailable while the view is rotated");
            return;
        }
        if (AnnotationInteractionHandler.GetCurrentPageAnnotations(vp) is not { } list)
        {
            ShowStatusToast("No highlights on this page");
            return;
        }
        // Refuse the whole sweep rather than silently skipping the protected clusters: the user
        // asked for the page, and a partial result with no explanation reads as a bug.
        foreach (var ann in list)
        {
            if (ann is TextMarkupAnnotation { Source: AnnotationSource.InPdf } m &&
                TextMarkupMerge.FindCluster(list, m).Count > 1 && !IsAnnotationStorageInPdf(vp.Owner))
            {
                ShowStatusToast(MergeBlockedMessage);
                return;
            }
        }

        int removed = vp.Owner.MergeOverlappingTextMarkup(vp.CurrentPage);
        if (removed == 0)
        {
            ShowStatusToast("No overlapping highlights of the same colour on this page");
            return;
        }
        // The selection may have been one of the merged-away sources.
        if (SelectedAnnotation is { } sel && !list.Contains(sel))
            SelectedAnnotation = null;
        OnPropertyChanged(nameof(SelectedAnnotation));
        InvalidateAnnotations();
        NotifyAnnotationsMutated();
        ShowStatusToast(removed == 1
            ? "Merged 2 overlapping highlights into one — Ctrl+Z to undo"
            : $"Merged overlapping highlights — {removed} fewer on this page — Ctrl+Z to undo");
    }

    private static string MergeRefusalMessage(TextMarkupMerge.Refusal refusal, TextMarkupAnnotation markup) => refusal switch
    {
        TextMarkupMerge.Refusal.ConflictingComments =>
            "These highlights have different comments — edit one to match (or clear it) before merging",
        TextMarkupMerge.Refusal.Threaded =>
            "One of these highlights has replies — merging would orphan them",
        _ => $"Nothing to merge — no other {MarkupNoun(markup)} of the same colour overlaps this one",
    };

    public void UndoAnnotation()
    {
        _controller.Annotations.UndoAnnotation(_controller.FocusedViewport);
        InvalidateAnnotations();
        NotifyAnnotationsMutated();
    }

    public void RedoAnnotation()
    {
        _controller.Annotations.RedoAnnotation(_controller.FocusedViewport);
        InvalidateAnnotations();
        NotifyAnnotationsMutated();
    }

    [RelayCommand]
    public async Task ExportAnnotated()
    {
        if (_window is null || ActiveTab is not { } tab) return;

        var file = await _window.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "Export with Annotations",
            DefaultExtension = "pdf",
            FileTypeChoices = [new FilePickerFileType("PDF Files") { Patterns = ["*.pdf"] }],
            SuggestedFileName = Path.GetFileNameWithoutExtension(tab.FilePath) + "_annotated.pdf",
        });
        if (file is null) return;

        var outputPath = file.TryGetLocalPath() ?? file.Path.LocalPath;
        if (outputPath is null) return;

        // A flattened export builds a brand-new PDF that cannot carry the source's encryption.
        // Core refuses rather than silently emit a plaintext copy of a confidential document;
        // catch it here and explain — the annotations are already saved inside the encrypted PDF.
        if (!string.IsNullOrEmpty(tab.Pdf.Password))
        {
            ShowStatusToast("This PDF is password-protected — a flattened export would remove its password, so it's blocked. Your annotations are already saved inside the encrypted PDF.");
            return;
        }

        try
        {
            await Task.Run(() =>
            {
                AnnotationExportService.Export(tab.Pdf, tab.Annotations, outputPath,
                    onProgress: (page, total) =>
                        _logger.Debug($"[Export] Page {page + 1} of {total}..."));
            });
            _logger.Info($"[Export] Saved to {outputPath}");
        }
        catch (InvalidOperationException ex)
        {
            // Backstop in case Core refuses for a reason the pre-check above missed.
            _logger.Error("[Export] Refused", ex);
            ShowStatusToast("This PDF can't be exported to a flattened copy. Your annotations are saved inside the original PDF.");
        }
        catch (Exception ex)
        {
            _logger.Error("[Export] Failed", ex);
        }
    }

    [RelayCommand]
    public async Task ExportAnnotationsJson()
    {
        if (_window is null || ActiveTab is not { } tab) return;

        var file = await _window.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "Export Annotations as JSON",
            DefaultExtension = "json",
            FileTypeChoices = [new FilePickerFileType("JSON Files") { Patterns = ["*.json"] }],
            SuggestedFileName = Path.GetFileNameWithoutExtension(tab.FilePath) + "_annotations.json",
        });
        if (file is null) return;

        var outputPath = file.TryGetLocalPath() ?? file.Path.LocalPath;
        if (outputPath is null) return;

        try
        {
            AnnotationService.ExportJson(tab.Annotations, outputPath);
            ShowStatusToast($"Annotations exported to {Path.GetFileName(outputPath)}");
        }
        catch (Exception ex)
        {
            _logger.Error("[Export JSON] Failed", ex);
        }
    }

    [RelayCommand]
    public async Task ImportAnnotationsJson()
    {
        if (_window is null || ActiveTab is not { } tab) return;

        var files = await _window.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Import Annotations",
            AllowMultiple = false,
            FileTypeFilter = [new FilePickerFileType("JSON Files") { Patterns = ["*.json"] }],
        });
        if (files is not { Count: > 0 }) return;

        var inputPath = files[0].TryGetLocalPath() ?? files[0].Path.LocalPath;
        if (inputPath is null) return;

        try
        {
            var imported = AnnotationService.ImportJson(inputPath);
            if (imported is null)
            {
                ShowStatusToast("Failed to read annotation file");
                return;
            }

            int added = AnnotationService.MergeInto(tab.State.Annotations, imported);
            tab.State.MarkAnnotationsDirty();
            InvalidateAnnotations();
            NotifyAnnotationsMutated();
            // MergeInto also appends imported bookmarks; the Bookmarks pane only listens to
            // BookmarksChanged, so signal it explicitly or imported bookmarks won't appear.
            NotifyBookmarksChanged();
            ShowStatusToast(added > 0
                ? $"Imported {added} annotation(s) from {Path.GetFileName(inputPath)}"
                : "No new annotations found in file");
        }
        catch (Exception ex)
        {
            _logger.Error("[Import JSON] Failed", ex);
            ShowStatusToast("Failed to import annotations");
        }
    }

    [RelayCommand]
    public async Task ExportDiagnosticLog()
    {
        if (_window is null || LogFilePath is null || !File.Exists(LogFilePath)) return;

        var file = await _window.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "Export Diagnostic Log",
            DefaultExtension = "log",
            FileTypeChoices = [new FilePickerFileType("Log Files") { Patterns = ["*.log", "*.txt"] }],
            SuggestedFileName = $"railreader2-log-{DateTime.Now:yyyyMMdd-HHmmss}.log",
        });
        if (file is null) return;

        var outputPath = file.TryGetLocalPath() ?? file.Path.LocalPath;
        if (outputPath is null) return;

        try
        {
            using var output = File.Create(outputPath);
            using var writer = new StreamWriter(output);

            // Include previous session log if available (may contain crash info)
            if (_logger.PreviousLogFilePath is { } prevPath && File.Exists(prevPath))
            {
                writer.WriteLine("=== Previous session ===");
                writer.Flush();
                using (var prev = File.OpenRead(prevPath)) prev.CopyTo(output);
                writer.WriteLine();
                writer.WriteLine("=== Current session ===");
                writer.Flush();
            }

            using (var current = File.OpenRead(LogFilePath)) current.CopyTo(output);
            ShowStatusToast($"Log exported to {Path.GetFileName(outputPath)}");
        }
        catch (Exception ex)
        {
            _logger.Error("[Export Log] Failed", ex);
        }
    }
}
