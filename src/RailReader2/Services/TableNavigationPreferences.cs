using System.Text.Json;
using System.Text.Json.Serialization;
using RailReader.Core.Services;

namespace RailReader2.Services;

/// <summary>How rail mode advances through a table block.</summary>
public enum TableNavMode
{
    /// <summary>Row-by-row, the default rail behaviour (the table row is one navigable line).</summary>
    Row,

    /// <summary>Cell-by-cell within a row (Left/Right step cells; rolls to the next/previous row at the
    /// row edges). Lets the reader follow "label …… value" across a financial table at any zoom.</summary>
    Cell,
}

/// <summary>Which region the focus aids (highlight tint and focus-dim) cover while reading a table.</summary>
public enum TableFocusScope
{
    /// <summary>The current cell only.</summary>
    Cell,

    /// <summary>The whole current row.</summary>
    Row,

    /// <summary>The whole current column (inferred — Core has no column model; see
    /// <see cref="TableColumnIndex"/>).</summary>
    Column,

    /// <summary>Both the current row and column (a cross centred on the current cell).</summary>
    RowAndColumn,
}

/// <summary>
/// App-level table-reading preferences (nav mode + focus aids). Shell-managed sidecar
/// (<c>ConfigDir/table_nav_prefs.json</c>) like <see cref="PortalPreferences"/>, since Core's
/// <see cref="AppConfig"/> is a NuGet type we don't extend. Global (not per-tab): the mode only
/// changes behaviour while the rail is on a table, so a single setting reads naturally everywhere.
/// </summary>
public sealed class TableNavigationPreferences
{
    /// <summary>Row-by-row vs cell-by-cell stepping. Defaults to cell — the point of the feature.</summary>
    public TableNavMode Mode { get; set; } = TableNavMode.Cell;

    /// <summary>Region the focus aids cover (cell / row / column / row+column). Whether the highlight
    /// and dim actually show is the usual line-highlight (H) and focus-dim (F) controls; this only
    /// shapes them while reading a table.</summary>
    public TableFocusScope FocusScope { get; set; } = TableFocusScope.Cell;

    public static string Path => System.IO.Path.Combine(AppConfig.ConfigDir, "table_nav_prefs.json");

    public static TableNavigationPreferences Load()
        => JsonSidecar.Load(Path, TableNavigationPreferencesJsonContext.Default.TableNavigationPreferences,
            static () => new TableNavigationPreferences());

    public void Save()
        => JsonSidecar.Save(Path, this, TableNavigationPreferencesJsonContext.Default.TableNavigationPreferences);
}

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower,
    UseStringEnumConverter = true,
    WriteIndented = true)]
[JsonSerializable(typeof(TableNavigationPreferences))]
internal partial class TableNavigationPreferencesJsonContext : JsonSerializerContext;
