using System;
using System.Collections.Generic;
using System.Data;

namespace DBUtil
{
    // Validate each row before SqlBulkCopy consumes it, without buffering the file.
    // The caller retains ownership of the underlying reader.
    internal sealed class ImportLengthDataReader : IDataReader
    {
        private readonly IDataReader reader;
        private readonly string sourceFile;
        private readonly List<(int Ordinal, int Length)> limits = new();
        private long rowNumber;

        public ImportLengthDataReader(IDataReader reader, IReadOnlyDictionary<string, int> lengthLimits, string sourceFile)
        {
            this.reader = reader;
            this.sourceFile = sourceFile;
            var mappedNames = new HashSet<string>(StringComparer.Ordinal);
            for (int i = 0; i < reader.FieldCount; i++)
                if (mappedNames.Add(reader.GetName(i)) && lengthLimits.TryGetValue(reader.GetName(i), out int length) && length >= 0)
                    limits.Add((i, length));
        }

        public void Dispose() { }
        public void Close() { }
        public bool IsClosed => reader.IsClosed;
        public int FieldCount => reader.FieldCount;
        public int Depth => reader.Depth;
        public int RecordsAffected => reader.RecordsAffected;
        public object this[int i] => reader[i];
        public object this[string name] => reader[name];
        public bool Read()
        {
            if (!reader.Read()) return false;
            rowNumber++;
            foreach (var limit in limits)
            {
                if (reader.GetValue(limit.Ordinal) is string value && value.Length > limit.Length)
                    throw new InvalidOperationException($"导入字段长度超限。文件：{sourceFile ?? "（未提供）"}；数据行号：{rowNumber}（不含表头）；列名：{reader.GetName(limit.Ordinal)}；实际长度：{value.Length}；允许长度：{limit.Length}（UTF-16 单元）。");
            }
            return true;
        }
        public bool NextResult() => reader.NextResult();
        public string GetName(int i) => reader.GetName(i);
        public Type GetFieldType(int i) => reader.GetFieldType(i);
        public int GetOrdinal(string name) => reader.GetOrdinal(name);
        public bool GetBoolean(int i) => reader.GetBoolean(i);
        public byte GetByte(int i) => reader.GetByte(i);
        public long GetBytes(int i, long offset, byte[] buffer, int bufferOffset, int length) => reader.GetBytes(i, offset, buffer, bufferOffset, length);
        public char GetChar(int i) => reader.GetChar(i);
        public long GetChars(int i, long offset, char[] buffer, int bufferOffset, int length) => reader.GetChars(i, offset, buffer, bufferOffset, length);
        public IDataReader GetData(int i) => reader.GetData(i);
        public string GetDataTypeName(int i) => reader.GetDataTypeName(i);
        public DateTime GetDateTime(int i) => reader.GetDateTime(i);
        public decimal GetDecimal(int i) => reader.GetDecimal(i);
        public double GetDouble(int i) => reader.GetDouble(i);
        public float GetFloat(int i) => reader.GetFloat(i);
        public Guid GetGuid(int i) => reader.GetGuid(i);
        public short GetInt16(int i) => reader.GetInt16(i);
        public int GetInt32(int i) => reader.GetInt32(i);
        public long GetInt64(int i) => reader.GetInt64(i);
        public DataTable GetSchemaTable() => reader.GetSchemaTable();
        public string GetString(int i) => reader.GetString(i);
        public object GetValue(int i) => reader.GetValue(i);
        public int GetValues(object[] values) => reader.GetValues(values);
        public bool IsDBNull(int i) => reader.IsDBNull(i);
    }
}

