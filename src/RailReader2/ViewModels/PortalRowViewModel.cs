using RailReader2.Services;

namespace RailReader2.ViewModels;

/// <summary>One row in the Portals management list: a <see cref="Services.Portal"/> plus its
/// display strings. Rebuilt from <see cref="MainWindowViewModel.BuildPortalRows"/> on add/delete/
/// rename and on tab switch (mirrors the Index/Comments panes' signature-guarded rebuild).</summary>
public sealed class PortalRowViewModel
{
    public required Portal Portal { get; init; }
    public required string Label { get; set; }
    public required string SourceText { get; init; }
    public required string TargetText { get; init; }
}
