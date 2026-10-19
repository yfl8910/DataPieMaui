using System;
using System.Collections.Generic;
using System.Data;
using System.Globalization;
using System.Linq;

namespace DataPieCore;

public sealed record SearchCondition(string Column, string Text);
public sealed record SearchField(string Key, string Name, string Hint = "Contains text");
public sealed record SearchColumn(string Name, Type Type, string Expression);
public sealed record SearchParameter(string Name, object Value);
public sealed record SearchPredicate(string Sql, IReadOnlyList<SearchParameter> Parameters);

public static class SearchConditions
{
    public static SearchPredicate BuildAnd(IEnumerable<SearchCondition> conditions, string provider,
        Func<string, SearchColumn> resolve, IReadOnlyList<SearchColumn> allTextColumns = null)
    {
        var clauses = new List<string>();
        var parameters = new List<SearchParameter>();
        foreach (var condition in conditions.Where(c => !string.IsNullOrEmpty(c.Text)))
        {
            string name = "@filter" + clauses.Count;
            if (string.IsNullOrEmpty(condition.Column) && allTextColumns != null)
            {
                if (allTextColumns.Count == 0) throw new ArgumentException("No text fields are available for searching.");
                clauses.Add("(" + string.Join(" OR ", allTextColumns.Select(c => c.Expression + " LIKE " + name + " ESCAPE '!'")) + ")");
                parameters.Add(new(name, ContainsValue(condition.Text, provider)));
                continue;
            }
            var column = resolve(condition.Column);
            if (column.Type == typeof(string))
            {
                clauses.Add(column.Expression + " LIKE " + name + " ESCAPE '!'");
                parameters.Add(new(name, ContainsValue(condition.Text, provider)));
            }
            else if (column.Type == typeof(DateTime) && DateTime.TryParseExact(condition.Text, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var day))
            {
                clauses.Add("(" + column.Expression + " >= " + name + " AND " + column.Expression + " < " + name + "End)");
                parameters.Add(new(name, day)); parameters.Add(new(name + "End", day.AddDays(1)));
            }
            else
            {
                try
                {
                    object value = column.Type == typeof(Guid) ? Guid.Parse(condition.Text)
                        : column.Type == typeof(DateTime) ? DateTime.Parse(condition.Text, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind)
                        : column.Type == typeof(DateTimeOffset) ? DateTimeOffset.Parse(condition.Text, CultureInfo.InvariantCulture)
                        : column.Type == typeof(TimeSpan) ? TimeSpan.Parse(condition.Text, CultureInfo.InvariantCulture)
                        : Convert.ChangeType(condition.Text, column.Type, CultureInfo.InvariantCulture);
                    clauses.Add(column.Expression + " = " + name); parameters.Add(new(name, value));
                }
                catch (Exception ex) when (ex is FormatException or OverflowException)
                { throw new ArgumentException($"Invalid search value for {column.Name} ({column.Type.Name}).", ex); }
            }
        }
        return new(string.Join(" AND ", clauses), parameters);
    }
    public static string ContainsValue(string text, string provider)
    {
        string literal = text.Replace("!", "!!").Replace("%", "!%").Replace("_", "!_");
        if (provider == "SQLSERVER") literal = literal.Replace("[", "![");
        return "%" + literal + "%";
    }
    public static void Bind(IDbCommand command, IEnumerable<SearchParameter> parameters)
    {
        foreach (var value in parameters)
        {
            var parameter = command.CreateParameter(); parameter.ParameterName = value.Name; parameter.Value = value.Value;
            command.Parameters.Add(parameter);
        }
    }
}
