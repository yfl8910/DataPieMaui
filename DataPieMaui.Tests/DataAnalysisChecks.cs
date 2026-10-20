using System.Data.SQLite;
using DataPieCore;
using DBUtil;
using DataPieMaui.Components;
using DataPieMaui.Components.Shared;
using DataPieMaui.Services;
using DataPieMaui.Services.Shared;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

internal static class DataAnalysisChecks
{
    public static async Task Run(string root, Func<string, Func<Task>, Task> test)
    {
        string path = Path.Combine(root, "analysis.db"); SQLiteConnection.CreateFile(path);
        string cs = new SQLiteConnectionStringBuilder { DataSource = path, FailIfMissing = true }.ToString();
        void Sql(string sql)
        {
            using var connection = new SQLiteConnection(cs); connection.Open();
            using var command = new SQLiteCommand(sql, connection); command.ExecuteNonQuery();
        }
        void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
        Sql("CREATE TABLE Sales(Region TEXT, PIC TEXT, Amount NUMERIC, Qty INTEGER, Note TEXT, Flag BOOLEAN); "
            + "INSERT INTO Sales VALUES('North','A',10.25,2,'literal %_''[',1),('North','A',20.75,4,'regular',0),('South','B',NULL,NULL,'regular',0),('North','B',5,1,'regular',1);"
            + "CREATE TABLE ManyGroups(Label TEXT, Value INTEGER); WITH RECURSIVE n(x) AS (SELECT 1 UNION ALL SELECT x+1 FROM n WHERE x<1505) INSERT INTO ManyGroups SELECT 'group '||x,x FROM n;");
        var source = TableAnalysis.Open(cs, "SQLITE", "main", "Sales");
        await test("Calculation sort dropdown events update, rerender, apply and restore selections", () => CalculationSortInteractionChecks.Run(source));
        await test("Default calculation sorting precedes preview limits and exports include one overall total outside the filter", () =>
        {
            var many = TableAnalysis.Open(cs, "SQLITE", "main", "ManyGroups");
            var request = new AnalysisRequest(new[] { "Label" }, new[] { new AnalysisCalculation("Value", "SUM") }, SortBy: "aggregate:SUM|Value", SortDescending: true);
            var preview = many.Analyze(request);
            Check(preview.RowCount == 1000 && Convert.ToInt64(preview.GetValue(0, 1)) == 1505 && Convert.ToInt64(preview.GetValue(999, 1)) == 506, "Default sort was applied after the preview limit");
            Check(Convert.ToInt64(many.Analyze(request with { SortDescending = false }).GetValue(0, 1)) == 1, "Ascending sort failed");
            try { many.Analyze(request with { SortBy = "1; DROP TABLE ManyGroups" }); throw new Exception("Expected invalid sort rejection"); } catch (ArgumentException) { }
            string path = Path.Combine(root, "sorted-with-total.xlsx"); many.Export(request, path);
            using (var exported = MiniExcelLibs.MiniExcel.QueryAsDataTable(path, useHeaderRow: true))
            {
                Check(exported.Rows.Count == 1506 && Convert.ToInt64(exported.Rows[0]["SUM Value"]) == 1505 && Convert.ToInt64(exported.Rows[1504]["SUM Value"]) == 1, "Export order differed from analysis");
                Check((string)exported.Rows[1505]["Label"] == "Total" && Convert.ToInt64(exported.Rows[1505]["SUM Value"]) == 1505L * 1506 / 2, "Overall total missing or based on preview");
            }
            using (var zip = System.IO.Compression.ZipFile.OpenRead(path))
            using (var input = zip.GetEntry("xl/worksheets/sheet1.xml")!.Open())
            {
                var xml = System.Xml.Linq.XDocument.Load(input); System.Xml.Linq.XNamespace ns = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
                Check(xml.Descendants(ns + "autoFilter").Single().Attribute("ref")?.Value == "A1:C1506" && xml.Descendants(ns + "c").Single(c => c.Attribute("r")?.Value == "B1507").Attribute("s")?.Value == "6", "Total row was filtered or unformatted");
            }
            var formula = new AnalysisRequest(new[] { "Region" }, new[] { new AnalysisCalculation("Amount", "AVG", Id: "amount"), new("Qty", "SUM", Id: "qty") },
                Formulas: new[] { new AnalysisFormula("Ratio %", "amount", "/", "qty") }, SortBy: "formula:Ratio %", SortDescending: true);
            var sorted = source.Analyze(formula);
            Check((string)sorted.GetValue(sorted.RowCount - 1, 0) == "South", "NULL sort results were not last");
            var sql = TableAnalysis.BuildQuery(new TableStruct { Name = "Sales", TableSchemaName = "dbo", Columns = new[] { new Column { Name = "Region", Type = "nvarchar(20)" }, new Column { Name = "Amount", Type = "numeric" }, new Column { Name = "Qty", Type = "int" } }.ToList() }, "SQLSERVER", formula);
            Check(sql.Sql.Contains("CASE WHEN") && sql.Sql.Contains(" DESC, "), "SQL Server default sort SQL missing");
            string noGroups = Path.Combine(root, "total-only.xlsx"); many.Export(request with { Groups = Array.Empty<string>() }, noGroups);
            using var totalOnly = MiniExcelLibs.MiniExcel.QueryAsDataTable(noGroups, useHeaderRow: true);
            Check(totalOnly.Rows.Count == 1, "Ungrouped export duplicated overall totals");
            return Task.CompletedTask;
        });
        await test("Analysis Excel export isolates numeric styles, headers, filters and frozen panes from shared exports", () =>
        {
            var request = new AnalysisRequest(new[] { "Region" }, new[] { new AnalysisCalculation("Amount", "SUM", "Revenue"), new("Qty", "SUM", "Units") },
                Formulas: new[] { new AnalysisFormula("Rate %", "SUM|Amount", "/", Constant: 100) });
            string output = Path.Combine(root, "styled-analysis.xlsx"); source.Export(request, output);
            using (var archive = System.IO.Compression.ZipFile.OpenRead(output))
            {
                System.Xml.Linq.XDocument Read(string name) { using var input = archive.GetEntry(name)!.Open(); return System.Xml.Linq.XDocument.Load(input); }
                System.Xml.Linq.XNamespace ns = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
                var sheet = Read("xl/worksheets/sheet1.xml"); var styles = Read("xl/styles.xml");
                Check(sheet.Descendants(ns + "pane").Single().Attribute("state")?.Value == "frozen" && sheet.Descendants(ns + "autoFilter").Single().Attribute("ref")?.Value == "A1:E3", "Freeze/filter ranges missing");
                Check(sheet.Descendants(ns + "col").Count() == 5 && sheet.Descendants(ns + "c").Where(c => c.Attribute("r")!.Value.EndsWith("1")).All(c => c.Attribute("s")?.Value == "1"), "Header styling or column widths missing");
                var cells = sheet.Descendants(ns + "c").ToDictionary(c => c.Attribute("r")!.Value);
                Check(cells["B2"].Attribute("s")?.Value == "2" && cells["E2"].Attribute("s")?.Value == "3" && cells["E2"].Element(ns + "v")?.Value == "0.36", "Numeric/percentage styles changed the raw value");
                Check(styles.Descendants(ns + "numFmt").Any(f => f.Attribute("formatCode")?.Value == "0.0%"), "Percentage format missing");
            }
            using (var data = MiniExcelLibs.MiniExcel.QueryAsDataTable(output, useHeaderRow: true))
                Check(data.Rows.Count == 3 && Convert.ToDouble(data.Rows[0]["Revenue"]) == 36 && Convert.ToDouble(data.Rows[0]["Rate %"]) == 0.36, "Styled workbook could not be read as numeric data");
            using var table = new System.Data.DataTable(); table.Columns.Add("Code"); table.Columns.Add("Value", typeof(decimal)); table.Rows.Add("=1+1", 0.256m);
            string shared = Path.Combine(root, "unchanged-shared.xlsx");
            using (var reader = table.CreateDataReader()) ExcelIO.SaveMiniExcel(shared, reader, "Data");
            using (var zip = System.IO.Compression.ZipFile.OpenRead(shared))
            using (var input = zip.GetEntry("xl/styles.xml")!.Open())
            using (var text = new StreamReader(input)) Check(!text.ReadToEnd().Contains("FF334E68"), "Analysis formatting changed shared exports");
            string direct = Path.Combine(root, "styled-text.xlsx");
            using (var reader = table.CreateDataReader()) AnalysisExcelExport.Save(direct, reader, 1);
            using (var zip = System.IO.Compression.ZipFile.OpenRead(direct))
            using (var input = zip.GetEntry("xl/worksheets/sheet1.xml")!.Open())
            {
                var xml = System.Xml.Linq.XDocument.Load(input); System.Xml.Linq.XNamespace ns = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
                Check(!xml.Descendants(ns + "f").Any() && xml.Descendants(ns + "t").Any(t => t.Value == "=1+1"), "Text interpreted as formula");
            }
            byte[] previous = File.ReadAllBytes(output);
            Sql("CREATE TABLE LongAnalysis(Label TEXT, Value NUMERIC); INSERT INTO LongAnalysis VALUES(hex(zeroblob(17000)),1)");
            var oversized = TableAnalysis.Open(cs, "SQLITE", "main", "LongAnalysis");
            try
            {
                oversized.Export(new AnalysisRequest(new[] { "Label" }, new[] { new AnalysisCalculation("Value", "SUM") }), output);
                throw new Exception("Expected Excel cell limit rejection");
            }
            catch (InvalidOperationException ex) { Check(ex.Message.Contains("32,767"), "Unexpected export failure"); }
            Check(File.ReadAllBytes(output).SequenceEqual(previous) && Directory.GetFiles(root, "styled-analysis.xlsx.*.xlsx").Length == 0, "Failed formatted export damaged the previous file or left temporary files");
            return Task.CompletedTask;
        });
        await test("Analysis percentage headers format ratios without changing raw values or ordinary columns", () =>
        {
            var culture = System.Globalization.CultureInfo.CurrentCulture;
            try
            {
                System.Globalization.CultureInfo.CurrentCulture = System.Globalization.CultureInfo.InvariantCulture;
                using var table = new System.Data.DataTable();
                table.Columns.Add("Margin %", typeof(decimal)); table.Columns.Add("Revenue", typeof(decimal)); table.Columns.Add("Text %", typeof(string));
                table.Rows.Add(0.256m, 1234.5m, "code"); table.Rows.Add(-0.125m, 0m, "value"); table.Rows.Add(DBNull.Value, DBNull.Value, DBNull.Value);
                using var reader = table.CreateDataReader();
                var snapshot = new QueryResultSnapshot(QueryPreviewResult.Read(reader, 1000), "Test");
                Check(snapshot.FormatGroupedCell(0, 0) == "25.6%" && snapshot.FormatGroupedCell(1, 0) == "-12.5%", "Ratio percentage formatting failed");
                Check(snapshot.FormatGroupedCell(0, 1) == "1,234.50" && snapshot.FormatGroupedCell(0, 2) == "code" && snapshot.IsNull(2, 0), "Percentage formatting changed normal columns or NULLs");
                Check(snapshot.CopyText(new[] { 0 }).Contains("0.256"), "Percentage display changed raw copied values");
            }
            finally { System.Globalization.CultureInfo.CurrentCulture = culture; }
            return Task.CompletedTask;
        });
        await test("Analysis groups multiple text fields and computes numeric aggregates and NULL-aware counts", () =>
        {
            Check(source.TextColumns.SequenceEqual(new[] { "Region", "PIC", "Note" }) && source.NumericColumns.Contains("Amount") && !source.NumericColumns.Contains("Flag"), "Wrong selectable column types");
            var request = new AnalysisRequest(new[] { "Region", "PIC" }, new[] { new AnalysisCalculation("Amount", "SUM"), new("Amount", "AVG"), new("Amount", "MIN"), new("Amount", "MAX") });
            var result = source.Analyze(request);
            Check(result.RowCount == 3 && Convert.ToDouble(result.GetValue(0, 2)) == 31 && Convert.ToDouble(result.GetValue(0, 3)) == 15.5, "Grouping, sum or fractional average wrong");
            Check(Convert.ToDouble(result.GetValue(0, 4)) == 10.25 && Convert.ToDouble(result.GetValue(0, 5)) == 20.75 && Convert.ToInt64(result.GetValue(0, 6)) == 2, "Min/max or record count wrong");
            Check(result.IsNull(2, 2) && Convert.ToInt64(result.GetValue(2, 6)) == 1, "NULL values were not handled correctly");
            var filtered = source.Analyze(request with { SearchColumn = "Region", SearchText = "North" });
            Check(filtered.RowCount == 2, "Search was not applied before grouping");
            var literal = source.Analyze(new(Array.Empty<string>(), new[] { new AnalysisCalculation("Amount", "SUM") }, true, "", "%_'["));
            Check(Convert.ToDouble(literal.GetValue(0, 0)) == 10.25 && Convert.ToInt64(literal.GetValue(0, 1)) == 1, "Literal wildcard/all-field search failed");
            var injection = source.Analyze(new(Array.Empty<string>(), Array.Empty<AnalysisCalculation>(), true, "Note", "' OR 1=1 --"));
            Check(Convert.ToInt64(injection.GetValue(0, 0)) == 0, "Search input was interpreted as SQL");
            return Task.CompletedTask;
        });
        await test("Analysis multi-field search matches any selected text field and keeps totals and export aligned", async () =>
        {
            Sql("CREATE TABLE SearchTargets(City TEXT, Owner TEXT, Hidden TEXT, Amount NUMERIC); INSERT INTO SearchTargets VALUES('Alpha','Other','Alpha',10),('Other','Alpha','Alpha',20),('Alpha','Alpha','Alpha',30),('Other','Other','Alpha',40)");
            var searchSource = TableAnalysis.Open(cs, "SQLITE", "main", "SearchTargets");
            var request = new AnalysisRequest(new[] { "City" }, new[] { new AnalysisCalculation("Amount", "SUM") }, true, SearchText: "Alpha", SearchColumns: new[] { "City", "Owner" });
            var preview = searchSource.AnalyzeWithTotals(request);
            Check(preview.Groups.RowCount == 2 && Convert.ToDouble(preview.Totals.GetValue(0, 0)) == 60 && Convert.ToInt64(preview.Totals.GetValue(0, 1)) == 3, "Multi-field OR search missed rows or counted a row twice");
            Check(Convert.ToDouble(searchSource.AnalyzeWithTotals(request with { SearchColumns = new[] { "City" } }).Totals.GetValue(0, 0)) == 40, "Selected-field restriction ignored");
            Check(Convert.ToDouble(searchSource.AnalyzeWithTotals(request with { SearchColumns = Array.Empty<string>() }).Totals.GetValue(0, 0)) == 100, "Empty selection did not search all text fields");
            try { searchSource.Analyze(request with { SearchColumns = new[] { "Amount" } }); throw new Exception("Expected nontext search field rejection"); } catch (ArgumentException) { }
            var session = new DataAnalysisSession(searchSource, "Test"); await session.AnalyzeAsync(request);
            Check(session.Matches(request with { SearchColumns = new[] { "Owner", "City" } }) && !session.Matches(request with { SearchColumns = new[] { "City" } }), "Field selection changes were not tracked correctly");
            string output = Path.Combine(root, "multi-search.xlsx"); searchSource.Export(request, output);
            using var exported = MiniExcelLibs.MiniExcel.QueryAsDataTable(output, useHeaderRow: true);
            Check(exported.Rows.Count == 3 && exported.Rows.Cast<System.Data.DataRow>().Take(2).Sum(r => Convert.ToDouble(r["SUM Amount"])) == 60, "Export did not use multi-field search");
        });
        await test("Analysis aggregates every source row and exports groups beyond the preview limit", () =>
        {
            var many = TableAnalysis.Open(cs, "SQLITE", "main", "ManyGroups");
            var request = new AnalysisRequest(Array.Empty<string>(), new[] { new AnalysisCalculation("Value", "SUM") });
            var total = many.Analyze(request);
            Check(total.RowCount == 1 && Convert.ToInt64(total.GetValue(0, 0)) == 1505L * 1506 / 2 && Convert.ToInt64(total.GetValue(0, 1)) == 1505, "Aggregation used a truncated source preview");
            request = request with { Groups = new[] { "Label" } };
            var preview = many.Analyze(request);
            Check(preview.RowCount == 1000 && preview.Truncated, "Group preview limit failed");
            var withTotals = many.AnalyzeWithTotals(request);
            Check(withTotals.Groups.RowCount == 1000 && withTotals.Groups.Truncated && Convert.ToInt64(withTotals.Totals.GetValue(0, 0)) == 1505L * 1506 / 2
                && Convert.ToInt64(withTotals.Totals.GetValue(0, 1)) == 1505, "Totals used only preview groups or current page");
            string output = Path.Combine(root, "analysis-all.xlsx"); many.Export(request, output);
            using var exported = MiniExcelLibs.MiniExcel.QueryAsDataTable(output, useHeaderRow: true);
            Check(exported.Rows.Count == 1506 && Directory.GetFiles(root, "analysis-all.xlsx.*.xlsx").Length == 0, "Export truncated groups or left temporary files");
            return Task.CompletedTask;
        });
        await test("Analysis validates field roles, operations and dynamically typed SQLite numbers", () =>
        {
            var invalid = new AnalysisRequest(new[] { "Missing" }, Array.Empty<AnalysisCalculation>());
            try { source.Analyze(invalid); throw new Exception("Expected invalid grouping rejection"); } catch (ArgumentException) { }
            try { source.Analyze(new(Array.Empty<string>(), new[] { new AnalysisCalculation("Amount", "SUM); DROP TABLE Sales") })); throw new Exception("Expected operation rejection"); } catch (ArgumentException) { }
            try { source.Analyze(new(Array.Empty<string>(), Array.Empty<AnalysisCalculation>(), false)); throw new Exception("Expected empty calculation rejection"); } catch (ArgumentException) { }
            Sql("INSERT INTO Sales VALUES('Invalid','X','not numeric',1,'invalid',0)");
            var request = new AnalysisRequest(Array.Empty<string>(), new[] { new AnalysisCalculation("Amount", "SUM") });
            try { source.Analyze(request); throw new Exception("Expected nonnumeric data rejection"); } catch (InvalidOperationException ex) { Check(ex.Message.Contains("nonnumeric"), "Wrong data validation failure"); }
            var filtered = source.Analyze(request with { SearchColumn = "Region", SearchText = "North" });
            Check(Convert.ToDouble(filtered.GetValue(0, 0)) == 36, "Numeric validation ignored source filtering");
            Sql("DELETE FROM Sales WHERE Region='Invalid'");
            return Task.CompletedTask;
        });
        await test("SQL Server analysis quotes identifiers, parameterizes filters and widens numeric aggregates", () =>
        {
            var table = new TableStruct { TableSchemaName = "sales]", Name = "Odd.Table]", Columns = new()
            {
                new() { Name = "Group]", Type = "ntext" }, new() { Name = "Qty", Type = "int" }, new() { Name = "Price", Type = "decimal", Precision = 18, Scale = 4 }
            } };
            var query = TableAnalysis.BuildQuery(table, "SQLSERVER", new(new[] { "Group]" }, new[] { new AnalysisCalculation("Qty", "SUM"), new("Price", "AVG") }, true, "Group]", "%'_["));
            Check(query.Sql.Contains("FROM [sales]]].[Odd.Table]]]") && query.Sql.Contains("CONVERT(nvarchar(max),[Group]]])"), "Qualified identifiers or legacy text grouping lost");
            Check(query.Sql.Contains("SUM(CONVERT(decimal(38,0),[Qty]))") && query.Sql.Contains("AVG(CONVERT(decimal(38,4),[Price]))") && query.Sql.Contains("COUNT_BIG(*)"), "SQL Server aggregation precision lost");
            Check(query.Sql.Contains("LIKE @search ESCAPE '!'") && !query.Sql.Contains("%'_[") && query.SearchValue == "%!%'!_![%", "Search was not parameterized and escaped");
            return Task.CompletedTask;
        });
        Sql("CREATE TABLE Pricing(Region TEXT, Revenue NUMERIC, Units INTEGER, SoldAt DATE, Flag BOOLEAN, Payload BLOB); "
            + "INSERT INTO Pricing VALUES('North',10,1,'2026-01-01',1,NULL),('North',90,3,'2026-01-01',1,NULL),('Zero',5,0,'2026-01-01',0,NULL),('Null',NULL,NULL,'2026-01-01',0,NULL),('Fraction',10,3,'2026-01-01',1,NULL)");
        var pricing = TableAnalysis.Open(cs, "SQLITE", "main", "Pricing");
        var priceRequest = new AnalysisRequest(new[] { "Region" }, new[] { new AnalysisCalculation("Revenue", "SUM", "Total revenue", "revenue"), new("Units", "SUM", "Total units", "units") }, false,
            Formulas: new[] { new AnalysisFormula("Average price", "revenue", "/", "units"), new("Half revenue", "revenue", "/", Constant: 2m), new("Difference", "revenue", "-", "units"), new("Combined", "revenue", "+", "units"), new("Product", "revenue", "*", "units") });
        var totalsRequest = new AnalysisRequest(new[] { "Region" }, new[]
        {
            new AnalysisCalculation("Revenue", "SUM", "Total revenue", "revenue"), new("Revenue", "AVG"), new("Revenue", "MIN"), new("Revenue", "MAX"), new("Units", "SUM", "Total units", "units")
        }, true, Formulas: new[] { new AnalysisFormula("Average price", "revenue", "/", "units") });
        await test("Analysis overall totals recompute averages and formulas over the entire filtered source", () =>
        {
            var preview = pricing.AnalyzeWithTotals(totalsRequest);
            var totals = preview.Totals;
            Check(totals.RowCount == 1 && Convert.ToDouble(totals.GetValue(0, 0)) == 115 && Convert.ToDouble(totals.GetValue(0, 1)) == 28.75, "Sum or weighted overall average wrong");
            Check(Convert.ToDouble(totals.GetValue(0, 2)) == 5 && Convert.ToDouble(totals.GetValue(0, 3)) == 90 && Convert.ToInt64(totals.GetValue(0, 4)) == 7 && Convert.ToInt64(totals.GetValue(0, 5)) == 5, "Overall min/max, quantity or count wrong");
            Check(Math.Abs(Convert.ToDouble(totals.GetValue(0, 6)) - 115d / 7) < 1e-10, "Total formula added group prices instead of dividing overall totals");
            var north = pricing.AnalyzeWithTotals(totalsRequest with { SearchColumn = "Region", SearchText = "North" });
            Check(Convert.ToDouble(north.Totals.GetValue(0, 0)) == 100 && Convert.ToDouble(north.Totals.GetValue(0, 6)) == 25, "Totals ignored the source search condition");
            var empty = pricing.AnalyzeWithTotals(totalsRequest with { SearchColumn = "Region", SearchText = "No such region" });
            Check(empty.Groups.RowCount == 0 && empty.Totals.IsNull(0, 0) && Convert.ToInt64(empty.Totals.GetValue(0, 5)) == 0 && empty.Totals.IsNull(0, 6), "Empty result totals did not preserve NULL/zero count semantics");
            Check(pricing.AnalyzeWithTotals(totalsRequest with { Groups = Array.Empty<string>() }).Totals == null, "Ungrouped analysis repeated its total row");
            return Task.CompletedTask;
        });
        await test("Analysis total row aligns with result columns and disappears for unapplied settings or reset", async () =>
        {
            var session = new DataAnalysisSession(pricing, "Test"); await session.AnalyzeAsync(totalsRequest);
            Check(session.Totals != null, "Session did not expose overall totals");
            await using var provider = new ServiceCollection().AddLogging().AddSingleton<Microsoft.JSInterop.IJSRuntime>(new NoopJs()).BuildServiceProvider();
            await using var renderer = new HtmlRenderer(provider, provider.GetRequiredService<ILoggerFactory>());
            await renderer.Dispatcher.InvokeAsync(async () =>
            {
                AnalysisHarness? instance = null;
                var component = await renderer.RenderComponentAsync<AnalysisHarness>(ParameterView.FromDictionary(new Dictionary<string, object?>
                {
                    ["Session"] = session, ["Desktop"] = new Desktop(), ["Settings"] = totalsRequest, ["Capture"] = (Action<AnalysisHarness>)(value => instance = value)
                }));
                string html = component.ToHtmlString();
                Check(html.Contains("<tfoot>") && html.Contains("analysis-total-row") && html.Contains(session.Totals!.FormatGroupedCell(0, 0)), "Aligned numeric total row missing");
                instance!.ApplyGroups(Array.Empty<string>()); instance.Render();
                Check(!component.ToHtmlString().Contains("<tfoot>"), "Old totals remained after settings changed");
            });
            session.Reset(); Check(session.Result == null && session.Totals == null && session.LastRequest == null, "Reset retained stale totals");
        });
        await test("Analysis formulas calculate ratios of group totals, fractional division and NULL on zero", () =>
        {
            var result = pricing.Analyze(priceRequest);
            int north = Enumerable.Range(0, result.RowCount).Single(i => (string)result.GetValue(i, 0) == "North");
            int zero = Enumerable.Range(0, result.RowCount).Single(i => (string)result.GetValue(i, 0) == "Zero");
            int nil = Enumerable.Range(0, result.RowCount).Single(i => (string)result.GetValue(i, 0) == "Null");
            int fraction = Enumerable.Range(0, result.RowCount).Single(i => (string)result.GetValue(i, 0) == "Fraction");
            Check(Convert.ToDouble(result.GetValue(north, 3)) == 25 && Convert.ToDouble(result.GetValue(north, 4)) == 50, "Used average row price instead of ratio of totals or lost constant");
            Check(result.IsNull(zero, 3) && result.IsNull(nil, 3), "Division by zero/NULL did not return NULL");
            Check(Math.Abs(Convert.ToDouble(result.GetValue(fraction, 3)) - 10d / 3) < 1e-10, "Integer division truncated fractional average price");
            Check(Convert.ToDouble(result.GetValue(north, 5)) == 96 && Convert.ToDouble(result.GetValue(north, 6)) == 104 && Convert.ToDouble(result.GetValue(north, 7)) == 400, "Formula arithmetic wrong");
            var filtered = pricing.Analyze(priceRequest with { SearchColumn = "Region", SearchText = "North" });
            Check(filtered.RowCount == 1 && Convert.ToDouble(filtered.GetValue(0, 3)) == 25, "Formula filtering did not happen before grouping");
            var renamed = pricing.Analyze(priceRequest with { Calculations = priceRequest.Calculations.Select(c => c with { Name = "Renamed " + c.Name }).ToArray() });
            Check(Convert.ToDouble(renamed.GetValue(north, 3)) == 25, "Renaming an aggregate broke formula references");
            return Task.CompletedTask;
        });
        await test("Analysis supports numeric date and boolean grouping and rejects invalid formulas", () =>
        {
            Check(pricing.Fields.Single(f => f.Name == "Payload").CanGroup == false && pricing.Fields.Where(f => f.Name != "Payload").All(f => f.CanGroup), "Grouping type support incorrect");
            Check(pricing.Analyze(new(new[] { "Units" }, Array.Empty<AnalysisCalculation>())).RowCount == 4, "Numeric grouping failed");
            Check(pricing.Analyze(new(new[] { "SoldAt", "Flag" }, Array.Empty<AnalysisCalculation>())).RowCount == 2, "Date/boolean grouping failed");
            foreach (var request in new[]
            {
                priceRequest with { Groups = new[] { "Payload" } },
                priceRequest with { Formulas = new[] { new AnalysisFormula("Broken", "revenue", "/", "missing") } },
                priceRequest with { Formulas = new[] { new AnalysisFormula("Unsafe", "revenue", "; DROP TABLE Pricing", "units") } },
                priceRequest with { Formulas = new[] { new AnalysisFormula("Total revenue", "revenue", "/", "units") } },
                priceRequest with { Formulas = new[] { new AnalysisFormula("Chained", "Average price", "/", "units") } }
            })
            {
                try { pricing.Analyze(request); throw new Exception("Expected invalid formula/group rejection"); } catch (ArgumentException) { }
            }
            var table = new TableStruct { TableSchemaName = "dbo", Name = "Sales", Columns = new() { new() { Name = "Qty", Type = "int" } } };
            var sql = TableAnalysis.BuildQuery(table, "SQLSERVER", new(Array.Empty<string>(), new[] { new AnalysisCalculation("Qty", "MIN", "Min units", "min") }, false,
                Formulas: new[] { new AnalysisFormula("Fraction", "min", "/", Constant: 3) })).Sql;
            Check(sql.Contains("CONVERT(decimal(38,0),s.[__a0]) / NULLIF(3,0)") && sql.Contains("FROM (SELECT"), "SQL Server formula did not widen integers or aggregate before arithmetic");
            string output = Path.Combine(root, "analysis-formulas.xlsx"); pricing.Export(priceRequest, output);
            using var exported = MiniExcelLibs.MiniExcel.QueryAsDataTable(output, useHeaderRow: true);
            var north = exported.Rows.Cast<System.Data.DataRow>().Single(r => Convert.ToString(r["Region"]) == "North");
            Check(Convert.ToDouble(north["Average price"]) == 25, "Export did not use the same formulas as preview");
            var overall = pricing.Analyze(priceRequest with { Groups = Array.Empty<string>() });
            var exportedTotal = exported.Rows[exported.Rows.Count - 1];
            Check((string)exportedTotal[0] == "Total", "Formula export omitted Total row");
            for (int i = 0; i < overall.ColumnCount; i++)
                Check(Convert.ToDouble(exportedTotal[priceRequest.Groups.Count + i]) == Convert.ToDouble(overall.GetValue(0, i)), "Export total used grouped sums instead of overall aggregates/formulas");
            return Task.CompletedTask;
        });
        await test("Analysis settings dialogs isolate drafts and apply grouping and calculation configuration", async () =>
        {
            await using var provider = new ServiceCollection().AddLogging().BuildServiceProvider();
            await using var renderer = new HtmlRenderer(provider, provider.GetRequiredService<ILoggerFactory>());
            await renderer.Dispatcher.InvokeAsync(async () =>
            {
                var original = new[] { "Region" };
                IReadOnlyList<string>? applied = null;
                bool canceled = false;
                GroupHarness? group = null;
                var rendered = await renderer.RenderComponentAsync<GroupHarness>(ParameterView.FromDictionary(new Dictionary<string, object?>
                {
                    ["Source"] = pricing, ["Selected"] = original, ["Capture"] = (Action<GroupHarness>)(value => group = value),
                    ["OnApply"] = EventCallback.Factory.Create<IReadOnlyList<string>>(new object(), value => applied = value),
                    ["OnCancel"] = EventCallback.Factory.Create(new object(), () => canceled = true)
                }));
                Check(rendered.ToHtmlString().Contains("Payload") && rendered.ToHtmlString().Contains("disabled"), "Unsupported grouping field not shown as disabled");
                group!.Toggle("Units", true); group.Toggle("Payload", true);
                await group.OnCancel.InvokeAsync();
                Check(canceled && applied == null && original.SequenceEqual(new[] { "Region" }), "Cancel changed live grouping settings");
                await group.ApplyAsync();
                Check(applied!.SequenceEqual(new[] { "Region", "Units" }), "Grouping apply lost selected order or allowed binary field");
                AnalysisCalculationSettings? calculationSettings = null;
                CalculationHarness? calculation = null;
                var calculationView = await renderer.RenderComponentAsync<CalculationHarness>(ParameterView.FromDictionary(new Dictionary<string, object?>
                {
                    ["Source"] = pricing, ["Settings"] = priceRequest, ["Capture"] = (Action<CalculationHarness>)(value => calculation = value),
                    ["OnApply"] = EventCallback.Factory.Create<AnalysisCalculationSettings>(new object(), value => calculationSettings = value)
                }));
                Check(calculationView.ToHtmlString().Contains("Aggregate formulas") && calculationView.ToHtmlString().Contains("Constant"), "Calculation formula editor missing");
                calculation!.AddAggregate(); calculation.AddFormula();
                await calculation.OnCancel.InvokeAsync();
                Check(calculationSettings == null && priceRequest.Calculations.Count == 2 && priceRequest.Formulas!.Count == 5, "Calculation draft mutated the applied settings");
                await calculation.ApplyAsync();
                Check(calculationSettings?.Calculations.Count == 3 && calculationSettings.Formulas.Count == 6, "Calculations/formulas were not applied");
            });
        });
        await test("Analysis session keeps results tied to executed settings and renders a reusable read-only grid", async () =>
        {
            var formulaSession = new DataAnalysisSession(pricing, "Test database");
            await formulaSession.AnalyzeAsync(priceRequest);
            Check(formulaSession.Matches(priceRequest) && !formulaSession.Matches(priceRequest with { Formulas = new[] { priceRequest.Formulas![0] with { Operation = "*" } } }), "Formula edits did not invalidate the previous analysis configuration");
            var session = new DataAnalysisSession(source, "Test database");
            var request = new AnalysisRequest(new[] { "Region" }, new[] { new AnalysisCalculation("Amount", "SUM") });
            await session.AnalyzeAsync(request);
            Check(session.Result?.RowCount == 2 && session.Matches(request) && !session.Matches(request with { SearchText = "changed" }), "Results were not bound to the executed settings");
            var desktop = new Desktop { Path = Path.Combine(root, "session-analysis.xlsx") };
            await session.ExportAsync(desktop);
            Check(File.Exists(desktop.Path) && session.Status.Contains(desktop.Path), "Session export failed or omitted the full path");
            await using var provider = new ServiceCollection().AddLogging().AddSingleton<Microsoft.JSInterop.IJSRuntime>(new NoopJs()).BuildServiceProvider();
            await using var renderer = new HtmlRenderer(provider, provider.GetRequiredService<ILoggerFactory>());
            await renderer.Dispatcher.InvokeAsync(async () =>
            {
                var component = await renderer.RenderComponentAsync<DataAnalysis>(ParameterView.FromDictionary(new Dictionary<string, object?> { ["Session"] = session, ["Desktop"] = desktop }));
                var html = component.ToHtmlString();
                Check(html.Contains("Group by") && html.Contains("Calculations") && html.Contains("Search conditions") && html.Contains("Export Excel") && html.Contains("SUM Amount") && html.Contains("North"), "Analysis controls or shared grid rows missing");
                Check(!html.Contains("Add record") && !html.Contains("Delete selected"), "Analysis exposed write actions");
            });
        });
    }
    private sealed class Desktop : IDesktopServices
    {
        public string? Path;
        public Task<string?> SaveAsync(string name, string extension) => Task.FromResult(Path);
        public Task<string?> SelectNewFilePathAsync(string name, string extension) => Task.FromResult<string?>(null);
        public Task<string?> OpenAsync(params string[] extensions) => Task.FromResult<string?>(null);
        public Task<string?> FolderAsync() => Task.FromResult<string?>(null);
        public Task CopyTextAsync(string text) => Task.CompletedTask;
        public Task ShowTablePreviewAsync(QueryResultSession session) => Task.CompletedTask;
        public Task ShowTableEditorAsync(TableEditorSession session, string connectionLabel) => Task.CompletedTask;
        public Task ShowDataAnalysisAsync(DataAnalysisSession session) => Task.CompletedTask;
    }
    public sealed class GroupHarness : AnalysisGroupDialog
    {
        [Parameter] public Action<GroupHarness>? Capture { get; set; }
        protected override void OnInitialized() { base.OnInitialized(); Capture?.Invoke(this); }
    }
    public sealed class AnalysisHarness : DataAnalysis
    {
        [Parameter] public AnalysisRequest Settings { get; set; } = default!;
        [Parameter] public Action<AnalysisHarness>? Capture { get; set; }
        protected override void OnInitialized()
        {
            ApplyGroups(Settings.Groups);
            ApplyCalculations(new(Settings.Calculations, Settings.IncludeCount, Settings.Formulas ?? []));
            Capture?.Invoke(this);
        }
        public void Render() => StateHasChanged();
    }
    public sealed class CalculationHarness : AnalysisCalculationDialog
    {
        [Parameter] public Action<CalculationHarness>? Capture { get; set; }
        protected override void OnInitialized() { base.OnInitialized(); Capture?.Invoke(this); }
    }
    private sealed class NoopJs : Microsoft.JSInterop.IJSRuntime
    {
        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, object?[]? args) => ValueTask.FromResult(default(TValue)!);
        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, CancellationToken token, object?[]? args) => ValueTask.FromResult(default(TValue)!);
    }
}
