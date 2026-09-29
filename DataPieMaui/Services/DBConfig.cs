using System;
using System.Data.SqlClient;
using System.Data.SQLite;

namespace DataPieMaui.Services
{
    public class DBConfig
    {
        public string ProviderName { get; set; } = string.Empty;
        public string ServerName { get; set; } = string.Empty;
        public string ValidataType { get; set; } = string.Empty;
        public string UserName { get; set; } = string.Empty;
        public string UserPwd { get; set; } = string.Empty;
        public string DataBase { get; set; } = string.Empty;

        public string GetSQLmasterConstring() => BuildSqlServerConnection("master", false);
        public string GetConstring() => ProviderName switch
        {
            "SQLSERVER" => BuildSqlServerConnection(DataBase, true),
            "SQLITE" => new SQLiteConnectionStringBuilder { DataSource = DataBase }.ToString(),
            _ => throw new NotSupportedException("Unsupported database type.")
        };

        private string BuildSqlServerConnection(string database, bool useDatabaseTimeout)
        {
            var builder = new SqlConnectionStringBuilder
            {
                DataSource = ServerName, InitialCatalog = database,
                IntegratedSecurity = ValidataType == "Windows"
            };
            if (builder.IntegratedSecurity)
            {
                if (useDatabaseTimeout) builder.ConnectTimeout = 30;
            }
            else
            {
                builder.UserID = UserName;
                builder.Password = UserPwd;
            }
            return builder.ToString();
        }
    }
}



