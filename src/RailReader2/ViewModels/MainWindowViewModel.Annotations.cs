using Avalonia.Platform.Storage;
using CommunityToolkit.Mvvm.Input;
using RailReader.Core;
using RailReader.Core.Models;
using RailReader.Core.Services;
using RailReader.Renderer.Skia;
using RailReader2.Views;

namespace RailReader2.ViewModels;

// Annotations: mode, tools, markup, pointer handlers, browse mode, undo/redo, review state, export/import
public sealed partial class MainWindowViewModel
{
    public void ToggleAnnotationMode() => IsAnnotationMode = !IsAnnotationMode;

    public void SetAnnotationTool(AnnotationTool tool)
    {
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
        if (_controller.ActiveDocument is not { } doc) return;
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
        if (_controller.ActiveDocument is not { } doc) return;
        if (AnnotationGeometry.GetAnnotationBounds(ann) is not { } b) return;

        var (ww, wh) = _controller.GetViewportSize();
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
        var (needsDialog, isEdit, existingNote, px, py) = _controller.Annotations.HandleAnnotationPointerDown(_controller.FocusedViewport, pageX, pageY);

        if (needsDialog)
        {
            if (isEdit && existingNote is not null)
                FireAndForget(EditTextNote(existingNote), nameof(EditTextNote));
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

    private async Task EditTextNote(TextNoteAnnotation note)
    {
        if (_window is null) return;
        var dialog = new TextNoteDialog(note.Text) { FontSize = CurrentFontSize };
        var result = await dialog.ShowDialog<string?>(_window);
        if (result is null) return;

        _controller.Annotations.CompleteTextNoteEdit(_controller.FocusedViewport, note, result);
        InvalidateAnnotations();
        NotifyAnnotationsMutated();
    }

    // --- Browse-mode annotation interaction ---

    /// <summary>
    /// Handle browse-mode pointer down. Returns true if an annotation was hit
    /// (caller should not start camera pan).
    /// </summary>
    public bool HandleBrowsePointerDown(float pageX, float pageY)
    {
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

    public (bool Handled, TextNoteAnnotation? EditNote) HandleBrowseClick(float pageX, float pageY, bool isDoubleClick = false)
    {
        var result = _controller.Annotations.HandleBrowseClick(_controller.FocusedViewport, pageX, pageY, isDoubleClick);
        if (result.Handled)
        {
            OnPropertyChanged(nameof(SelectedAnnotation));
            InvalidateAnnotations();
        }
        if (result.EditNote is not null)
            FireAndForget(EditTextNote(result.EditNote), nameof(EditTextNote));
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
        if (files.Count == 0) return;

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
