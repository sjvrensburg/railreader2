namespace RailReader.Core.Tests;

/// <summary>
/// Thread marshaller that executes actions synchronously on the calling thread.
/// Used for headless testing without a UI thread.
/// </summary>
public sealed class SynchronousThreadMarshaller : IThreadMarshaller
{
    public void Post(Action action) => action();
}
