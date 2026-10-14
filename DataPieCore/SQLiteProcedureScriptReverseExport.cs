using System;
using System.Collections.Generic;
using System.Data;
using System.Data.SQLite;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
namespace DataPieCore;
internal static partial class SQLiteProcedureScripts
{
    internal static List<string> SqlServerDefinitions(string connectionString,List<string> warnings)
    {
        string database=new SQLiteConnectionStringBuilder(connectionString).DataSource;
        var result=new List<string>(); string folder=Folder(database);
        if(!Directory.Exists(folder))return result;
        Dictionary<string,ProcedureExportEntry> entries=null;
        if(File.Exists(IndexPath(database)))
        {
            using var document=JsonDocument.Parse(File.ReadAllText(IndexPath(database)));
            if(document.RootElement.ValueKind!=JsonValueKind.Array)
            {
                var index=JsonSerializer.Deserialize<ProcedureExportIndex>(document.RootElement.GetRawText());
                if(index?.Version!=2 || !string.Equals(index.DatabaseFile,Path.GetFileName(database),StringComparison.OrdinalIgnoreCase))throw new IOException("Invalid procedure export index.");
                entries=index.Procedures.ToDictionary(e=>e.ScriptFile??e.Name+".txt",StringComparer.OrdinalIgnoreCase);
            }
        }
        foreach(string path in Directory.GetFiles(folder,"*.txt").OrderBy(p=>p,StringComparer.OrdinalIgnoreCase))
        {
            string name=Path.GetFileNameWithoutExtension(path);
            try
            {
                if(entries!=null && (!entries.TryGetValue(Path.GetFileName(path),out var entry)||entry.Status!="Success"))
                { warnings.Add(name+": skipped; no Status = Success record for this database.");continue; }
                string text=File.ReadAllText(path); var metadata=new Metadata();
                if(text.StartsWith(Header,StringComparison.Ordinal))
                {
                    var script=Read(path); metadata=script.Metadata??throw new IOException("Missing procedure metadata.");text=script.Sql;
                    if(!string.Equals(metadata.DatabaseFile,Path.GetFileName(database),StringComparison.OrdinalIgnoreCase))throw new NotSupportedException("Script belongs to another database.");
                    if(metadata.Error!=null || metadata.Warnings.Count>0)throw new NotSupportedException("Failed or partial conversion metadata.");
                }
                var variables=metadata.Parameters.Concat(metadata.Locals).ToList();
                if(variables.Any(p=>!Regex.IsMatch(p.Name??"",@"^@[\p{L}_][\p{L}\p{N}_]*$")) || variables.Select(p=>p.Name).Distinct(StringComparer.OrdinalIgnoreCase).Count()!=variables.Count)throw new NotSupportedException("Invalid or duplicate variable names.");
                string body=SQLiteToTSql.Body(text,variables.Select(p=>p.Name).ToArray());
                if(string.IsNullOrWhiteSpace(body))throw new NotSupportedException("Empty procedure body.");
                var declarations=new List<string>();
                string clock="@__DataPieNow";
                while(variables.Any(v=>v.Name.Equals(clock,StringComparison.OrdinalIgnoreCase)))clock+="_";
                if(metadata.Locals.Any(p=>p.Initializer!=null))declarations.Add("DECLARE "+clock+" DATETIME2 = GETDATE();");
                foreach(var local in metadata.Locals)
                {
                    string initial=local.SourceVariable??(local.Initializer switch {null=>DefaultLiteral(local),"GETDATE"=>clock,"YEAR"=>"YEAR("+clock+")","MONTH"=>"MONTH("+clock+")","DAY"=>"DAY("+clock+")",_=>throw new NotSupportedException("Unsupported initializer.")});
                    if(local.SourceVariable!=null && !variables.Any(v=>v.Name.Equals(local.SourceVariable,StringComparison.OrdinalIgnoreCase)))throw new NotSupportedException("Unknown local source variable.");
                    if(local.Offset!=0)initial="("+initial+") + ("+local.Offset.ToString(CultureInfo.InvariantCulture)+")";
                    declarations.Add("DECLARE "+local.Name+" "+ReverseType(local.Type)+" = "+initial+";");
                }
                foreach(var assignment in metadata.Assignments)
                {
                    if(!variables.Any(v=>v.Name==assignment.Target)||!new[]{"=","<>","!=","<",">","<=",">="}.Contains(assignment.Operator))throw new NotSupportedException("Invalid conditional assignment.");
                    string expressions=SQLiteToTSql.Body("SELECT "+assignment.Left+", "+assignment.Right+", "+assignment.Expression,variables.Select(v=>v.Name).ToArray());
                    declarations.Add("IF "+SQLiteToTSql.Expression(assignment.Left)+" "+assignment.Operator+" "+SQLiteToTSql.Expression(assignment.Right)+" SET "+assignment.Target+" = "+SQLiteToTSql.Expression(assignment.Expression)+";");
                }
                string parameters=string.Join(",\n",metadata.Parameters.Select(p=>p.Name+" "+ReverseType(p.Type)+(p.HasDefault?" = "+DefaultLiteral(p):"")));
                result.Add("CREATE OR ALTER PROCEDURE [dbo].["+name.Replace("]","]]")+"]\n"+parameters+"\nAS\nBEGIN\nSET NOCOUNT ON;\n"+string.Join("\n",declarations)+"\n"+body+";\nEND;");
                warnings.Add(name+": translated from current SQLite script; review SQL Server type, collation and NULL semantics before execution.");
            }
            catch(Exception ex) when(ex is NotSupportedException || ex is IOException || ex is JsonException || ex is FormatException || ex is InvalidOperationException)
            {warnings.Add(name+": skipped; "+ex.Message);}
        }
        return result;
    }
    private static string ReverseType(DbType type)=>type switch
    {
        DbType.Int64=>"BIGINT",DbType.Decimal=>"DECIMAL(38,10)",DbType.Double=>"FLOAT",DbType.Boolean=>"BIT",DbType.DateTime=>"DATETIME2",DbType.Guid=>"UNIQUEIDENTIFIER",DbType.String=>"NVARCHAR(MAX)",DbType.Binary=>"VARBINARY(MAX)",_=>throw new NotSupportedException("Unsupported parameter type: "+type)
    };
    private static string DefaultLiteral(Parameter p)
    {
        if(p.DefaultValue==null)return "NULL";
        if(p.Type is DbType.Int64 or DbType.Decimal or DbType.Double or DbType.Boolean)
        {
            if(!decimal.TryParse(p.DefaultValue,NumberStyles.Float,CultureInfo.InvariantCulture,out _))throw new NotSupportedException("Invalid numeric default.");
            return p.DefaultValue;
        }
        if(p.Type==DbType.Binary)throw new NotSupportedException("Binary defaults require manual conversion.");
        return "N'"+p.DefaultValue.Replace("'","''")+"'";
    }
}
