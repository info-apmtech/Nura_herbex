using Microsoft.AspNetCore.Components;

namespace Nuraherbex.UI.Components.Admin;

/// <summary>Describes one column of a <see cref="DataTable{TItem}"/>: what it shows, how it searches, sorts and exports.</summary>
public class DataColumn<TItem>
{
    public required string Header { get; init; }
    /// <summary>Plain text for the cell. Used for search, sorting (unless <see cref="SortKey"/> is set) and every export format.</summary>
    public required Func<TItem, string> Text { get; init; }
    /// <summary>Optional richer cell (badges, links). When null, <see cref="Text"/> is shown.</summary>
    public RenderFragment<TItem>? Cell { get; init; }
    /// <summary>Optional value to sort by, such as a date or a number, when the text does not sort correctly.</summary>
    public Func<TItem, IComparable?>? SortKey { get; init; }
    public string Align { get; init; } = "left";
    public string Class { get; init; } = "";
    public string? Width { get; init; }
    public bool Sortable { get; init; } = true;
    /// <summary>False for columns such as action buttons that should not appear in a CSV, Excel or PDF file.</summary>
    public bool Export { get; init; } = true;
    /// <summary>Starts hidden; people can switch it on from the Columns menu.</summary>
    public bool Hidden { get; init; }
    /// <summary>When set, the text is shown as a coloured pill. Return good, warn, bad, info or muted.</summary>
    public Func<TItem, string>? Tone { get; init; }
}
