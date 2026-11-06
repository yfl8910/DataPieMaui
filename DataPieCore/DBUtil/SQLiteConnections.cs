using System;
using System.Data;
using System.Data.Common;
using System.Data.SQLite;
using Microsoft.Data.Sqlite;

namespace DBUtil;

// Keep the existing provider for plain files and use SQLCipher for password-protected files.
// Every reopen must go through this factory, including exports and definition editing.
internal static class SQLiteConnections
{
    public static DbConnection Create(string connectionString)
    {
        var source = new SQLiteConnectionStringBuilder(connectionString);
        if (string.IsNullOrEmpty(source.Password)) return new SQLiteConnection(connectionString);
        var target = new SqliteConnectionStringBuilder
        {
            DataSource = source.DataSource,
            Password = source.Password,
            Mode = source.ReadOnly ? SqliteOpenMode.ReadOnly : source.FailIfMissing ? SqliteOpenMode.ReadWrite : SqliteOpenMode.ReadWriteCreate,
            // Avoid retaining password-bearing connections in a process-wide pool.
            Pooling = false,
            DefaultTimeout = source.DefaultTimeout
        };
        return new SqliteConnection(target.ToString());
    }

    public static DbCommand Command(string sql, DbConnection connection)
    {
        var command = connection.CreateCommand();
        command.CommandText = sql;
        return command;
    }

    public static void AddParameters(IDbCommand command, System.Collections.Generic.IEnumerable<IDataParameter> parameters)
    {
        foreach (var source in parameters)
        {
            var parameter = command.CreateParameter();
            parameter.ParameterName = source.ParameterName;
            parameter.DbType = source.DbType;
            parameter.Direction = source.Direction;
            parameter.Value = source.Value ?? DBNull.Value;
            if (source is IDbDataParameter sized) parameter.Size = sized.Size;
            command.Parameters.Add(parameter);
        }
    }

    internal sealed class Adapter : DbDataAdapter
    {
        public Adapter(DbCommand command) { SelectCommand = command; }
    }
}
