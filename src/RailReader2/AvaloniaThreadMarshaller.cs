using Avalonia.Threading;
using RailReader.Core;

namespace RailReader2;

public sealed class AvaloniaThreadMarshaller : IThreadMarshaller
{
    public void Post(Action action) => Dispatcher.UIThread.Post(action);
}
