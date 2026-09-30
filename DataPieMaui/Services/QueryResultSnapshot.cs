using DataPieCore;
using System.Text;

namespace DataPieMaui.Services;

public sealed class QueryResultSnapshot
{
    private readonly QueryPreviewResult data;
    public int RowCount => data.RowCount;
    public int ColumnCount => data.ColumnCount;
    public bool Truncated => data.Truncated;
    public string Connection { get; }
    public string Source { get; }
    public string GetColumnName(int column) => data.GetColumnName(column);
    public Type GetColumnType(int column) => data.GetColumnType(column);
    public bool IsNull(int row, int column) => data.IsNull(row, column);
    public string FormatCell(int row, int column) => data.FormatCell(row, column);

    // The immutable result is shared, without copying rows or binary buffers.
    public QueryResultSnapshot(QueryPreviewResult data, string connection, string source = "SQL query")
    {
        this.data = data;
        Connection = connection;
        Source = source;
    }

    public string CopyText(IEnumerable<int> indices)
    {
        static string Escape(string text) => text.IndexOfAny(['\t', '\r', '\n', '"']) < 0 ? text : "\"" + text.Replace("\"", "\"\"") + "\"";
        var output = new StringBuilder();
        output.AppendLine(string.Join('\t', Enumerable.Range(0, ColumnCount).Select(c => Escape(GetColumnName(c)))));
        foreach (int index in indices)
            output.AppendLine(string.Join('\t', Enumerable.Range(0, ColumnCount).Select(c => Escape(FormatCell(index, c)))));
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
    public void Set(QueryResultSnapshot? snapshot) { Current = snapshot; Changed?.Invoke(); }
}
