using System.Diagnostics;
using DataPieMaui.ViewModels;

namespace DataPieMaui.Services.Shared;

/// <summary>Gives page actions access to the same state, database session, and operation gate.</summary>
public abstract class WorkspacePageActions(WorkspaceContext context)
{
    private protected WorkspaceContext Context { get; } = context;
    private protected DatabaseOperations Database => Context.Database;
    private protected IDesktopServices Desktop => Context.Desktop;
    private protected WorkspaceState State => Context.State;
    private protected WorkspaceForm Form => Context.Form;
    private protected WorkspaceDialogs Dialogs => Context.Dialogs;
    private protected QueryResultSession Results => Context.Results;
    private protected bool IsOperationRunning => Context.Operations.IsRunning;
    private protected string Type => Database.Connection?.DatabaseType ?? throw new InvalidOperationException("Connect to a database first.");
    private protected string Section { get => Context.Section; set => Context.Section = value; }
    private protected string QuerySummary { get => Context.QuerySummary; set => Context.QuerySummary = value; }
    private protected IReadOnlyList<Dbinfo> Connections { get => Context.Connections; set => Context.Connections = value; }
    private protected IReadOnlyList<string> Databases { get => Context.Databases; set => Context.Databases = value; }
    private protected IReadOnlyList<string> Tables { get => Context.Tables; set => Context.Tables = value; }
    private protected List<SelectableObject> Exports => Context.Exports;
    private protected List<SelectableObject> Procedures => Context.Procedures;

    private protected void Notify() => Context.Notify();
    private protected Task RunAsync(string title, Func<Stopwatch, Task> action) => Context.Operations.RunAsync(title, action);
    private protected static string Required(string value, string message) =>
        string.IsNullOrWhiteSpace(value) ? throw new InvalidOperationException(message) : value;
}
