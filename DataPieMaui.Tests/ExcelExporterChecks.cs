using System.Data;
using System.IO.Compression;
using System.Xml.Linq;
using DataPieCore;

internal static class ExcelExporterChecks
{
    private static readonly XNamespace Ns = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
    private static XDocument Read(ZipArchive zip, string name)
    {
        using var stream = zip.GetEntry(name)!.Open();
        return XDocument.Load(stream);
    }
    private static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
    public static async Task Run(string root, Func<string, Func<Task>, Task> test)
    {
        await test("Standard Excel exports retain values in formatted and plain modes", () =>
        {
            using var table = new DataTable();
            table.Columns.Add("Text", typeof(string)); table.Columns.Add("Ratio %", typeof(decimal));
            table.Columns.Add("Date", typeof(DateTime)); table.Columns.Add("Flag", typeof(bool)); table.Columns.Add("Id", typeof(long));
            table.Rows.Add("=1+1", 0.125m, new DateTime(2026, 10, 27, 12, 30, 0), true, 1234567890123456789L);
            table.Rows.Add(" spaced \n text ", DBNull.Value, DBNull.Value, false, 2L);
            foreach (bool formatted in new[] { true, false })
            {
                string path = Path.Combine(root, $"standard-{formatted}.xlsx");
                using var reader = table.CreateDataReader();
                ExcelExporter.Save(path, reader, "Data", new ExcelExportOptions
                {
                    Formatted = formatted, CalculationStartColumn = 1, PercentageColumns = [1],
                    Totals = ["Total", 0.125m, DBNull.Value, DBNull.Value, DBNull.Value]
                });
                Check(!reader.IsClosed, "Exporter disposed caller's reader");
                using var zip = ZipFile.OpenRead(path);
                var xml = Read(zip, "xl/worksheets/sheet1.xml");
                XElement Cell(string reference) => xml.Descendants(Ns + "c").Single(c => (string?)c.Attribute("r") == reference);
                Check(!xml.Descendants(Ns + "f").Any() && Cell("A2").Descendants(Ns + "t").Single().Value == "=1+1", "Text became a formula");
                Check(Cell("E2").Descendants(Ns + "t").Single().Value == "1234567890123456789", "Long ID precision was lost");
                Check(Cell("B2").Element(Ns + "v")!.Value == "0.125" && (string?)Cell("D2").Attribute("t") == "b", "Typed values changed");
                Check((string?)Cell("C2").Attribute("s") == "4", "Date lost its readable date format");
                Check(xml.Descendants(Ns + "row").Count() == 4 && Cell("A4").Descendants(Ns + "t").Single().Value == "Total", "Totals were removed");
                Check(xml.Descendants(Ns + "pane").Any() == formatted && xml.Descendants(Ns + "cols").Any() == formatted && xml.Descendants(Ns + "autoFilter").Any() == formatted, "Formatting mode did not control layout");
                Check((string?)Cell("B2").Attribute("s") == (formatted ? "3" : "0") && (string?)Cell("A1").Attribute("s") == (formatted ? "1" : "0"), "Plain export retained decorative formats");
                using var loaded = MiniExcelLibs.MiniExcel.QueryAsDataTable(path, useHeaderRow: true);
                Check(loaded.Rows.Count == 3 && Convert.ToString(loaded.Rows[0]["Id"]) == "1234567890123456789", "Workbook could not be read back");
            }
            return Task.CompletedTask;
        });
        await test("Standard Excel supports sequential sheets, safe unique names and empty sheets", () =>
        {
            string path = Path.Combine(root, "standard-sheets.xlsx");
            using (var workbook = new ExcelExporter(path))
            {
                foreach (string name in new[] { "a/b", "a?b", new string('x', 40), new string('x', 40), "'", "History" })
                {
                    using var table = new DataTable(); table.Columns.Add("Raw %", typeof(decimal)); table.Columns.Add("Blob", typeof(byte[]));
                    if (name != "'") for (int i = 0; i < 205; i++) table.Rows.Add(0.5m, new byte[] { 0, 255 });
                    using var reader = table.CreateDataReader();
                    workbook.WriteSheet(name, reader, new ExcelExportOptions { Formatted = false });
                }
                workbook.Complete();
            }
            using var zip = ZipFile.OpenRead(path);
            var names = Read(zip, "xl/workbook.xml").Descendants(Ns + "sheet").Select(e => (string)e.Attribute("name")!).ToArray();
            Check(names.Length == 6 && names.Distinct(StringComparer.OrdinalIgnoreCase).Count() == 6 && names.All(n => n.Length <= 31 && n.IndexOfAny(":\\/?*[]".ToCharArray()) < 0), "Sheet names are invalid or duplicate");
            var sheet = Read(zip, "xl/worksheets/sheet1.xml");
            Check(sheet.Descendants(Ns + "row").Count() == 206 && sheet.Descendants(Ns + "t").Any(t => t.Value == "00FF"), "Rows beyond sample or binary values were lost");
            Check(sheet.Descendants(Ns + "c").All(c => (string?)c.Attribute("s") == "0"), "Percent column name changed raw batch export formatting");
            Check(Read(zip, "xl/worksheets/sheet5.xml").Descendants(Ns + "row").Count() == 1, "Empty sheet lost headers");
            return Task.CompletedTask;
        });
        await test("Standard Excel failures preserve existing output and remove temporary files", () =>
        {
            string path = Path.Combine(root, "standard-preserved.xlsx"); File.WriteAllText(path, "previous");
            using var table = new DataTable(); table.Columns.Add("Text"); table.Rows.Add(new string('x', 32768));
            try
            {
                using var reader = table.CreateDataReader();
                ExcelExporter.Save(path, reader);
                throw new Exception("Expected oversized cell failure");
            }
            catch (InvalidOperationException ex) { Check(ex.Message.Contains("32,767"), "Unexpected cell validation failure"); }
            using (var workbook = new ExcelExporter(path))
            {
                using var small = new DataTable(); small.Columns.Add("Name"); small.Rows.Add("valid");
                using var first = small.CreateDataReader(); workbook.WriteSheet("Valid", first);
                using var invalid = table.CreateDataReader();
                try { workbook.WriteSheet("Invalid", invalid); throw new Exception("Expected second-sheet failure"); }
                catch (InvalidOperationException) { }
                try { workbook.Complete(); throw new Exception("Failed workbook was published"); }
                catch (InvalidOperationException) { }
            }
            Check(File.ReadAllText(path) == "previous" && Directory.GetFiles(root, "standard-preserved.xlsx.*.xlsx").Length == 0, "Failure damaged output or left temporary files");
            return Task.CompletedTask;
        });
    }
}
