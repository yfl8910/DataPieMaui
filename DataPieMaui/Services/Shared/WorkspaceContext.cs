using DataPieMaui.ViewModels;

namespace DataPieMaui.Services.Shared;

/// <summary>State and shared capabilities owned by one desktop workspace, not by a rendered page.</summary>
public sealed class WorkspaceContext
{
    internal bool ConnectionsInitialized { get; set; }
    internal ConnectionStore ConnectionStore => connectionStore ??= new ConnectionStore(StorePath);
    private ConnectionStore? connectionStore;

    internal DatabaseOperations Database { get; } = new();
    internal IDesktopServices Desktop { get; }
    internal string StorePath { get; }
    public WorkspaceState State { get; } = new();
    public WorkspaceForm Form { get; } = new();
    public WorkspaceDialogs Dialogs { get; } = new();
    public QueryResultSession Results { get; } = new();
    public WorkspaceOperationRunner Operations { get; }
    public QueryPreviewService Preview { get; }
    public DefinitionService Definitions { get; }
    public string Section { get; internal set; } = "Connections";
    public string QuerySummary { get; internal set; } = "Run a preview to open the result table in a separate window.";
    public IReadOnlyList<Dbinfo> Connections { get; internal set; } = [];
    public IReadOnlyList<string> Databases { get; internal set; } = [];
    public IReadOnlyList<string> Tables { get; internal set; } = [];
    public List<SelectableObject> Exports { get; } = [];
    public List<SelectableObject> Procedures { get; } = [];
    public event Action? Changed;

    public WorkspaceContext(IDesktopServices desktop, string storePath)
    {
        Desktop = desktop;
        StorePath = storePath;
        Operations = new WorkspaceOperationRunner(State, Dialogs);
        Preview = new QueryPreviewService(this);
        Definitions = new DefinitionService(this);
        State.PropertyChanged += (_, _) => Notify();
        Dialogs.Changed += Notify;
    }

    internal async Task RefreshSchemaAsync()
    {
        await Database.ConnectAsync(Database.Connection!);
        Tables = Database.Schema!.Tables.Select(t => t.Name).ToArray();
        var selected = Exports.Where(x => x.Selected).Select(x => (x.Name, x.Kind)).ToHashSet();
        Exports.Clear();
        Exports.AddRange(Tables.Select(n => new SelectableObject(n, "Table") { Selected = selected.Contains((n, "Table")) }));
        Exports.AddRange(Database.Schema.DefinitionObjects.Where(o => o.Kind == "View").Select(o => new SelectableObject(o.Name, o.Kind) { DefinitionObject = o, Selected = selected.Contains((o.Name, o.Kind)) }));
        if (!Database.Schema.Tables.Any(t => TableSelection.Key(t, Database.Connection!.DatabaseType) == Form.ImportTable)) Form.ImportTable = "";
        if (!Database.Schema.Tables.Any(t => TableSelection.Key(t, Database.Connection!.DatabaseType) == Form.QueryTable)) Form.QueryTable = "";
        if (!Tables.Contains(Form.DropTable)) Form.DropTable = "";
        Notify();
    }

    internal void Notify() => Changed?.Invoke();
}
