using RailReader2.References;
using Xunit;

namespace RailReader.Export.Tests;

public class ReferenceBenchmarkTests
{
    private static MentionResult Resolved(int page, string kind, string number, int tp, int tb) => new()
    {
        Page = page, Kind = kind, Number = number, Status = "resolved", TargetPage = tp, TargetBlock = tb,
    };

    private static MentionResult Abstained(int page, string kind, string number) => new()
    {
        Page = page, Kind = kind, Number = number,
    };

    private static LabelledMention Label(int page, string kind, string number, int? tp = null, int? tb = null) => new()
    {
        Page = page, Kind = kind, Number = number, TargetPage = tp, TargetBlock = tb,
    };

    [Fact]
    public void Score_CountsCorrectWrongAndMissed()
    {
        var truth = new GroundTruth
        {
            Mentions = [Label(1, "Figure", "1", 0, 2), Label(2, "Figure", "2", 0, 5), Label(3, "Table", "1", 3, 1)],
        };
        var failures = new List<string>();
        var m = ReferenceBenchmark.Score(
            [Resolved(1, "Figure", "1", 0, 2), Resolved(2, "Figure", "2", 0, 9), Abstained(3, "Table", "1")],
            truth, failures);

        Assert.Equal(1, m.Overall.TruePositives);
        Assert.Equal(1, m.Overall.FalsePositives);   // wrong target
        Assert.Equal(2, m.Overall.FalseNegatives);   // wrong target + abstained
        Assert.Equal(0.5, m.Overall.Precision);
        Assert.Equal(1.0 / 3, m.Overall.Recall, 3);
        Assert.Equal(2, failures.Count);
    }

    [Fact]
    public void Score_RewardsAbstentionAndPenalisesWrongAnswerForNoTarget()
    {
        var truth = new GroundTruth { Mentions = [Label(1, "Figure", "9"), Label(2, "Table", "9")] };
        var m = ReferenceBenchmark.Score(
            [Abstained(1, "Figure", "9"), Resolved(2, "Table", "9", 2, 0)], truth, null);

        Assert.Equal(1, m.Overall.CorrectAbstentions);
        Assert.Equal(1, m.Overall.FalsePositives);
        Assert.Equal(0.5, m.Overall.AbstentionRate);
    }

    [Fact]
    public void Score_EmptyPagesRejectAnyResolution()
    {
        var truth = new GroundTruth { EmptyPages = [4] };
        var m = ReferenceBenchmark.Score([Resolved(4, "Figure", "1", 3, 0)], truth, null);
        Assert.Equal(1, m.Overall.FalsePositives);
    }

    [Fact]
    public void Score_IgnoresUnlabelledMentionsOnPagesWithoutLabels()
    {
        var truth = new GroundTruth { Mentions = [Label(1, "Figure", "1", 0, 0)] };
        var preds = new List<MentionResult> { Resolved(1, "Figure", "1", 0, 0), Resolved(7, "Figure", "3", 6, 1) };
        var m = ReferenceBenchmark.Score(preds, truth, null);
        Assert.Equal(0, m.Overall.FalsePositives);
        Assert.Equal("unlabelled", preds[1].Outcome);
    }

    [Fact]
    public void Score_KindMatchingIsCaseInsensitive_AndSplitsByKind()
    {
        var truth = new GroundTruth { Mentions = [Label(1, "figure", "1", 0, 0), Label(1, "table", "1", 0, 1)] };
        var m = ReferenceBenchmark.Score(
            [Resolved(1, "Figure", "1", 0, 0), Resolved(1, "Table", "1", 0, 7)], truth, null);
        Assert.Equal(1.0, m.ByKind["Figure"].Precision);
        Assert.Equal(0.0, m.ByKind["Table"].Precision);
    }

    [Fact]
    public void Score_AcceptsAlternateBlocksOfASplitFloat()
    {
        var label = Label(1, "Figure", "2", 1, 2);
        label.AlsoAcceptBlocks = [1];
        var m = ReferenceBenchmark.Score([Resolved(1, "Figure", "2", 1, 1)], new GroundTruth { Mentions = [label] }, null);
        Assert.Equal(1, m.Overall.TruePositives);
    }
}
