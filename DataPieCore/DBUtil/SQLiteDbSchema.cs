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
                var script = DataPieCore.SQLiteProcedureScripts.Load(ConnectionString, item.Name);
                return new(script.Sql, "Converted SQLite procedure script. " + string.Join(" | ", script.Metadata.Warnings));
            }
            if (item.Kind != "View" || item.Schema != "main") throw new ArgumentException("Unsupported definition object.");
            if (conn.State != ConnectionState.Open) conn.Open();
            using var command = conn.CreateCommand();
            command.CommandTimeout = 30;
            command.CommandText = "SELECT sql FROM main.sqlite_schema WHERE type='view' AND name=@name";
            var parameter = command.CreateParameter(); parameter.ParameterName = "@name"; parameter.Value = item.Name; command.Parameters.Add(parameter);
            return new(command.ExecuteScalar() as string ?? throw new InvalidOperationException("View no longer available. Reconnect to refresh the list."));
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
