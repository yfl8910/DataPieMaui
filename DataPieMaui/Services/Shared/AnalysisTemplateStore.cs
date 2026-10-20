using System.Data.SQLite;
using System.Globalization;
using System.Text.Json;
using DataPieCore;

namespace DataPieMaui.Services.Shared;

public sealed record AnalysisTemplateInfo(long Id, string TableName, string Name, DateTime UpdatedUtc);

// Shares the application's configuration database; templates never contain connection strings or result rows.
public sealed class AnalysisTemplateStore(string path)
{
    private sealed record Payload(int Version, AnalysisRequest Settings);
    private static readonly JsonSerializerOptions JsonOptions = new() { IgnoreReadOnlyProperties = true, MaxDepth = 16 };
    private SQLiteConnection Open()
    {
        var connection = new SQLiteConnection(new SQLiteConnectionStringBuilder { DataSource = Path.GetFullPath(path) }.ToString());
        try
        {
            connection.Open();
            using var command = new SQLiteCommand("""
                CREATE TABLE IF NOT EXISTS AnalysisTemplates(
                    Id INTEGER PRIMARY KEY AUTOINCREMENT,
                    TableName TEXT NOT NULL COLLATE NOCASE,
                    Name TEXT NOT NULL COLLATE NOCASE,
                    ConfigJson TEXT NOT NULL,
                    UpdatedUtc TEXT NOT NULL,
                    UNIQUE(TableName, Name))
                """, connection);
            command.ExecuteNonQuery();
            return connection;
        }
        catch { connection.Dispose(); throw; }
    }
    public IReadOnlyList<AnalysisTemplateInfo> List(string tableName)
    {
        using var connection = Open();
        using var command = new SQLiteCommand("SELECT Id,TableName,Name,UpdatedUtc FROM AnalysisTemplates WHERE TableName=@table ORDER BY Name COLLATE NOCASE", connection);
        command.Parameters.AddWithValue("@table", tableName);
        using var reader = command.ExecuteReader();
        var results = new List<AnalysisTemplateInfo>();
        while (reader.Read()) results.Add(new(reader.GetInt64(0), reader.GetString(1), reader.GetString(2), DateTime.Parse(reader.GetString(3), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind)));
        return results;
    }
    public long Save(string tableName, string name, AnalysisRequest request)
    {
        ArgumentException.ThrowIfNullOrEmpty(tableName);
        name = name.Trim();
        if (name.Length is 0 or > 100) throw new ArgumentException("Template names must contain 1 to 100 characters.");
        var settings = WithoutSearch(request);
        string json = JsonSerializer.Serialize(new Payload(1, settings), JsonOptions);
        using var connection = Open();
        using var command = new SQLiteCommand("""
            INSERT INTO AnalysisTemplates(TableName,Name,ConfigJson,UpdatedUtc) VALUES(@table,@name,@json,@updated)
            ON CONFLICT(TableName,Name) DO UPDATE SET ConfigJson=excluded.ConfigJson,UpdatedUtc=excluded.UpdatedUtc
            RETURNING Id
            """, connection);
        command.Parameters.AddWithValue("@table", tableName);
        command.Parameters.AddWithValue("@name", name);
        command.Parameters.AddWithValue("@json", json);
        command.Parameters.AddWithValue("@updated", DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture));
        return Convert.ToInt64(command.ExecuteScalar(), CultureInfo.InvariantCulture);
    }
    public void Update(string tableName, long id, string name, AnalysisRequest request)
    {
        name = name.Trim();
        if (name.Length is 0 or > 100) throw new ArgumentException("Template names must contain 1 to 100 characters.");
        string json = JsonSerializer.Serialize(new Payload(1, WithoutSearch(request)), JsonOptions);
        using var connection = Open();
        using var command = new SQLiteCommand("UPDATE AnalysisTemplates SET Name=@name,ConfigJson=@json,UpdatedUtc=@updated WHERE TableName=@table AND Id=@id", connection);
        command.Parameters.AddWithValue("@name", name);
        command.Parameters.AddWithValue("@json", json);
        command.Parameters.AddWithValue("@updated", DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture));
        command.Parameters.AddWithValue("@table", tableName);
        command.Parameters.AddWithValue("@id", id);
        try
        {
            if (command.ExecuteNonQuery() == 0) throw new InvalidOperationException("The template is no longer available. Reopen the template manager.");
        }
        catch (SQLiteException ex) when (ex.ResultCode == SQLiteErrorCode.Constraint)
        { throw new InvalidOperationException("A template with this name already exists for this table. Choose a different name.", ex); }
    }
    public AnalysisRequest Load(string tableName, long id)
    {
        using var connection = Open();
        using var command = new SQLiteCommand("SELECT ConfigJson FROM AnalysisTemplates WHERE TableName=@table AND Id=@id", connection);
        command.Parameters.AddWithValue("@table", tableName); command.Parameters.AddWithValue("@id", id);
        string json = command.ExecuteScalar() as string ?? throw new InvalidOperationException("The template is no longer available. Refresh the template list.");
        try
        {
            var payload = JsonSerializer.Deserialize<Payload>(json, JsonOptions) ?? throw new InvalidDataException("Template settings are empty.");
            if (payload.Version != 1) throw new InvalidDataException("This template version is not supported.");
            return WithoutSearch(payload.Settings);
        }
        catch (JsonException ex) { throw new InvalidDataException("The template contains invalid settings.", ex); }
    }
    public void Delete(string tableName, long id)
    {
        using var connection = Open();
        using var command = new SQLiteCommand("DELETE FROM AnalysisTemplates WHERE TableName=@table AND Id=@id", connection);
        command.Parameters.AddWithValue("@table", tableName); command.Parameters.AddWithValue("@id", id);
        if (command.ExecuteNonQuery() == 0) throw new InvalidOperationException("The template is no longer available. Refresh the template list.");
    }
    internal static AnalysisRequest WithoutSearch(AnalysisRequest request)
    {
        if (request == null || request.Groups == null || request.Calculations == null || request.Groups.Any(string.IsNullOrEmpty)
            || request.Calculations.Any(c => c == null || string.IsNullOrEmpty(c.Column) || string.IsNullOrEmpty(c.Operation))
            || (request.Formulas ?? []).Any(f => f == null || string.IsNullOrWhiteSpace(f.Name) || string.IsNullOrEmpty(f.Left) || string.IsNullOrEmpty(f.Operation)))
            throw new InvalidDataException("The template has incomplete field or calculation settings.");
        return request with { Groups = request.Groups.ToArray(), Calculations = request.Calculations.ToArray(), Formulas = request.Formulas?.ToArray(), SearchColumn = "", SearchColumns = null, SearchText = "", Conditions = null };
    }
}
