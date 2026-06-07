using RailReader.Demo;
using Xunit;

namespace RailReader.Demo.Tests;

public class DemoSequencerTests
{
    /// <summary>Records every verb call; optionally auto-raises the matching signal so a
    /// signal-waiting sequencer proceeds. Lets tests drive the sequencer with no app or bus.</summary>
    private sealed class FakeControlClient : IControlClient
    {
        public List<string> Calls { get; } = [];
        public bool AutoSignal { get; set; } = true;
        public bool FrameResult { get; set; } = true;

        public event Action? Settled;
        public event Action<int>? PageChanged;
        public event Action<string>? DocumentOpened;

        public Task<bool> OpenDocumentAsync(string path, CancellationToken ct)
        {
            Calls.Add($"open:{path}");
            DocumentOpened?.Invoke(path);
            return Task.FromResult(true);
        }

        public Task GoToPageAsync(int page, CancellationToken ct)
        {
            Calls.Add($"goto:{page}");
            if (AutoSignal) PageChanged?.Invoke(page);
            return Task.CompletedTask;
        }

        public Task FitPageAsync(CancellationToken ct) { Calls.Add("fit_page"); return Task.CompletedTask; }
        public Task FitWidthAsync(CancellationToken ct) { Calls.Add("fit_width"); return Task.CompletedTask; }

        public Task<bool> FrameRoleAsync(string role, int occurrence, double zoom, CancellationToken ct)
        {
            Calls.Add($"frame_role:{role}:{occurrence}:{zoom}");
            if (AutoSignal && FrameResult) Settled?.Invoke();
            return Task.FromResult(FrameResult);
        }

        public Task<bool> FrameBlockAsync(int index, double zoom, CancellationToken ct)
        {
            Calls.Add($"frame_block:{index}:{zoom}");
            if (AutoSignal && FrameResult) Settled?.Invoke();
            return Task.FromResult(FrameResult);
        }

        public ValueTask DisposeAsync() => default;
    }

    private static (DemoSequencer seq, List<TimeSpan> delays) Make(IControlClient client)
    {
        var delays = new List<TimeSpan>();
        var seq = new DemoSequencer(client, TextWriter.Null,
            delay: (ts, _) => { delays.Add(ts); return Task.CompletedTask; },
            settleTimeout: TimeSpan.FromSeconds(10));
        return (seq, delays);
    }

    [Fact]
    public async Task IssuesVerbsInScriptOrder()
    {
        var fake = new FakeControlClient();
        var script = DslParser.Parse("""
            source: /tmp/x.pdf
            steps:
              - open
              - goto_page: 3
              - fit_page
              - frame_role: { role: figure, index: 1, zoom: 2.5 }
              - frame_block: { index: 7 }
            """);

        var (seq, _) = Make(fake);
        await seq.RunAsync(script);

        Assert.Equal(
            ["open:/tmp/x.pdf", "goto:3", "fit_page", "frame_role:figure:1:2.5", "frame_block:7:0"],
            fake.Calls);
    }

    [Fact]
    public async Task HoldDwellsForParsedDuration()
    {
        var fake = new FakeControlClient();
        var script = DslParser.Parse("steps:\n  - hold: 800ms\n  - hold: 2s");
        var (seq, delays) = Make(fake);

        await seq.RunAsync(script);

        Assert.Contains(TimeSpan.FromMilliseconds(800), delays);
        Assert.Contains(TimeSpan.FromSeconds(2), delays);
    }

    [Fact]
    public async Task FrameThatMatchesNothing_DoesNotWaitForSettled()
    {
        var fake = new FakeControlClient { AutoSignal = false, FrameResult = false };
        var script = DslParser.Parse("steps:\n  - frame_role: { role: figure }");
        var (seq, delays) = Make(fake);

        await seq.RunAsync(script);   // would hang on a real Settled wait; returns because ok==false skips it

        Assert.DoesNotContain(TimeSpan.FromSeconds(10), delays); // settle timeout never armed
    }

    [Fact]
    public async Task FrameWithNoSettledSignal_TimesOutAndContinues()
    {
        var fake = new FakeControlClient { AutoSignal = false, FrameResult = true };
        var script = DslParser.Parse("steps:\n  - frame_role: { role: figure }\n  - fit_page");
        var (seq, delays) = Make(fake);

        await seq.RunAsync(script);

        Assert.Contains(TimeSpan.FromSeconds(10), delays);          // armed the timeout
        Assert.Equal(["frame_role:figure:0:0", "fit_page"], fake.Calls); // still ran the next step
    }

    [Fact]
    public async Task ExplicitDurationWait_SleepsInsteadOfAwaitingSignal()
    {
        var fake = new FakeControlClient { AutoSignal = false };
        var script = DslParser.Parse("steps:\n  - frame_role: { role: table }\n    wait: 500ms");
        var (seq, delays) = Make(fake);

        await seq.RunAsync(script);

        Assert.Contains(TimeSpan.FromMilliseconds(500), delays);
        Assert.DoesNotContain(TimeSpan.FromSeconds(10), delays); // no signal timeout for an explicit duration wait
    }

    [Fact]
    public async Task UnknownVerb_Throws()
    {
        var fake = new FakeControlClient();
        var script = DslParser.Parse("steps:\n  - teleport: 5");
        var (seq, _) = Make(fake);

        var ex = await Assert.ThrowsAsync<DemoRunException>(() => seq.RunAsync(script));
        Assert.Contains("unknown verb 'teleport'", ex.Message);
    }

    [Fact]
    public async Task OpenWithoutSource_Throws()
    {
        var fake = new FakeControlClient();
        var script = DslParser.Parse("steps:\n  - open");
        var (seq, _) = Make(fake);

        await Assert.ThrowsAsync<DemoRunException>(() => seq.RunAsync(script));
    }
}
