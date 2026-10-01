#nullable enable
using System;
using System.Collections.Generic;
using System.Data;
using System.Globalization;
using System.Linq;
using DBUtil;

namespace DataPieCore;

public sealed record SchemaObject(string Kind, string Schema, string Name, int Id = 0)
{
    public string DisplayName => string.IsNullOrEmpty(Schema) ? Name : Schema + "." + Name;
}
public sealed record SchemaField(string Name, string Type, bool Nullable, bool PrimaryKey, bool Identity, string Default, string Extra);
public sealed record SchemaParameter(string Name, string Type, string Direction);
public sealed class SchemaDetails
{
    public string Definition { get; init; } = "";
    public string Notice { get; init; } = "";
    public IReadOnlyList<SchemaField> Fields { get; init; } = Array.Empty<SchemaField>();
    public IReadOnlyList<SchemaParameter> Parameters { get; init; } = Array.Empty<SchemaParameter>();
}

// Read-only catalog access, separate from migration models and data queries.
public static class SchemaBrowser
{
    private static IDbCommand Command(IDbAccess access, string sql, params (string Name, object Value)[] parameters)
    {
        if (access.conn.State != ConnectionState.Open) access.conn.Open();
        var command = access.conn.CreateCommand();
        command.CommandText = sql;
        command.CommandTimeout = 30;
        foreach (var item in parameters)
        {
            var parameter = command.CreateParameter();
            parameter.ParameterName = item.Name; parameter.Value = item.Value;
            command.Parameters.Add(parameter);
        }
        return command;
    }

    public static IReadOnlyList<SchemaObject> List(IDbAccess access)
    {
        bool sqlServer = access.DataBaseType == DataBaseType.SQLSERVER;
        string sql = sqlServer ? """
            SELECT CASE o.type WHEN 'U' THEN 'Table' WHEN 'V' THEN 'View' ELSE 'Procedure' END,
                   s.name, o.name, o.object_id
            FROM sys.objects o JOIN sys.schemas s ON s.schema_id=o.schema_id
            WHERE o.type IN ('U','V','P','PC') AND o.is_ms_shipped=0
            ORDER BY s.name,o.name
            """ : "SELECT CASE type WHEN 'table' THEN 'Table' ELSE 'View' END, 'main', name, 0 FROM main.sqlite_schema WHERE type IN ('table','view') AND name NOT GLOB 'sqlite_*' ORDER BY name";
        var result = new List<SchemaObject>();
        using (var command = Command(access, sql))
        using (var reader = command.ExecuteReader())
            while (reader.Read()) result.Add(new SchemaObject(reader.GetString(0), reader.GetString(1), reader.GetString(2), Convert.ToInt32(reader[3])));
        if (!sqlServer)
            result.AddRange(SQLiteProcedureScripts.List(access.ConnectionString).Select(p => new SchemaObject("Procedure", "", p.Name)));
        return result;
    }

    public static SchemaDetails Read(IDbAccess access, SchemaObject item)
        => access.DataBaseType == DataBaseType.SQLSERVER ? ReadSqlServer(access, item) : ReadSqlite(access, item);

    private static string Text(IDataRecord reader, int index) => reader.IsDBNull(index) ? "" : Convert.ToString(reader[index], CultureInfo.InvariantCulture)!;
    private static string TypeName(string name, int length, int precision, int scale, bool userDefined)
    {
        if (userDefined) return name;
        return name switch
        {
            "nvarchar" or "nchar" => name + "(" + (length < 0 ? "max" : (length / 2).ToString(CultureInfo.InvariantCulture)) + ")",
            "varchar" or "char" or "binary" or "varbinary" => name + "(" + (length < 0 ? "max" : length.ToString(CultureInfo.InvariantCulture)) + ")",
            "decimal" or "numeric" => $"{name}({precision},{scale})",
            "datetime2" or "datetimeoffset" or "time" => $"{name}({scale})",
            _ => name
        };
    }

    private static SchemaDetails ReadSqlServer(IDbAccess access, SchemaObject item)
    {
        string definition, notice;
        using (var command = Command(access, """
            SELECT m.definition, CONVERT(int,OBJECTPROPERTYEX(o.object_id,'IsEncrypted')), o.type
            FROM sys.objects o JOIN sys.schemas s ON s.schema_id=o.schema_id
            LEFT JOIN sys.sql_modules m ON m.object_id=o.object_id
            WHERE o.object_id=@id AND s.name=@schema AND o.name=@name
            """, ("@id", item.Id), ("@schema", item.Schema), ("@name", item.Name)))
        using (var reader = command.ExecuteReader())
        {
            if (!reader.Read()) throw new InvalidOperationException("Object is no longer available. Refresh the schema or check permissions.");
            definition = Text(reader, 0);
            notice = item.Kind == "Table" ? "" : definition.Length > 0 ? "" :
                Text(reader, 1) == "1" ? "The definition is encrypted." : Text(reader, 2).Trim() == "PC" ? "CLR procedures do not have a T-SQL definition." :
                "Definition unavailable. Check VIEW DEFINITION permission; the object may be encrypted.";
        }
        var fields = new List<SchemaField>();
        var parameters = new List<SchemaParameter>();
        if (item.Kind != "Procedure")
        {
            using var command = Command(access, """
                SELECT c.name, t.name, c.max_length,c.precision,c.scale,t.is_user_defined,
                       SCHEMA_NAME(t.schema_id),c.is_nullable,c.is_identity,d.definition,c.is_computed,
                       CASE WHEN EXISTS(SELECT 1 FROM sys.indexes i JOIN sys.index_columns ic
                         ON i.object_id=ic.object_id AND i.index_id=ic.index_id
                         WHERE i.object_id=c.object_id AND i.is_primary_key=1 AND ic.column_id=c.column_id AND ic.key_ordinal>0) THEN 1 ELSE 0 END
                FROM sys.columns c JOIN sys.types t ON t.user_type_id=c.user_type_id
                LEFT JOIN sys.default_constraints d ON d.object_id=c.default_object_id
                WHERE c.object_id=@id ORDER BY c.column_id
                """, ("@id", item.Id));
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                bool custom = Convert.ToBoolean(reader[5]);
                string type = TypeName(custom ? Text(reader, 6) + "." + Text(reader, 1) : Text(reader, 1), Convert.ToInt32(reader[2]), Convert.ToInt32(reader[3]), Convert.ToInt32(reader[4]), custom);
                fields.Add(new SchemaField(Text(reader, 0), type, Convert.ToBoolean(reader[7]), Convert.ToInt32(reader[11]) == 1, Convert.ToBoolean(reader[8]), Text(reader, 9), Convert.ToBoolean(reader[10]) ? "Computed" : ""));
            }
        }
        else
        {
            using var command = Command(access, """
                SELECT p.name,t.name,p.max_length,p.precision,p.scale,t.is_user_defined,SCHEMA_NAME(t.schema_id),p.is_output,p.is_readonly
                FROM sys.parameters p JOIN sys.types t ON t.user_type_id=p.user_type_id
                WHERE p.object_id=@id AND p.parameter_id>0 ORDER BY p.parameter_id
                """, ("@id", item.Id));
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                bool custom = Convert.ToBoolean(reader[5]);
                string type = TypeName(custom ? Text(reader, 6) + "." + Text(reader, 1) : Text(reader, 1), Convert.ToInt32(reader[2]), Convert.ToInt32(reader[3]), Convert.ToInt32(reader[4]), custom);
                parameters.Add(new SchemaParameter(Text(reader, 0), type, Convert.ToBoolean(reader[7]) ? "Input / Output" : Convert.ToBoolean(reader[8]) ? "Input (read-only)" : "Input"));
            }
        }
        return new SchemaDetails { Definition = definition, Notice = notice, Fields = fields, Parameters = parameters };
    }

    private static SchemaDetails ReadSqlite(IDbAccess access, SchemaObject item)
    {
        if (item.Kind == "Procedure")
        {
            var script = SQLiteProcedureScripts.Load(access.ConnectionString, item.Name);
            return new SchemaDetails
            {
                Definition = script.Sql,
                Notice = "Converted SQLite procedure script. " + string.Join(" | ", script.Metadata.Warnings),
                Parameters = script.Metadata.Parameters.Select(p => new SchemaParameter(p.Name, p.Type.ToString(), "Input")).ToArray()
            };
        }
        string definition;
        using (var command = Command(access, "SELECT sql FROM main.sqlite_schema WHERE name=@name AND type=@type", ("@name", item.Name), ("@type", item.Kind == "Table" ? "table" : "view")))
        {
            definition = command.ExecuteScalar() as string ?? throw new InvalidOperationException("Object is no longer available. Refresh the schema.");
        }
        var fields = new List<SchemaField>();
        using (var command = Command(access, "SELECT name,type,\"notnull\",dflt_value,pk,hidden FROM pragma_table_xinfo(@name, 'main') ORDER BY cid", ("@name", item.Name)))
        using (var reader = command.ExecuteReader())
            while (reader.Read()) fields.Add(new SchemaField(Text(reader, 0), Text(reader, 1), Convert.ToInt32(reader[2]) == 0, Convert.ToInt32(reader[4]) > 0, false, Text(reader, 3), Convert.ToInt32(reader[5]) switch { 1 => "Hidden", 2 => "Generated (virtual)", 3 => "Generated (stored)", _ => "" }));
        return new SchemaDetails { Definition = definition, Fields = fields, Notice = "SQLite nullability reflects the declared NOT NULL constraint; primary-key rules may impose additional restrictions." };
    }
}
