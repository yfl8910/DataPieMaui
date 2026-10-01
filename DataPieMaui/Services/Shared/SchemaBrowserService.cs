using DataPieCore;
using DataPieMaui.Services;

namespace DataPieMaui.Services.Shared;

public sealed class SchemaBrowserService(WorkspaceContext context)
{
    private DatabaseConnectionInfo? connection;
    private readonly Dictionary<SchemaObject, SchemaDetails> cache = new();
    public IReadOnlyList<SchemaObject> Objects { get; private set; } = [];
    public SchemaObject? Selected { get; private set; }
    public SchemaDetails? Details { get; private set; }
    public string Kind { get; set; } = "Table";
    public string Search { get; set; } = "";
    public string Error { get; private set; } = "";
    public bool Loaded { get; private set; }

    public void Reset()
    {
        connection = context.Database.Connection;
        Objects = []; Selected = null; Details = null; cache.Clear();
        Kind = "Table"; Search = ""; Error = ""; Loaded = false;
    }
    public Task EnsureAsync()
    {
        if (!ReferenceEquals(connection, context.Database.Connection)) Reset();
        return Loaded ? Task.CompletedTask : RefreshAsync();
    }
    public Task RefreshAsync() => context.Operations.RunAsync("Load database schema", async watch =>
    {
        if (!ReferenceEquals(connection, context.Database.Connection)) Reset();
        var current = context.Database.Connection;
        if (current is null) return;
        watch.Start(); Error = ""; cache.Clear(); Details = null; Selected = null; Loaded = false;
        try
        {
            var objects = await Task.Run(() =>
            {
                using var access = DBUtil.DbAccessFactory.Create(current.ConnectionString, current.DatabaseType);
                return SchemaBrowser.List(access);
            });
            if (!ReferenceEquals(current, context.Database.Connection)) return;
            Objects = objects; Loaded = true;
        }
        catch (Exception ex) { if (ReferenceEquals(current, context.Database.Connection)) { Objects = []; Error = ex.Message; context.State.Status = "Load database schema failed: " + ex.Message; } }
    });
    public Task SelectAsync(SchemaObject item) => context.Operations.RunAsync("Load object details", async watch =>
    {
        var current = context.Database.Connection;
        if (current is null || !ReferenceEquals(current, connection) || !Objects.Contains(item)) return;
        watch.Start(); Selected = item; Details = null; Error = "";
        try
        {
            if (!cache.TryGetValue(item, out var details))
                details = await Task.Run(() =>
                {
                    using var access = DBUtil.DbAccessFactory.Create(current.ConnectionString, current.DatabaseType);
                    return SchemaBrowser.Read(access, item);
                });
            if (!ReferenceEquals(current, context.Database.Connection)) return;
            cache[item] = details; Details = details;
        }
        catch (Exception ex) { if (ReferenceEquals(current, context.Database.Connection)) { Error = ex.Message; context.State.Status = "Load object details failed: " + ex.Message; } }
    });
    public void ChangeKind(string kind) { Kind = kind; Selected = null; Details = null; Error = ""; }
}
