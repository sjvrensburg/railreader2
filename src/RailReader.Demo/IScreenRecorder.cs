namespace RailReader.Demo;

/// <summary>
/// Captures the screen for the duration of a demo run. The sequencer brackets the steps with
/// <see cref="StartAsync"/>/<see cref="StopAsync"/>; because step motion is cut on the real
/// <c>Settled</c> signal, the captured video equals the on-screen experience. A seam so the
/// sequencer is testable with a fake (no real capture).
/// </summary>
public interface IScreenRecorder : IAsyncDisposable
{
    /// <summary>Begin capturing to (or toward) <paramref name="outputPath"/> at <paramref name="fps"/>.
    /// <paramref name="drawCursor"/> controls whether the pointer is drawn into the capture.</summary>
    Task StartAsync(string outputPath, int fps, bool drawCursor, CancellationToken ct);

    /// <summary>Stop capturing; returns the final video path actually written.</summary>
    Task<string> StopAsync(CancellationToken ct);
}

/// <summary>No-op recorder used when no <c>recorder:</c> is requested (or for dry runs).</summary>
public sealed class NullScreenRecorder : IScreenRecorder
{
    public Task StartAsync(string outputPath, int fps, bool drawCursor, CancellationToken ct) => Task.CompletedTask;
    public Task<string> StopAsync(CancellationToken ct) => Task.FromResult("");
    public ValueTask DisposeAsync() => default;
}
