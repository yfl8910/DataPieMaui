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
        await context.Dialogs.ShowDefinitionAsync(key.DisplayName, result.Sql, result.Notice);
    });
}
