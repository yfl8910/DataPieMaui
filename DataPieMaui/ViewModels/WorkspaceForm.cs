namespace DataPieMaui.ViewModels;

// Page inputs outlive individual Razor components when navigating between sections.
public sealed class WorkspaceForm
{
    public string ExportTab { get; set; } = "Table";
    public string ConnectionTab { get; set; } = "SqlServer";
    public string Server { get; set; } = "(local)";
    public string Authentication { get; set; } = "Windows";
    public string UserName { get; set; } = "";
    public string Password { get; set; } = "";
    public string DatabaseName { get; set; } = "";
    public string SqlitePath { get; set; } = "";
    public int SelectedConnectionId { get; set; }
    public string ConnectionName { get; set; } = "";
    public string ConnectionType { get; set; } = "SQLSERVER";
    public string ConnectionString { get; set; } = "";
    public string QueryTable { get; set; } = "";
    public string Sql { get; set; } = "";
    public string ImportTable { get; set; } = "";
    public string ImportPath { get; set; } = "";
    public string ImportFolder { get; set; } = "";
}

public sealed class SelectableObject(string name, string kind)
{
    public string Name { get; } = name;
    public string Kind { get; } = kind;
    public DBUtil.DefinitionObject? DefinitionObject { get; init; }
    public string DisplayName => DefinitionObject?.DisplayName ?? Name;
    public bool Selected { get; set; }
}
