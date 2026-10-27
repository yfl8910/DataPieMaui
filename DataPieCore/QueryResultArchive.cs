using System;
using System.Collections.Generic;
using System.Data;
using System.Globalization;
using System.IO;
using System.Text;

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
    public static QueryResultArchive Capture(IDataReader reader)
    {
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
            while (reader.Read())
            {
                for (int i = 0; i < columns.Length; i++)
                {
                    if (reader.IsDBNull(i)) { writer.Write((byte)0); continue; }
                    if (types[i] == typeof(byte[]))
                    {
                        long length = reader.GetBytes(i, 0, null, 0, 0); if (length > int.MaxValue) throw new InvalidOperationException("A binary value is too large to preview.");
                        writer.Write((byte)14); writer.Write((int)length); long offset = 0;
                        while (offset < length) { int read = (int)reader.GetBytes(i, offset, buffer, 0, (int)Math.Min(buffer.Length, length - offset)); if (read <= 0) throw new EndOfStreamException("Incomplete binary query value."); writer.Write(buffer, 0, read); offset += read; }
                    }
                    else Write(writer, reader.GetValue(i));
                }
                archive.RowCount = checked(archive.RowCount + 1);
            }
            writer.Flush(); return archive;
        }
        catch { stream.Dispose(); throw; }
    }
    private static void Write(BinaryWriter w, object value)
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
            default: w.Write((byte)1); w.Write(Convert.ToString(value, CultureInfo.CurrentCulture) ?? ""); break;
        }
    }
    private static object Read(BinaryReader r) => r.ReadByte() switch
    {
        0 => DBNull.Value, 1 => r.ReadString(), 2 => r.ReadBoolean(), 3 => r.ReadByte(), 4 => r.ReadInt16(), 5 => r.ReadInt32(), 6 => r.ReadInt64(),
        7 => r.ReadSingle(), 8 => r.ReadDouble(), 9 => r.ReadDecimal(), 10 => DateTime.FromBinary(r.ReadInt64()),
        11 => new DateTimeOffset(r.ReadInt64(), TimeSpan.FromMinutes(r.ReadInt16())), 12 => new Guid(r.ReadBytes(16)), 13 => TimeSpan.FromTicks(r.ReadInt64()),
        14 => r.ReadBytes(r.ReadInt32()), 15 => r.ReadSByte(), 16 => r.ReadUInt16(), 17 => r.ReadUInt32(), 18 => r.ReadUInt64(),
        _ => throw new InvalidDataException("Invalid query snapshot value.")
    };
    public QueryPreviewResult Search(Func<Func<string, object>, bool> matcher, int limit = 1000)
    {
        if (limit < 1) throw new ArgumentOutOfRangeException(nameof(limit));
        lock (gate)
        {
            if (disposed) throw new ObjectDisposedException(nameof(QueryResultArchive));
            stream.Position = 0;
            using var reader = new BinaryReader(stream, Encoding.UTF8, true);
            var rows = new List<object[]>(limit); bool truncated = false;
            for (long n = 0; n < RowCount; n++)
            {
                var row = new object[columns.Length]; for (int i = 0; i < row.Length; i++) row[i] = Read(reader);
                if (matcher != null && !matcher(name => row[int.Parse(name, CultureInfo.InvariantCulture)])) continue;
                if (rows.Count == limit) { truncated = true; break; }
                rows.Add(row);
            }
            return QueryPreviewResult.FromRows(columns, types, rows, truncated);
        }
    }
    public void Dispose() { lock (gate) { if (disposed) return; disposed = true; stream.Dispose(); } }
}
