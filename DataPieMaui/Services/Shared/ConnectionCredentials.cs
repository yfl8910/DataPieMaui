using System.Data.Common;
using System.Data.SQLite;

namespace DataPieMaui.Services.Shared;

internal static class ConnectionCredentials
{
    private static bool IsSecret(string key) => key.Contains("password", StringComparison.OrdinalIgnoreCase)
        || key.Equals("pwd", StringComparison.OrdinalIgnoreCase);

    public static string WithoutPassword(string connectionString)
    {
        var builder = new DbConnectionStringBuilder { ConnectionString = connectionString };
        foreach (string key in builder.Keys.Cast<string>().Where(IsSecret).ToArray()) builder.Remove(key);
        return builder.ConnectionString;
    }

    public static string Display(string connectionString)
    {
        var builder = new DbConnectionStringBuilder { ConnectionString = connectionString };
        foreach (string key in builder.Keys.Cast<string>().Where(IsSecret).ToArray()) builder[key] = "********";
        return builder.ConnectionString;
    }

    public static string WithSqlitePassword(string connectionString, string password)
    {
        var builder = new SQLiteConnectionStringBuilder(WithoutPassword(connectionString)) { FailIfMissing = true };
        if (password.Length > 0) builder.Password = password;
        return builder.ToString();
    }
}
