using RailReader.Core.Models;

namespace RailReader2.ViewModels;

/// <summary>
/// Undoable change of an annotation's review state (Accepted/Rejected/etc.).
/// Core has no built-in state-edit action, but <see cref="Annotation.State"/> is a
/// public settable property and <c>IUndoAction</c> is public, so we define the action
/// consumer-side. Like Core's MoveAnnotationAction it mutates the annotation directly
/// and ignores the <see cref="AnnotationFile"/> parameter.
/// </summary>
public sealed class ChangeReviewStateAction(Annotation annotation, ReviewState oldState, ReviewState newState)
    : IUndoAction
{
    public void Undo(AnnotationFile file) => annotation.State = oldState;
    public void Redo(AnnotationFile file) => annotation.State = newState;
}
