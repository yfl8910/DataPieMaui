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
    public SchemaBrowserService SchemaBrowser { get; }
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
        SchemaBrowser = new SchemaBrowserService(this);
        State.PropertyChanged += (_, _) => Notify();
        Dialogs.Changed += Notify;
    }

    internal void Notify() => Changed?.Invoke();
}
