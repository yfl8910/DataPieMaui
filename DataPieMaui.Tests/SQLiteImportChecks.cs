using System.Data;
using System.Data.SQLite;
using DataPieCore;
using DataPieMaui.Services;
using DBUtil;

internal static class SQLiteImportChecks
{
    public static async Task Run(string root, Func<string, Func<Task>, Task> test)
    {
        await test("SQLite import handles numeric blanks and preserves text and mixed values", () =>
        {
            var path = Path.Combine(root, "numeric-blanks.db");
            using var target = DbAccessFactory.Create(new SQLiteConnectionStringBuilder { DataSource = path }.ToString(), "SQLITE");
            target.ExecuteSql("CREATE TABLE Mixed(Amount REAL, Count INTEGER, Created DATE, Label TEXT, Flexible TEXT, Payload BLOB)");
            using var data = new DataTable();
            foreach (var column in new[] { "Amount", "Count", "Created", "Label", "Flexible", "Payload" }) data.Columns.Add(column, typeof(object));
            data.Rows.Add(12.5, 1, new DateTime(2026,10,12), "", 4.5, new byte[] { 1,2 });
            data.Rows.Add("", "  ", "", "  ", "", new byte[] { 3 });
            data.Rows.Add(DBNull.Value, 3, new DateTime(2026,10,13,10,0,0), "after", "text", DBNull.Value);
            using (var reader = data.CreateDataReader()) target.BulkInsert("Mixed", reader);
            using (var reader = target.GetDataReader("SELECT Amount,Count,Created,Label,Flexible,Payload FROM Mixed ORDER BY rowid"))
            {
                Check(reader.Read() && Convert.ToDouble(reader[0]) == 12.5 && Convert.ToString(reader[3]) == "", "First row changed");
                Check(reader.Read() && reader.IsDBNull(0) && reader.IsDBNull(1) && reader.IsDBNull(2), "Numeric/date blanks were not NULL");
                Check(Convert.ToString(reader[3]) == "  " && Convert.ToString(reader[4]) == "" && ((byte[])reader[5]).SequenceEqual(new byte[] {3}), "Text or binary values changed");
                Check(reader.Read() && reader.IsDBNull(0) && Convert.ToInt32(reader[1]) == 3 && Convert.ToString(reader[4]) == "text", "Parameter types leaked across rows");
            }
            target.ExecuteSql("CREATE TABLE ExcelBlanks(Amount REAL, Label TEXT)");
            var excel = Path.Combine(root, "numeric-blanks.xlsx");
            MiniExcelLibs.MiniExcel.SaveAs(excel, new[]
            {
                new Dictionary<string,object?> { ["Amount"] = 1.5, ["Label"] = "first" },
                new Dictionary<string,object?> { ["Amount"] = "", ["Label"] = "" },
                new Dictionary<string,object?> { ["Amount"] = "  ", ["Label"] = "  " },
                new Dictionary<string,object?> { ["Amount"] = 2.5, ["Label"] = "last" }
            }, sheetName:"ExcelBlanks");
            DatabaseOperations.Import(excel, "ExcelBlanks", target);
            using var count = target.GetDataReader("SELECT COUNT(*), SUM(Amount IS NULL), SUM(Amount) FROM ExcelBlanks"); count.Read();
            Check(Convert.ToInt64(count[0]) == 4 && Convert.ToInt64(count[1]) == 2 && Convert.ToDouble(count[2]) == 4, "Excel import with numeric blanks failed");
            return Task.CompletedTask;
        });
        await test("SQLite import errors identify the data row and roll back the file", () =>
        {
            var path = Path.Combine(root,"blank-rollback.db");
            using var target = DbAccessFactory.Create(new SQLiteConnectionStringBuilder { DataSource = path }.ToString(),"SQLITE");
            target.ExecuteSql("CREATE TABLE RequiredValues(Amount REAL NOT NULL)");
            using var data = new DataTable(); data.Columns.Add("Amount",typeof(object)); data.Rows.Add(1.5); data.Rows.Add("");
            using var reader = data.CreateDataReader();
            try { target.BulkInsert("RequiredValues",reader); throw new Exception("Expected NOT NULL failure"); }
            catch (InvalidOperationException ex) { Check(ex.Message.Contains("data row 2") && ex.InnerException != null,"Missing row context"); }
            using var count = target.GetDataReader("SELECT COUNT(*) FROM RequiredValues"); count.Read();
            Check(Convert.ToInt64(count[0]) == 0,"Failed file was partially committed");
            return Task.CompletedTask;
        });
    }
    private static void Check(bool condition,string message) { if(!condition) throw new Exception(message); }
}
