using Microsoft.JSInterop;

namespace Nuraherbex.UI.Services;

/// <summary>Downloads a table as CSV, Excel (.xlsx) or PDF. The file is built in the browser, so nothing is sent to a server.</summary>
public class ExportService(IJSRuntime js)
{
    public static readonly (string Format, string Label)[] Formats = [("csv", "CSV"), ("xlsx", "Excel"), ("pdf", "PDF")];

    public async Task<bool> RunAsync(string format, string fileName, string title, IReadOnlyList<string> columns, IReadOnlyList<IReadOnlyList<string>> rows, IEnumerable<string>? meta = null)
    {
        try
        {
            await js.InvokeVoidAsync("nuraExport.run", format, fileName, title, columns, rows, (meta ?? []).ToArray());
            return true;
        }
        catch { return false; }
    }
}
