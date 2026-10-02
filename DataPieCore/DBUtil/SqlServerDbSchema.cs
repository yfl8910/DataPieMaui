using System;
using System.Collections.Generic;
using System.Data;
using System.Data.SqlClient;
using System.Linq;

namespace DBUtil
{
    public partial class SqlServerDbAccess : IDbAccess, IDefinitionReader
    {
        public IReadOnlyList<DefinitionObject> ListDefinitionObjects()
        {
            using var reader = GetDataReader("SELECT CASE o.type WHEN 'V' THEN 'View' ELSE 'Procedure' END, s.name, o.name, o.object_id FROM sys.objects o JOIN sys.schemas s ON s.schema_id=o.schema_id WHERE o.type IN ('V','P','PC') AND o.is_ms_shipped=0 ORDER BY s.name,o.name");
            var items = new List<DefinitionObject>();
            while (reader.Read()) items.Add(new(reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetInt32(3)));
            return items;
        }

        public ObjectDefinition ReadDefinition(DefinitionObject item) => ReadDefinition(item, null);

        private ObjectDefinition ReadDefinition(DefinitionObject item, IDbTransaction transaction)
        {
            if (item.Kind is not ("View" or "Procedure")) throw new ArgumentException("Only views and procedures have definitions.");
            if (conn.State != ConnectionState.Open) conn.Open();
            using var command = conn.CreateCommand();
            command.Transaction = transaction;
            command.CommandTimeout = 30;
            command.CommandText = """
                SELECT m.definition, CONVERT(int,OBJECTPROPERTYEX(o.object_id,'IsEncrypted')), o.type,
                       m.uses_quoted_identifier,m.uses_ansi_nulls,o.create_date,o.modify_date,
                       HAS_PERMS_BY_NAME(QUOTENAME(s.name)+'.'+QUOTENAME(o.name),'OBJECT','ALTER'),
                       CASE WHEN EXISTS(SELECT 1 FROM sys.indexes i WHERE i.object_id=o.object_id AND i.index_id>0) THEN 1 ELSE 0 END,
                       CASE WHEN EXISTS(SELECT 1 FROM sys.crypt_properties c WHERE c.class=1 AND c.major_id=o.object_id) THEN 1 ELSE 0 END,
                       m.uses_native_compilation
                FROM sys.objects o JOIN sys.schemas s ON s.schema_id=o.schema_id
                LEFT JOIN sys.sql_modules m ON m.object_id=o.object_id
                WHERE o.object_id=@id AND s.name=@schema AND o.name=@name
                  AND ((@kind='View' AND o.type='V') OR (@kind='Procedure' AND o.type IN ('P','PC')))
                """;
            foreach (var entry in new (string Name, object Value)[] { ("@id", item.Id), ("@schema", item.Schema), ("@name", item.Name), ("@kind", item.Kind) })
            {
                var parameter = command.CreateParameter(); parameter.ParameterName = entry.Name; parameter.Value = entry.Value; command.Parameters.Add(parameter);
            }
            using var reader = command.ExecuteReader();
            if (!reader.Read()) throw new InvalidOperationException("Object no longer available. Reconnect to refresh the list or check permissions.");
            bool Flag(int column) => !reader.IsDBNull(column) && Convert.ToInt32(reader[column]) == 1;
            string sql = reader.IsDBNull(0) ? "" : reader.GetString(0);
            string restriction = sql.Length == 0 ? (Flag(1) ? "The definition is encrypted." : reader.GetString(2).Trim() == "PC" ? "CLR procedures do not have a T-SQL definition." : "Definition unavailable. Check VIEW DEFINITION permission.") :
                !Flag(7) ? "ALTER permission is required to edit this object." :
                Flag(8) ? "Indexed views are read-only here: ALTER VIEW would remove their indexes." :
                Flag(9) ? "Signed modules are read-only here: altering them removes their signatures." :
                Flag(10) ? "Natively compiled modules are read-only here." : "";
            return new ObjectDefinition(sql, sql.Length == 0 ? restriction : "")
            {
                CanEdit = restriction.Length == 0, EditRestriction = restriction,
                QuotedIdentifier = Flag(3), AnsiNulls = Flag(4),
                Version = reader.GetDateTime(5).Ticks + ":" + reader.GetDateTime(6).Ticks
            };
        }

        public ObjectDefinition SaveDefinition(DefinitionObject item, ObjectDefinition original, string draft)
        {
            if (!original.CanEdit) throw new InvalidOperationException("This definition is read-only.");
            string sql = DataPieCore.DefinitionSqlValidator.PrepareAlter(item, draft, original.QuotedIdentifier);
            if (conn.State != ConnectionState.Open) conn.Open();
            using var transaction = conn.BeginTransaction();
            try
            {
                // Serializes saves from this editor. External tools do not participate in this lock.
                using (var gate = conn.CreateCommand())
                {
                    gate.Transaction = transaction; gate.CommandTimeout = 30;
                    gate.CommandText = "DECLARE @r int; EXEC @r=sys.sp_getapplock @Resource=@resource,@LockMode='Exclusive',@LockOwner='Transaction',@LockTimeout=10000; IF @r<0 THROW 50001,'Another definition save is in progress. Retry.',1;";
                    var parameter = gate.CreateParameter(); parameter.ParameterName = "@resource"; parameter.Value = "DataPie.Definition." + item.Id; gate.Parameters.Add(parameter);
                    gate.ExecuteNonQuery();
                }
                var current = ReadDefinition(item, transaction);
                EnsureUnchanged(original, current);
                if (!current.CanEdit) throw new InvalidOperationException(current.EditRestriction);
                using (var options = conn.CreateCommand())
                {
                    options.Transaction = transaction;
                    options.CommandText = "SET QUOTED_IDENTIFIER " + (current.QuotedIdentifier ? "ON" : "OFF") + "; SET ANSI_NULLS " + (current.AnsiNulls ? "ON" : "OFF") + ";";
                    options.ExecuteNonQuery();
                }
                using (var command = conn.CreateCommand())
                {
                    command.Transaction = transaction; command.CommandTimeout = 30;
                    command.CommandText = sql;
                    command.ExecuteNonQuery(); // ALTER only; never EXEC the procedure body.
                }
                var saved = ReadDefinition(item, transaction);
                if (string.IsNullOrEmpty(saved.Sql)) throw new InvalidOperationException("Saved definition could not be verified.");
                transaction.Commit();
                return saved;
            }
            catch
            {
                try { transaction.Rollback(); } catch { /* Preserve the original failure. */ }
                throw;
            }
        }

        internal static void EnsureUnchanged(ObjectDefinition original, ObjectDefinition current)
        {
            if (original.Sql != current.Sql || original.Version != current.Version ||
                original.QuotedIdentifier != current.QuotedIdentifier || original.AnsiNulls != current.AnsiNulls)
                throw new InvalidOperationException("The database definition changed after it was opened. Close and reopen to review the latest version; your draft has not been saved.");
        }
        public DbSchema ShowDbSchema()
        {
            var objects = ListDefinitionObjects();
            DbSchema dbs = new DbSchema
            {
                Name = GetDbName(),
                Tables = ShowTables(),
                DefinitionObjects = objects,
                ViewNames = objects.Where(o => o.Kind == "View").Select(o => o.Name).ToList(),
                Procedures = objects.Where(o => o.Kind == "Procedure").Select(o => new Proc { Name = o.Name, SchemaName = o.Schema }).ToList()
            };
            return dbs;
        }

        /// <summary>
        /// 获得所有表
        /// </summary>
        /// <returns></returns>
        public List<TableStruct> ShowTables()
        {
            // Read both result sets on one connection and construct models directly.
            const string sql = @"SELECT name, SCHEMA_NAME(schema_id), object_id FROM sys.tables;
WITH PrimaryKeyColumns AS (
    SELECT ic.object_id, ic.column_id
    FROM sys.indexes i
    JOIN sys.index_columns ic ON ic.object_id = i.object_id AND ic.index_id = i.index_id
    WHERE i.is_primary_key = 1 AND ic.key_ordinal > 0
)
SELECT c.object_id, c.name, ty.name, c.max_length,
OBJECT_DEFINITION(c.default_object_id), c.is_nullable, c.is_identity,
CASE WHEN pk.column_id IS NULL THEN 0 ELSE 1 END
FROM sys.tables t
JOIN sys.columns c ON c.object_id = t.object_id
JOIN sys.types ty ON ty.user_type_id = CASE WHEN c.system_type_id = 240 THEN c.user_type_id ELSE c.system_type_id END
LEFT JOIN PrimaryKeyColumns pk ON pk.object_id = c.object_id AND pk.column_id = c.column_id
WHERE ty.name NOT IN ('geometry', 'geography')
ORDER BY c.object_id, c.column_id";
            using var reader = GetDataReader(sql);
            return ReadTableSchema(reader);
        }

        internal static List<TableStruct> ReadTableSchema(IDataReader reader)
        {
            var tables = new List<TableStruct>();
            var tablesById = new Dictionary<int, TableStruct>();

            while (reader.Read())
            {
                int tableId = reader.GetInt32(2);
                var table = new TableStruct
                {
                    Name = reader.GetString(0),
                    TableSchemaName = reader.GetValue(1).ToString(),
                    Columns = new List<Column>()
                };
                tables.Add(table);
                tablesById.Add(tableId, table);
            }

            if (!reader.NextResult())
                throw new InvalidOperationException("The column metadata result set is missing.");
            while (reader.Read())
            {
                if (!tablesById.TryGetValue(reader.GetInt32(0), out var table)) continue;
                table.Columns.Add(new Column
                {
                    Name = reader.GetString(1),
                    Type = reader.GetString(2),
                    MaxLength = Convert.ToInt32(reader.GetValue(3)),
                    Default = reader.IsDBNull(4) ? "" : reader.GetString(4),
                    IsNullable = reader.GetBoolean(5),
                    IsIdentity = reader.GetBoolean(6),
                    IsPrimaryKey = reader.GetInt32(7) == 1
                });
            }
            return tables;
        }

        // Foreign keys are only needed when exporting the schema to SQLite.
        public void LoadForeignKeys(IEnumerable<TableStruct> tables)
        {
            ArgumentNullException.ThrowIfNull(tables);
            const string sql = @"SELECT SCHEMA_NAME(pt.schema_id), fk.object_id, rt.name,
pc.name, rc.name, fk.delete_referential_action_desc, fk.update_referential_action_desc, pt.name
FROM sys.foreign_keys fk
JOIN sys.foreign_key_columns fc ON fc.constraint_object_id = fk.object_id
JOIN sys.tables pt ON pt.object_id = fk.parent_object_id
JOIN sys.tables rt ON rt.object_id = fk.referenced_object_id
JOIN sys.columns pc ON pc.object_id = fc.parent_object_id AND pc.column_id = fc.parent_column_id
JOIN sys.columns rc ON rc.object_id = fc.referenced_object_id AND rc.column_id = fc.referenced_column_id
WHERE fk.is_disabled = 0
ORDER BY fk.object_id, fc.constraint_column_id";
            using var reader = GetDataReader(sql);
            var keys = new List<(string Schema, string Table, ForeignKeySchema Key)>();
            int previousId = -1;
            ForeignKeySchema key = null;
            while (reader.Read())
            {
                int id = reader.GetInt32(1);
                if (id != previousId)
                {
                    key = new ForeignKeySchema
                    {
                        ReferencedTableName = reader.GetString(2),
                        OnDelete = reader.GetString(5).Replace('_', ' '),
                        OnUpdate = reader.GetString(6).Replace('_', ' ')
                    };
                    keys.Add((reader.GetString(0), reader.GetString(7), key));
                    previousId = id;
                }
                key.ColumnNames.Add(reader.GetString(3));
                key.ReferencedColumnNames.Add(reader.GetString(4));
            }

            var byTable = keys.ToLookup(item => (item.Schema, item.Table), item => item.Key);
            foreach (var table in tables)
                table.ForeignKeys = byTable[(table.TableSchemaName, table.Name)].ToList();
        }

        public List<string> ShowViews()
        {
            const string sql = "SELECT TABLE_NAME FROM INFORMATION_SCHEMA.VIEWS";
            using var reader = GetDataReader(sql);
            var views = new List<string>();

            while (reader.Read())
                views.Add(reader.GetString(0));
            return views;
        }

        public List<ViewSchema> ReadViewDefinitions()
        {
            const string sql = @"SELECT s.name AS SchemaName, v.name AS ViewName, m.definition
FROM sys.views v
JOIN sys.schemas s ON s.schema_id = v.schema_id
LEFT JOIN sys.sql_modules m ON m.object_id = v.object_id
WHERE v.is_ms_shipped = 0
ORDER BY s.name, v.name";
            using var reader = GetDataReader(sql);
            var views = new List<ViewSchema>();

            while (reader.Read())
                views.Add(new ViewSchema
                {
                    SchemaName = reader.GetString(0),
                    ViewName = reader.GetString(1),
                    ViewSQL = reader.IsDBNull(2) ? null : reader.GetString(2)
                });
            return views;
        }

        private string GetDbName() => new SqlConnectionStringBuilder(ConnectionString).InitialCatalog;

        /// <summary>
        /// 获取当前数据库的用户自定义存储过程
        /// </summary>
        /// <returns></returns>
        public List<Proc> ReadProcedureDefinitions()
        {
            const string sql = @"SELECT s.name, p.name, m.definition
FROM sys.procedures p
JOIN sys.schemas s ON s.schema_id = p.schema_id
LEFT JOIN sys.sql_modules m ON m.object_id = p.object_id
WHERE p.is_ms_shipped = 0 ORDER BY s.name, p.name";
            using var reader = GetDataReader(sql);
            var procedures = new List<Proc>();

            while (reader.Read())
                procedures.Add(new Proc
                {
                    SchemaName = reader.GetString(0),
                    Name = reader.GetString(1),
                    CreateSql = reader.IsDBNull(2) ? null : reader.GetString(2)
                });
            return procedures;
        }

        public List<Proc> GetProcs()
        {
            const string sql = @"SELECT ROUTINE_NAME
FROM INFORMATION_SCHEMA.ROUTINES
WHERE ROUTINE_TYPE = 'PROCEDURE'";
            using var reader = GetDataReader(sql);
            var procedures = new List<Proc>();

            while (reader.Read())
                procedures.Add(new Proc
                {
                    Name = reader.GetString(0)
                });
            return procedures;
        }

        /// <summary>
        /// 获取当前数据库列表
        /// </summary>
        /// <returns></returns>
        public List<string> GetDataBaseInfo()
        {
            const string sql = "SELECT name FROM sys.databases WHERE name NOT IN ('master', 'tempdb', 'model', 'msdb', 'ReportServer', 'ReportServerTempDB')";
            using var reader = GetDataReader(sql);
            var databases = new List<string>();

            while (reader.Read())
                databases.Add(reader.GetString(0));
            return databases;
        }

    }
}
