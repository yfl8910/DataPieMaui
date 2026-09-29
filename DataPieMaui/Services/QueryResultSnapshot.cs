using System.Data;
using System.Globalization;
using System.Text;

namespace DataPieMaui.Services;

public sealed class QueryResultSnapshot
{
    public string[] Columns { get; }
    public Type[] Types { get; }
    public object?[][] Rows { get; }
    public bool Truncated { get; }
    public string Connection { get; }
    public string Source { get; }

    public QueryResultSnapshot(DataTable table, bool truncated, string connection, string source = "SQL query")
    {
        Columns = table.Columns.Cast<DataColumn>().Select(c => c.ColumnName).ToArray();
        Types = table.Columns.Cast<DataColumn>().Select(c => c.DataType).ToArray();
        Rows = table.Rows.Cast<DataRow>().Select(r => r.ItemArray.Select(v => v is byte[] bytes ? bytes.ToArray() : v).ToArray()).ToArray();
        Truncated = truncated;
        Connection = connection;
        Source = source;
    }

    public static string Format(object? value) => value switch
    {
        null or DBNull => "NULL",
        byte[] bytes => Convert.ToHexString(bytes),
        _ => Convert.ToString(value, CultureInfo.CurrentCulture) ?? ""
    };

    public string CopyText(IEnumerable<int> indices)
    {
        static string Escape(string text) => text.IndexOfAny(['\t', '\r', '\n', '"']) < 0 ? text : "\"" + text.Replace("\"", "\"\"") + "\"";
        var output = new StringBuilder();
        output.AppendLine(string.Join('\t', Columns.Select(Escape)));
        foreach (int index in indices)
            output.AppendLine(string.Join('\t', Rows[index].Select(v => Escape(Format(v)))));
        return output.ToString();
    }

    public int[] Sort(int column, bool descending)
    {
        // Keep original types so 2 sorts before 10 and dates sort chronologically.
        var comparer = Comparer<object?>.Create((a, b) =>
        {
            if (a is null or DBNull) return b is null or DBNull ? 0 : -1;
            if (b is null or DBNull) return 1;
            return System.Collections.Comparer.Default.Compare(a, b);
        });
        var indices = Enumerable.Range(0, Rows.Length);
        if (Types[column] == typeof(byte[])) return indices.ToArray();
        return (descending ? indices.OrderByDescending(i => Rows[i][column], comparer) : indices.OrderBy(i => Rows[i][column], comparer)).ToArray();
    }
}

public sealed class QueryResultSession
{
    public QueryResultSnapshot? Current { get; private set; }
    public event Action? Changed;
    public void Set(QueryResultSnapshot? snapshot) { Current = snapshot; Changed?.Invoke(); }
}
