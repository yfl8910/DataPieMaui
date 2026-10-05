using System;
using System.Collections.Generic;
using System.Data;
using Microsoft.Data.SqlClient;
using System.Globalization;
using System.Linq;

namespace DBUtil;

public sealed record ProcedureParameter(string Name, SqlDbType Type, int Size, byte Precision, byte Scale, ParameterDirection Direction)
{
    public bool Supported => Type is SqlDbType.BigInt or SqlDbType.Int or SqlDbType.SmallInt or SqlDbType.TinyInt or SqlDbType.Bit
        or SqlDbType.Decimal or SqlDbType.Money or SqlDbType.SmallMoney or SqlDbType.Float or SqlDbType.Real
        or SqlDbType.Date or SqlDbType.DateTime or SqlDbType.DateTime2 or SqlDbType.SmallDateTime or SqlDbType.DateTimeOffset or SqlDbType.Time
        or SqlDbType.UniqueIdentifier or SqlDbType.Char or SqlDbType.NChar or SqlDbType.VarChar or SqlDbType.NVarChar or SqlDbType.Text or SqlDbType.NText or SqlDbType.Xml;
    public string Description => Type is SqlDbType.Decimal ? $"{Type}({Precision},{Scale})" : Size != 0 ? $"{Type}({(Size == -1 ? "max" : Size.ToString())})" : Type.ToString();
    public SqlParameter Bind(string mode, string text)
    {
        if (!Supported) throw new ArgumentException($"{Name}: unsupported parameter type {Type}.");
        object value = DBNull.Value;
        if (mode == "Value")
        {
            var culture = CultureInfo.InvariantCulture;
            value = Type switch
            {
                SqlDbType.BigInt => long.Parse(text, culture), SqlDbType.Int => int.Parse(text, culture),
                SqlDbType.SmallInt => short.Parse(text, culture), SqlDbType.TinyInt => byte.Parse(text, culture),
                SqlDbType.Bit => text.Trim() switch { "1" => true, "0" => false, _ => bool.Parse(text) },
                SqlDbType.Decimal or SqlDbType.Money or SqlDbType.SmallMoney => decimal.Parse(text, NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint, culture),
                SqlDbType.Float => double.Parse(text, NumberStyles.Float, culture), SqlDbType.Real => float.Parse(text, NumberStyles.Float, culture),
                SqlDbType.UniqueIdentifier => Guid.Parse(text),
                SqlDbType.DateTimeOffset => DateTimeOffset.Parse(text, culture),
                SqlDbType.Time => TimeSpan.Parse(text, culture),
                SqlDbType.Date or SqlDbType.DateTime or SqlDbType.DateTime2 or SqlDbType.SmallDateTime => DateTime.Parse(text, culture),
                _ => text
            };
            if (value is string s && Size > 0 && s.Length > Size) throw new ArgumentException($"{Name}: maximum length is {Size}.");
            if (value is double d && !double.IsFinite(d) || value is float f && !float.IsFinite(f)) throw new ArgumentException($"{Name}: enter a finite number.");
            if (value is TimeSpan time && (time < TimeSpan.Zero || time >= TimeSpan.FromDays(1))) throw new ArgumentException($"{Name}: time must be between 00:00:00 and 24:00:00.");
            if (value is decimal number && Type == SqlDbType.Decimal && Precision > 0)
            {
                string digits = Math.Abs(number).ToString("0.############################", culture);
                string[] parts = digits.Split('.');
                int integers = parts[0].TrimStart('0').Length;
                if (integers > Precision - Scale || parts.Length > 1 && parts[1].Length > Scale) throw new ArgumentException($"{Name}: value exceeds {Description}.");
            }
        }
        else if (mode != "NULL") throw new ArgumentException("Invalid parameter mode.");
        return new SqlParameter(Name, Type, Size) { Precision = Precision, Scale = Scale, Direction = Direction, Value = value };
    }
}

public sealed record ProcedureExecutionResult(int RowsAffected, int ReturnValue, IReadOnlyDictionary<string, object> Outputs);

public partial class SqlServerDbAccess
{
    public static string QualifiedProcedureName(DefinitionObject target)
    {
        if (target.Kind != "Procedure" || string.IsNullOrEmpty(target.Schema)) throw new ArgumentException("A schema-qualified procedure is required.");
        return "[" + target.Schema.Replace("]", "]]") + "].[" + target.Name.Replace("]", "]]") + "]";
    }
    public IReadOnlyList<ProcedureParameter> ReadProcedureParameters(DefinitionObject target)
    {
        using var connection = new SqlConnection(ConnectionString);
        using var command = new SqlCommand(QualifiedProcedureName(target), connection) { CommandType = CommandType.StoredProcedure, CommandTimeout = 30 };
        connection.Open();
        using (var identity = new SqlCommand("SELECT OBJECT_ID(@name, 'P')", connection))
        {
            identity.Parameters.Add("@name", SqlDbType.NVarChar, 776).Value = QualifiedProcedureName(target);
            object id = identity.ExecuteScalar();
            if (id is not int objectId || target.Id > 0 && objectId != target.Id)
                throw new InvalidOperationException("Procedure is missing, inaccessible, unsupported, or was recreated. Reconnect to refresh the list.");
        }
        SqlCommandBuilder.DeriveParameters(command);
        return command.Parameters.Cast<SqlParameter>().Where(p => p.Direction != ParameterDirection.ReturnValue)
            .Select(p => new ProcedureParameter(p.ParameterName, p.SqlDbType, p.Size, p.Precision, p.Scale, p.Direction)).ToArray();
    }
    public ProcedureExecutionResult ExecuteProcedure(DefinitionObject target, IReadOnlyList<ProcedureParameter> expected, IReadOnlyList<SqlParameter> values)
    {
        if (!ReadProcedureParameters(target).SequenceEqual(expected)) throw new InvalidOperationException("Procedure parameters changed. Close and reopen the parameter window.");
        using var connection = new SqlConnection(ConnectionString);
        using var command = new SqlCommand(QualifiedProcedureName(target), connection) { CommandType = CommandType.StoredProcedure, CommandTimeout = 120 };
        foreach (var value in values) command.Parameters.Add((SqlParameter)((ICloneable)value).Clone());
        string returnName = "@__DataPieReturn";
        while (expected.Any(p => p.Name.Equals(returnName, StringComparison.OrdinalIgnoreCase))) returnName += "_";
        var result = command.Parameters.Add(returnName, SqlDbType.Int); result.Direction = ParameterDirection.ReturnValue;
        connection.Open();
        int rows = command.ExecuteNonQuery();
        var outputs = command.Parameters.Cast<SqlParameter>().Where(p => p.Direction is ParameterDirection.Output or ParameterDirection.InputOutput)
            .ToDictionary(p => p.ParameterName, p => p.Value);
        return new(rows, result.Value is int code ? code : 0, outputs);
    }
}
