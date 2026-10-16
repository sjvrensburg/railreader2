using RailReader.Core.Models;

namespace RailReader2.ViewModels;

/// <summary>
/// Undoable recolour of an existing annotation. Core has no recolour action, but
/// <see cref="Annotation.Color"/> is public and settable (see <see cref="ChangeReviewStateAction"/>
/// for the same consumer-side pattern). <see cref="Annotation.ColorComponents"/> — the floats read
/// from a PDF's /C — describe the OLD colour, and the PDF writer takes the hex <c>Color</c> as the
/// source of truth, so redo drops them and undo restores them alongside the old hex.
/// </summary>
public sealed class ChangeColorAction(Annotation annotation, string oldColor, float[]? oldComponents, string newColor)
    : IUndoAction
{
    public void Undo(AnnotationFile file)
    {
        annotation.Color = oldColor;
        annotation.ColorComponents = oldComponents;
    }

    public void Redo(AnnotationFile file)
    {
        annotation.Color = newColor;
        annotation.ColorComponents = null;
    }
}
