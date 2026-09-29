using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace DataPieMaui.ViewModels;

public class WorkspaceState : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;
    private string? databaseType;
    private bool busy;
    private bool converting;
    private string status = "请选择或创建数据库连接。";
    private string connection = "未连接";
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
    public string Status { get => status; set { status = value; Notify(); } }
    public string Connection { get => connection; set { connection = value; Notify(); } }
    private void Notify([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new(name));
}
