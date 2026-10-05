using Microsoft.Data.SqlClient;
using System.Diagnostics;
using DBUtil;

namespace DataPieMaui.Services.Shared;

public sealed class ProcedureParameterInput(ProcedureParameter parameter)
{
    public ProcedureParameter Parameter { get; } = parameter;
    public string Mode { get; set; } = parameter.Direction is System.Data.ParameterDirection.Output or System.Data.ParameterDirection.InputOutput ? "NULL" : "Value";
    public string Text { get; set; } = "";
}

public sealed class ProcedureExecutionSession(string database, DefinitionObject target, IReadOnlyList<ProcedureParameter> parameters,
    Func<IReadOnlyList<SqlParameter>, Task<ProcedureExecutionResult>> execute)
{
    public string Database { get; } = database;
    public DefinitionObject Target { get; } = target;
    public List<ProcedureParameterInput> Inputs { get; } = parameters.Select(p => new ProcedureParameterInput(p)).ToList();
    public bool Supported => Inputs.All(p => p.Parameter.Supported);
    public bool Reviewing { get; private set; }
    public bool Running { get; private set; }
    public event Action? BecameIdle;
    public string Error { get; private set; } = "";
    public string Summary { get; private set; } = "";
    public ProcedureExecutionResult? Result { get; private set; }
    public double ElapsedSeconds { get; private set; }
    private SqlParameter[] approved = [];
    public void Review()
    {
        if (Running || Result != null || !Supported) return;
        Error = "";
        try
        {
            var values = new List<SqlParameter>();
            foreach (var input in Inputs)
            {
                if (input.Mode == "Omit") continue;
                try { values.Add(input.Parameter.Bind(input.Mode, input.Text)); }
                catch (Exception ex) when (ex is FormatException or OverflowException or ArgumentException)
                { throw new ArgumentException(input.Parameter.Name + ": " + ex.Message); }
            }
            approved = values.ToArray();
            Summary = string.Join(Environment.NewLine, Inputs.Select(p => p.Parameter.Name + " = " + (p.Mode == "Omit" ? "[omitted; server default if available]" : p.Mode == "NULL" ? "NULL" : "\"" + p.Text + "\"")));
            Reviewing = true;
        }
        catch (Exception ex) { Error = ex.Message; Reviewing = false; approved = []; }
    }
    public void Back() { if (!Running) { Reviewing = false; approved = []; } }
    public async Task ConfirmAsync()
    {
        if (!Reviewing || Running || Result != null) return;
        Running = true; Error = "";
        var watch = Stopwatch.StartNew();
        try { Result = await execute(approved); }
        catch (Exception ex) { Error = ex.Message + " Execution may have partially modified data. Check the database before retrying."; }
        finally { ElapsedSeconds = watch.Elapsed.TotalSeconds; Running = false; Reviewing = false; approved = []; BecameIdle?.Invoke(); }
    }
}
