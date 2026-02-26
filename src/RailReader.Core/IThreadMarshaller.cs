namespace RailReader.Core;

/// <summary>
/// Abstracts UI thread marshalling so Core types can post work
/// to the main thread without depending on Avalonia.
/// </summary>
public interface IThreadMarshaller
{
    void Post(Action action);
}

/// <summary>
/// Executes actions synchronously on the calling thread.
/// Used for headless contexts (tests, CLI agent) where no UI thread exists.
/// </summary>
public sealed class SynchronousThreadMarshaller : IThreadMarshaller
{
    public void Post(Action action) => action();
}
