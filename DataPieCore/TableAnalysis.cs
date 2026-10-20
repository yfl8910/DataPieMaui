using System;
using System.Collections.Generic;
using System.Data;
using System.Globalization;
using System.IO;
using System.Linq;
using DBUtil;

namespace DataPieCore;

public sealed record AnalysisCalculation(string Column, string Operation, string Name = "", string Id = "")
{
    public string Key => string.IsNullOrEmpty(Id) ? Operation + "|" + Column : Id;
    public string DisplayName => string.IsNullOrWhiteSpace(Name) ? Operation + " " + Column : Name.Trim();
}
public sealed record AnalysisFormula(string Name, string Left, string Operation, string Right = "", decimal? Constant = null);
public sealed record AnalysisField(string Name, string Type, bool CanGroup, string Notice);
public sealed record AnalysisRequest(IReadOnlyList<string> Groups, IReadOnlyList<AnalysisCalculation> Calculations,
    bool IncludeCount = true, string SearchColumn = "", string SearchText = "", IReadOnlyList<AnalysisFormula> Formulas = null,
    IReadOnlyList<string> SearchColumns = null, IReadOnlyList<SearchCondition> Conditions = null,
    string SortBy = "", bool SortDescending = false)
{
    public IReadOnlyList<string> EffectiveSearchColumns => SearchColumns ?? (string.IsNullOrEmpty(SearchColumn) ? Array.Empty<string>() : new[] { SearchColumn });
}
public sealed record AnalysisQuery(string Sql, string SearchValue, string ValidationSql, IReadOnlyList<SearchParameter> Parameters = null);
public sealed record AnalysisPreview(QueryPreviewResult Groups, QueryPreviewResult Totals);

// Bound to one source table; aggregate the full filtered source before limiting preview groups.
public sealed class TableAnalysis
{
    private readonly string connectionString, provider;
    private readonly TableStruct table;
    public string TableName => provider == "SQLSERVER" ? table.TableSchemaName + "." + table.Name : table.Name;
    public string TemplateTableName => table.Name;
    public IReadOnlyList<string> TextColumns => table.Columns.Where(IsText).Select(c => c.Name).ToArray();
    public IReadOnlyList<string> NumericColumns => table.Columns.Where(IsNumeric).Select(c => c.Name).ToArray();
    public IReadOnlyList<AnalysisField> Fields => table.Columns.Select(c => new AnalysisField(c.Name, c.Type, IsGroupable(c, provider), IsGroupable(c, provider) ? "" : "Binary or unsupported grouping type.")).ToArray();
    public string Provider => provider;
    public void ValidateRequest(AnalysisRequest request) => BuildQuery(table, provider, request);

    private TableAnalysis(string connectionString, string provider, TableStruct table)
    { this.connectionString = connectionString; this.provider = provider; this.table = table; }

    public static TableAnalysis Open(string connectionString, string provider, string schema, string name)
    {
        using var access = DbAccessFactory.Create(connectionString, provider);
        var table = access.ShowTables().SingleOrDefault(t => t.Name == name && (provider != "SQLSERVER" || t.TableSchemaName == schema))
            ?? throw new InvalidOperationException("The table is no longer available. Refresh the schema.");
        if (access is SQLiteDbAccess sqlite) table.Columns = sqlite.ReadSchemaColumns(name);
        return new(connectionString, provider, table);
    }

    private static string BaseType(Column column) => (column.Type ?? "").Split('(')[0].Trim().ToLowerInvariant();
    private static bool IsText(Column c) => BaseType(c) is "char" or "nchar" or "varchar" or "nvarchar" or "text" or "ntext" or "clob" or "string" or "sysname";
    private static bool IsNumeric(Column c) => BaseType(c) is "tinyint" or "smallint" or "int" or "integer" or "bigint" or "int2" or "int8"
        or "mediumint" or "unsigned big int" or "smallmoney" or "money" or "decimal" or "numeric" or "real" or "float" or "double" or "double precision";
    private static bool IsGroupable(Column c) => IsText(c) || IsNumeric(c) || BaseType(c) is "bit" or "bool" or "boolean"
        or "date" or "datetime" or "datetime2" or "smalldatetime" or "datetimeoffset" or "time" or "uniqueidentifier" or "guid";
    private static bool IsGroupable(Column c, string provider) => IsGroupable(c) || provider == "SQLITE" && BaseType(c) == "timestamp";

    public static AnalysisQuery BuildQuery(TableStruct table, string provider, AnalysisRequest request)
    {
        if (provider is not ("SQLSERVER" or "SQLITE")) throw new ArgumentException("Unsupported database type.");
        string Q(string name) => SqlQueryBuilder.QuoteIdentifier(name, provider);
        var columns = table.Columns.ToDictionary(c => c.Name, StringComparer.Ordinal);
        Column Require(string name, bool numeric)
        {
            if (!columns.TryGetValue(name, out var column) || !(numeric ? IsNumeric(column) : IsText(column)))
                throw new ArgumentException("Select a valid " + (numeric ? "numeric" : "text") + " column: " + name);
            return column;
        }
        if (request.Groups.Distinct(StringComparer.Ordinal).Count() != request.Groups.Count) throw new ArgumentException("Duplicate grouping fields.");
        if (request.Calculations.Select(c => (c.Column, c.Operation)).Distinct().Count() != request.Calculations.Count || request.Calculations.Select(c => c.Key).Distinct(StringComparer.Ordinal).Count() != request.Calculations.Count) throw new ArgumentException("Duplicate calculations.");
        var names = request.Calculations.Select(c => c.DisplayName).Concat((request.Formulas ?? Array.Empty<AnalysisFormula>()).Select(f => f.Name?.Trim() ?? "")).ToArray();
        if (names.Any(string.IsNullOrWhiteSpace) || request.Calculations.Any(c => (c.Name?.Trim().Length ?? 0) > 110)
            || (request.Formulas ?? Array.Empty<AnalysisFormula>()).Any(f => f.Name.Trim().Length > 110)) throw new ArgumentException("Custom result names must contain 1 to 110 characters.");
        if (names.Distinct(StringComparer.OrdinalIgnoreCase).Count() != names.Length) throw new ArgumentException("Use a unique name for each aggregate and formula.");
        if (!request.IncludeCount && request.Calculations.Count == 0) throw new ArgumentException("Select at least one calculation or record count.");
        string TextExpression(Column c) => provider == "SQLSERVER" && BaseType(c) is "text" or "ntext" ? "CONVERT(nvarchar(max)," + Q(c.Name) + ")" : Q(c.Name);
        var groups = request.Groups.Select(n =>
        {
            if (!columns.TryGetValue(n, out var column) || !IsGroupable(column, provider)) throw new ArgumentException("Select a supported grouping column: " + n);
            return TextExpression(column);
        }).ToArray();
        var select = request.Groups.Select((name, i) => groups[i] + " AS " + Q("__g" + i)).ToList();
        var output = request.Groups.Select((name, i) => "s." + Q("__g" + i) + " AS " + Q(name)).ToList();
        var references = new Dictionary<string, string>(StringComparer.Ordinal);
        var sortExpressions = new Dictionary<string, string>(StringComparer.Ordinal);
        var aliases = request.Groups.ToHashSet(StringComparer.OrdinalIgnoreCase);
        string Alias(string name)
        {
            string stem = name.Length > 110 ? name[..110] : name;
            string result = stem;
            for (int i = 2; !aliases.Add(result); i++) result = stem + " " + i;
            return Q(result);
        }
        foreach (var calculation in request.Calculations)
        {
            var c = Require(calculation.Column, true);
            if (calculation.Operation is not ("SUM" or "AVG" or "MIN" or "MAX")) throw new ArgumentException("Unsupported calculation.");
            string expression = Q(c.Name);
            if (provider == "SQLSERVER" && calculation.Operation is "SUM" or "AVG")
            {
                string type = BaseType(c);
                int scale = c.Scale ?? (type is "money" or "smallmoney" ? 4 : 0);
                if (type is "decimal" or "numeric" && !c.Scale.HasValue && c.Type.Contains(','))
                    scale = int.Parse(c.Type.Split(',')[1].TrimEnd(')', ' '), CultureInfo.InvariantCulture);
                expression = type is "real" or "float" ? "CONVERT(float," + expression + ")" : $"CONVERT(decimal(38,{Math.Clamp(scale, 0, 38)}),{expression})";
            }
            string internalName = "__a" + references.Count;
            string reference = "s." + Q(internalName);
            // Widen MIN/MAX of integers as well when used in arithmetic, preventing integer division/overflow.
            references.Add(calculation.Key, provider == "SQLSERVER" && calculation.Operation is "MIN" or "MAX"
                && BaseType(c) is not ("real" or "float" or "decimal" or "numeric" or "money" or "smallmoney")
                ? "CONVERT(decimal(38,0)," + reference + ")" : reference);
            select.Add(calculation.Operation + "(" + expression + ") AS " + Q(internalName));
            output.Add(reference + " AS " + Alias(calculation.DisplayName));
            sortExpressions.Add("aggregate:" + calculation.Key, reference);
        }
        if (request.IncludeCount)
        {
            select.Add((provider == "SQLSERVER" ? "COUNT_BIG(*)" : "COUNT(*)") + " AS " + Q("__count"));
            output.Add("s." + Q("__count") + " AS " + Alias("Records"));
            sortExpressions.Add("count", "s." + Q("__count"));
        }
        foreach (var formula in request.Formulas ?? Array.Empty<AnalysisFormula>())
        {
            if (string.IsNullOrWhiteSpace(formula.Name)) throw new ArgumentException("Name each formula.");
            if (formula.Operation is not ("+" or "-" or "*" or "/")) throw new ArgumentException("Unsupported formula operator.");
            if (!references.TryGetValue(formula.Left, out var left)) throw new ArgumentException("Select a valid left aggregate for " + formula.Name);
            string right;
            if (formula.Constant.HasValue)
            {
                if (!string.IsNullOrEmpty(formula.Right)) throw new ArgumentException("Choose either a constant or an aggregate.");
                right = formula.Constant.Value.ToString("0.############################", CultureInfo.InvariantCulture);
            }
            else if (!references.TryGetValue(formula.Right ?? "", out right)) throw new ArgumentException("Select a valid right aggregate or constant for " + formula.Name);
            string expression = formula.Operation == "/" ? (provider == "SQLITE" ? "(1.0 * " + left + ")" : left) + " / NULLIF(" + right + ",0)"
                : left + " " + formula.Operation + " " + right;
            output.Add("(" + expression + ") AS " + Alias(formula.Name.Trim()));
            sortExpressions.Add("formula:" + formula.Name.Trim(), "(" + expression + ")");
        }
        string tableName = provider == "SQLSERVER" ? Q(table.TableSchemaName) + "." + Q(table.Name) : Q("main") + "." + Q(table.Name);
        string filter = "", searchValue = null;
        IReadOnlyList<SearchParameter> searchParameters = null;
        if (request.Conditions != null)
        {
            var predicate = SearchConditions.BuildAnd(request.Conditions, provider,
                name => { var c = Require(name, false); return new SearchColumn(c.Name, typeof(string), TextExpression(c)); },
                table.Columns.Where(IsText).Select(c => new SearchColumn(c.Name, typeof(string), TextExpression(c))).ToArray());
            filter = predicate.Sql.Length == 0 ? "" : " WHERE " + predicate.Sql;
            searchParameters = predicate.Parameters;
        }
        else if (!string.IsNullOrEmpty(request.SearchText))
        {
            var selectedSearchColumns = request.EffectiveSearchColumns;
            var searchColumns = selectedSearchColumns.Count == 0 ? table.Columns.Where(IsText).ToArray()
                : selectedSearchColumns.Distinct(StringComparer.Ordinal).Select(n => Require(n, false)).ToArray();
            if (searchColumns.Length == 0) throw new ArgumentException("No text fields are available for searching.");
            string escaped = request.SearchText.Replace("!", "!!").Replace("%", "!%").Replace("_", "!_");
            if (provider == "SQLSERVER") escaped = escaped.Replace("[", "![");
            searchValue = "%" + escaped + "%";
            filter = " WHERE (" + string.Join(" OR ", searchColumns.Select(c => TextExpression(c) + " LIKE @search ESCAPE '!'")) + ")";
        }
        string validation = null;
        if (provider == "SQLITE" && request.Calculations.Count > 0)
        {
            var invalid = request.Calculations.Select(c => c.Column).Distinct().Select(n => "typeof(" + Q(n) + ") NOT IN ('null','integer','real')");
            validation = "SELECT 1 FROM " + tableName + filter + (filter.Length == 0 ? " WHERE (" : " AND (") + string.Join(" OR ", invalid) + ") LIMIT 1";
        }
        string sql = "SELECT " + string.Join(", ", select) + " FROM " + tableName + filter;
        if (groups.Length > 0) sql += " GROUP BY " + string.Join(", ", groups);
        sql = "SELECT " + string.Join(", ", output) + " FROM (" + sql + ") s";
        if (!string.IsNullOrEmpty(request.SortBy) && !sortExpressions.ContainsKey(request.SortBy)) throw new ArgumentException("Select an available default sort calculation, or choose No default sort.");
        if (groups.Length > 0)
        {
            string order = string.Join(", ", request.Groups.Select((_, i) => "s." + Q("__g" + i)));
            if (!string.IsNullOrEmpty(request.SortBy))
            {
                string expression = sortExpressions[request.SortBy];
                order = $"CASE WHEN {expression} IS NULL THEN 1 ELSE 0 END, {expression}" + (request.SortDescending ? " DESC, " : " ASC, ") + order;
            }
            sql += " ORDER BY " + order;
        }
        return new(sql, searchValue, validation, searchParameters);
    }

    private static IDbCommand Command(IDbConnection connection, string sql, string searchValue, IReadOnlyList<SearchParameter> parameters = null, IDbTransaction transaction = null)
    {
        var command = connection.CreateCommand(); command.CommandText = sql; command.CommandTimeout = 120;
        command.Transaction = transaction;
        if (searchValue != null)
        {
            var parameter = command.CreateParameter(); parameter.ParameterName = "@search"; parameter.DbType = DbType.String; parameter.Value = searchValue;
            command.Parameters.Add(parameter);
        }
        if (parameters != null) SearchConditions.Bind(command, parameters);
        return command;
    }
    private static void Validate(IDbConnection connection, AnalysisQuery query, IDbTransaction transaction = null)
    {
        if (query.ValidationSql == null) return;
        using var validation = Command(connection, query.ValidationSql, query.SearchValue, query.Parameters, transaction);
        if (validation.ExecuteScalar() != null) throw new InvalidOperationException("Selected numeric fields contain nonnumeric values in matching source rows. Correct the source data or change the calculation fields.");
    }
    public QueryPreviewResult Analyze(AnalysisRequest request)
    {
        var query = BuildQuery(table, provider, request);
        using var access = DbAccessFactory.Create(connectionString, provider); access.conn.Open();
        Validate(access.conn, query);
        return ReadPreview(access.conn, query);
    }
    public AnalysisPreview AnalyzeWithTotals(AnalysisRequest request)
    {
        if (request.Groups.Count == 0) return new(Analyze(request), null);
        var grouped = BuildQuery(table, provider, request);
        var overall = BuildQuery(table, provider, request with { Groups = Array.Empty<string>() });
        using var access = DbAccessFactory.Create(connectionString, provider); access.conn.Open();
        Validate(access.conn, grouped);
        // Publish both results together; totals include all matching source rows, not just preview groups.
        var rows = ReadPreview(access.conn, grouped);
        var totals = ReadPreview(access.conn, overall);
        return new(rows, totals);
    }
    private static QueryPreviewResult ReadPreview(IDbConnection connection, AnalysisQuery query, IDbTransaction transaction = null)
    {
        using var command = Command(connection, query.Sql, query.SearchValue, query.Parameters, transaction);
        using var reader = command.ExecuteReader();
        var result = QueryPreviewResult.Read(reader, 1000);
        if (result.Truncated) command.Cancel();
        return result;
    }
    public void Export(AnalysisRequest request, string path)
    {
        var query = BuildQuery(table, provider, request);
        path = Path.GetFullPath(path);
        string temporary = path + "." + Guid.NewGuid().ToString("N") + ".xlsx";
        try
        {
            using (var access = DbAccessFactory.Create(connectionString, provider))
            {
                access.conn.Open();
                using var transaction = access.conn.BeginTransaction(IsolationLevel.Serializable);
                Validate(access.conn, query, transaction);
                object[] totals = null;
                if (request.Groups.Count > 0)
                {
                    var overall = BuildQuery(table, provider, request with { Groups = Array.Empty<string>() });
                    var result = ReadPreview(access.conn, overall, transaction);
                    totals = new object[request.Groups.Count + result.ColumnCount];
                    totals[0] = "Total";
                    for (int i = 0; i < result.ColumnCount; i++) totals[request.Groups.Count + i] = result.GetValue(0, i);
                }
                using var command = Command(access.conn, query.Sql, query.SearchValue, query.Parameters, transaction);
                using var reader = command.ExecuteReader();
                AnalysisExcelExport.Save(temporary, reader, request.Groups.Count, totals);
                reader.Close(); transaction.Commit();
            }
            File.Move(temporary, path, true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}
