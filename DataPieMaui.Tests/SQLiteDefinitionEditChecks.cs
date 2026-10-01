using System.Data.SQLite;
using DataPieCore;
using DBUtil;
using DataPieMaui.Services.Shared;

internal static class SQLiteDefinitionEditChecks
{
    private static void Check(bool value, string message) { if (!value) throw new Exception(message); }
    private static void Reject(Action action)
    {
        try { action(); } catch (Exception e) when (e is ArgumentException or InvalidOperationException or SQLiteException or IOException) { return; }
        throw new Exception("Invalid edit was accepted");
    }
    public static async Task Run(string root, Func<string, Func<Task>, Task> test)
    {
        string path = Path.Combine(root, "edit-views.db");
        SQLiteConnection.CreateFile(path);
        string cs = new SQLiteConnectionStringBuilder { DataSource = path, FailIfMissing = true }.ToString();
        void Sql(string sql) { using var c = new SQLiteConnection(cs); c.Open(); using var cmd = new SQLiteCommand(sql, c); cmd.ExecuteNonQuery(); }
        Sql("CREATE TABLE Items(Id INTEGER, Name TEXT); CREATE VIEW Report AS SELECT Id,Name FROM Items; CREATE TRIGGER ReportInsert INSTEAD OF INSERT ON Report BEGIN INSERT INTO Items VALUES(new.Id,new.Name); END; CREATE VIEW Dependent AS SELECT Name FROM Report;");
        var target = new DefinitionObject("View", "main", "Report");
        await test("SQLite view editing preserves triggers, rolls back failures and detects stale definitions", () =>
        {
            using var access = (SQLiteDbAccess)DbAccessFactory.Create(cs, "SQLITE");
            var original = access.ReadDefinition(target);
            var saved = access.SaveDefinition(target, original, "-- comment ;\nCREATE VIEW main.Report AS SELECT Id, Name FROM Items WHERE Id > 0;");
            Check(saved.CanEdit && saved.Sql.Contains("Id > 0"), "View not saved");
            Sql("INSERT INTO Report VALUES(1,'kept')");
            foreach (var draft in new[] { "CREATE VIEW Report AS SELECT Missing FROM Items", "CREATE VIEW Report AS SELECT Id FROM Items", "CREATE VIEW Other AS SELECT 1", "CREATE VIEW Report AS SELECT 1; DELETE FROM Items;" })
            {
                Reject(() => access.SaveDefinition(target, saved, draft));
                Check(access.ReadDefinition(target).Sql == saved.Sql, "Failed edit changed view");
            }
            Reject(() => access.SaveDefinition(target, original, "CREATE VIEW Report AS SELECT Id,Name FROM Items"));
            Sql("INSERT INTO Report VALUES(2,'trigger still works')");
            return Task.CompletedTask;
        });
        await test("SQLite view editing protects dependent views without evaluating SQL", () =>
        {
            Sql("CREATE VIEW Base AS SELECT Id,Name FROM Items; CREATE VIEW Child AS SELECT Name FROM Base");
            using var access = (SQLiteDbAccess)DbAccessFactory.Create(cs, "SQLITE");
            var key = new DefinitionObject("View", "main", "Base");
            var original = access.ReadDefinition(key);
            Reject(() => access.SaveDefinition(key, original, "CREATE VIEW Base AS SELECT Id FROM Items"));
            Check(access.ReadDefinition(key).Sql == original.Sql, "Dependency error did not roll back");
            return Task.CompletedTask;
        });
        await test("SQLite procedure edits preserve metadata and backup, compile without execution and reject conflicts", async () =>
        {
            string database = Path.Combine(root, "converted.db");
            string connection = new SQLiteConnectionStringBuilder { DataSource = database, FailIfMissing = true }.ToString();
            using var access = (SQLiteDbAccess)DbAccessFactory.Create(connection, "SQLITE");
            var key = access.ListDefinitionObjects().Single(o => o.Kind == "Procedure");
            var original = access.ReadDefinition(key);
            string file = Path.Combine(root, "StoredProcedures", key.Name + ".txt");
            byte[] before = File.ReadAllBytes(file);
            string header = File.ReadLines(file).First();
            var editor = new DefinitionEditorSession(key, original, database, (old, sql) => Task.FromResult(access.SaveDefinition(key, old, sql)), sql => SQLiteDefinitionValidator.Prepare(key, sql));
            editor.BeginEdit(); editor.Draft = "-- semicolons in strings are not statements\nINSERT INTO Items(Id,Name) VALUES(2,'text; -- literal'); SELECT 1.25, X'0123';";
            editor.Review(); await editor.ConfirmSaveAsync();
            Check(!editor.Editing && editor.Error == "", "SQLite editor did not save: " + editor.Error);
            Check(File.ReadLines(file).First() == header, "Metadata changed");
            Check(Directory.GetFiles(Path.GetDirectoryName(file)!, key.Name + ".txt.*.bak").Any(f => File.ReadAllBytes(f).SequenceEqual(before)), "Original backup missing");
            using (var c = new SQLiteConnection(connection))
            { c.Open(); using var cmd = new SQLiteCommand("SELECT count(*) FROM Items", c); Check((long)cmd.ExecuteScalar() == 1, "Save executed the script"); }
            var saved = access.ReadDefinition(key);
            foreach (string invalid in new[] { "DELETE FROM Items; DROP TABLE Items", "SELECT @Unknown", "PRAGMA user_version=4", "ATTACH 'other.db' AS other", "BEGIN; SELECT 1; COMMIT", "SELECT * FROM Missing", "INSERT INTO Items VALUES(3,'x'); SELECT FROM", "SELECT 'unterminated" })
            {
                Reject(() => access.SaveDefinition(key, saved, invalid));
                Check(access.ReadDefinition(key).Version == saved.Version, "Invalid script modified file");
            }
            Reject(() => access.SaveDefinition(key, original, "SELECT 1"));
            File.AppendAllText(file, "\n-- external edit");
            Reject(() => access.SaveDefinition(key, saved, "SELECT 1"));
            Check(File.ReadAllText(file).EndsWith("-- external edit"), "External edit overwritten");
            access.RunProcedure(key.Name);
        });
    }
}
