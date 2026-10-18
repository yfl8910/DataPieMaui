using System;
using System.Collections.Generic;
using System.Data;
using System.IO;

namespace DataPieCore;

// Owns all row containers and binary buffers. No mutable backing storage escapes.
public sealed class QueryPreviewResult
{
    private readonly string[] columns;
    private readonly Type[] types;
    private readonly object[][] rows;
    public int RowCount => rows.Length;
    public int ColumnCount => columns.Length;
    public bool Truncated { get; }
    public string GetColumnName(int column) => columns[column];
    public Type GetColumnType(int column) => types[column];
    public bool IsNull(int row, int column) => rows[row][column] is null or DBNull;
    // Binary values are copied only when explicitly requested by an external caller.
    public object GetValue(int row, int column) => rows[row][column] is byte[] bytes ? bytes.Clone() : rows[row][column];
    public string FormatDisplayCell(int row, int column) => NumericDisplay.Format(rows[row][column], System.Globalization.CultureInfo.CurrentCulture) ?? FormatCell(row, column);
    public string FormatCell(int row, int column) => rows[row][column] switch
    {
        null or DBNull => "NULL",
        byte[] bytes => Convert.ToHexString(bytes),
        var value => Convert.ToString(value, System.Globalization.CultureInfo.CurrentCulture) ?? ""
    };
    public int Compare(int left, int right, int column)
    {
        var a = rows[left][column]; var b = rows[right][column];
        if (a is null or DBNull) return b is null or DBNull ? 0 : -1;
        if (b is null or DBNull) return 1;
        return System.Collections.Comparer.Default.Compare(a, b);
    }
    private QueryPreviewResult(string[] columns, Type[] types, object[][] rows, bool truncated)
    {
        this.columns = columns; this.types = types; this.rows = rows; Truncated = truncated;
    }

    // Borrows the reader; the caller disposes it. Reads one extra row to detect truncation.
    public static QueryPreviewResult Read(IDataReader reader, int limit)
    {
        if (limit < 1) throw new ArgumentOutOfRangeException(nameof(limit));
        var columns = new string[reader.FieldCount];
        var types = new Type[reader.FieldCount];
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < columns.Length; i++)
        {
            string original = reader.GetName(i);
            if (string.IsNullOrEmpty(original)) original = "Column" + (i + 1);
            string name = original;
            for (int suffix = 1; !names.Add(name); suffix++) name = original + "_" + suffix;
            columns[i] = name; types[i] = reader.GetFieldType(i);
        }
        var rows = new List<object[]>(Math.Min(limit, 1000));
        while (rows.Count < limit && reader.Read())
        {
            var values = new object[columns.Length];
            for (int i = 0; i < values.Length; i++)
            {
                if (reader.IsDBNull(i)) { values[i] = DBNull.Value; continue; }
                if (types[i] != typeof(byte[]))
                {
                    var value = reader.GetValue(i);
                    // Some providers report object metadata for dynamically typed blobs.
                    values[i] = value is byte[] binary ? binary.Clone() : value;
                    continue;
                }
                // Fill the final owned buffer directly; never retain a provider's reusable buffer.
                var bytes = new byte[checked((int)reader.GetBytes(i, 0, null, 0, 0))];
                int offset = 0;
                while (offset < bytes.Length)
                {
                    int read = checked((int)reader.GetBytes(i, offset, bytes, offset, bytes.Length - offset));
                    if (read <= 0) throw new EndOfStreamException("Incomplete binary query result.");
                    offset += read;
                }
                values[i] = bytes;
            }
            rows.Add(values);
        }
        bool truncated = rows.Count == limit && reader.Read();
        // Copies row references only. Row containers and binary buffers are transferred unchanged.
        return new QueryPreviewResult(columns, types, rows.ToArray(), truncated);
    }
}
