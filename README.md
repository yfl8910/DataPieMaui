# DataPieMaui

A Windows desktop workspace for querying, importing, exporting, and converting SQL Server and SQLite data. Built with .NET 11 RC1 and .NET MAUI Blazor Hybrid, with a Blazor interface throughout the workspace and a separate query-results window.

<img width="2277" height="1606" alt="image" src="https://github.com/user-attachments/assets/bfabcc73-e812-47e5-b7b6-a5d1bee77a1a" />


## Features

- Connect to SQL Server using Windows or SQL Server authentication, or open a local SQLite database.
- Navigate between Blazor pages without losing SQL or form inputs; review operations in shared confirmation dialogs and a persistent status bar.
- Save and manage database connections.
- Generate SELECT, UPDATE, and DELETE statements and execute SQL.
- Preview query results in a separate, resizable Blazor window with dynamic columns, typed sorting, pagination, and copying.
- Export queries and tables to Excel or CSV; export multiple tables or views to worksheets or separate CSV files.
- Import Excel, CSV, or a matching table from a SQLite database; batch-import CSV and XLSX files from a folder.
- Run selected procedures without required parameters.
- Convert SQL Server databases to SQLite, with warnings for unsupported view or procedure SQL.

## Requirements

- Windows x64. The project declares Windows 10 version 1809 as its minimum OS version.
- .NET SDK `11.0.100-rc.1.26425.128` and the `maui-windows` workload set `11.0.100-rc.1.26458.5` for development. `global.json` selects the SDK feature band and allows prerelease SDKs.
- Microsoft Edge WebView2 Runtime for the entire application interface, including when running a published copy. If it is missing, the native host displays an installation message instead of opening the workspace.
- Access to NuGet when restoring dependencies for the first time.

The current project targets Windows only.

As of October 4, 2026, .NET 11 RC1 is the latest .NET 11 release; it is a release candidate, not the final release. MAUI Controls and Blazor WebView use `11.0.0-rc.1.26451.6`. Install the matching SDK from https://dotnet.microsoft.com/en-us/download/dotnet/11.0 before using the commands below. Visual Studio must support this SDK and have preview SDK usage enabled where required.

Visual Studio needs the SDK installed system-wide (normally under `C:\Program Files\dotnet`), plus the matching MAUI workload. An SDK installed only in `tmp/net11/sdk/` is sufficient for explicit command-line validation but is not automatically discovered by Visual Studio. `global.json` selects an installed SDK; it does not install one.

If Visual Studio reports that the current SDK cannot target .NET 11, install the x64 SDK version above, run the workload installation command below, and restart Visual Studio. Check `dotnet --list-sdks` and `dotnet --version` from the repository root. This repository explicitly enables prerelease SDK selection through `global.json`.

## Build and run

Run these PowerShell commands from the repository root:

```powershell
dotnet workload install maui-windows --version 11.0.100-rc.1.26458.5
dotnet restore DataPieMaui\DataPieMaui.csproj
dotnet build DataPieMaui\DataPieMaui.csproj -c Debug
dotnet build DataPieMaui\DataPieMaui.csproj -t:Run -c Debug
```

Alternatively, open `DataPieMaui.sln` in a Visual Studio installation that supports .NET 11 RC1 and .NET MAUI, select `DataPieMaui` as the startup project, and run the Windows target.



## View object definitions

Use **View definition** beside a view on **Batch export**, or beside a procedure on **Stored procedures**. Only clicking this button reads the selected definition; initialization does not read SQL bodies. The dialog supports **Copy SQL**, **Edit** for supported objects, and **Close**. Closing releases the text; reopening reads the latest definition. SQL Server permissions or encryption may prevent viewing a definition. SQLite shows converted procedure scripts. Database conversion still loads the definitions it needs.

### Editing SQL Server definitions

For ordinary SQL Server views and T-SQL procedures, **Edit** opens a draft. **Save changes…** validates one schema-qualified definition and displays a text diff and the full SQL to submit. Only **Confirm save** changes the database. Canceling or closing a modified draft prompts before discarding it.

The editor accepts CREATE, ALTER, or CREATE OR ALTER for the selected object and submits ALTER without executing the procedure. It rejects extra batches, object renames, encrypted definitions, and CLR/native procedures. A lightweight declaration scanner checks the exact target, quoted identifiers, comments, batch separators and unsupported options without a parser package. Complete syntax validation is performed by SQL Server during the save transaction, so some syntax errors appear only after Confirm save. The scanner conservatively rejects extra view commands and bare GO/ENCRYPTION/NATIVE_COMPILATION/EXTERNAL keywords; quote identifiers that use these names. This is not a full T-SQL grammar parser. Indexed views and signed modules remain read-only to avoid losing indexes or signatures. ALTER permission is required. Definitions lacking explicit schema qualification must be qualified before saving.

Saving rechecks the original definition, modification version, and persisted SET options inside a transaction, preserves QUOTED_IDENTIFIER / ANSI_NULLS, and reloads the result before commit. Errors keep the draft for correction. Saves from this editor use a transaction-scoped application lock; external tools such as SSMS do not share that lock, so the optimistic check is not a guarantee against a simultaneous external DDL change between the check and ALTER. Coordinate concurrent schema changes. If a connection fails during commit, reopen the object to verify its actual state before retrying.

### Editing SQLite views and procedure scripts

SQLite uses the same draft, diff, confirmation and unsaved-change controls. Views accept one `CREATE VIEW` statement for the selected `main` view. Saving reserves a database write transaction, checks the original text and schema version, and recreates the view and its triggers atomically. Validation prepares SQL without evaluating it and checks that previously valid views still compile. A view with triggers must retain its column names and order. Failures roll back the replacement and retain the draft. Column compatibility does not guarantee unchanged business semantics of triggers; review those when changing a view's logic.

Converted procedures edit only the SQL body in their existing `StoredProcedures/*.txt` file. Saving checks the active database export, full-file hash, SQL syntax, object references and existing parameter/local names. SELECT/INSERT/UPDATE/DELETE/REPLACE and WITH statements are supported; DDL, transaction and connection commands are rejected. Validation uses EXPLAIN and does not run the procedure. The original metadata header is retained, including parameter defaults, local initializers and conversion warnings. New parameters and changes to conversion metadata are outside this editor's scope.

Each successful script save atomically replaces the file and retains the original as a uniquely named sibling `.bak` file. File permissions must allow this. In-process saves are serialized and the file hash is rechecked immediately before replacement; an external editor can still race that final check, so avoid simultaneous external edits. Backups are retained for manual recovery. Saved scripts are used on the next explicit procedure execution.

## Using the app

1. Open **Database connection** and connect to SQL Server or SQLite.
2. Open **SQL query**, select a table to generate SQL or enter your own statement, and click **Preview query**.
3. View results in the separate window. Click column headers to sort, select a row to copy it, or copy all preview rows. Subsequent queries update the same window. Use **Open results** to reopen a closed result window without rerunning the query.
4. Use the export buttons for the full query result, or **Batch export** for selected tables and views.
5. Use **Import data**, **Stored procedures**, or **Convert to SQLite** for the corresponding operations.

On **Import data**, select a **Target table** and click **Query data** to inspect it. On **Batch export**, each table/view has its own **Query data** button, independent of the export checkboxes. These actions reuse the results window, show the object name, and preserve your SQL editor and selections. Object previews have the same 1,000-row limit, sorting, paging, and copying as SQL previews.

### Query preview behavior

- Previews are limited to the first 1,000 rows and displayed in pages of 100. Paging and sorting operate on those preview rows only.
- Query preview uses a 30-second command timeout.
- **Copy all preview rows** copies the loaded preview in its current sort order, including column headers.
- Full exports execute the query again and are not limited to the preview rows. Data may have changed since the preview.
- The result table is read-only; SQL entered in the editor can still modify the database.

For operations with a save picker or confirmation dialog, elapsed-time measurement starts after the final confirmation, excluding time spent choosing a destination or waiting to confirm.

Only one operation can run at a time. Navigation and operation buttons are disabled while it runs; conversion cancellation remains available in the status bar. Confirmation dialogs support keyboard focus and Escape to cancel. Native Windows file pickers remain in use for opening files and choosing export destinations.

### Local data and conversion

Saved connections are stored in `%LOCALAPPDATA%\DataPie\connections.db`. Connection strings are stored without application-level encryption and can contain database passwords; treat this file as private configuration.

When distributing a converted SQLite database, keep its generated `StoredProcedures` folder alongside it. SQLite procedures are handled as converted scripts; unsupported SQL is reported in warnings. A canceled or failed conversion may leave an incomplete output database, which should be regenerated before use.

## Publish for Windows x64

```powershell
dotnet publish DataPieMaui\DataPieMaui.csproj `
  -c Release `
  -f net11.0-windows10.0.19041.0 `
  -r win-x64 `
  --self-contained true `
  -p:WindowsAppSDKSelfContained=true `
  -o publish\win-x64
```

Launch `publish\win-x64\DataPieMaui.exe`. Distribute the **entire folder**, including `wwwroot` and the dependency files, rather than the executable alone.

This produces an unpackaged application with .NET and Windows App SDK dependencies. WebView2 Runtime remains a separate prerequisite on the destination computer.

## Regression checks

The test project is a console-based regression runner. Run it with:

```powershell
dotnet run --project DataPieMaui.Tests\DataPieMaui.Tests.csproj -c Release
```

Checks cover SQLite access, connection management, query previews, import/export, procedure conversion, cancellation, result snapshots, typed sorting, and copy formatting. Workspace checks also exercise confirmation and cancellation, concurrent-operation protection, state retention, and rendering of the actual Razor pages. These checks use a test desktop adapter; they do not automate native window/file-picker interactions or require a live SQL Server instance.

To also write static page previews to the ignored `tmp/ui-review/` directory, run:

```powershell
dotnet run --project DataPieMaui.Tests\DataPieMaui.Tests.csproj -c Release -- --render-ui
```

These previews are render snapshots, not an interactive web version of the desktop application.

## UI architecture

- `MainPage.cs` hosts the main `BlazorWebView` and provides a native fallback when WebView2 is missing.
- `Components/Workspace.razor` provides navigation and the status bar; `Components/Pages/` contains the business pages, and `Components/Shared/` contains shared controls and dialogs.
- All business pages, including login and connection management, keep their markup and page-specific workflows together in a single `.razor` file using an `@code` block. The static About page needs no code block.
- `WorkspaceController` composes shared services and coordinates navigation and native-window lifecycle. Pages use `WorkspacePageBase` to access shared state and the operation runner. The connection page initializes saved connections once per workspace; revisiting the page preserves loaded configuration and operation status.
- `Services/Shared/WorkspaceContext` owns session state and the shared capabilities for one workspace. Shared services survive navigation; `WorkspaceForm` retains input values when page components are recreated. `DatabaseConversionService` owns conversion execution, progress, and cancellation so the status bar and native close handler do not depend on a page instance.
- `Services/Shared/WorkspaceOperationRunner` provides the single operation gate, error handling, and timing contract used by every page. `QueryPreviewService` shares table/view and SQL result loading, and `FileNames` shares export filename normalization.
- `IDesktopServices` separates the workspace from Windows-specific operations. `WindowsDesktopServices` dispatches native work to the UI thread, binds file pickers to their owner, and manages the result window.
- `DataPieCore` remains the database/import/export engine. Razor components run locally; no web server is required.

## Repository layout

| Path | Purpose |
| --- | --- |
| `DataPieMaui/` | Native MAUI host, services, state, and window management |
| `DataPieMaui/Components/` | Blazor workspace, business pages, dialogs, and query results |
| `DataPieMaui/Components/Pages/*.razor` | Page markup and page-specific workflows in one file |
| `DataPieMaui/Services/Shared/` | Shared context, operation execution, result previews, and utilities |
| `DataPieMaui/wwwroot/` | Local HTML, CSS, and Blazor host assets |
| `DataPieCore/` | Database access, import/export, and SQL Server-to-SQLite conversion |
| `DataPieMaui.Tests/` | Console regression checks |

Generated directories such as `publish/`, `.vs/`, `bin/`, and `obj/` are excluded from Git. Source assets under `DataPieMaui/wwwroot/` remain tracked.

### Publish from Visual Studio

Open `DataPieMaui.sln` in Visual Studio with .NET 11 RC1 and the .NET MAUI workload installed. Stop debugging, set `DataPieMaui` as the startup project, and select **Release** and **Windows Machine**. Right-click the **DataPieMaui project** and select **Publish**, then use `FolderProfile`.

The project explicitly declares `Publish` and `FolderPublish` capabilities for its unpackaged Windows configuration. After changing the project file, reload the project (or reopen the solution) so Visual Studio refreshes the commands. The profile publishes a self-contained Windows x64 application to `publish/win-x64`; distribute the entire directory. WebView2 Runtime is still required.

If the MAUI tooling still disables the Publish command, the same profile can be run from Visual Studio's terminal:

```powershell
dotnet publish DataPieMaui/DataPieMaui.csproj -c Release -p:PublishProfile=FolderProfile
```

Microsoft's standard MAUI Windows Publish wizard creates MSIX packages. This project uses folder deployment, so do not enable MSIX packaging solely to activate that wizard.

### Running SQL Server procedures with parameters

On **Stored procedures**, SQL Server procedures are grouped into **Without parameters** and **With parameters** tabs. Only the first tab offers selection and batch execution; the second offers **Run with parameters** per procedure. Classification reads only parameter existence alongside object names, excluding RETURN values. Defaulted and OUTPUT parameters count as parameters. Detailed parameter metadata loads only when the execution window is opened; definitions and parameter lists are not fetched at connection time. Reconnect after external signature changes to refresh classification. Enter values, select **NULL**, or choose **Omit** to let SQL Server use a default if one exists. An omitted required parameter produces a server error. Empty text remains an empty string. OUTPUT parameters support initial values; omitting one also means its output is not collected.

**Review and run…** displays the server/database, schema-qualified procedure and submitted values. Only **Confirm run** executes the command. The application passes typed SqlParameters, rechecks parameter metadata before execution, blocks duplicate submissions and window closure while running, and displays elapsed time, OUTPUT values and the procedure RETURN code separately from affected rows. Affected rows may be unavailable with NOCOUNT. Execution errors retain input; a failed or timed-out procedure may already have modified data, so inspect the database before retrying. Timeout is 120 seconds; execution is not automatically wrapped in an application transaction.

Supported inputs include common numeric, string, date/time, GUID and XML types. Decimal input currently uses .NET decimal's range/precision. Table-valued, binary, CLR/UDT and other unsupported parameter types disable this execution window. Complete value constraints and EXECUTE/metadata permissions are enforced by SQL Server. Result sets are not displayed in this first phase. The existing batch button remains available without parameter configuration and now uses schema-qualified SQL Server names. SQLite execution is unchanged. No new package dependency is required.
