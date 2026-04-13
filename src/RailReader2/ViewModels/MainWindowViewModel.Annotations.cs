using Avalonia.Platform.Storage;
using CommunityToolkit.Mvvm.Input;
using RailReader.Core.Models;
using RailReader.Core.Services;
using RailReader.Renderer.Skia;
using RailReader2.Views;

namespace RailReader2.ViewModels;

// Annotations: radial menu, tools, pointer handlers, browse mode, undo/redo, export/import
public sealed partial class MainWindowViewModel
{
    public void OpenRadialMenu(double screenX, double screenY)
    {
        double menuSize = 210 * Config.UiFontScale;
        RadialMenuX = screenX - menuSize / 2;
        RadialMenuY = screenY - menuSize / 2;
        IsRadialMenuOpen = true;
    }

    public void CloseRadialMenu() => IsRadialMenuOpen = false;

    public void SetAnnotationTool(AnnotationTool tool)
    {
        _controller.Annotations.SetAnnotationTool(tool);

        if (tool != AnnotationTool.TextSelect)
        {
            OnPropertyChanged(nameof(SelectedText));
            InvalidateAnnotations();
        }

        CloseRadialMenu();
        OnPropertyChanged(nameof(IsAnnotating));
        OnPropertyChanged(nameof(ActiveTool));
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
        var (needsDialog, isEdit, existingNote, px, py) = _controller.Annotations.HandleAnnotationPointerDown(_controller.ActiveDocument, pageX, pageY);

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
        if (_controller.Annotations.HandleAnnotationPointerMove(_controller.ActiveDocument, pageX, pageY, shiftHeld))
        {
            OnPropertyChanged(nameof(SelectedText));
            InvalidateAnnotations();
        }
    }

    public void HandleAnnotationPointerUp(double pageX, double pageY)
    {
        if (_controller.Annotations.HandleAnnotationPointerUp(_controller.ActiveDocument, pageX, pageY))
            InvalidateAnnotations();
    }

    private async Task CreateTextNote(float pageX, float pageY)
    {
        if (_window is null) return;
        var dialog = new TextNoteDialog { FontSize = CurrentFontSize };
        var result = await dialog.ShowDialog<string?>(_window);
        if (string.IsNullOrEmpty(result)) return;

        _controller.Annotations.CompleteTextNote(_controller.ActiveDocument, pageX, pageY, result);
        InvalidateAnnotations();
    }

    private async Task EditTextNote(TextNoteAnnotation note)
    {
        if (_window is null) return;
        var dialog = new TextNoteDialog(note.Text) { FontSize = CurrentFontSize };
        var result = await dialog.ShowDialog<string?>(_window);
        if (result is null) return;

        _controller.Annotations.CompleteTextNoteEdit(_controller.ActiveDocument, note, result);
        InvalidateAnnotations();
    }

    // --- Browse-mode annotation interaction ---

    /// <summary>
    /// Handle browse-mode pointer down. Returns true if an annotation was hit
    /// (caller should not start camera pan).
    /// </summary>
    public bool HandleBrowsePointerDown(float pageX, float pageY)
    {
        bool hit = _controller.Annotations.HandleBrowsePointerDown(_controller.ActiveDocument, pageX, pageY);
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
        if (_controller.Annotations.HandleBrowsePointerUp(_controller.ActiveDocument, pageX, pageY))
        {
            InvalidateAnnotations();
            return true;
        }
        return false;
    }

    public (bool Handled, TextNoteAnnotation? EditNote) HandleBrowseClick(float pageX, float pageY, bool isDoubleClick = false)
    {
        var result = _controller.Annotations.HandleBrowseClick(_controller.ActiveDocument, pageX, pageY, isDoubleClick);
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
        CloseRadialMenu();
    }

    public void DeleteSelectedAnnotation()
    {
        if (_controller.Annotations.DeleteSelectedAnnotation(_controller.ActiveDocument))
        {
            OnPropertyChanged(nameof(SelectedAnnotation));
            InvalidateAnnotations();
        }
    }

    public void UndoAnnotation()
    {
        _controller.Annotations.UndoAnnotation(_controller.ActiveDocument);
        InvalidateAnnotations();
    }

    public void RedoAnnotation()
    {
        _controller.Annotations.RedoAnnotation(_controller.ActiveDocument);
        InvalidateAnnotations();
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
