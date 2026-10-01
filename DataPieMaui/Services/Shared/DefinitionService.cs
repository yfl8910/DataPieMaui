using DBUtil;
using DataPieMaui.ViewModels;

namespace DataPieMaui.Services.Shared;

public sealed class DefinitionService(WorkspaceContext context)
{
    public Task ShowAsync(SelectableObject item) => context.Operations.RunAsync("Load definition", async watch =>
    {
        var current = context.Database.Connection;
        if (current is null || item.DefinitionObject is not { } key ||
            !(context.Exports.Contains(item) || context.Procedures.Contains(item))) return;
        watch.Start();
        var result = await Task.Run(() =>
        {
            using var access = DbAccessFactory.Create(current.ConnectionString, current.DatabaseType);
            return ((IDefinitionReader)access).ReadDefinition(key);
        });
        watch.Stop();
        if (!ReferenceEquals(current, context.Database.Connection)) return;
        DefinitionEditorSession? editor = null;
        if (current.DatabaseType == "SQLSERVER" && !string.IsNullOrEmpty(result.Sql))
        {
            var builder = new System.Data.SqlClient.SqlConnectionStringBuilder(current.ConnectionString);
            editor = new DefinitionEditorSession(key, result, builder.DataSource + " / " + builder.InitialCatalog, async (original, draft) =>
            {
                if (!ReferenceEquals(current, context.Database.Connection)) throw new InvalidOperationException("Connection changed. Reopen the definition.");
                return await Task.Run(() =>
                {
                    using var access = (SqlServerDbAccess)DbAccessFactory.Create(current.ConnectionString, current.DatabaseType);
                    return access.SaveDefinition(key, original, draft);
                });
            });
        }
        else if (current.DatabaseType == "SQLITE")
        {
            var builder = new System.Data.SQLite.SQLiteConnectionStringBuilder(current.ConnectionString);
            editor = new DefinitionEditorSession(key, result, builder.DataSource, async (original, draft) =>
            {
                if (!ReferenceEquals(current, context.Database.Connection)) throw new InvalidOperationException("Connection changed. Reopen the definition.");
                return await Task.Run(() =>
                {
                    using var access = (SQLiteDbAccess)DbAccessFactory.Create(current.ConnectionString, current.DatabaseType);
                    return access.SaveDefinition(key, original, draft);
                });
            }, draft => DataPieCore.SQLiteDefinitionValidator.Prepare(key, draft));
        }
        await context.Dialogs.ShowDefinitionAsync(key.DisplayName, result.Sql, result.Notice, editor);
    });
}
