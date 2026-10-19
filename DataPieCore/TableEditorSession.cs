using System;
using System.Collections.Generic;
using System.Data;
using Microsoft.Data.SqlClient;
using System.Globalization;
using System.Linq;
using DBUtil;

namespace DataPieCore;

// One editor is permanently bound to its original database and qualified table.
public sealed class TableEditorSession
{
    private readonly string connectionString, databaseType, destination;
    private readonly HashSet<string> keys;
    private readonly Dictionary<string, (string SqlType, bool ReadOnly, int Size)> metadata = new();
    private readonly HashSet<string> insertable = new(StringComparer.Ordinal);
    private readonly HashSet<string> defaults;
    public string TableName { get; }
    public const int PageSize = 100;
    public DataTable Data { get; private set; } = new();
    public int Page { get; private set; }
    public bool HasNext { get; private set; }
    public long TotalRows { get; private set; }
    public string? SearchColumn { get; private set; }
    public string SearchText { get; private set; } = "";
    public IReadOnlyList<SearchCondition> Conditions { get; private set; } = Array.Empty<SearchCondition>();
    public bool IsSearchable(string column) => Data.Columns.Contains(column)
        && Comparable(Data.Columns[column].DataType) && Data.Columns[column].DataType != typeof(byte[]);
    public bool HasDraft { get; set; }
    public bool IsBusy { get; set; }
    public bool CanEdit => keys.Count > 0 && Data.Rows.Cast<DataRow>().All(r => keys.All(k => !r.IsNull(k)))
        && Data.Columns.Cast<DataColumn>().All(c => Comparable(c.DataType));
    public bool CanInsert => CanEdit;
    public string ReadOnlyReason => "Editing requires a primary key with non-NULL values and supported column types.";
    private string Quote(string name) => SqlQueryBuilder.QuoteIdentifier(name, databaseType);
    private TableEditorSession(string connectionString, string databaseType, TableStruct table)
    {
        this.connectionString = connectionString;
        this.databaseType = databaseType;
        keys = table.Columns.Where(c => c.IsPrimaryKey).Select(c => c.Name).ToHashSet(StringComparer.Ordinal);
        defaults = table.Columns.Where(c => !string.IsNullOrEmpty(c.Default)).Select(c => c.Name).ToHashSet(StringComparer.Ordinal);
        TableName = databaseType == "SQLSERVER" ? table.TableSchemaName + "." + table.Name : table.Name;
        destination = databaseType == "SQLSERVER" ? Quote(table.TableSchemaName) + "." + Quote(table.Name) : Quote(table.Name);
    }

    public static TableEditorSession Open(string connectionString, string databaseType, string schema, string name)
    {
        using var access = DbAccessFactory.Create(connectionString, databaseType);
        var table = access.ShowTables().SingleOrDefault(t => t.Name == name && (databaseType != "SQLSERVER" || t.TableSchemaName == schema))
            ?? throw new InvalidOperationException("The selected table is no longer available. Refresh the table list.");
        var editor = new TableEditorSession(connectionString, databaseType, table);
        editor.LoadPage(0);
        return editor;
    }

    public void LoadPage(int page)
        => LoadPage(page, Conditions);

    public void Search(string? column, string text) => Search(string.IsNullOrEmpty(text) ? Array.Empty<SearchCondition>() : new[] { new SearchCondition(column, text) });
    public void Search(IReadOnlyList<SearchCondition> conditions) => LoadPage(0, conditions.Where(c => !string.IsNullOrEmpty(c.Text)).ToArray());

    private SearchPredicate BuildPredicate(IReadOnlyList<SearchCondition> conditions) => SearchConditions.BuildAnd(conditions, databaseType, name =>
    {
        if (string.IsNullOrEmpty(name) || !IsSearchable(name)) throw new ArgumentException("Select a searchable column: " + name);
        return new SearchColumn(name, Data.Columns[name].DataType, Quote(name));
    });

    // Export every row matching the applied search, independently of the current page.
    public void ExportExcel(string path)
    {
        var predicate = BuildPredicate(Conditions);
        path = System.IO.Path.GetFullPath(path);
        string temporary = path + "." + Guid.NewGuid().ToString("N") + ".xlsx";
        try
        {
            using (var access = DbAccessFactory.Create(connectionString, databaseType))
            {
                access.conn.Open();
                using var command = access.conn.CreateCommand();
                command.CommandTimeout = 300;
                SearchConditions.Bind(command, predicate.Parameters);
                string filter = predicate.Sql.Length == 0 ? "" : " WHERE " + predicate.Sql;
                string order = keys.Count > 0 ? " ORDER BY " + string.Join(",", keys.Select(Quote)) : "";
                command.CommandText = $"SELECT * FROM {destination}{filter}{order}";
                using var reader = command.ExecuteReader();
                ExcelIO.SaveMiniExcel(temporary, reader, "Data");
            }
            System.IO.File.Move(temporary, path, true);
        }
        finally { if (System.IO.File.Exists(temporary)) System.IO.File.Delete(temporary); }
    }

    private void LoadPage(int page, IReadOnlyList<SearchCondition> conditions)
    {
        if (page < 0) throw new ArgumentOutOfRangeException(nameof(page));
        long offset = checked((long)page * PageSize);
        using var access = DbAccessFactory.Create(connectionString, databaseType);
        access.conn.Open();
        using var command = access.conn.CreateCommand();
        command.CommandTimeout = 30;
        var predicate = BuildPredicate(conditions);
        SearchConditions.Bind(command, predicate.Parameters);
        string filter = predicate.Sql.Length == 0 ? "" : " WHERE " + predicate.Sql;
        string order = keys.Count > 0 ? string.Join(",", keys.Select(Quote)) : "(SELECT NULL)";
        command.CommandText = $"SELECT {(databaseType == "SQLSERVER" ? "COUNT_BIG(*)" : "COUNT(*)")} FROM {destination}{filter}";
        long totalRows = Convert.ToInt64(command.ExecuteScalar(), CultureInfo.InvariantCulture);
        command.CommandText = databaseType == "SQLSERVER"
            ? $"SELECT * FROM {destination}{filter} ORDER BY {order} OFFSET {offset} ROWS FETCH NEXT {PageSize + 1} ROWS ONLY"
            : $"SELECT * FROM {destination}{filter} ORDER BY {order} LIMIT {PageSize + 1} OFFSET {offset}";
        using var reader = command.ExecuteReader(CommandBehavior.KeyInfo);
        using var schema = reader.GetSchemaTable();
        var nextMetadata = new Dictionary<string, (string, bool, int)>();
        var nextInsertable = new HashSet<string>(StringComparer.Ordinal);
        for (int i = 0; i < reader.FieldCount; i++)
        {
            DataRow info = schema.Rows[i];
            bool Flag(string field) => schema.Columns.Contains(field) && info[field] is bool flag && flag;
            string type = reader.GetDataTypeName(i).ToLowerInvariant();
            if (!Flag("IsReadOnly") && !Flag("IsExpression") && !Flag("IsAutoIncrement")
                && type is not ("timestamp" or "rowversion") && Comparable(reader.GetFieldType(i)) && reader.GetFieldType(i) != typeof(byte[]))
                nextInsertable.Add(reader.GetName(i));
            nextMetadata.Add(reader.GetName(i), (type, Flag("IsReadOnly") || Flag("IsExpression") || Flag("IsAutoIncrement")
                || keys.Contains(reader.GetName(i)) || type is "timestamp" or "rowversion" || !Comparable(reader.GetFieldType(i))
                || reader.GetFieldType(i) == typeof(byte[]), Convert.ToInt32(info["ColumnSize"])));
        }
        var next = new DataTable();
        try { next.Load(reader); }
        catch { next.Dispose(); throw; }
        reader.Close();
        if (databaseType == "SQLITE")
        {
            // SQLite's reader schema does not mark generated table columns read-only.
            using var columns = access.conn.CreateCommand();
            columns.CommandText = "PRAGMA table_xinfo(" + destination + ")";
            using var columnReader = columns.ExecuteReader();
            var sqliteKeys = new List<(string Name, string Type)>();
            while (columnReader.Read())
            {
                string name = columnReader.GetString(1);
                if (!columnReader.IsDBNull(4)) defaults.Add(name);
                else defaults.Remove(name);
                if (Convert.ToInt32(columnReader.GetValue(5)) > 0) sqliteKeys.Add((name, columnReader.GetString(2)));
                if (Convert.ToInt32(columnReader.GetValue(6)) != 0 && nextMetadata.TryGetValue(name, out var info))
                {
                    nextMetadata[name] = (info.Item1, true, info.Item3);
                    nextInsertable.Remove(name);
                }
            }
            columnReader.Close();
            // INTEGER PRIMARY KEY aliases rowid only when no separate PK index exists.
            using var indexes = access.conn.CreateCommand();
            indexes.CommandText = "PRAGMA index_list(" + destination + ")";
            using var indexReader = indexes.ExecuteReader();
            bool hasPrimaryIndex = false;
            while (indexReader.Read()) if (indexReader.GetString(3) == "pk") hasPrimaryIndex = true;
            if (sqliteKeys.Count == 1 && sqliteKeys[0].Type.Equals("INTEGER", StringComparison.OrdinalIgnoreCase) && !hasPrimaryIndex)
                nextInsertable.Remove(sqliteKeys[0].Name);
        }
        bool hasNext = next.Rows.Count > PageSize;
        if (hasNext) next.Rows.RemoveAt(PageSize);
        next.AcceptChanges();
        Data.Dispose(); Data = next;
        metadata.Clear(); foreach (var entry in nextMetadata) metadata.Add(entry.Key, entry.Value);
        insertable.Clear(); insertable.UnionWith(nextInsertable);
        Page = page; HasNext = hasNext;
        TotalRows = totalRows;
        Conditions = conditions.ToArray();
        SearchColumn = Conditions.FirstOrDefault()?.Column;
        SearchText = Conditions.FirstOrDefault()?.Text ?? "";
    }

    public bool IsEditable(string column) => CanEdit && metadata.TryGetValue(column, out var info) && !info.ReadOnly;
    public bool IsPrimaryKey(string column) => keys.Contains(column);
    public bool IsInsertable(string column) => CanInsert && insertable.Contains(column);
    public bool HasDefault(string column) => defaults.Contains(column);
    public bool IsRequiredForInsert(string column) => IsInsertable(column) && !HasDefault(column)
        && (!Data.Columns[column].AllowDBNull || keys.Contains(column));
    public object Parse(string column, string text, bool isNull, bool forInsert = false)
    {
        if (!(forInsert ? IsInsertable(column) : IsEditable(column))) throw new InvalidOperationException($"Column {column} is read-only.");
        var dataColumn = Data.Columns[column];
        if (isNull)
        {
            if (!dataColumn.AllowDBNull) throw new InvalidOperationException($"{column} cannot be NULL.");
            return DBNull.Value;
        }
        var type = dataColumn.DataType;
        if (type == typeof(string))
        {
            int max = metadata[column].Size;
            if (max > 0 && text.Length > max) throw new InvalidOperationException($"{column}: length {text.Length} exceeds {max}.");
            return text;
        }
        if (type == typeof(Guid)) return Guid.Parse(text);
        if (type == typeof(DateTime)) return DateTime.Parse(text, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
        if (type == typeof(DateTimeOffset)) return DateTimeOffset.Parse(text, CultureInfo.InvariantCulture);
        if (type == typeof(TimeSpan)) return TimeSpan.Parse(text, CultureInfo.InvariantCulture);
        return Convert.ChangeType(text, type, CultureInfo.InvariantCulture);
    }

    public void Save(DataRow original, IReadOnlyDictionary<string, object> changes)
    {
        EnsureRow(original);
        if (changes.Count == 0) return;
        foreach (var column in changes.Keys)
            if (!IsEditable(column)) throw new InvalidOperationException($"Column {column} is read-only.");
        Write(new[] { original }, changes);
    }
    public void Insert(IReadOnlyDictionary<string, object> values)
    {
        if (!CanInsert) throw new InvalidOperationException(ReadOnlyReason);
        foreach (var column in values.Keys)
            if (!IsInsertable(column)) throw new InvalidOperationException($"Column {column} is generated or read-only.");
        foreach (DataColumn column in Data.Columns)
            if (IsRequiredForInsert(column.ColumnName) && (!values.TryGetValue(column.ColumnName, out var value) || value is null or DBNull))
                throw new InvalidOperationException($"{column.ColumnName} is required.");
        using var access = DbAccessFactory.Create(connectionString, databaseType);
        access.conn.Open();
        if (databaseType == "SQLITE")
        {
            using var enable = access.conn.CreateCommand(); enable.CommandText = "PRAGMA foreign_keys=ON"; enable.ExecuteNonQuery();
        }
        using var transaction = access.conn.BeginTransaction();
        using var command = access.conn.CreateCommand(); command.Transaction = transaction; command.CommandTimeout = 30;
        foreach (var value in values)
        {
            var parameter = command.CreateParameter(); parameter.ParameterName = "@p" + command.Parameters.Count; parameter.Value = value.Value ?? DBNull.Value;
            if (parameter is SqlParameter sql && Enum.TryParse<SqlDbType>(metadata[value.Key].SqlType, true, out var type)) sql.SqlDbType = type;
            command.Parameters.Add(parameter);
        }
        command.CommandText = values.Count == 0 ? $"INSERT INTO {destination} DEFAULT VALUES"
            : $"INSERT INTO {destination} ({string.Join(",", values.Keys.Select(Quote))}) VALUES ({string.Join(",", Enumerable.Range(0, values.Count).Select(i => "@p" + i))})";
        if (command.ExecuteNonQuery() != 1) throw new InvalidOperationException("The inserted record could not be verified. No changes were committed.");
        transaction.Commit();
    }
    public void Delete(IReadOnlyList<DataRow> originals)
    {
        if (originals.Count == 0) return;
        foreach (var row in originals) EnsureRow(row);
        if (originals.Distinct().Count() != originals.Count) throw new ArgumentException("Duplicate rows.");
        Write(originals, null);
    }
    private void EnsureRow(DataRow row)
    {
        if (!CanEdit || !ReferenceEquals(row.Table, Data) || row.RowState != DataRowState.Unchanged)
            throw new InvalidOperationException("Reload the table before editing this record.");
    }
    private void Write(IReadOnlyList<DataRow> originals, IReadOnlyDictionary<string, object> changes)
    {
        using var access = DbAccessFactory.Create(connectionString, databaseType);
        access.conn.Open();
        // SQLite's default connection may leave FK enforcement disabled.
        if (databaseType == "SQLITE")
        {
            using var enable = access.conn.CreateCommand();
            enable.CommandText = "PRAGMA foreign_keys=ON"; enable.ExecuteNonQuery();
        }
        using var transaction = access.conn.BeginTransaction(IsolationLevel.Serializable);
        foreach (var original in originals)
        {
            using var command = access.conn.CreateCommand(); command.Transaction = transaction; command.CommandTimeout = 30;
            string Parameter(string column, object value)
            {
                string name = "@p" + command.Parameters.Count;
                var parameter = command.CreateParameter(); parameter.ParameterName = name; parameter.Value = value ?? DBNull.Value;
                if (parameter is SqlParameter sql && Enum.TryParse<SqlDbType>(metadata[column].SqlType, true, out var type))
                    sql.SqlDbType = type;
                command.Parameters.Add(parameter); return name;
            }
            string assignments = changes == null ? "" : string.Join(",", changes.Select(c => Quote(c.Key) + "=" + Parameter(c.Key, c.Value)));
            // Compare the entire original row, including rowversion. Any conflict rolls back the batch.
            string predicate = string.Join(" AND ", Data.Columns.Cast<DataColumn>().Select(c =>
            {
                if (original.IsNull(c)) return Quote(c.ColumnName) + " IS NULL";
                string value = Parameter(c.ColumnName, original[c]);
                if (c.DataType == typeof(string))
                    return databaseType == "SQLSERVER"
                        ? $"CONVERT(varbinary(max),{Quote(c.ColumnName)})=CONVERT(varbinary(max),{value})"
                        : $"{Quote(c.ColumnName)} COLLATE BINARY={value}";
                return Quote(c.ColumnName) + "=" + value;
            }));
            command.CommandText = changes == null ? $"DELETE FROM {destination} WHERE {predicate}" : $"UPDATE {destination} SET {assignments} WHERE {predicate}";
            if (command.ExecuteNonQuery() != 1)
                throw new InvalidOperationException("The record changed or was deleted, or the update could not be verified. No changes were committed. Cancel and refresh before retrying.");
        }
        transaction.Commit();
    }
    private static bool Comparable(Type type) => type == typeof(string) || type == typeof(byte[]) || type == typeof(Guid)
        || type == typeof(DateTime) || type == typeof(DateTimeOffset) || type == typeof(TimeSpan)
        || type == typeof(bool) || type == typeof(byte) || type == typeof(short) || type == typeof(int) || type == typeof(long)
        || type == typeof(float) || type == typeof(double) || type == typeof(decimal);
}
