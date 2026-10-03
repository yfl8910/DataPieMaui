using System.Data;
using System.Data.SqlClient;
using DBUtil;
using DataPieMaui.Services.Shared;

internal static class ProcedureExecutionChecks
{
    private static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
    private static void Reject(Action action)
    {
        try { action(); } catch (Exception e) when (e is ArgumentException or FormatException or OverflowException) { return; }
        throw new Exception("Invalid parameter accepted");
    }
    public static async Task Run(Func<string, Func<Task>, Task> test)
    {
        await test("Procedure parameter binding preserves explicit types, NULL, empty strings and range checks", () =>
        {
            var text = new ProcedureParameter("@Name", SqlDbType.NVarChar, 3, 0, 0, ParameterDirection.Input);
            Check((string)text.Bind("Value", "").Value == "", "Empty string became NULL");
            Check(text.Bind("NULL", "ignored").Value == DBNull.Value, "NULL was not bound");
            Reject(() => text.Bind("Value", "long"));
            var integer = new ProcedureParameter("@Id", SqlDbType.Int, 0, 0, 0, ParameterDirection.Input);
            Check(integer.Bind("Value", "42").Value is int i && i == 42, "Integer became a string");
            Reject(() => integer.Bind("Value", "2147483648"));
            Reject(() => integer.Bind("Value", "1; DROP TABLE Items"));
            var number = new ProcedureParameter("@Amount", SqlDbType.Decimal, 0, 5, 2, ParameterDirection.InputOutput);
            var bound = number.Bind("Value", "123.45");
            Check(bound.Precision == 5 && bound.Scale == 2 && bound.Direction == ParameterDirection.InputOutput, "Metadata lost");
            Reject(() => number.Bind("Value", "1000")); Reject(() => number.Bind("Value", "1.001"));
            Reject(() => (integer with { Type = SqlDbType.Float }).Bind("Value", "NaN"));
            Reject(() => (integer with { Type = SqlDbType.Time }).Bind("Value", "25:00:00"));
            Reject(() => (integer with { Type = SqlDbType.Structured }).Bind("NULL", ""));
            Check(SqlServerDbAccess.QualifiedProcedureName(new("Procedure", "a]b", "p.name")) == "[a]]b].[p.name]", "Identifier escaping failed");
            return Task.CompletedTask;
        });
        await test("Procedure execution requires review, snapshots parameters and blocks duplicate submissions", async () =>
        {
            var completion = new TaskCompletionSource<ProcedureExecutionResult>();
            int calls = 0;
            SqlParameter[] submitted = [];
            var specs = new[] { new ProcedureParameter("@Id", SqlDbType.Int, 0, 0, 0, ParameterDirection.Input), new ProcedureParameter("@Optional", SqlDbType.NVarChar, 20, 0, 0, ParameterDirection.Input) };
            var session = new ProcedureExecutionSession("server / db", new("Procedure", "dbo", "Run"), specs, values => { calls++; submitted = values.ToArray(); return completion.Task; });
            await session.ConfirmAsync(); Check(calls == 0, "Execution bypassed review");
            session.Inputs[0].Text = "bad"; session.Review(); Check(!session.Reviewing && session.Error.Contains("@Id"), "Bad input accepted");
            session.Inputs[0].Text = "7"; session.Inputs[1].Mode = "Omit"; session.Review();
            session.Inputs[0].Text = "99";
            var pending = session.ConfirmAsync(); await session.ConfirmAsync();
            Check(calls == 1 && session.Running && submitted.Length == 1 && (int)submitted[0].Value == 7, "Duplicate execution or changed approved values");
            completion.SetResult(new(-1, 5, new Dictionary<string, object> { ["@Out"] = 42 })); await pending;
            Check(session.Result?.ReturnValue == 5 && (int)session.Result.Outputs["@Out"] == 42 && !session.Running, "Output not retained");
            session.Review(); await session.ConfirmAsync(); Check(calls == 1, "Completed procedure executed again");
        });
        await test("Procedure execution keeps inputs after failure and rejects unsupported types", async () =>
        {
            var spec = new ProcedureParameter("@Id", SqlDbType.Int, 0, 0, 0, ParameterDirection.Input);
            var session = new ProcedureExecutionSession("db", new("Procedure", "dbo", "Run"), [spec], _ => throw new InvalidOperationException("SQL failure"));
            session.Inputs[0].Text = "10"; session.Review(); await session.ConfirmAsync();
            Check(session.Inputs[0].Text == "10" && session.Error.Contains("partially") && !session.Running && !session.Reviewing, "Failure lost draft or left running state");
            var unsupported = new ProcedureExecutionSession("db", new("Procedure", "dbo", "Run"), [spec with { Type = SqlDbType.Structured }], _ => throw new Exception("Must not run"));
            unsupported.Review(); await unsupported.ConfirmAsync(); Check(!unsupported.Supported && !unsupported.Reviewing, "Unsupported parameter allowed");
        });
    }
}
