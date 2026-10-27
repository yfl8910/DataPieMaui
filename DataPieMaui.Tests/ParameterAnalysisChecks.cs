using System.Data;
using System.Data.SQLite;
using System.Globalization;
using System.IO.Compression;
using System.Text.Json;
using System.Xml.Linq;
using DataPieCore;
using DataPieMaui.Components;
using DataPieMaui.Services;
using DataPieMaui.Services.Shared;

internal static class ParameterAnalysisChecks
{
    public static async Task Run(string root, Func<string, Func<Task>, Task> test)
    {
        void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
        string path = Path.Combine(root, "parameters.db"); SQLiteConnection.CreateFile(path);
        string cs = new SQLiteConnectionStringBuilder { DataSource = path }.ToString();
        using (var db = new SQLiteConnection(cs))
        {
            db.Open(); using var command = new SQLiteCommand("""
                CREATE TABLE Sales(Id INTEGER, Customer TEXT, Amount NUMERIC, Flag BOOLEAN, SoldAt DATETIME, Note TEXT);
                INSERT INTO Sales VALUES(1,'A',100,1,'2026-01-01 08:00:00',NULL),(2,'A',200,0,'2026-01-31 23:59:59',''),
                (3,'B',300,1,'2026-02-01 00:00:00','x'),(4,'B',400,0,'2026-01-15 00:00:00',NULL),
                (5,'C',NULL,1,'2026-01-20 00:00:00','A,B'),(6,'A%_![]',50,0,'2026-01-20 00:00:00','literal');
                """, db); command.ExecuteNonQuery();
        }
        var source = TableAnalysis.Open(cs, "SQLITE", "main", "Sales");
        await test("Advanced analysis dialogs render defaults and handle runtime date/format input events", () => AdvancedAnalysisUiChecks.Run(source, root));
        AnalysisFilterRule Rule(string column, string op, params string[] values) => new(Guid.NewGuid().ToString("N"), column, op, values);
        AnalysisFilterConfig Config(params AnalysisFilterRule[] rules) => new(new[] { new AnalysisFilterGroup(rules) }, Array.Empty<AnalysisParameter>());
        var basic = new AnalysisRequest(new[] { "Customer" }, new[] { new AnalysisCalculation("Amount", "SUM", "Revenue", "revenue") });
        long Count(AnalysisFilterConfig config) => Convert.ToInt64(source.Analyze(basic with { Groups = Array.Empty<string>(), Filters = config }).GetValue(0, 1));
        await test("Quick search combines with advanced OR groups across totals, export and saved templates", async () =>
        {
            var filters = new AnalysisFilterConfig(new[] { new AnalysisFilterGroup(new[] { Rule("Customer", "Equals", "A") }), new AnalysisFilterGroup(new[] { Rule("Customer", "Equals", "B") }) }, Array.Empty<AnalysisParameter>());
            var request = basic with { Filters = filters, Conditions = new[] { new SearchCondition("Note", "x") } };
            var result = source.AnalyzeWithTotals(request);
            Check(result.Groups.RowCount == 1 && (string)result.Groups.GetValue(0, 0) == "B" && Convert.ToDouble(result.Totals.GetValue(0, 0)) == 300 && Convert.ToInt64(result.Totals.GetValue(0, 1)) == 1, "Search was not ANDed with the entire OR expression");
            var serverTable = new DBUtil.TableStruct { Name = "Sales", TableSchemaName = "dbo", Columns = new() { new() { Name = "Customer", Type = "nvarchar(100)" }, new() { Name = "Note", Type = "nvarchar(100)" }, new() { Name = "Amount", Type = "numeric" } } };
            var serverQuery = TableAnalysis.BuildQuery(serverTable, "SQLSERVER", request);
            Check(serverQuery.Parameters.Count == 3 && serverQuery.Parameters.Select(p => p.Name).Distinct().Count() == 3 && serverQuery.Sql.Contains("@filter0") && serverQuery.Sql.Contains("@af0"), "Combined SQL Server parameters collided");
            string output = Path.Combine(root, "combined-search.xlsx"); source.Export(request, output);
            using (var rows = MiniExcelLibs.MiniExcel.QueryAsDataTable(output, useHeaderRow: true))
                Check(rows.Rows.Count == 2 && Convert.ToDouble(rows.Rows[1]["Revenue"]) == 300, "Combined filter was not used by the exported total");
            var store = new AnalysisTemplateStore(Path.Combine(root, "combined-search-templates.db"));
            long id = store.Save("Sales", "Combined", request);
            var session = new DataAnalysisSession(source, "Source", store); var component = new DataAnalysis { Session = session };
            await component.ApplyTemplateAsync(id);
            Check(session.LastRequest!.Filters?.Groups.Count == 2 && session.LastRequest.Conditions!.Single().Text == "x" && session.Result!.RowCount == 1, "Template did not restore both search and advanced filters");
            component.SetConditions(new[] { new SearchCondition("Customer", "A") }); await component.AnalyzeAsync();
            Check(session.LastRequest!.Filters?.Groups.Count == 2 && session.Result!.RowCount == 1 && session.Result.FormatCell(0, 0) == "A", "Editing quick search discarded the advanced filters");
            component.SetConditions(Array.Empty<SearchCondition>()); await component.AnalyzeAsync();
            Check(session.LastRequest!.Filters?.Groups.Count == 2 && session.Result!.RowCount == 2, "Clearing quick search discarded advanced filters");
        });
        await test("Typed analysis filters support inclusive dates, every operator and grouped OR without interpreting SQL text", () =>
        {
            var config = new AnalysisFilterConfig(new[] {
                new AnalysisFilterGroup(new[] { Rule("Customer", "Equals", "A"), Rule("SoldAt", "Range", "2026-01-01", "2026-01-31") }),
                new AnalysisFilterGroup(new[] { Rule("Customer", "Equals", "B"), Rule("Amount", "GreaterOrEqual", "350") }) }, Array.Empty<AnalysisParameter>());
            var result = source.AnalyzeWithTotals(basic with { Filters = config });
            var serverTable = new DBUtil.TableStruct { Name = "Sales", TableSchemaName = "dbo", Columns = new() { new() { Name = "Customer", Type = "nvarchar(100)" }, new() { Name = "SoldAt", Type = "datetime2" }, new() { Name = "Amount", Type = "numeric" } } };
            var serverQuery = TableAnalysis.BuildQuery(serverTable, "SQLSERVER", basic with { Filters = config });
            Check(serverQuery.Parameters.Count == 5 && serverQuery.Parameters.Select(p => p.Name).Distinct().Count() == 5 && serverQuery.Parameters.Count(p => p.Value is DateTime) == 2 && serverQuery.Sql.Contains(" OR "), "SQL Server typed OR parameters collided or dates lost their type");
            Check(Convert.ToDouble(result.Totals.GetValue(0, 0)) == 700 && Convert.ToInt64(result.Totals.GetValue(0, 1)) == 3, "OR precedence or inclusive end-date failed");
            Check(Count(Config(Rule("SoldAt", "Range", "2026-01-01", "2026-01-31"))) == 5, "End day omitted or next day included");
            Check(Count(Config(Rule("Customer", "In", "A", "B"), Rule("Amount", "Range", "150", "350"))) == 2, "Numeric range or multi-value AND failed");
            Check(Count(Config(Rule("Note", "In", "A,B"))) == 1, "Multi-value input split a literal comma");
            Check(Count(Config(Rule("Note", "IsNull"))) == 2 && Count(Config(Rule("Note", "Equals", ""))) == 1 && Count(Config(Rule("Note", "IsNotNull"))) == 4, "NULL and empty text were conflated");
            Check(Count(Config(Rule("Note", "NotEquals", ""))) == 3, "NotEquals unexpectedly included NULL");
            Check(Count(Config(Rule("Customer", "Contains", "%_![]"))) == 1 && Count(Config(Rule("Customer", "Equals", "A' OR 1=1--"))) == 0, "Literal escaping or parameterization failed");
            Check(Count(Config(Rule("Amount", "Greater", "300"))) == 1 && Count(Config(Rule("Amount", "Less", "100"))) == 1 && Count(Config(Rule("Amount", "LessOrEqual", "100"))) == 2, "Comparison operators failed");
            Check(Count(Config(Rule("Flag", "Equals", "true"))) == 3, "Boolean value conversion failed");
            try { Count(Config(Rule("Amount", "Contains", "1"))); throw new Exception("Expected type rejection"); } catch (ArgumentException) { }
            try { Count(Config(Rule("Amount", "Range", "200", "100"))); throw new Exception("Expected reversed range rejection"); } catch (ArgumentException) { }
            return Task.CompletedTask;
        });
        await test("Optional parameters remove empty OR groups, required values validate at runtime and relative defaults resolve per calendar", () =>
        {
            var parameter = new AnalysisParameter("customer", "Customer", Array.Empty<string>());
            var config = new AnalysisFilterConfig(new[] { new AnalysisFilterGroup(new[] { Rule("Customer", "Equals", "A") }),
                new AnalysisFilterGroup(new[] { Rule("Customer", "Equals") with { ParameterId = parameter.Id } }) }, new[] { parameter });
            Check(Count(config) == 2, "Empty optional OR group broadened the filter to all records");
            var required = config with { Parameters = new[] { parameter with { Required = true } } };
            source.ValidateRequest(basic with { Filters = required }); // Required values may be entered when running a template.
            try { Count(required); throw new Exception("Expected required-parameter rejection"); } catch (ArgumentException) { }
            var january = AnalysisFilters.Defaults(parameter with { RelativeDate = "LastMonth" }, new DateTime(2026, 2, 12));
            Check(january.SequenceEqual(new[] { "2026-01-01", "2026-01-31" }), "Relative date defaults incorrect");
            var dates = Config(Rule("SoldAt", "Range") with { ParameterId = "dates" }) with { Parameters = new[] { new AnalysisParameter("dates", "Period", Array.Empty<string>(), false, "ThisMonth") } };
            source.ValidateRequest(basic with { Filters = dates });
            var incompatible = new AnalysisFilterConfig(new[] { new AnalysisFilterGroup(new[] { Rule("Customer", "Equals") with { ParameterId = "p" }, Rule("Amount", "Equals") with { ParameterId = "p" } }) }, new[] { new AnalysisParameter("p", "Shared", new[] { "1" }) });
            try { Count(incompatible); throw new Exception("Expected incompatible shared-parameter rejection"); } catch (ArgumentException) { }
            return Task.CompletedTask;
        });
        await test("Parameterized templates retain defaults, isolate runtime values and freeze analysis/export parameters", async () =>
        {
            string settings = Path.Combine(root, "parameter-templates.db"); var store = new AnalysisTemplateStore(settings);
            var config = Config(Rule("Customer", "Equals") with { ParameterId = "customer" }) with { Parameters = new[] { new AnalysisParameter("customer", "Customer", new[] { "A" }, true) } };
            var values = new Dictionary<string, IReadOnlyList<string>> { ["customer"] = new[] { "B" } };
            var request = basic with { Filters = config, ParameterValues = values };
            long id = store.Save("Sales", "Parameterized", request);
            var loaded = store.Load("Sales", id);
            Check(loaded.ParameterValues == null && loaded.Filters.Parameters[0].Defaults.Single() == "A", "Runtime inputs overwrote saved defaults");
            long oldId = store.Save("Sales", "Old AND", basic with { Conditions = new[] { new SearchCondition("Customer", "A") } });
            using (var oldConnection = new SQLiteConnection(new SQLiteConnectionStringBuilder { DataSource = settings }.ToString()))
            {
                oldConnection.Open(); using var upgrade = new SQLiteCommand("UPDATE AnalysisTemplates SET ConfigJson=replace(ConfigJson,'\"Version\":3','\"Version\":1') WHERE Id=@id", oldConnection);
                upgrade.Parameters.AddWithValue("@id", oldId); upgrade.ExecuteNonQuery();
                using var verify = new SQLiteCommand("SELECT ConfigJson FROM AnalysisTemplates WHERE Id=@id", oldConnection); verify.Parameters.AddWithValue("@id", oldId);
                Check(((string)verify.ExecuteScalar()).Contains("\"Version\":1"), "Legacy version-1 fixture was not prepared");
            }
            var old = store.Load("Sales", oldId);
            var migrated = AnalysisFilters.Migrate(old.Conditions, source.FilterFields);
            Check(migrated.Groups.Single().Rules.Single().Operation == "Contains" && source.Analyze(old).RowCount == source.Analyze(old with { Filters = migrated }).RowCount, "Version-1 AND template migration changed results");
            long oldV2 = store.Save("Sales", "Old v2", request);
            using (var oldConnection = new SQLiteConnection(new SQLiteConnectionStringBuilder { DataSource = settings }.ToString()))
            {
                oldConnection.Open(); using var upgrade = new SQLiteCommand("UPDATE AnalysisTemplates SET ConfigJson=replace(ConfigJson,'\"Version\":3','\"Version\":2') WHERE Id=@id", oldConnection);
                upgrade.Parameters.AddWithValue("@id", oldV2); upgrade.ExecuteNonQuery();
                using var verify = new SQLiteCommand("SELECT ConfigJson FROM AnalysisTemplates WHERE Id=@id", oldConnection); verify.Parameters.AddWithValue("@id", oldV2);
                Check(((string)verify.ExecuteScalar()).Contains("\"Version\":2"), "Legacy version-2 fixture was not prepared");
            }
            Check(store.Load("Sales", oldV2).Filters.Parameters[0].Defaults.Single() == "A", "Version-2 template could not be loaded");
            var session = new DataAnalysisSession(source, "Test", store);
            await session.AnalyzeAsync(request);
            Check(session.Result!.FormatCell(0, 0) == "B" && session.Matches(request), "Runtime parameter did not drive analysis or match its result");
            values["customer"] = new[] { "A" };
            Check(!session.Matches(request) && session.LastRequest!.ParameterValues!["customer"].Single() == "B", "Changing inputs mutated executed parameter snapshot");
            string output = Path.Combine(root, "parameter-snapshot.xlsx");
            source.Export(session.LastRequest!, output);
            using (var exported = MiniExcelLibs.MiniExcel.QueryAsDataTable(output, useHeaderRow: true)) Check((string)exported.Rows[0]["Customer"] == "B" && exported.Rows.Count == 2, "Export did not retain executed filter parameters");
            var component = new DataAnalysis { Session = session };
            await session.RefreshTemplatesAsync();
            await component.ApplyTemplateAsync(id);
            Check(session.Result!.FormatCell(0, 0) == "A", "Applying template failed to initialize default parameters");
            component.SetParameterValues(new Dictionary<string, IReadOnlyList<string>> { ["customer"] = new[] { "B" } });
            await component.SaveCurrentTemplateAsync();
            Check(store.Load("Sales", id).Filters.Parameters.Single().Defaults.Single() == "A", "Saving runtime input implicitly replaced defaults");
            component.SaveParameterDefaults = true; await component.SaveCurrentTemplateAsync();
            Check(store.Load("Sales", id).Filters.Parameters.Single().Defaults.Single() == "B" && !component.SaveParameterDefaults, "Explicit save-as-default did not update/reset the setting");
            await component.NewTemplateAsync(); await component.SaveCurrentTemplateAsync(); component.NewTemplateName = "Parameterized copy"; await component.SaveNewTemplateAsync();
            long copiedId = store.List("Sales").Single(t => t.Name == "Parameterized copy").Id;
            Check(store.Load("Sales", copiedId).Filters.Parameters.Single().Defaults.Single() == "B" && store.Load("Sales", copiedId).ParameterValues == null, "Copying a parameterized template lost defaults or stored runtime state");
            using var connection = new SQLiteConnection(new SQLiteConnectionStringBuilder { DataSource = settings }.ToString()); connection.Open();
            using var command = new SQLiteCommand("SELECT ConfigJson FROM AnalysisTemplates WHERE Id=@id", connection); command.Parameters.AddWithValue("@id", id);
            using var json = JsonDocument.Parse((string)command.ExecuteScalar()); Check(json.RootElement.GetProperty("Version").GetInt32() == 3, "New template version not written");
        });
        await test("Independent numeric formats persist by indicator identity and agree between preview, totals and Excel", () =>
        {
            var format = new AnalysisNumberFormat("Percent", 3, false);
            var request = basic with { Calculations = new[] { new AnalysisCalculation("Amount", "SUM", "Revenue", "revenue", new("Number", 0, true)) },
                Formulas = new[] { new AnalysisFormula("Ratio", "revenue", "/", Constant: 1000, Id: "ratio-id", Format: format) }, CountFormat = new("Number", 0, false), SortBy = "formula:ratio-id", SortDescending = true };
            var result = source.AnalyzeWithTotals(request);
            var snapshot = new QueryResultSnapshot(result.Groups, "Test", formats: TableAnalysis.ResultFormats(request));
            int row = Enumerable.Range(0, result.Groups.RowCount).Single(i => (string)result.Groups.GetValue(i, 0) == "A");
            Check(snapshot.FormatGroupedCell(row, 3) == 0.3m.ToString("0.000%", CultureInfo.CurrentCulture), "Preview did not use independent percentage format");
            string output = Path.Combine(root, "independent-formats.xlsx"); source.Export(request, output);
            using (var zip = ZipFile.OpenRead(output))
            {
                XNamespace ns = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
                using var styleInput = zip.GetEntry("xl/styles.xml")!.Open(); var styles = XDocument.Load(styleInput);
                Check(styles.Descendants(ns + "numFmt").Any(f => f.Attribute("formatCode")?.Value == "0.000%"), "Excel custom format missing");
                using var sheetInput = zip.GetEntry("xl/worksheets/sheet1.xml")!.Open(); var sheet = XDocument.Load(sheetInput);
                var cells = sheet.Descendants(ns + "c").Where(c => c.Attribute("r")!.Value.StartsWith("D") && c.Element(ns + "v") != null).ToArray();
                Check(cells.Length > 0 && cells.All(c => int.Parse(c.Attribute("s")!.Value) >= 9), "Custom styles missing on numbers or Total");
            }
            var renamed = request with { Formulas = new[] { request.Formulas![0] with { Name = "Renamed ratio" } } };
            source.ValidateRequest(renamed); // Fixed formula ID keeps default sorting valid after renaming.
            var store = new AnalysisTemplateStore(Path.Combine(root, "format-templates.db")); long id = store.Save("Sales", "Formats", request);
            Check(store.Load("Sales", id).Formulas![0].Format == format, "Template lost indicator format");
            return Task.CompletedTask;
        });
    }
}
