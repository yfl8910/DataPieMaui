using DBUtil;

namespace DataPieMaui.Services.Shared;

public sealed class ProcedureActionService(WorkspaceContext context) : WorkspacePageActions(context)
{
    private DefinitionObject Resolve(DefinitionObject target) => Database.Schema!.DefinitionObjects
        .SingleOrDefault(p => p.Kind == "Procedure" && p.Schema == target.Schema && p.Name == target.Name)
        ?? throw new InvalidOperationException("The procedure is no longer available. Refresh the schema.");

    public Task RunSingleAsync(DefinitionObject target) => target.HasParameters && Type == "SQLSERVER"
        ? RunWithParametersAsync(target) : RunProceduresAsync(new[] { target });

    public Task RunWithParametersAsync(DefinitionObject target) => RunAsync("Load procedure parameters", async watch =>
    {
        var connection = Database.Connection;
        target = Resolve(target);
        if (connection?.DatabaseType != "SQLSERVER" || !target.HasParameters) return;
        var parameters = await Task.Run(() =>
        {
            using var access = (SqlServerDbAccess)DbAccessFactory.Create(connection.ConnectionString, "SQLSERVER");
            return access.ReadProcedureParameters(target);
        });
        var builder = new Microsoft.Data.SqlClient.SqlConnectionStringBuilder(connection.ConnectionString);
        var session = new ProcedureExecutionSession(builder.DataSource + " / " + builder.InitialCatalog, target, parameters, async values =>
        {
            if (!ReferenceEquals(connection, Database.Connection)) throw new InvalidOperationException("Connection changed. Reopen this window.");
            State.Status = $"Running procedure {target.DisplayName}…";
            return await Task.Run(() =>
            {
                using var access = (SqlServerDbAccess)DbAccessFactory.Create(connection.ConnectionString, "SQLSERVER");
                return access.ExecuteProcedure(target, parameters, values);
            });
        });
        await Dialogs.ShowProcedureAsync(session);
        State.Status = session.Result != null ? $"Procedure {target.DisplayName} completed in {session.ElapsedSeconds:F1} s."
            : session.Error.Length > 0 ? $"Procedure {target.DisplayName} was not completed. Check the database before retrying." : "Procedure execution canceled.";
    });

    public Task RunProceduresAsync(IReadOnlyList<DefinitionObject> targets) => RunAsync("Run stored procedures", async watch =>
    {
        var selected = targets.Select(Resolve).ToArray();
        if (Type == "SQLSERVER" && selected.Any(p => p.HasParameters)) throw new InvalidOperationException("Use the parameter dialog for procedures with parameters.");
        var names = selected.Select(p => Type == "SQLSERVER" ? SqlServerDbAccess.QualifiedProcedureName(p) : p.Name).ToArray();
        if (names.Length == 0) throw new InvalidOperationException("Select stored procedures.");
        if (!await Dialogs.ConfirmAsync("Run stored procedures", $"Run {string.Join(", ", selected.Select(p => p.DisplayName))} in {State.Connection}? They may modify data.", "Run")) return;
        watch.Start();
        var warnings = await Task.Run(() =>
        {
            using var access = Database.OpenAccess(); var messages = new List<string>();
            var scripts = access is SQLiteDbAccess ? access.GetProcs() : new List<Proc>();
            foreach (var name in names)
            {
                access.RunProcedure(name);
                var warning = scripts.FirstOrDefault(p => p.Name == name)?.ScriptWarning;
                if (!string.IsNullOrEmpty(warning)) messages.Add(name + ": " + warning);
            }
            return messages;
        });
        watch.Stop();
        if (warnings.Count > 0)
        {
            State.Status = $"Procedures completed in {watch.Elapsed.TotalSeconds:F1} s with skipped SQL statements.";
            await Dialogs.AlertAsync("Partial execution warning", string.Join(Environment.NewLine, warnings));
        }
    });

}