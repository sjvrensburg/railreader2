namespace RailReader.Core.Models;

/// <summary>
/// Tracks a deferred page-skip sequence while waiting for async layout analysis.
/// Stored on <see cref="DocumentState"/> and consumed by <see cref="DocumentController"/>.
/// </summary>
public sealed record PendingPageSkip(bool Forward, int Skipped, double SavedVerticalBias);
