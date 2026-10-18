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
                catch (SQLiteException) { /* Existing invalid views must not block unrelated repairs. */ }
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

            using (SQLiteConnection connection = new SQLiteConnection(ConnectionString))
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
            string quoted = DataPieCore.SqlQueryBuilder.QuoteIdentifier(tableName, "SQLITE");
            var columns = new List<Column>();
            using (var reader = GetDataReader("PRAGMA main.table_xinfo(" + quoted + ")"))
                while (reader.Read())
                    columns.Add(new Column
                    {
                        Name = reader.GetString(1), Type = reader.GetString(2),
                        IsNullable = Convert.ToInt32(reader.GetValue(3)) == 0,
                        Default = reader.IsDBNull(4) ? "" : reader.GetValue(4).ToString(), DefaultLoaded = true,
                        IsPrimaryKey = Convert.ToInt32(reader.GetValue(5)) > 0,
                        IsComputed = Convert.ToInt32(reader.GetValue(6)) is 2 or 3
                    });
            bool primaryIndex = false;
            using (var reader = GetDataReader("PRAGMA main.index_list(" + quoted + ")"))
                while (reader.Read()) if (reader.GetString(3) == "pk") primaryIndex = true;
            var keys = columns.Where(c => c.IsPrimaryKey).ToList();
            if (!primaryIndex && keys.Count == 1 && keys[0].Type.Equals("INTEGER", StringComparison.OrdinalIgnoreCase))
            {
                keys[0].IsIdentity = true;
                keys[0].IsNullable = false;
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
            using var connection = new SQLiteConnection(ConnectionString);
            connection.Open();
            return connection.GetSchema(collectionName, restictionValues);
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
