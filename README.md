# DataPieMaui

A Windows desktop workspace for querying, importing, exporting, and converting SQL Server and SQLite data. Built with .NET 10 and .NET MAUI Blazor Hybrid, with a Blazor interface throughout the workspace and a separate query-results window.

## Features

- Connect to SQL Server using Windows or SQL Server authentication, or open a local SQLite database.
- Navigate between Blazor pages without losing SQL or form inputs; review operations in shared confirmation dialogs and a persistent status bar.
- Save and manage connections, including importing legacy connection settings.
- Generate SELECT, UPDATE, and DELETE statements and execute SQL.
- Preview query results in a separate, resizable Blazor window with dynamic columns, typed sorting, pagination, and copying.
- Export queries and tables to Excel or CSV; export multiple tables or views to worksheets or separate CSV files.
- Import Excel, CSV, or a matching table from a SQLite database; batch-import CSV and XLSX files from a folder.
- Run selected procedures without required parameters.
- Convert SQL Server databases to SQLite, with warnings for unsupported view or procedure SQL.

## Requirements

- Windows x64. The project declares Windows 10 version 1809 as its minimum OS version.
- .NET 10 SDK and the `maui-windows` workload for development.
- Microsoft Edge WebView2 Runtime for the entire application interface, including when running a published copy. If it is missing, the native host displays an installation message instead of opening the workspace.
- Access to NuGet when restoring dependencies for the first time.

The current project targets Windows only.

## Build and run

Run these PowerShell commands from the repository root:

```powershell
dotnet workload install maui-windows
dotnet restore DataPieMaui\DataPieMaui.csproj
dotnet build DataPieMaui\DataPieMaui.csproj -c Debug
dotnet build DataPieMaui\DataPieMaui.csproj -t:Run -c Debug
```

Alternatively, open `DataPieMaui.sln` in a Visual Studio installation that supports .NET 10 and .NET MAUI, select `DataPieMaui` as the startup project, and run the Windows target.

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
  -f net10.0-windows10.0.19041.0 `
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
