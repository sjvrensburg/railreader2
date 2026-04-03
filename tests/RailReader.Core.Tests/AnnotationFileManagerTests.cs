using RailReader.Core;
using RailReader.Core.Models;
using RailReader.Core.Services;
using Xunit;

namespace RailReader.Core.Tests;

public class AnnotationFileManagerTests : IDisposable
{
    private readonly string _tempDir;
    private readonly AnnotationFileManager _manager;

    public AnnotationFileManagerTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"railreader_test_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
        _manager = new AnnotationFileManager(new SynchronousThreadMarshaller());
    }

    public void Dispose()
    {
        _manager.Dispose();
        try { Directory.Delete(_tempDir, recursive: true); } catch { }
    }

    private string MakeFakePdf(string name = "test.pdf")
    {
        var path = Path.Combine(_tempDir, name);
        File.WriteAllText(path, "fake-pdf");
        return path;
    }

    [Fact]
    public void Checkout_ReturnsAnnotationFile()
    {
        var path = MakeFakePdf();
        var file = _manager.Checkout(path);

        Assert.NotNull(file);
    }

    [Fact]
    public void Checkout_SamePath_ReturnsSameInstance()
    {
        var path = MakeFakePdf();
        var file1 = _manager.Checkout(path);
        var file2 = _manager.Checkout(path);

        Assert.Same(file1, file2);
    }

    [Fact]
    public void Checkout_DifferentPaths_ReturnsDifferentInstances()
    {
        var path1 = MakeFakePdf("a.pdf");
        var path2 = MakeFakePdf("b.pdf");
        var file1 = _manager.Checkout(path1);
        var file2 = _manager.Checkout(path2);

        Assert.NotSame(file1, file2);
    }

    [Fact]
    public void Release_LastConsumer_RemovesEntry()
    {
        var path = MakeFakePdf();
        var file1 = _manager.Checkout(path);
        _manager.Release(path);

        // Entry was removed — second checkout creates a fresh instance
        var file2 = _manager.Checkout(path);
        Assert.NotSame(file1, file2);
    }

    [Fact]
    public void Release_WithRemainingConsumer_KeepsEntry()
    {
        var path = MakeFakePdf();
        var file1 = _manager.Checkout(path);
        _manager.Checkout(path); // second consumer
        _manager.Release(path); // first consumer releases

        // Entry should still be alive — checkout returns same instance
        var file3 = _manager.Checkout(path);
        Assert.Same(file1, file3);
    }

    [Fact]
    public void MarkDirty_UnknownPath_DoesNotThrow()
    {
        // Should be a no-op, not an exception
        _manager.MarkDirty("/nonexistent/path.pdf");
    }

    [Fact]
    public void Release_UnknownPath_DoesNotThrow()
    {
        _manager.Release("/nonexistent/path.pdf");
    }

    [Fact]
    public void Dispose_FlushesAndClearsEntries()
    {
        var path = MakeFakePdf();
        _manager.Checkout(path);
        _manager.Dispose();

        // After dispose, new checkout on a fresh manager should not share state
        var manager2 = new AnnotationFileManager(new SynchronousThreadMarshaller());
        var file = manager2.Checkout(path);
        Assert.NotNull(file);
        manager2.Dispose();
    }

}
