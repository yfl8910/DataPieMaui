using DataPieCore;
using System.Text;

namespace DataPieMaui.Services;

public sealed class QueryResultSnapshot : IDisposable
{
    private readonly QueryPreviewResult data;
    private readonly IReadOnlyList<AnalysisNumberFormat>? formats;
    private readonly QueryResultArchive? archive;
    private Action? cancelCapture;
    public int RowCount => data.RowCount;
    public int ColumnCount => data.ColumnCount;
    public bool Truncated => data.Truncated;
    public bool HasFullResult => archive != null || !data.Truncated;
    public bool IsCapturing => cancelCapture != null;
    public void CancelCapture() => cancelCapture?.Invoke();
    public long TotalRows => archive?.RowCount ?? data.RowCount;
    public string Connection { get; }
    public string Source { get; }
    public string GetColumnName(int column) => data.GetColumnName(column);
    public Type GetColumnType(int column) => data.GetColumnType(column);
    public bool IsNull(int row, int column) => data.IsNull(row, column);
    public object GetValue(int row, int column) => data.GetValue(row, column);
    public string FormatCell(int row, int column) => data.FormatDisplayCell(row, column);
    public string FormatGridCell(int row, int column) => data.FormatGridCell(row, column);
    public string FormatGroupedCell(int row, int column) => data.GetValue(row, column) switch
    {
        IFormattable number when number is byte or sbyte or short or ushort or int or uint or long or ulong or float or double or decimal
            => formats != null && column < formats.Count && formats[column] is { Kind: not "Auto" } format
                ? number.ToString(format.Code(), System.Globalization.CultureInfo.CurrentCulture)
                : GetColumnName(column).Contains('%')
                ? number.ToString("0.0%", System.Globalization.CultureInfo.CurrentCulture)
                : number.ToString("N2", System.Globalization.CultureInfo.CurrentCulture),
        _ => FormatCell(row, column)
    };

    // The immutable result is shared, without copying rows or binary buffers.
    public QueryResultSnapshot(QueryPreviewResult data, string connection, string source = "SQL query", IReadOnlyList<AnalysisNumberFormat>? formats = null, QueryResultArchive? archive = null, Action? cancelCapture = null)
    {
        this.data = data;
        this.formats = formats;
        this.archive = archive;
        this.cancelCapture = cancelCapture;
        Connection = connection;
        Source = source;
    }
    public QueryResultSnapshot SearchFull(Func<Func<string, object>, bool> matcher, IReadOnlyCollection<int>? matchColumns = null)
    {
        if (archive == null) throw new InvalidOperationException("The full query result is not available. Run the query again to enable full-result search.");
        return new QueryResultSnapshot(archive.Search(matcher, matchColumns: matchColumns), Connection, Source, formats, archive);
    }
    public bool HasArchive => archive != null;
    public void Export(string path, bool csv)
    {
        if (archive == null) throw new InvalidOperationException("The full query result is unavailable.");
        archive.Export(path, csv);
    }
    internal bool SharesPreviewWith(QueryResultSnapshot other) => ReferenceEquals(data, other.data);
    public void Dispose()
    {
        cancelCapture = null;
        archive?.Dispose();
    }

    public string CopyText(IEnumerable<int> indices)
    {
        static string Escape(string text) => text.IndexOfAny(['\t', '\r', '\n', '"']) < 0 ? text : "\"" + text.Replace("\"", "\"\"") + "\"";
        var output = new StringBuilder();
        output.AppendLine(string.Join('\t', Enumerable.Range(0, ColumnCount).Select(c => Escape(GetColumnName(c)))));
        foreach (int index in indices)
            output.AppendLine(string.Join('\t', Enumerable.Range(0, ColumnCount).Select(c => Escape(data.FormatCell(index, c)))));
        return output.ToString();
    }

    public int[] Sort(int column, bool descending)
    {
        var indices = Enumerable.Range(0, RowCount);
        if (GetColumnType(column) == typeof(byte[])) return indices.ToArray();
        var comparer = Comparer<int>.Create((a, b) => data.Compare(a, b, column));
        return (descending ? indices.OrderByDescending(i => i, comparer) : indices.OrderBy(i => i, comparer)).ToArray();
    }
}

public sealed class QueryResultSession
{
    public QueryResultSnapshot? Current { get; private set; }
    public event Action? Changed;
    public void Set(QueryResultSnapshot? snapshot)
    {
        var previous = Current; Current = snapshot;
        try { Changed?.Invoke(); }
        finally { if (!ReferenceEquals(previous, snapshot)) previous?.Dispose(); }
    }
}
