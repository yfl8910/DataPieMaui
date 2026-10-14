using System;
using System.Collections.Generic;
using System.Data.SQLite;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using DBUtil;
namespace DataPieCore;
public sealed record SqlServerScriptExport(string Sql, int Tables, int Procedures, IReadOnlyList<string> Warnings);
public static class SQLiteSqlServerExporter
{
    private const int TextSampleRows = 1000;
    private static string Q(string name) => "[" + name.Replace("]", "]]") + "]";
    private static string SQLiteName(string name) => "\"" + name.Replace("\"", "\"\"") + "\"";
    private static bool IsText(string type) => !type.Contains("INT") &&
        (type.Contains("CHAR") || type.Contains("TEXT") || type.Contains("CLOB"));
    private static Dictionary<string, string> InferTextTypes(SQLiteConnection db, string table, List<object[]> columns)
    {
        var names = columns.Where(c => IsText(Convert.ToString(c[2]).ToUpperInvariant()))
            .Select(c => Convert.ToString(c[1])).ToArray();
        var types = names.ToDictionary(name => name, _ => "NVARCHAR(255)");
        if (names.Length == 0) return types;
        // One bounded scan per table. CAST preserves numeric/mixed values as text;
        // .NET string.Length measures UTF-16 code units, including trailing spaces.
        string projection = string.Join(", ", names.Select(name => "CAST(" + SQLiteName(name) + " AS TEXT)"));
        using var command = new SQLiteCommand("SELECT " + projection + " FROM " + SQLiteName(table) + " LIMIT " + TextSampleRows, db);
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            for (int i = 0; i < names.Length; i++)
            {
                if (reader.IsDBNull(i)) continue;
                string value = reader.GetString(i);
                if (!string.IsNullOrWhiteSpace(value) && value.Length > 255) types[names[i]] = "NVARCHAR(MAX)";
            }
        }
        return types;
    }
    private static List<object[]> Rows(SQLiteConnection db,string sql)
    {
        using var cmd=new SQLiteCommand(sql,db); using var reader=cmd.ExecuteReader(); var result=new List<object[]>();
        while(reader.Read()) { var values=new object[reader.FieldCount]; reader.GetValues(values); result.Add(values); }
        return result;
    }
    public static SqlServerScriptExport Generate(string connectionString)
    {
        var builder=new SQLiteConnectionStringBuilder(connectionString) { ReadOnly=true, FailIfMissing=true, Pooling=false };
        var warnings=new List<string>(); var sql=new StringBuilder("-- DataPie SQLite to SQL Server: schema only; no data is copied.\n-- Review and execute in the intended SQL Server database. Tables are mapped to dbo.\n-- Text types sample the first 1000 returned rows per table (no explicit ordering).\n-- NULL/empty/whitespace-only values are ignored; UTF-16 length <=255 uses NVARCHAR(255), otherwise NVARCHAR(MAX).\n-- Empty samples use NVARCHAR(255); later rows may require a wider type.\n\n");
        using var db=new SQLiteConnection(builder.ToString()); db.Open();
        var tables=Rows(db,"SELECT name,sql FROM sqlite_master WHERE type='table' AND name NOT GLOB 'sqlite_*' ORDER BY name");
        var exported=new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach(var table in tables)
        {
            string name=Convert.ToString(table[0]); string definition=Convert.ToString(table[1]);
            var columns=Rows(db,"PRAGMA table_xinfo("+Q(name)+")");
            if(columns.Count==0 || columns.Any(c=>Convert.ToInt32(c[6])!=0) || definition.TrimStart().StartsWith("CREATE VIRTUAL",StringComparison.OrdinalIgnoreCase))
            { warnings.Add(name+": generated/hidden columns or virtual table require manual conversion; table skipped."); continue; }
            var textTypes = InferTextTypes(db, name, columns);
            var pk=columns.Where(c=>Convert.ToInt32(c[5])>0).OrderBy(c=>Convert.ToInt32(c[5])).ToList();
            bool identity=pk.Count==1 && Convert.ToString(pk[0][2]).Equals("INTEGER",StringComparison.OrdinalIgnoreCase) && System.Text.RegularExpressions.Regex.IsMatch(definition,@"\bAUTOINCREMENT\b",System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            var fields=new List<string>();
            foreach(var column in columns)
            {
                string columnName=Convert.ToString(column[1]); string type=Convert.ToString(column[2]).ToUpperInvariant();
                string target=type.Contains("INT") ? "BIGINT" : IsText(type) ? textTypes[columnName]
                    : type.Contains("BLOB") || type.Length==0 ? "VARBINARY(MAX)" : type.Contains("REAL") || type.Contains("FLOA") || type.Contains("DOUB") ? "FLOAT"
                    : type.Contains("DATE") || type.Contains("TIME") ? "DATETIME2" : type.Contains("BOOL") ? "BIT" : "DECIMAL(38,10)";
                if(target=="DECIMAL(38,10)") warnings.Add(name+"."+columnName+": numeric precision mapped to DECIMAL(38,10); review before execution.");
                bool key=Convert.ToInt32(column[5])>0;
                if(column[4]!=DBNull.Value) warnings.Add(name+"."+columnName+": SQLite default omitted; translate it manually.");
                fields.Add("    "+Q(columnName)+" "+target+(identity && key ? " IDENTITY(1,1)" : "")+(key || Convert.ToInt32(column[3])!=0 ? " NOT NULL" : " NULL"));
            }
            if(pk.Count>0) fields.Add("    PRIMARY KEY ("+string.Join(", ",pk.Select(c=>Q(Convert.ToString(c[1]))))+")");
            sql.AppendLine("CREATE TABLE [dbo]."+Q(name)+" (").AppendLine(string.Join(",\n",fields)).AppendLine(");").AppendLine("GO").AppendLine(); exported.Add(name);
            if (System.Text.RegularExpressions.Regex.IsMatch(definition,@"\bCHECK\s*\(",System.Text.RegularExpressions.RegexOptions.IgnoreCase)) warnings.Add(name+": CHECK constraints omitted; review manually.");
            if (Rows(db,"PRAGMA index_list("+Q(name)+")").Any(row=>Convert.ToString(row[3])=="u")) warnings.Add(name+": UNIQUE constraints omitted; review manually.");
        }
        foreach(var name in exported)
        {
            foreach(var key in Rows(db,"PRAGMA foreign_key_list("+Q(name)+")").GroupBy(c=>Convert.ToInt32(c[0])))
            {
                var parts=key.OrderBy(c=>Convert.ToInt32(c[1])).ToList(); string parent=Convert.ToString(parts[0][2]);
                if(!exported.Contains(parent) || parts.Any(c=>c[4]==DBNull.Value)) { warnings.Add(name+": foreign key to "+parent+" requires manual conversion."); continue; }
                sql.AppendLine("ALTER TABLE [dbo]."+Q(name)+" ADD FOREIGN KEY ("+string.Join(", ",parts.Select(c=>Q(Convert.ToString(c[3]))))+") REFERENCES [dbo]."+Q(parent)+" ("+string.Join(", ",parts.Select(c=>Q(Convert.ToString(c[4]))))+");").AppendLine("GO").AppendLine();
                if(Convert.ToString(parts[0][5])!="NO ACTION" || Convert.ToString(parts[0][6])!="NO ACTION") warnings.Add(name+": foreign key cascading actions omitted; review manually.");
            }
        }
        foreach(var item in Rows(db,"SELECT type,name FROM sqlite_master WHERE type IN ('view','trigger','index') AND name NOT GLOB 'sqlite_*'")) warnings.Add(Convert.ToString(item[0])+" "+Convert.ToString(item[1])+": omitted; manual SQL Server conversion required.");
        var procedures=SQLiteProcedureScripts.SqlServerDefinitions(connectionString,warnings);
        foreach(var procedure in procedures) sql.AppendLine(procedure).AppendLine("GO").AppendLine();
        sql.AppendLine("-- Export warnings:");
        foreach(var warning in warnings) sql.AppendLine("-- "+warning.Replace("\r"," ").Replace("\n"," "));
        return new(sql.ToString(),exported.Count,procedures.Count,warnings);
    }
}
