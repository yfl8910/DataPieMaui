namespace DataPieMaui.ViewModels;

// Page inputs outlive individual Razor components when navigating between sections.
public sealed class WorkspaceForm
{
    public string TableDesignTab { get; set; } = "Create";
    public Services.Shared.TableImportOptions ImportOptions { get; } = new("", () => Task.FromResult<string?>(null), () => Task.FromResult<string?>(null));
    public string ImportTab { get => ImportOptions.Batch ? "Batch" : "File"; set => ImportOptions.Batch = value == "Batch"; }
    public string DropTable { get; set; } = "";

    public string CreateTablePath { get; set; } = "";
    public string CreateTableSheet { get; set; } = "";
    public string CreateTableSql { get; set; } = "";
    public string[] CreateTableSheets { get; set; } = [];

    public string ProcedureTab { get; set; } = "WithoutParameters";
    public string ExportTab { get; set; } = "Table";
    public string ConnectionTab { get; set; } = "SqlServer";
    public string Server { get; set; } = "(local)";
    public string Authentication { get; set; } = "Windows";
    public string UserName { get; set; } = "";
    public string Password { get; set; } = "";
    public string DatabaseName { get; set; } = "";
    public string SqlitePath { get; set; } = "";
    public int SelectedConnectionId { get; set; }
    public string ConnectionString { get; set; } = "";
    public string QueryTable { get; set; } = "";
    public string Sql { get; set; } = "";

    public string ImportTable { get; set; } = "";
    public string ImportPath { get => ImportOptions.Path; set => ImportOptions.Path = value; }
    public string ImportFolder { get => ImportOptions.Folder; set => ImportOptions.Folder = value; }
}

public sealed class SelectableObject(string name, string kind)
{
    public string Name { get; } = name;
    public string Kind { get; } = kind;
    public DBUtil.DefinitionObject? DefinitionObject { get; init; }
    public string DisplayName => DefinitionObject?.DisplayName ?? Name;
    public bool Selected { get; set; }
}
