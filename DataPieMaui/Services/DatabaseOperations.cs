using DataPieCore;
using DBUtil;
using System.Diagnostics;

namespace DataPieMaui.Services;

internal sealed class DatabaseOperations
{
    public DatabaseConnectionInfo? Connection { get; private set; }
    public DbSchema? Schema { get; private set; }
    public string ConnectionTiming { get; private set; } = "";
    public IDbAccess OpenAccess() => Connection is { } connection
        ? DbAccessFactory.Create(connection.ConnectionString, connection.DatabaseType)
        : throw new InvalidOperationException("Connect to a database first.");

    public async Task ConnectAsync(DatabaseConnectionInfo connection, IProgress<string>? progress = null)
    {
        var result = await Task.Run(() =>
        {
            using var access = DbAccessFactory.Create(connection.ConnectionString, connection.DatabaseType);
            var watch = Stopwatch.StartNew();
            progress?.Report("Connecting to database…");
            access.conn.Open();
            var opened = watch.Elapsed;
            progress?.Report("Reading database object list…");
            var schema = access is SqlServerDbAccess sqlServer ? sqlServer.ShowDbSchemaSummary() : access.ShowDbSchema();
            var metadata = watch.Elapsed - opened;
            string timings = $"Connection {opened.TotalSeconds:F2}s · Object list {metadata.TotalSeconds:F2}s · {schema.Tables.Count:N0} tables · {schema.DefinitionObjects.Count:N0} views/procedures";
            Trace.TraceInformation("Database connection stages: {0}", timings);
            return (schema, timings);
        });
        Connection = connection;
        Schema = result.schema;
        ConnectionTiming = result.timings;
    }

    public async Task EnsureColumnsAsync(TableStruct table)
    {
        if (Connection?.DatabaseType != "SQLSERVER" || table.ColumnsLoaded) return;
        var owner = Schema;
        var watch = Stopwatch.StartNew();
        var columns = await Task.Run(() => { using var access = (SqlServerDbAccess)OpenAccess(); return access.ReadTable(table.TableSchemaName, table.Name).Columns; });
        if (!ReferenceEquals(owner, Schema) || owner == null || !owner.Tables.Contains(table)) throw new InvalidOperationException("The database schema changed. Refresh and select the table again.");
        table.Columns = columns; table.ColumnsLoaded = true;
        Trace.TraceInformation("Loaded table columns in {0:F2}s; {1} columns.", watch.Elapsed.TotalSeconds, columns.Count);
    }

    public static void Import(string path, string table, IDbAccess access, bool validateFieldLengths = false, string sourceTableName = null)
    {
        switch (Path.GetExtension(path).ToLowerInvariant())
        {
            case ".xlsx": ExcelIO.MiniExcelReaderImport(path, table, access, validateFieldLengths, sourceTableName); break;
            case ".xls": ExcelIO.ExcelDataReaderImport(path, table, access, validateFieldLengths, sourceTableName); break;
            case ".csv": ExcelIO.CsvImport(path, table, access, validateFieldLengths); break;
            case ".db":
                using (var source = DbAccessFactory.Create(new DBConfig { ProviderName = "SQLITE", DataBase = path }.GetConstring(), "SQLITE"))
                using (var reader = source.GetDataReader(SqlQueryBuilder.BuildSelectAll(sourceTableName ?? table, "SQLITE")))
                    if (validateFieldLengths && access is SqlServerDbAccess sqlServer)
                        sqlServer.BulkInsert(table, reader, path);
                    else
                        access.BulkInsert(table, reader);
                break;
            default: throw new NotSupportedException("Unsupported file format: " + path);
        }
    }

    public Task DropTableAsync(string table) => Task.Run(() =>
    {
        if (Schema == null || !Schema.Tables.Any(t => Shared.TableSelection.Key(t, Connection!.DatabaseType) == table)) throw new InvalidOperationException("Select a table in the current database.");
        using var access = OpenAccess();
        if (Connection!.DatabaseType == "SQLITE")
        {
            // SQLite may cascade deletes when DROP TABLE is used. Reject incoming foreign keys instead.
            foreach (var other in Schema.Tables.Where(t => t.Name != table))
            {
                using var reader = access.GetDataReader("PRAGMA foreign_key_list(" + SqlQueryBuilder.QuoteIdentifier(other.Name, "SQLITE") + ")");
                while (reader.Read())
                    if (string.Equals(Convert.ToString(reader.GetValue(2)), table, StringComparison.OrdinalIgnoreCase))
                        throw new InvalidOperationException("Cannot delete this table because " + other.Name + " references it with a foreign key.");
            }
        }
        access.ExecuteSql(SqlQueryBuilder.BuildDropTable(table, Connection!.DatabaseType));
    });

    public string ConvertToSqlite(string path)
    {
        if (Connection?.DatabaseType != "SQLSERVER" || Schema == null)
            throw new InvalidOperationException("Connect to SQL Server first.");
        using var access = (SqlServerDbAccess)OpenAccess();
        // The existing converter has process-wide state. The UI serializes all operations.
        var fullTables = access.ShowTables();
        access.LoadForeignKeys(fullTables);
        Schema.ViewDefinitions = access.ReadViewDefinitions();
        Schema.Procedures = access.ReadProcedureDefinitions();
        SqlServerToSQLite.dbs = new DbSchema { Name = Schema.Name, Tables = fullTables, ViewDefinitions = Schema.ViewDefinitions, Procedures = Schema.Procedures };
        var migration = SqlServerToSQLite.CreateSQLiteDatabase(path, null, true);
        SqlServerToSQLite.CopySqlServerRowsToSQLiteDB(access.ConnectionString, path, null);
        var warnings = migration.ViewErrors.Concat(migration.ProcedureErrors).ToArray();
        return $"Copied {SqlServerToSQLite.TotalCopyed:N0} rows. View conversion issues: {migration.ViewErrors.Count}; procedure conversion issues: {migration.ProcedureErrors.Count}."
            + (warnings.Length == 0 ? "" : Environment.NewLine + string.Join(Environment.NewLine, warnings));
    }
}
