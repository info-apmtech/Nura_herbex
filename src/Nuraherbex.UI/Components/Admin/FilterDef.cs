namespace Nuraherbex.UI.Components.Admin;

public enum FilterKind
{
    /// <summary>Free text: matches anywhere in the value. Phone filters compare digits only.</summary>
    Text,
    /// <summary>One value from a drop-down built from the data.</summary>
    Select,
    /// <summary>Several values at once as toggle chips (falls back to a drop-down when there are too many).</summary>
    Chips,
    /// <summary>Minimum and maximum of a number.</summary>
    Range,
    /// <summary>From and to dates.</summary>
    Date,
}

/// <summary>One filter in a <see cref="FilterPanel{T}"/>: what to call it and how to read its value from a record.</summary>
public class FilterDef<T>
{
    public required string Label { get; init; }
    public FilterKind Kind { get; init; } = FilterKind.Text;
    /// <summary>Text, Select and Chips: the value to match.</summary>
    public Func<T, string?>? Value { get; init; }
    /// <summary>Range: the number to compare.</summary>
    public Func<T, decimal?>? Number { get; init; }
    /// <summary>Date: the day to compare.</summary>
    public Func<T, DateTime?>? Date { get; init; }
    /// <summary>Text only: compare digits only, so "98765 43210" finds "919876543210".</summary>
    public bool Phone { get; init; }
    public string Placeholder { get; init; } = "";
    /// <summary>Shows the filter on the first row even when the panel is collapsed.</summary>
    public bool Primary { get; init; }
}
