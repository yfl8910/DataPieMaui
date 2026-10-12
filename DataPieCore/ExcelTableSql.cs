using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using ExcelDataReader;
namespace DataPieCore;
public static class ExcelTableSql
{
    public const int InferenceRowLimit = 1000;
    private static readonly string[] DateTextFormats =
    {
        "yyyy-M-d", "yyyy/M/d", "yyyy年M月d日",
        "yyyy-M-d H:mm", "yyyy/M/d H:mm",
        "yyyy-M-d H:mm:ss", "yyyy/M/d H:mm:ss",
        "yyyy-M-d H:mm:ss.FFFFFFF", "yyyy/M/d H:mm:ss.FFFFFFF",
        "yyyy-M-d'T'H:mm", "yyyy-M-d'T'H:mm:ss", "yyyy-M-d'T'H:mm:ss.FFFFFFF"
    };
    private static IExcelDataReader Open(string path, out Stream stream)
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        stream = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        try { return ExcelReaderFactory.CreateReader(stream); }
        catch { stream.Dispose(); throw; }
    }
    public static string[] Sheets(string path)
    {
        using var reader = Open(path, out var stream);
        using (stream)
        {
            var names = new List<string>();
            do { names.Add(reader.Name); } while (reader.NextResult());
            return names.ToArray();
        }
    }
    public static string Generate(string path, string sheet, string databaseType)
    {
        if (databaseType != "SQLSERVER" && databaseType != "SQLITE") throw new NotSupportedException("Unsupported database type.");
        using var reader = Open(path, out var stream);
        using (stream)
        {
            while (reader.Name != sheet)
                if (!reader.NextResult()) throw new InvalidOperationException("The selected worksheet no longer exists.");
            if (!reader.Read() || reader.FieldCount == 0) throw new InvalidOperationException("The worksheet has no header row.");
            var names = Enumerable.Range(0, reader.FieldCount).Select(i => Convert.ToString(reader.GetValue(i), CultureInfo.InvariantCulture)?.Trim() ?? "").ToArray();
            if (names.Any(string.IsNullOrWhiteSpace)) throw new InvalidOperationException("The first row contains empty column names. Update the Excel header row first.");
            if (names.Distinct(StringComparer.OrdinalIgnoreCase).Count() != names.Length) throw new InvalidOperationException("The first row contains duplicate column names. Update the Excel header row first.");
            var kinds = new string[names.Length];
            var maxLengths = new int[names.Length];
            var needsBigInt = new bool[names.Length];
            var hasTime = new bool[names.Length];
            int sampledRows = 0;
            while (sampledRows < InferenceRowLimit && reader.Read())
            {
                sampledRows++;
                for (int i = 0; i < names.Length; i++)
                {
                    var value = reader.GetValue(i);
                    if (value == null || value == DBNull.Value || value is string text && string.IsNullOrWhiteSpace(text)) continue;
                    // string.Length counts UTF-16 code units, including surrogate pairs.
                    maxLengths[i] = Math.Max(maxLengths[i], (Convert.ToString(value, CultureInfo.InvariantCulture) ?? "").Length);
                    string kind = value switch
                    {
                        bool => "bool", DateTime => "date",
                        string dateText when DateTime.TryParseExact(dateText.Trim(), DateTextFormats, CultureInfo.InvariantCulture, DateTimeStyles.None, out _) => "date",
                        double d when double.IsFinite(d) && d == Math.Truncate(d) && d >= -9007199254740991d && d <= 9007199254740991d => "integer",
                        byte or short or int or long => "integer",
                        double or float or decimal => "number", _ => "text"
                    };
                    if (kind == "date")
                        hasTime[i] |= value is DateTime date ? date.TimeOfDay != TimeSpan.Zero
                            : ((string)value).Contains(':');
                    if (kind == "integer")
                    {
                        var integer = Convert.ToDecimal(value, CultureInfo.InvariantCulture);
                        needsBigInt[i] |= integer < int.MinValue || integer > int.MaxValue;
                    }
                    if (kinds[i] == null) kinds[i] = kind;
                    else if (kinds[i] != kind)
                        kinds[i] = (kinds[i] is "integer" or "number") && (kind is "integer" or "number") ? "number" : "text";
                }
            }
            bool sqlite = databaseType == "SQLITE";
            string Quote(string name) => sqlite ? "\"" + name.Replace("\"", "\"\"") + "\"" : "[" + name.Replace("]", "]]") + "]";
            string Type(int column) => sqlite ? kinds[column] switch { "integer" or "bool" => "INTEGER", "number" => "REAL", _ => "TEXT" }
                : kinds[column] switch { "integer" => needsBigInt[column] ? "BIGINT" : "INT", "number" => "FLOAT", "bool" => "BIT", "date" => hasTime[column] ? "DATETIME2" : "DATE", _ => maxLengths[column] <= 255 ? "NVARCHAR(255)" : "NVARCHAR(MAX)" };
            return "CREATE TABLE " + Quote(sheet) + " (" + Environment.NewLine
                + string.Join("," + Environment.NewLine, names.Select((name, i) => "    " + Quote(name) + " " + (string.Equals(name, "Id", StringComparison.OrdinalIgnoreCase) ? (sqlite ? "INTEGER PRIMARY KEY AUTOINCREMENT" : "BIGINT IDENTITY(1,1) NOT NULL PRIMARY KEY") : Type(i) + " NULL"))) + Environment.NewLine + ");";
        }
    }
}
