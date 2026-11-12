using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace DataPieMaui.ViewModels;

public class WorkspaceState : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;
    private string? databaseType;
    private bool busy;
    private bool converting;
    private string status = "Select or create a database connection.";
    public string? ChineseStatus { get; private set; } = "请选择或创建数据库连接。";
    internal int StatusVersion { get; private set; }
    private string connection = "Not connected";
    public string? DatabaseType
    {
        get => databaseType;
        set
        {
            if (databaseType == value) return;
            databaseType = value;
            Notify();
            Notify(nameof(IsConnected));
            Notify(nameof(CanConvertToSqlite));
        }
    }
    public bool IsConnected => DatabaseType is "SQLSERVER" or "SQLITE";
    public bool CanConvertToSqlite => DatabaseType == "SQLSERVER";
    public bool Busy { get => busy; set { busy = value; Notify(); Notify(nameof(Idle)); } }
    public bool Idle => !Busy;
    public bool Converting { get => converting; set { converting = value; Notify(); } }
    public string Status { get => status; set => SetStatus(value); }
    internal void SetStatus(string english, string? chinese = null)
    {
        status = english;
        ChineseStatus = chinese;
        StatusVersion++;
        Notify(nameof(Status));
    }
    public string Connection { get => connection; set { connection = value; Notify(); } }
    private void Notify([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new(name));
}
