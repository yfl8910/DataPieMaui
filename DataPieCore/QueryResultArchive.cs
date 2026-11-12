using System;
using System.Collections.Generic;
using System.Data;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading;

namespace DataPieCore;

// A temporary, sequential snapshot of one query result; the original SQL is executed only once.
public sealed class QueryResultArchive : IDisposable
{
    private readonly FileStream stream;
    private readonly object gate = new();
    private readonly string[] columns;
    private readonly Type[] types;
    private bool disposed;
    public long RowCount { get; private set; }
    internal string StoragePath => stream.Name;
    private QueryResultArchive(FileStream stream, string[] columns, Type[] types) { this.stream = stream; this.columns = columns; this.types = types; }
    public static QueryResultArchive Capture(IDataReader reader, int previewLimit = 0,
        Action<QueryPreviewResult> previewReady = null, CancellationToken cancellation = default)
        => Capture(reader, previewLimit, previewReady, cancellation, out _);

    internal static QueryResultArchive Capture(IDataReader reader, int previewLimit,
        Action<QueryPreviewResult> previewReady, CancellationToken cancellation, out QueryPreviewResult preview)
    {
        if (previewReady != null && previewLimit < 1) throw new ArgumentOutOfRangeException(nameof(previewLimit));
        preview = null;
        string path = Path.Combine(Path.GetTempPath(), "datapie-query-" + Guid.NewGuid().ToString("N") + ".tmp");
        var stream = new FileStream(path, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.Read, 65536, FileOptions.DeleteOnClose | FileOptions.SequentialScan);
        try
        {
            var columns = new string[reader.FieldCount]; var types = new Type[reader.FieldCount];
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < columns.Length; i++)
            {
                string original = reader.GetName(i); if (string.IsNullOrEmpty(original)) original = "Column" + (i + 1);
                string name = original; for (int suffix = 1; !names.Add(name); suffix++) name = original + "_" + suffix;
                columns[i] = name; types[i] = reader.GetFieldType(i);
            }
            var archive = new QueryResultArchive(stream, columns, types);
            using var writer = new BinaryWriter(stream, Encoding.UTF8, true);
            byte[] buffer = new byte[65536];
            var firstRows = new List<object[]>(Math.Min(Math.Max(previewLimit, 0), 1000));
            while (true)
            {
                cancellation.ThrowIfCancellationRequested();
                if (!reader.Read()) break;
                if (previewLimit > 0 && archive.RowCount == previewLimit)
                {
                    preview = QueryPreviewResult.FromRows(columns, types, firstRows, true);
                    previewReady?.Invoke(preview);
                    cancellation.ThrowIfCancellationRequested();
                }
                var row = archive.RowCount < previewLimit ? new object[columns.Length] : null;
                for (int i = 0; i < columns.Length; i++)
                {
                    if (reader.IsDBNull(i))
                    {
                        writer.Write((byte)0);
                        if (row != null) row[i] = DBNull.Value;
                        continue;
                    }
                    if (types[i] == typeof(byte[]))
                    {
                        long length = reader.GetBytes(i, 0, null, 0, 0); if (length > int.MaxValue) throw new InvalidOperationException("A binary value is too large to preview.");
                        byte[] retained = row == null ? null : new byte[(int)length];
                        writer.Write((byte)14); writer.Write((int)length); long offset = 0;
                        while (offset < length)
                        {
                            cancellation.ThrowIfCancellationRequested();
                            int read = (int)reader.GetBytes(i, offset, buffer, 0, (int)Math.Min(buffer.Length, length - offset));
                            if (read <= 0) throw new EndOfStreamException("Incomplete binary query value.");
                            writer.Write(buffer, 0, read);
                            if (retained != null) Buffer.BlockCopy(buffer, 0, retained, (int)offset, read);
                            offset += read;
                        }
                        if (row != null) row[i] = retained;
                    }
                    else
                    {
                        var value = Write(writer, reader.GetValue(i));
                        if (row != null) row[i] = value is byte[] bytes ? bytes.Clone() : value;
                    }
                }
                if (row != null) firstRows.Add(row);
                archive.RowCount = checked(archive.RowCount + 1);
            }
            writer.Flush();
            if (previewLimit > 0) preview ??= QueryPreviewResult.FromRows(columns, types, firstRows, false);
            return archive;
        }
        catch { stream.Dispose(); throw; }
    }
    private static object Write(BinaryWriter w, object value)
    {
        switch (value)
        {
            case null or DBNull: w.Write((byte)0); break;
            case string x: w.Write((byte)1); w.Write(x); break;
            case bool x: w.Write((byte)2); w.Write(x); break;
            case byte x: w.Write((byte)3); w.Write(x); break;
            case short x: w.Write((byte)4); w.Write(x); break;
            case int x: w.Write((byte)5); w.Write(x); break;
            case long x: w.Write((byte)6); w.Write(x); break;
            case float x: w.Write((byte)7); w.Write(x); break;
            case double x: w.Write((byte)8); w.Write(x); break;
            case decimal x: w.Write((byte)9); w.Write(x); break;
            case DateTime x: w.Write((byte)10); w.Write(x.ToBinary()); break;
            case DateTimeOffset x: w.Write((byte)11); w.Write(x.Ticks); w.Write((short)x.Offset.TotalMinutes); break;
            case Guid x: w.Write((byte)12); w.Write(x.ToByteArray()); break;
            case TimeSpan x: w.Write((byte)13); w.Write(x.Ticks); break;
            case byte[] x: w.Write((byte)14); w.Write(x.Length); w.Write(x); break;
            case sbyte x: w.Write((byte)15); w.Write(x); break;
            case ushort x: w.Write((byte)16); w.Write(x); break;
            case uint x: w.Write((byte)17); w.Write(x); break;
            case ulong x: w.Write((byte)18); w.Write(x); break;
            default: value = Convert.ToString(value, CultureInfo.CurrentCulture) ?? ""; w.Write((byte)1); w.Write((string)value); break;
        }
        return value ?? DBNull.Value;
    }
    private static object Read(BinaryReader r) => r.ReadByte() switch
    {
        0 => DBNull.Value, 1 => r.ReadString(), 2 => r.ReadBoolean(), 3 => r.ReadByte(), 4 => r.ReadInt16(), 5 => r.ReadInt32(), 6 => r.ReadInt64(),
        7 => r.ReadSingle(), 8 => r.ReadDouble(), 9 => r.ReadDecimal(), 10 => DateTime.FromBinary(r.ReadInt64()),
        11 => new DateTimeOffset(r.ReadInt64(), TimeSpan.FromMinutes(r.ReadInt16())), 12 => new Guid(r.ReadBytes(16)), 13 => TimeSpan.FromTicks(r.ReadInt64()),
        14 => r.ReadBytes(r.ReadInt32()), 15 => r.ReadSByte(), 16 => r.ReadUInt16(), 17 => r.ReadUInt32(), 18 => r.ReadUInt64(),
        _ => throw new InvalidDataException("Invalid query snapshot value.")
    };
    public QueryPreviewResult Search(Func<Func<string, object>, bool> matcher, int limit = 1000, IReadOnlyCollection<int> matchColumns = null)
    {
        if (limit < 1) throw new ArgumentOutOfRangeException(nameof(limit));
        return SearchRows(matcher, limit, matchColumns);
    }

    private static void Skip(BinaryReader reader)
    {
        int length = reader.ReadByte() switch
        {
            0 => 0,
            1 => reader.Read7BitEncodedInt(),
            2 or 3 or 15 => 1,
            4 or 16 => 2,
            5 or 7 or 17 => 4,
            6 or 8 or 10 or 13 or 18 => 8,
            9 or 12 => 16,
            11 => 10,
            14 => reader.ReadInt32(),
            _ => throw new InvalidDataException("Invalid archived query value.")
        };
        if (length > 0) reader.BaseStream.Seek(length, SeekOrigin.Current);
    }

    private QueryPreviewResult SearchRows(Func<Func<string, object>, bool> matcher, int limit, IReadOnlyCollection<int> matchColumns)
    {
        lock (gate)
        {
            if (disposed) throw new ObjectDisposedException(nameof(QueryResultArchive));
            stream.Position = 0;
            using var reader = new BinaryReader(stream, Encoding.UTF8, true);
            var rows = new List<object[]>(limit); bool truncated = false;
            bool[] selected = null;
            if (matcher != null && matchColumns != null)
            {
                selected = new bool[columns.Length];
                foreach (int column in matchColumns) selected[column] = true;
                if (Array.TrueForAll(selected, value => value)) selected = null;
            }
            var row = new object[columns.Length];
            object GetValue(string name)
            {
                int column = int.Parse(name, CultureInfo.InvariantCulture);
                if (selected != null && !selected[column]) throw new InvalidOperationException("The search accessed an undeclared column.");
                return row[column];
            }
            Func<string, object> getValue = GetValue;
            for (long n = 0; n < RowCount; n++)
            {
                long start = selected == null ? 0 : stream.Position;
                for (int i = 0; i < row.Length; i++)
                {
                    if (selected == null || selected[i]) row[i] = Read(reader);
                    else Skip(reader);
                }
                if (matcher != null && !matcher(getValue)) continue;
                if (rows.Count == limit) { truncated = true; break; }
                if (selected != null)
                {
                    stream.Position = start;
                    for (int i = 0; i < row.Length; i++)
                    {
                        if (selected[i]) Skip(reader);
                        else row[i] = Read(reader);
                    }
                }
                rows.Add(row);
                row = new object[columns.Length];
            }
            return QueryPreviewResult.FromRows(columns, types, rows, truncated);
        }
    }
    public void Dispose() { lock (gate) { if (disposed) return; disposed = true; stream.Dispose(); } }

    public void Export(string path, bool csv)
    {
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            stream.Position = 0;
            using var input = new BinaryReader(stream, Encoding.UTF8, true);
            using var rows = new ArchiveReader(input, columns, types, RowCount, csv);
            if (!csv)
            {
                ExcelExporter.Save(path, rows, "Query", new ExcelExportOptions { Formatted = false });
                return;
            }
            string temporary = Path.GetFullPath(path) + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                CsvExporter.SaveCsv(rows, temporary);
                File.Move(temporary, path, overwrite: true);
            }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
        }
    }

    // The exporter consumes each row synchronously while the archive owns the stream.
    private sealed class ArchiveReader(BinaryReader input, string[] names, Type[] types, long rowCount, bool binaryAsHex) : IDataReader
    {
        private readonly object[] values = new object[names.Length];
        private long position;
        public bool Read()
        {
            if (IsClosed || position == rowCount) return false;
            for (int i = 0; i < values.Length; i++) values[i] = QueryResultArchive.Read(input);
            position++;
            return true;
        }

        public int FieldCount => values.Length;
        public object GetValue(int i) => binaryAsHex && values[i] is byte[] bytes ? Convert.ToHexString(bytes) : values[i];
        public int GetValues(object[] destination)
        {
            int count = Math.Min(destination.Length, values.Length);
            Array.Copy(values, destination, count);
            return count;
        }
        public string GetName(int i) => names[i];
        public Type GetFieldType(int i) => types[i];
        public int GetOrdinal(string name)
        {
            int index = Array.FindIndex(names, column => string.Equals(column, name, StringComparison.OrdinalIgnoreCase));
            return index >= 0 ? index : throw new IndexOutOfRangeException(name);
        }
        public bool IsDBNull(int i) => values[i] is DBNull;
        public object this[int i] => GetValue(i);
        public object this[string name] => GetValue(GetOrdinal(name));
        public bool NextResult() => false;
        public int Depth => 0;
        public int RecordsAffected => -1;
        public bool IsClosed { get; private set; }
        public void Close() => IsClosed = true;
        public void Dispose() => Close();
        public string GetDataTypeName(int i) => GetFieldType(i).Name;
        public bool GetBoolean(int i) => (bool)values[i];
        public byte GetByte(int i) => (byte)values[i];
        public char GetChar(int i) => (char)values[i];
        public DateTime GetDateTime(int i) => (DateTime)values[i];
        public decimal GetDecimal(int i) => (decimal)values[i];
        public double GetDouble(int i) => (double)values[i];
        public float GetFloat(int i) => (float)values[i];
        public Guid GetGuid(int i) => (Guid)values[i];
        public short GetInt16(int i) => (short)values[i];
        public int GetInt32(int i) => (int)values[i];
        public long GetInt64(int i) => (long)values[i];
        public string GetString(int i) => (string)values[i];
        public DataTable GetSchemaTable() => throw new NotSupportedException();
        public IDataReader GetData(int i) => throw new NotSupportedException();
        public long GetBytes(int i, long offset, byte[] buffer, int start, int length) => throw new NotSupportedException();
        public long GetChars(int i, long offset, char[] buffer, int start, int length) => throw new NotSupportedException();
    }
}
