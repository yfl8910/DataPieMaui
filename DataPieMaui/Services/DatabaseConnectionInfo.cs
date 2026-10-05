using System;

namespace DataPieMaui.Services
{
    public sealed class DatabaseConnectionInfo
    {
        public string DatabaseType { get; }
        public string ConnectionString { get; }
        public string DisplayName { get; }

        public DatabaseConnectionInfo(string databaseType, string connectionString, string displayName)
        {
            if (databaseType != "SQLSERVER" && databaseType != "SQLITE")
                throw new ArgumentException("Unsupported database type.", nameof(databaseType));
            if (string.IsNullOrWhiteSpace(connectionString))
                throw new ArgumentException("Please provide a connection string.", nameof(connectionString));
            if (string.IsNullOrWhiteSpace(displayName))
                throw new ArgumentException("Please provide a connection name.", nameof(displayName));
            DatabaseType = databaseType;
            ConnectionString = databaseType == "SQLSERVER"
                ? new Microsoft.Data.SqlClient.SqlConnectionStringBuilder(connectionString)
                {
                    Encrypt = Microsoft.Data.SqlClient.SqlConnectionEncryptOption.Mandatory,
                    TrustServerCertificate = true
                }.ToString()
                : connectionString;
            DisplayName = displayName;
        }
    }
}

