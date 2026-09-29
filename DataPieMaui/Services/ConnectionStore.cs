using System;
using System.Collections.Generic;
using System.Data.SQLite;

namespace DataPieMaui.Services
{
    internal sealed class ConnectionStore
    {
        private readonly string connectionString;
        public ConnectionStore(string path = "data.db")
            => connectionString = new SQLiteConnectionStringBuilder { DataSource = path }.ToString();

        public void ImportLegacy(string path)
        {
            path = System.IO.Path.GetFullPath(path);
            using var source = new SQLiteConnection(new SQLiteConnectionStringBuilder
                { DataSource = path, ReadOnly = true, FailIfMissing = true }.ToString());
            source.Open();
            using var command = new SQLiteCommand("SELECT Dbname, ConnectionStrings, UPPER(Dbtype) FROM Dbinfo WHERE UPPER(Dbtype) IN ('SQLSERVER', 'SQLITE')", source);
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                string connection = reader.GetString(1), type = reader.GetString(2);
                if (type == "SQLITE")
                {
                    var builder = new SQLiteConnectionStringBuilder(connection);
                    if (!string.IsNullOrEmpty(builder.DataSource) && builder.DataSource != ":memory:" && !System.IO.Path.IsPathRooted(builder.DataSource))
                        builder.DataSource = System.IO.Path.GetFullPath(builder.DataSource, System.IO.Path.GetDirectoryName(path)!);
                    connection = builder.ToString();
                }
                Save(new Dbinfo { Dbname = reader.GetString(0), ConnectionStrings = connection, Dbtype = type }, true);
            }
        }
        private SQLiteConnection Open()
        {
            var connection = new SQLiteConnection(connectionString);
            try
            {
                connection.Open();
                using var command = new SQLiteCommand("CREATE TABLE IF NOT EXISTS Dbinfo(Id INTEGER PRIMARY KEY AUTOINCREMENT, Dbname varchar(50) NOT NULL, ConnectionStrings varchar(255) NOT NULL, Dbtype varchar(20) NOT NULL)", connection);
                command.ExecuteNonQuery();
                return connection;
            }
            catch { connection.Dispose(); throw; }
        }

        public List<Dbinfo> Load()
        {
            using var connection = Open();
            using var command = new SQLiteCommand("SELECT Id, Dbname, ConnectionStrings, UPPER(Dbtype) FROM Dbinfo WHERE UPPER(Dbtype) IN ('SQLSERVER', 'SQLITE') ORDER BY Id", connection);
            using var reader = command.ExecuteReader();
            var records = new List<Dbinfo>();
            while (reader.Read())
                records.Add(new Dbinfo { Id = Convert.ToInt32(reader[0]), Dbname = reader.GetString(1), ConnectionStrings = reader.GetString(2), Dbtype = reader.GetString(3) });
            return records;
        }

        public void Save(Dbinfo record, bool onlyIfMissing = false)
        {
            if (string.IsNullOrWhiteSpace(record.Dbname) || string.IsNullOrWhiteSpace(record.ConnectionStrings))
                throw new ArgumentException("Please provide a name and connection string.");
            if (record.Dbtype != "SQLSERVER" && record.Dbtype != "SQLITE")
                throw new ArgumentException("Unsupported database type.");
            using var connection = Open();
            string sql = record.Id > 0
                ? "UPDATE Dbinfo SET Dbname=@name, ConnectionStrings=@connection, Dbtype=@type WHERE Id=@id"
                : "INSERT INTO Dbinfo(Dbname, ConnectionStrings, Dbtype) SELECT @name, @connection, @type";
            if (record.Id == 0 && onlyIfMissing)
                sql += " WHERE NOT EXISTS (SELECT 1 FROM Dbinfo WHERE Dbname=@name AND ConnectionStrings=@connection AND UPPER(Dbtype)=@type)";
            using var command = new SQLiteCommand(sql, connection);
            command.Parameters.AddWithValue("@id", record.Id);
            command.Parameters.AddWithValue("@name", record.Dbname);
            command.Parameters.AddWithValue("@connection", record.ConnectionStrings);
            command.Parameters.AddWithValue("@type", record.Dbtype);
            command.ExecuteNonQuery();
        }

        public void Delete(int id)
        {
            using var connection = Open();
            using var command = new SQLiteCommand("DELETE FROM Dbinfo WHERE Id=@id", connection);
            command.Parameters.AddWithValue("@id", id);
            command.ExecuteNonQuery();
        }
    }

    public class Dbinfo
    {
        public int Id { get; set; }
        public string Dbname { get; set; } = string.Empty;
        public string ConnectionStrings { get; set; } = string.Empty;
        public string Dbtype { get; set; } = string.Empty;
    }
}



