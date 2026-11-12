using System.Data.Common;
using System.Collections.Generic;
using System;
using System.Linq;
using System.Data;
using System.Data.SQLite;

namespace DBUtil
{
    public partial class SQLiteDbAccess : IDbAccess, IDefinitionReader
    {
        public IReadOnlyList<DefinitionObject> ListDefinitionObjects()
            => ShowViews().Select(name => new DefinitionObject("View", "main", name))
                .Concat(GetProcs().Select(p => new DefinitionObject("Procedure", "", p.Name))).ToArray();

        public ObjectDefinition ReadDefinition(DefinitionObject item)
        {
            if (item.Kind == "Procedure")
            {
                return DataPieCore.SQLiteProcedureScripts.ReadDefinition(ConnectionString, item.Name);
            }
            if (item.Kind != "View" || item.Schema != "main") throw new ArgumentException("Unsupported definition object.");
            if (conn.State != ConnectionState.Open) conn.Open();
            using var command = conn.CreateCommand();
            command.CommandTimeout = 30;
            command.CommandText = "SELECT sql FROM main.sqlite_schema WHERE type='view' AND name=@name";
            var parameter = command.CreateParameter(); parameter.ParameterName = "@name"; parameter.Value = item.Name; command.Parameters.Add(parameter);
            string sql = command.ExecuteScalar() as string ?? throw new InvalidOperationException("View no longer available. Reconnect to refresh the list.");
            command.CommandText = "PRAGMA main.schema_version";
            string version = Convert.ToString(command.ExecuteScalar());
            return new(sql) { CanEdit = !new SQLiteConnectionStringBuilder(ConnectionString).ReadOnly, Version = version, EditRestriction = "The database connection is read-only." };
        }

        public ObjectDefinition SaveDefinition(DefinitionObject item, ObjectDefinition original, string draft)
        {
            DataPieCore.SQLiteDefinitionValidator.Prepare(item, draft);
            if (item.Kind == "Procedure") return DataPieCore.SQLiteProcedureScripts.SaveDefinition(ConnectionString, item.Name, original, draft);
            if (conn.State != ConnectionState.Open) conn.Open();
            // Serializable takes the write reservation before the conflict check.
            using var transaction = conn.BeginTransaction(IsolationLevel.Serializable);
            var current = ReadDefinition(item);
            if (!current.CanEdit || original.Sql != current.Sql || original.Version != current.Version)
                throw new InvalidOperationException("The definition changed or is read-only. Reopen it before editing.");
            using var command = conn.CreateCommand();
            command.Transaction = transaction;
            command.CommandTimeout = 30;
            command.CommandText = "SELECT sql FROM main.sqlite_schema WHERE type='trigger' AND tbl_name=@name ORDER BY name";
            var parameter = command.CreateParameter(); parameter.ParameterName = "@name"; parameter.Value = item.Name; command.Parameters.Add(parameter);
            var triggers = new List<string>();
            using (var reader = command.ExecuteReader()) while (reader.Read()) triggers.Add(reader.GetString(0));
            command.Parameters.Clear();
            string quoted = "\"" + item.Name.Replace("\"", "\"\"") + "\"";
            List<string> Columns()
            {
                command.CommandText = "PRAGMA main.table_info(" + quoted + ")";
                var columns = new List<string>();
                using var reader = command.ExecuteReader();
                while (reader.Read()) columns.Add(reader.GetString(1));
                return columns;
            }
            var originalColumns = Columns();
            command.CommandText = "SELECT name FROM main.sqlite_schema WHERE type='view'";
            var views = new List<string>();
            using (var reader = command.ExecuteReader()) while (reader.Read()) views.Add(reader.GetString(0));
            var validViews = new List<string>();
            foreach (string view in views)
            {
                command.CommandText = "EXPLAIN SELECT * FROM main.\"" + view.Replace("\"", "\"\"") + "\"";
                try { using var reader = command.ExecuteReader(); while (reader.Read()) { } validViews.Add(view); }
                catch (DbException) { /* Existing invalid views must not block unrelated repairs. */ }
            }
            command.CommandText = "DROP VIEW main." + quoted; command.ExecuteNonQuery();
            command.CommandText = draft; command.ExecuteNonQuery();
            if (triggers.Count > 0 && !originalColumns.SequenceEqual(Columns(), StringComparer.OrdinalIgnoreCase))
                throw new InvalidOperationException("This view has triggers. Keep its column names and order unchanged when editing.");
            foreach (string trigger in triggers) { command.CommandText = trigger; command.ExecuteNonQuery(); }
            // Preparing checks missing columns/references without evaluating user SQL.
            command.CommandText = "EXPLAIN SELECT * FROM main." + quoted;
            using (var reader = command.ExecuteReader()) while (reader.Read()) { }
            foreach (string view in validViews)
            {
                command.CommandText = "EXPLAIN SELECT * FROM main.\"" + view.Replace("\"", "\"\"") + "\"";
                using var reader = command.ExecuteReader(); while (reader.Read()) { }
            }
            var saved = ReadDefinition(item);
            transaction.Commit();
            return saved;
        }
        public DbSchema ShowDbSchema()
        {
            DbSchema dbs = new DbSchema
            {
                Name = GetDbName(),
                Tables = ShowTables(),
                ViewNames = ShowViews(),
                Procedures = GetProcs()
            };
            dbs.DefinitionObjects = dbs.ViewNames.Select(name => new DefinitionObject("View", "main", name)).Concat(dbs.Procedures.Select(p => new DefinitionObject("Procedure", "", p.Name))).ToArray();
            return dbs;
        }

        public DbSchema ShowDbSchemaSummary()
        {
            if (conn.State != ConnectionState.Open) conn.Open();
            var schema = new DbSchema { Name = conn.Database, Tables = new(), ViewNames = new() };
            using (var command = conn.CreateCommand())
            {
                command.CommandText = "SELECT name, type FROM main.sqlite_master WHERE type IN ('table','view')";
                using var reader = command.ExecuteReader();
                while (reader.Read())
                {
                    string name = reader.GetString(0);
                    if (reader.GetString(1) == "table") schema.Tables.Add(new TableStruct { Name = name, ColumnsLoaded = false });
                    else schema.ViewNames.Add(name);
                }
            }
            schema.Procedures = GetProcs();
            schema.DefinitionObjects = schema.ViewNames.Select(name => new DefinitionObject("View", "main", name))
                .Concat(schema.Procedures.Select(p => new DefinitionObject("Procedure", "", p.Name))).ToArray();
            return schema;
        }

        internal TableStruct ReadTable(string name)
        {
            using var connection = SQLiteConnections.Create(ConnectionString);
            connection.Open();
            using var command = SQLiteConnections.Command("SELECT 1 FROM main.sqlite_master WHERE type = 'table' AND name = @name", connection);
            var parameter = command.CreateParameter();
            parameter.ParameterName = "@name";
            parameter.Value = name;
            command.Parameters.Add(parameter);
            if (command.ExecuteScalar() == null) return null;
            using var columns = ReadSchema(connection, "Columns", new[] { null, null, name, null });
            return new TableStruct { Name = name, Columns = columns.AsEnumerable().Select(ReadColumn).ToList() };
        }

        public List<Proc> GetProcs()
        {
            return DataPieCore.SQLiteProcedureScripts.List(ConnectionString);
        }

        public List<string> GetDataBaseInfo()
        {
            using var databases = GetSchema("Catalogs");
            return databases.Rows.Count == 0 ? null :
                databases.AsEnumerable().Select(row => (string)row["Catalog_name"]).ToList();
        }

        public string GetDbName()
        {
            string DBName;

            using (DbConnection connection = SQLiteConnections.Create(ConnectionString))
            {
                DBName = connection.Database.ToString();
            }
            return DBName;
        }

        /// <summary>
        /// 获得所有表,注意返回的集合中的表模型中只有表名
        /// </summary>
        /// <returns></returns>
        public List<TableStruct> ShowTables()
        {
            using var columns = GetSchema("Columns");
            var byTable = columns.AsEnumerable().ToLookup(r => r["TABLE_NAME"].ToString(), StringComparer.OrdinalIgnoreCase);
            using var reader = GetDataReader("SELECT tbl_name FROM sqlite_master WHERE type = 'table'");
            var tables = new List<TableStruct>();
            while (reader.Read())
            {
                string name = reader.GetString(0);
                tables.Add(new TableStruct
                {
                    Name = name,
                    Columns = byTable[name].Select(ReadColumn).ToList()
                });
            }
            return tables;
        }

        public List<Column> ShowColumns(string tablename)
        {
            using var columns = GetSchema("Columns", new[] { null, null, tablename, null });
            return columns.AsEnumerable().Select(ReadColumn).ToList();
        }

        public List<Column> ReadSchemaColumns(string tableName)
        {
            using var connection = SQLiteConnections.Create(ConnectionString);
            connection.Open();
            return ReadSchemaColumns(connection, tableName);
        }

        private static List<Column> ReadSchemaColumns(DbConnection connection, string tableName, bool? hasPrimaryIndex = null)
        {
            string quoted = DataPieCore.SqlQueryBuilder.QuoteIdentifier(tableName, "SQLITE");
            var columns = new List<Column>();
            using (var command = SQLiteConnections.Command("PRAGMA main.table_xinfo(" + quoted + ")", connection))
            using (var reader = command.ExecuteReader())
                while (reader.Read())
                    columns.Add(new Column
                    {
                        Name = reader.GetString(1), Type = reader.GetString(2),
                        IsNullable = Convert.ToInt32(reader.GetValue(3)) == 0,
                        Default = reader.IsDBNull(4) ? "" : reader.GetValue(4).ToString(), DefaultLoaded = true,
                        IsPrimaryKey = Convert.ToInt32(reader.GetValue(5)) > 0,
                        IsComputed = Convert.ToInt32(reader.GetValue(6)) is 2 or 3
                    });
            var keys = columns.Where(c => c.IsPrimaryKey).ToList();
            if (keys.Count == 1 && keys[0].Type.Equals("INTEGER", StringComparison.OrdinalIgnoreCase))
            {
                if (hasPrimaryIndex == null)
                {
                    hasPrimaryIndex = false;
                    using var command = SQLiteConnections.Command("PRAGMA main.index_list(" + quoted + ")", connection);
                    using var reader = command.ExecuteReader();
                    while (reader.Read())
                        if (reader.GetString(3) == "pk") { hasPrimaryIndex = true; break; }
                }
                if (hasPrimaryIndex == false)
                {
                    keys[0].IsIdentity = true;
                    keys[0].IsNullable = false;
                }
            }
            return columns;
        }

        private static Column ReadColumn(DataRow row) => new Column
        {
            Name = row["COLUMN_NAME"].ToString(),
            Type = row["DATA_TYPE"].ToString(),
            IsNullable = Convert.ToBoolean(row["IS_NULLABLE"]),
            IsPrimaryKey = Convert.ToBoolean(row["PRIMARY_KEY"]),
            IsUnique = Convert.ToBoolean(row["UNIQUE"]),
            MaxLength = row.IsNull("CHARACTER_MAXIMUM_LENGTH") ? 0 : Convert.ToInt32(row["CHARACTER_MAXIMUM_LENGTH"]),
            IsIdentity = Convert.ToBoolean(row["AUTOINCREMENT"]),
        };
        public DataTable GetSchema(string collectionName, string[] restictionValues)
        {
            using var connection = SQLiteConnections.Create(ConnectionString);
            connection.Open();
            return ReadSchema(connection, collectionName, restictionValues);
        }

        private DataTable ReadSchema(DbConnection connection, string collectionName, string[] restrictions)
            => connection is Microsoft.Data.Sqlite.SqliteConnection
                ? ReadCipherSchema(connection, collectionName, restrictions)
                : connection.GetSchema(collectionName, restrictions);

        private DataTable ReadCipherSchema(DbConnection connection, string collectionName, string[] restrictions)
        {
            var result = new DataTable(collectionName);
            if (collectionName == "Catalogs")
            {
                result.Columns.Add("Catalog_name");
                using var command = SQLiteConnections.Command("PRAGMA database_list", connection);
                using var reader = command.ExecuteReader();
                while (reader.Read()) result.Rows.Add(reader.GetString(1));
                return result;
            }
            if (collectionName != "Columns") throw new NotSupportedException("Unsupported SQLCipher schema collection: " + collectionName);
            foreach (string name in new[] { "TABLE_NAME", "COLUMN_NAME", "DATA_TYPE" }) result.Columns.Add(name);
            foreach (string name in new[] { "IS_NULLABLE", "PRIMARY_KEY", "UNIQUE", "AUTOINCREMENT" }) result.Columns.Add(name, typeof(bool));
            result.Columns.Add("CHARACTER_MAXIMUM_LENGTH", typeof(int));
            var tables = new List<string>();
            using (var command = SQLiteConnections.Command("SELECT name FROM sqlite_schema WHERE type IN ('table','view') ORDER BY name", connection))
            using (var reader = command.ExecuteReader())
                while (reader.Read()) tables.Add(reader.GetString(0));
            foreach (string table in tables)
            {
                if (restrictions?.Length > 2 && restrictions[2] != null && restrictions[2] != table) continue;
                var unique = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                var indexes = new List<string>();
                bool hasPrimaryIndex = false;
                using (var command = SQLiteConnections.Command("PRAGMA index_list(" + QuoteIdentifier(table) + ")", connection))
                using (var reader = command.ExecuteReader())
                    while (reader.Read())
                    {
                        if (reader.GetString(3) == "pk") hasPrimaryIndex = true;
                        if (reader.GetInt64(2) == 1 && reader.GetInt64(4) == 0) indexes.Add(reader.GetString(1));
                    }
                foreach (string index in indexes)
                {
                    using var command = SQLiteConnections.Command("PRAGMA index_info(" + QuoteIdentifier(index) + ")", connection);
                    using var reader = command.ExecuteReader();
                    if (reader.Read())
                    {
                        string column = reader.IsDBNull(2) ? null : reader.GetString(2);
                        if (!reader.Read() && column != null) unique.Add(column);
                    }
                }
                foreach (var column in ReadSchemaColumns(connection, table, hasPrimaryIndex))
                {
                    if (restrictions?.Length > 3 && restrictions[3] != null && restrictions[3] != column.Name) continue;
                    result.Rows.Add(table, column.Name, column.Type, column.IsNullable, column.IsPrimaryKey,
                        unique.Contains(column.Name) || column.IsIdentity, column.IsIdentity, 0);
                }
            }
            return result;
        }

        public DataTable GetSchema(string collectionName) => GetSchema(collectionName, null);

        public List<string> ShowViews()
        {
            using var reader = GetDataReader("SELECT name FROM sqlite_master WHERE type = 'view'");
            var views = new List<string>();
            while (reader.Read())
                views.Add(reader.GetString(0));
            return views;
        }
    }
}
