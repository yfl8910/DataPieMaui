using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace DataPieCore;

public sealed record AnalysisFilterRule(string Id, string Column, string Operation, IReadOnlyList<string> Values, string ParameterId = "");
public sealed record AnalysisFilterGroup(IReadOnlyList<AnalysisFilterRule> Rules);
public sealed record AnalysisParameter(string Id, string Name, IReadOnlyList<string> Defaults, bool Required = false, string RelativeDate = "");
public sealed record AnalysisFilterConfig(IReadOnlyList<AnalysisFilterGroup> Groups, IReadOnlyList<AnalysisParameter> Parameters, IReadOnlyList<AnalysisFilterRule> CommonRules = null);
public sealed record AnalysisFilterField(string Name, string Kind, string DisplayName = "");

public static class AnalysisFilters
{
    public static string Summary(AnalysisFilterConfig config)
    {
        if (config == null) return "";
        string Describe(AnalysisFilterRule r) => (string.IsNullOrEmpty(r.Column) ? "All text fields" : r.Column) + " " + r.Operation + (r.Operation is "IsNull" or "IsNotNull" ? "" : string.IsNullOrEmpty(r.ParameterId) ? " [" + string.Join("; ", r.Values) + "]" : " @" + (config.Parameters.FirstOrDefault(p => p.Id == r.ParameterId)?.Name ?? r.ParameterId));
        string grouped = string.Join(" OR ", config.Groups.Where(g => g.Rules.Count > 0).Select(g => "(" + string.Join(" AND ", g.Rules.Select(Describe)) + ")"));
        string common = string.Join(" AND ", (config.CommonRules ?? Array.Empty<AnalysisFilterRule>()).Select(Describe));
        return string.Join(" AND ", new[] { grouped.Length > 0 && common.Length > 0 ? "(" + grouped + ")" : grouped, common }.Where(s => s.Length > 0));
    }
    public static readonly string[] Operations = { "Equals", "NotEquals", "Contains", "Range", "In", "IsNull", "IsNotNull", "Greater", "GreaterOrEqual", "Less", "LessOrEqual" };
    public static AnalysisFilterConfig Snapshot(AnalysisFilterConfig config)
    {
        if (config == null) return null;
        if (config.Groups == null || config.Parameters == null || config.Groups.Any(g => g?.Rules == null || g.Rules.Any(r => r == null || string.IsNullOrWhiteSpace(r.Id) || !Operations.Contains(r.Operation)))
            || config.CommonRules?.Any(r => r == null || string.IsNullOrWhiteSpace(r.Id) || !Operations.Contains(r.Operation)) == true
            || config.Parameters.Any(p => p == null || string.IsNullOrWhiteSpace(p.Id) || string.IsNullOrWhiteSpace(p.Name) || p.Name.Length > 100))
            throw new ArgumentException("The template contains an invalid filter configuration.");
        var rules = config.Groups.SelectMany(g => g.Rules).Concat(config.CommonRules ?? Array.Empty<AnalysisFilterRule>()).ToArray();
        if (rules.Select(r => r.Id).Distinct(StringComparer.Ordinal).Count() != rules.Length
            || config.Parameters.Select(p => p.Id).Distinct(StringComparer.Ordinal).Count() != config.Parameters.Count) throw new ArgumentException("Duplicate filter or parameter IDs.");
        return new(config.Groups.Select(g => new AnalysisFilterGroup(g.Rules.Select(r => r with { Values = r.Values?.ToArray() ?? Array.Empty<string>() }).ToArray())).ToArray(),
            config.Parameters.Select(p => p with { Defaults = p.Defaults?.ToArray() ?? Array.Empty<string>() }).ToArray(),
            config.CommonRules?.Select(r => r with { Values = r.Values?.ToArray() ?? Array.Empty<string>() }).ToArray());
    }
    public static AnalysisFilterConfig Migrate(IReadOnlyList<SearchCondition> conditions, IReadOnlyList<AnalysisFilterField> fields)
    {
        var rules = (conditions ?? Array.Empty<SearchCondition>()).Where(c => !string.IsNullOrEmpty(c.Text)).Select(c =>
        {
            string kind = fields.FirstOrDefault(f => f.Name == c.Column)?.Kind ?? "Text";
            bool day = kind == "Date" && DateTime.TryParseExact(c.Text, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out _);
            return new AnalysisFilterRule(Guid.NewGuid().ToString("N"), c.Column, day ? "Range" : kind is "Number" or "Date" or "Boolean" or "Guid" or "Time" ? "Equals" : "Contains", day ? new[] { c.Text, c.Text } : new[] { c.Text });
        }).ToArray();
        return new(rules.Length == 0 ? Array.Empty<AnalysisFilterGroup>() : new[] { new AnalysisFilterGroup(rules) }, Array.Empty<AnalysisParameter>());
    }
    public static AnalysisFilterConfig MergeLegacy(AnalysisFilterConfig config, IReadOnlyList<SearchCondition> conditions, IReadOnlyList<AnalysisFilterField> fields)
    {
        var legacy = Migrate(conditions, fields).Groups.SelectMany(g => g.Rules).ToArray();
        if (config == null) return new(legacy.Length == 0 ? Array.Empty<AnalysisFilterGroup>() : new[] { new AnalysisFilterGroup(legacy) }, Array.Empty<AnalysisParameter>());
        if (legacy.Length == 0) return Snapshot(config);
        return new(config.Groups, config.Parameters, (config.CommonRules ?? Array.Empty<AnalysisFilterRule>()).Concat(legacy).ToArray());
    }
    // Relative dates use the application's local calendar. Resolve once per execution, then export the frozen values.
    public static IReadOnlyList<string> Defaults(AnalysisParameter parameter, DateTime today)
    {
        var month = new DateTime(today.Year, today.Month, 1);
        return parameter.RelativeDate switch
        {
            "" => parameter.Defaults ?? Array.Empty<string>(),
            "Today" => new[] { today.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), today.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) },
            "ThisMonth" => new[] { month.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), month.AddMonths(1).AddDays(-1).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) },
            "LastMonth" => new[] { month.AddMonths(-1).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), month.AddDays(-1).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) },
            _ => throw new ArgumentException("Unsupported relative date default.")
        };
    }
    public static IReadOnlyDictionary<string, IReadOnlyList<string>> Resolve(AnalysisFilterConfig config,
        IReadOnlyDictionary<string, IReadOnlyList<string>> values = null)
        => (config?.Parameters ?? Array.Empty<AnalysisParameter>()).ToDictionary(p => p.Id,
            p => (IReadOnlyList<string>)((values != null && values.TryGetValue(p.Id, out var input) ? input : Defaults(p, DateTime.Today)) ?? Array.Empty<string>()).ToArray(), StringComparer.Ordinal);
    public static Func<Func<string, object>, bool> CreatePreviewMatcher(AnalysisFilterConfig config, Func<string, SearchColumn> resolve, IReadOnlyList<SearchColumn> allText)
    {
        // Reuse the database compiler for validation and type checking before changing the active preview.
        Build(config, null, "SQLITE", resolve, allText);
        var values = Resolve(config);
        Func<AnalysisFilterRule, Func<Func<string, object>, bool>> compile = rule =>
        {
            var column = string.IsNullOrEmpty(rule.Column) ? null : resolve(rule.Column);
            var input = string.IsNullOrEmpty(rule.ParameterId) ? rule.Values ?? Array.Empty<string>() : values[rule.ParameterId];
            if (rule.Operation == "In") input = input.Where(v => !string.IsNullOrEmpty(v)).ToArray();
            if (rule.Operation is not ("IsNull" or "IsNotNull") && (input.Count == 0 || (!string.IsNullOrEmpty(rule.ParameterId) && input.All(string.IsNullOrEmpty)))) return null;
            object Parse(string value) => column == null || column.Type == typeof(string) ? value : column.Type == typeof(Guid) ? Guid.Parse(value) : column.Type == typeof(DateTime) ? DateTime.Parse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind) : column.Type == typeof(DateTimeOffset) ? DateTimeOffset.Parse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind) : column.Type == typeof(TimeSpan) ? TimeSpan.Parse(value, CultureInfo.InvariantCulture) : Convert.ChangeType(value, column.Type, CultureInfo.InvariantCulture);
            object[] operands = input.Select(Parse).ToArray();
            int Compare(object a, object b) => a is string text ? string.Compare(text, (string)b, StringComparison.OrdinalIgnoreCase) : ((IComparable)a).CompareTo(b);
            return get =>
            {
                if (column == null) return allText.Any(c => get(c.Name) is string text && text.Contains(input[0], StringComparison.OrdinalIgnoreCase));
                object value = get(column.Name);
                if (rule.Operation == "IsNull") return value is null or DBNull;
                if (rule.Operation == "IsNotNull") return value is not null and not DBNull;
                if (value is null or DBNull) return false;
                if (rule.Operation == "Contains") return value is string text && text.Contains(input[0], StringComparison.OrdinalIgnoreCase);
                if (rule.Operation == "In") return operands.Any(v => Compare(value, v) == 0);
                if (rule.Operation == "Range")
                {
                    bool dateOnly = (column.Type == typeof(DateTime) || column.Type == typeof(DateTimeOffset)) && DateTime.TryParseExact(input[1], "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out _);
                    object end = operands[1];
                    if (dateOnly) { if (end is DateTimeOffset offset) end = offset.AddDays(1); else end = ((DateTime)end).AddDays(1); }
                    return Compare(value, operands[0]) >= 0 && (dateOnly ? Compare(value, end) < 0 : Compare(value, end) <= 0);
                }
                int comparison = Compare(value, operands[0]);
                return rule.Operation switch { "Equals" => comparison == 0, "NotEquals" => comparison != 0, "Greater" => comparison > 0, "GreaterOrEqual" => comparison >= 0, "Less" => comparison < 0, "LessOrEqual" => comparison <= 0, _ => false };
            };
        };
        var groups = config.Groups.Select(g => g.Rules.Select(compile).Where(f => f != null).ToArray()).Where(g => g.Length > 0).ToArray();
        var common = (config.CommonRules ?? Array.Empty<AnalysisFilterRule>()).Select(compile).Where(f => f != null).ToArray();
        return get => common.All(f => f(get)) && (groups.Length == 0 || groups.Any(g => g.All(f => f(get))));
    }
    public static SearchPredicate Build(AnalysisFilterConfig config, IReadOnlyDictionary<string, IReadOnlyList<string>> values,
        string provider, Func<string, SearchColumn> resolve, IReadOnlyList<SearchColumn> allText = null, bool validateOnly = false, string parameterPrefix = "@af")
    {
        if (config?.Groups == null || config.Parameters == null || config.Groups.Any(g => g?.Rules == null || g.Rules.Any(r => r == null)) || config.CommonRules?.Any(r => r == null) == true
            || config.Parameters.Any(p => p == null || string.IsNullOrWhiteSpace(p.Id) || string.IsNullOrWhiteSpace(p.Name))) throw new ArgumentException("Invalid filter configuration.");
        if (config.Groups.Count > 30 || config.Groups.Sum(g => g.Rules.Count) + (config.CommonRules?.Count ?? 0) > 100) throw new ArgumentException("Use at most 30 OR groups and 100 conditions.");
        if (config.Parameters.Select(p => p.Id).Distinct(StringComparer.Ordinal).Count() != config.Parameters.Count) throw new ArgumentException("Duplicate parameter IDs.");
        var definitions = config.Parameters.ToDictionary(p => p.Id, StringComparer.Ordinal);
        var actual = Resolve(config, values);
        var signatures = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var rule in config.Groups.SelectMany(g => g.Rules).Concat(config.CommonRules ?? Array.Empty<AnalysisFilterRule>()).Where(r => !string.IsNullOrEmpty(r.ParameterId)))
        {
            if (!definitions.TryGetValue(rule.ParameterId, out var definition)) throw new ArgumentException("Missing parameter: " + rule.ParameterId);
            Type type = string.IsNullOrEmpty(rule.Column) ? typeof(string) : resolve(rule.Column).Type;
            string signature = type.FullName + ":" + (rule.Operation is "Range" or "In" ? rule.Operation : "Single");
            if (signatures.TryGetValue(rule.ParameterId, out var previous) && previous != signature) throw new ArgumentException("A shared parameter must use the same field type and value shape: " + definition.Name);
            signatures[rule.ParameterId] = signature;
        }
        var parameters = new List<SearchParameter>(); var groups = new List<string>();
        string Add(object value)
        {
            int limit = provider == "SQLITE" ? 900 : 1800;
            if (parameters.Count >= limit) throw new ArgumentException("Too many filter values. Bound-value limit: " + limit);
            string id = parameterPrefix + parameters.Count; parameters.Add(new(id, value)); return id;
        }
        foreach (var group in config.Groups)
        {
            var clauses = new List<string>();
            foreach (var rule in group.Rules)
            {
                if (!Operations.Contains(rule.Operation)) throw new ArgumentException("Unsupported filter operator.");
                bool all = string.IsNullOrEmpty(rule.Column);
                var column = all ? new SearchColumn("All text fields", typeof(string), "") : resolve(rule.Column);
                if (all && (rule.Operation != "Contains" || allText == null || allText.Count == 0)) throw new ArgumentException("All text fields supports Contains only.");
                if (rule.Operation == "Contains" && column.Type != typeof(string)) throw new ArgumentException("Contains requires a text field.");
                AnalysisParameter definition = null;
                IReadOnlyList<string> input = rule.Values ?? Array.Empty<string>();
                if (!string.IsNullOrEmpty(rule.ParameterId))
                {
                    if (!definitions.TryGetValue(rule.ParameterId, out definition)) throw new ArgumentException("Missing parameter: " + rule.ParameterId);
                    string signature = column.Type.FullName + ":" + (rule.Operation is "Range" or "In" ? rule.Operation : "Single");
                    if (signatures.TryGetValue(rule.ParameterId, out var previous) && previous != signature) throw new ArgumentException("A shared parameter must use the same field type and value shape: " + definition.Name);
                    signatures[rule.ParameterId] = signature;
                    input = actual[rule.ParameterId];
                    if (!string.IsNullOrEmpty(definition.RelativeDate) && ((column.Type != typeof(DateTime) && column.Type != typeof(DateTimeOffset)) || rule.Operation != "Range")) throw new ArgumentException("Relative dates require a date range.");
                }
                if (rule.Operation is "IsNull" or "IsNotNull") { clauses.Add(column.Expression + (rule.Operation == "IsNull" ? " IS NULL" : " IS NOT NULL")); continue; }
                if (rule.Operation == "In") input = input.Where(v => !string.IsNullOrEmpty(v)).ToArray();
                bool empty = input.Count == 0 || (definition != null && input.All(string.IsNullOrEmpty));
                if (empty)
                {
                    if (definition != null && !definition.Required) continue;
                    if (validateOnly && definition != null) continue;
                    throw new ArgumentException("Enter a value for " + (definition?.Name ?? column.Name));
                }
                if (input.Any(v => v == null)) throw new ArgumentException("Filter values cannot be null; use IsNull.");
                object Parse(string value)
                {
                    try
                    {
                        if (column.Type == typeof(string)) return value;
                        if (column.Type == typeof(DateTime)) return DateTime.Parse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
                        if (column.Type == typeof(DateTimeOffset)) return DateTimeOffset.Parse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
                        if (column.Type == typeof(Guid)) return Guid.Parse(value);
                        if (column.Type == typeof(TimeSpan)) return TimeSpan.Parse(value, CultureInfo.InvariantCulture);
                        return Convert.ChangeType(value, column.Type, CultureInfo.InvariantCulture);
                    }
                    catch (Exception ex) when (ex is FormatException or OverflowException) { throw new ArgumentException("Invalid value for " + column.Name + ".", ex); }
                }
                string expression = column.Expression;
                if (rule.Operation == "In")
                {
                    if (input.Count > 500) throw new ArgumentException("A multi-value filter supports at most 500 values.");
                    clauses.Add(expression + " IN (" + string.Join(",", input.Distinct(StringComparer.Ordinal).Select(v => Add(Parse(v)))) + ")"); continue;
                }
                if (rule.Operation == "Range")
                {
                    if (input.Count != 2 || input.Any(string.IsNullOrWhiteSpace)) throw new ArgumentException("Enter both range bounds for " + column.Name);
                    if (column.Type == typeof(string) || column.Type == typeof(bool) || column.Type == typeof(Guid)) throw new ArgumentException("Ranges require a numeric or date field.");
                    var start = Parse(input[0]); var end = Parse(input[1]);
                    if (((IComparable)start).CompareTo(end) > 0) throw new ArgumentException("Range start exceeds its end: " + column.Name);
                    bool dateOnly = (column.Type == typeof(DateTime) || column.Type == typeof(DateTimeOffset)) && DateTime.TryParseExact(input[1], "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out _);
                    if (dateOnly)
                    {
                        if (end is DateTimeOffset offset) end = offset.AddDays(1);
                        else end = ((DateTime)end).AddDays(1);
                    }
                    clauses.Add("(" + expression + ">=" + Add(start) + " AND " + expression + (dateOnly ? "<" : "<=") + Add(end) + ")"); continue;
                }
                if (input.Count != 1) throw new ArgumentException("Enter one value for " + column.Name);
                if (rule.Operation == "Contains")
                {
                    string parameter = Add(SearchConditions.ContainsValue(input[0], provider));
                    clauses.Add(all ? "(" + string.Join(" OR ", allText.Select(c => c.Expression + " LIKE " + parameter + " ESCAPE '!'")) + ")" : expression + " LIKE " + parameter + " ESCAPE '!'"); continue;
                }
                string op = rule.Operation switch { "Equals" => "=", "NotEquals" => "<>", "Greater" => ">", "GreaterOrEqual" => ">=", "Less" => "<", "LessOrEqual" => "<=", _ => throw new ArgumentException("Invalid filter operator.") };
                clauses.Add(expression + op + Add(Parse(input[0])));
            }
            // An optional, empty OR group is removed, never turned into TRUE.
            if (clauses.Count > 0) groups.Add("(" + string.Join(" AND ", clauses) + ")");
        }
        string sql = string.Join(" OR ", groups);
        if (config.CommonRules?.Count > 0)
        {
            var common = Build(new AnalysisFilterConfig(new[] { new AnalysisFilterGroup(config.CommonRules) }, config.Parameters), values, provider, resolve, allText, validateOnly, "@ac");
            string extra = common.Sql;
            parameters.AddRange(common.Parameters);
            if (parameters.Count > (provider == "SQLITE" ? 900 : 1800)) throw new ArgumentException("Too many filter values.");
            if (extra.Length > 0) sql = sql.Length == 0 ? extra : "(" + sql + ") AND " + extra;
        }
        return new(sql, parameters);
    }
}

public sealed record AnalysisNumberFormat(string Kind = "Auto", int Decimals = 2, bool Thousands = true, string Symbol = "")
{
    public void Validate()
    {
        if (Kind is not ("Auto" or "Number" or "Percent" or "Currency") || Decimals is < 0 or > 10 || Symbol?.Length > 12 || (Symbol?.Any(c => char.IsControl(c) || c == '"') ?? false))
            throw new ArgumentException("Invalid numeric format (use 0–10 decimal places).");
    }
    public string Code()
    {
        Validate();
        string digits = (Thousands ? "#,##0" : "0") + (Decimals == 0 ? "" : "." + new string('0', Decimals));
        return (Kind == "Currency" && !string.IsNullOrEmpty(Symbol) ? "\"" + Symbol.Replace("\"", "\"\"") + "\" " : "") + digits + (Kind == "Percent" ? "%" : "");
    }
}
