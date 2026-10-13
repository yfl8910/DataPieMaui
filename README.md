# DataPieMaui

English | [简体中文](README.zh-CN.md)

A Windows desktop workspace for querying, importing, exporting, designing tables, and converting SQL Server and SQLite data. Built with .NET 11 RC1 and .NET MAUI Blazor Hybrid, with a Blazor interface throughout the workspace and a separate query-results window.

<img width="1262" height="937" alt="image" src="https://github.com/user-attachments/assets/b3ca694d-e905-4772-8ec2-1fb8b9aeb46b" />

<img width="1260" height="938" alt="image" src="https://github.com/user-attachments/assets/9efb7965-92d6-4c95-9f90-fb3be5076f4e" />


## Features

- Connect to SQL Server using Windows or SQL Server authentication, open a local SQLite database, or create a blank SQLite database and connect automatically without saving its connection configuration.
- Navigate between Blazor pages without losing SQL or form inputs; review operations in shared confirmation dialogs and a persistent status bar.
- Save and manage database connections.
- Generate SELECT, UPDATE, and DELETE statements and execute SQL.
- Preview query results in a separate, resizable Blazor window with dynamic columns, typed sorting, pagination, and copying.
- Export queries and tables to Excel or CSV; export multiple tables or views to worksheets or separate CSV files.
- Import Excel, CSV, or a matching table from a SQLite database through Import a file; use Batch import for CSV/XLSX folders. Optional single-file and automatic batch length validation report SQL Server field-length problems.
- Design tables from Excel worksheets, review CREATE TABLE SQL, and permanently delete tables through separate Table design tabs.
- Run selected procedures without parameters, or run SQL Server procedures individually with typed parameter inputs, review, OUTPUT values, and RETURN codes.
- View and edit supported SQL Server and SQLite view/procedure definitions with diff review and explicit save confirmation.
- Convert SQL Server databases to SQLite, with warnings for unsupported view or procedure SQL.

## Requirements

- Windows x64. The project declares Windows 10 version 1809 as its minimum OS version.
- The .NET SDK `11.0.100-rc.1.26425.128` selected in `global.json` and the matching `maui-windows` workload for development. The project currently targets `net11.0-windows10.0.19041.0`.
- Microsoft Edge WebView2 Runtime for the entire application interface, including when running a published copy. If it is missing, the native host displays an installation message instead of opening the workspace.
- Access to NuGet when restoring dependencies for the first time.

The current project targets Windows only.

The repository currently uses .NET 11 RC1 packages: MAUI Controls and Blazor WebView `11.0.0-rc.1.26451.6`. Install the SDK selected by `global.json` and a compatible MAUI workload. Visual Studio must support that SDK and allow preview SDK usage where required.

Visual Studio needs the SDK installed system-wide (normally under `C:\Program Files\dotnet`), plus the matching MAUI workload. An SDK installed only in `tmp/net11/sdk/` is sufficient for explicit command-line validation but is not automatically discovered by Visual Studio. `global.json` selects an installed SDK; it does not install one.

If Visual Studio reports that the current SDK cannot target .NET 11, install the x64 SDK version above, run the workload installation command below, and restart Visual Studio. Check `dotnet --list-sdks` and `dotnet --version` from the repository root. This repository explicitly enables prerelease SDK selection through `global.json`.

## Build and run

Run these PowerShell commands from the repository root:

```powershell
dotnet workload install maui-windows
dotnet restore DataPieMaui\DataPieMaui.csproj
dotnet build DataPieMaui\DataPieMaui.csproj -c Debug
dotnet build DataPieMaui\DataPieMaui.csproj -t:Run -c Debug
```

Alternatively, open `DataPieMaui.sln` in a Visual Studio installation that supports .NET 11 RC1 and .NET MAUI, select `DataPieMaui` as the startup project, and run the Windows target.



## View object definitions

Use **View definition** beside a view on **Batch export**, or beside a procedure on **Stored procedures**. Only clicking this button reads the selected definition; initialization does not read SQL bodies. The viewing dialog supports **Format SQL**, **Show original**, **Copy SQL**, **Edit** for supported objects, and **Close**. Format SQL performs local, conservative whitespace formatting; it preserves SQL tokens, quoted literals/identifiers, comments and standalone GO batch separators. Copy SQL copies the current display. Show original restores the exact original definition. Viewing-state formatting does not change the database or editor draft. In editing mode, **Format SQL** formats the current draft and marks it as unsaved when the text changes; **Save changes…** and **Confirm save** are still required. Formatting is disabled in the full-SQL save review and during saving; failures retain the draft. Unsupported or incomplete text keeps its current display and shows a formatting error; formatting is not syntax validation. Closing releases the text; reopening reads the latest definition. SQL Server permissions or encryption may prevent viewing a definition. SQLite shows converted procedure scripts. Database conversion still loads the definitions it needs.

### Editing SQL Server definitions

For ordinary SQL Server views and T-SQL procedures, **Edit** opens a draft. **Save changes…** validates one schema-qualified definition and displays a text diff and the full SQL to submit. Only **Confirm save** changes the database. Canceling or closing a modified draft prompts before discarding it.

The editor accepts CREATE, ALTER, or CREATE OR ALTER for the selected object and submits ALTER without executing the procedure. It rejects extra batches, object renames, encrypted definitions, and CLR/native procedures. A lightweight declaration scanner checks the exact target, quoted identifiers, comments, batch separators and unsupported options without a parser package. Complete syntax validation is performed by SQL Server during the save transaction, so some syntax errors appear only after Confirm save. The scanner conservatively rejects extra view commands and bare GO/ENCRYPTION/NATIVE_COMPILATION/EXTERNAL keywords; quote identifiers that use these names. This is not a full T-SQL grammar parser. Indexed views and signed modules remain read-only to avoid losing indexes or signatures. ALTER permission is required. Definitions lacking explicit schema qualification must be qualified before saving.

Saving rechecks the original definition, modification version, and persisted SET options inside a transaction, preserves QUOTED_IDENTIFIER / ANSI_NULLS, and reloads the result before commit. Errors keep the draft for correction. Saves from this editor use a transaction-scoped application lock; external tools such as SSMS do not share that lock, so the optimistic check is not a guarantee against a simultaneous external DDL change between the check and ALTER. Coordinate concurrent schema changes. If a connection fails during commit, reopen the object to verify its actual state before retrying.

### Editing SQLite views and procedure scripts

SQLite uses the same draft, diff, confirmation and unsaved-change controls. Views accept one `CREATE VIEW` statement for the selected `main` view. Saving reserves a database write transaction, checks the original text and schema version, and recreates the view and its triggers atomically. Validation prepares SQL without evaluating it and checks that previously valid views still compile. A view with triggers must retain its column names and order. Failures roll back the replacement and retain the draft. Column compatibility does not guarantee unchanged business semantics of triggers; review those when changing a view's logic.

Converted procedures edit only the SQL body in their existing `StoredProcedures/*.txt` file. Saving checks the active database export, full-file hash, SQL syntax, object references and existing parameter/local names. SELECT/INSERT/UPDATE/DELETE/REPLACE and WITH statements are supported; DDL, transaction and connection commands are rejected. Validation uses EXPLAIN and does not run the procedure. The original metadata header is retained, including parameter defaults, local initializers and conversion warnings. New parameters and changes to conversion metadata are outside this editor's scope.

Each successful script save atomically replaces the file and retains the original as a uniquely named sibling `.bak` file. File permissions must allow this. In-process saves are serialized and the file hash is rechecked immediately before replacement; an external editor can still race that final check, so avoid simultaneous external edits. Backups are retained for manual recovery. Saved scripts are used on the next explicit procedure execution.

## Using the app

1. Open **Database connection** and connect to SQL Server or SQLite. In the **SQLite** tab, **Create blank database and connect** opens a save picker, creates a valid empty `.db` file and connects without saving its connection configuration. Existing files are never overwritten; choose a new filename. Canceling the picker preserves the current connection. Use **Table design** afterward to create tables.
2. Open **SQL query**, select a table to generate SQL or enter your own statement, and click **Preview query**.
3. View results in the separate window. Click column headers to sort, select a row to copy it, or copy all preview rows. Subsequent queries update the same window. Use **Open results** to reopen a closed result window without rerunning the query.
4. Use the export buttons for the full query result, or **Batch export** for selected tables and views.
5. Use **Import data** for its **Import a file** and **Batch import** tabs; **Clear data** empties the target table while keeping its structure.
6. Use **Stored procedures** to run procedures or inspect definitions, and **Convert to SQLite** to export a SQL Server database.
7. Open **Table design**, immediately after **Convert to SQLite**, and choose **Create table** or **Drop table**. Creation defaults to the Excel sheet name; deletion removes the structure and data after confirmation.

On **Import data**, select a **Target table** and click **Query data** to inspect it. On **Batch export**, each table/view has its own **Query data** button, independent of the export checkboxes. These actions reuse the results window, show the object name, and preserve your SQL editor and selections. Object previews have the same 1,000-row limit, sorting, paging, and copying as SQL previews.

### Import data layout and validation

The top card contains **Target table**, **Query data**, **Export template**, and **Clear data**. Below it, **Import a file** and **Batch import** switch the content inside one shared white card. The target table and file/folder paths remain available when switching tabs or navigating away.

**Import a file** supports XLSX, XLS, CSV, and a SQLite database containing the matching source table. SQL Server import normalizes blank strings and whitespace to NULL for numeric, date/time, and bit destination columns in both normal and length-validation modes; text columns retain their contents. NOT NULL constraints without a usable default can still reject missing values. SQLite import resets parameter types for each row. Blank strings and whitespace in numeric/date destination columns become NULL; text columns retain empty strings and whitespace. NOT NULL constraints still apply. A failed file is rolled back, and errors identify the target table and data-row number. **Validate lengths and import** additionally checks SQL Server destination field lengths. **Batch import** appends supported CSV/XLSX files sequentially and enables length validation. A failed batch retains data already imported; the status identifies the failing file. **Export template** writes the selected table's columns to an empty Excel workbook. **Clear data** requires confirmation and retains the table structure.

### Running SQL Server procedures with parameters

On **Stored procedures**, SQL Server procedures are grouped into **Without parameters** and **With parameters** tabs. Only the first tab offers selection and batch execution; the second offers **Run with parameters** per procedure. Classification reads only parameter existence alongside object names, excluding RETURN values. Defaulted and OUTPUT parameters count as parameters. Detailed parameter metadata loads only when the execution window is opened; definitions and parameter lists are not fetched at connection time. Reconnect after external signature changes to refresh classification. Enter values, select **NULL**, or choose **Omit** to let SQL Server use a default if one exists. An omitted required parameter produces a server error. Empty text remains an empty string. OUTPUT parameters support initial values; omitting one also means its output is not collected.

**Review and run…** displays the server/database, schema-qualified procedure and submitted values. Only **Confirm run** executes the command. The application passes typed SqlParameters, rechecks parameter metadata before execution, blocks duplicate submissions and window closure while running, and displays elapsed time, OUTPUT values and the procedure RETURN code separately from affected rows. Affected rows may be unavailable with NOCOUNT. Execution errors retain input; a failed or timed-out procedure may already have modified data, so inspect the database before retrying. Timeout is 120 seconds; execution is not automatically wrapped in an application transaction.

Supported inputs include common numeric, string, date/time, GUID and XML types. Decimal input currently uses .NET decimal's range/precision. Table-valued, binary, CLR/UDT and other unsupported parameter types disable this execution window. Complete value constraints and EXECUTE/metadata permissions are enforced by SQL Server. Result sets are not displayed in the parameter execution dialog. The existing batch button remains available without parameter configuration and now uses schema-qualified SQL Server names. SQLite converted scripts use their existing execution flow.

### SQL Server driver and dialog reliability

SQL Server uses Microsoft.Data.SqlClient 7.0.3. All application SQL Server connections use Encrypt=Mandatory and TrustServerCertificate=True by default, including saved connections with older settings. Traffic stays encrypted while server certificate validation is skipped. There is no certificate selection control. Updated saved settings are persisted after a successful connection. The Azure authentication extension is not included; the application supports SQL login and Windows authentication.

If opening a modal via JavaScript fails or times out, its controls remain available in a plain open dialog, preserving drafts. When the WebView disappears, pending confirmations are canceled. An executing procedure or definition save retains its operation lock until completion before the unavailable dialog is dismissed. SQLite errors retain their original stack trace.

### Table design: Create table

Connect to SQL Server or SQLite, open **Table design → Create table**, and select an XLSX or XLS file and worksheet. **Generate SQL** samples at most the first 1,000 data rows after the header (all rows if fewer) and uses the worksheet name as the table name and the first row as column names. Review the editable SQL, including the table name, then select **Create table** and confirm execution. The shared table lists refresh after successful execution. This creates structure only; use **Import data** to load rows afterward.

Empty cells, empty strings, and whitespace-only strings are ignored during inference; blank rows still count toward the 1,000-row limit. Native Excel dates and valid year-first date text (yyyy-M-d, yyyy/M/d, yyyy年M月d日, and supported year-first date/time forms) infer DATE on SQL Server when the sample contains only dates; any nonzero native time or explicitly written time in recognized date text promotes the column to DATETIME2. Date cells and recognized date text can coexist in one date column. Ambiguous day/month-first dates, numeric date codes, invalid dates, and columns containing other text remain text; SQLite dates use TEXT. Other nonblank text retains its text type, including leading-zero identifiers. Values beyond the sample may require wider numeric types or longer text columns; review the generated SQL before importing. SQL Server whole numeric columns use INT within its range and BIGINT when any value exceeds that range; SQLite uses INTEGER. Text, mixed-type, and empty columns use NVARCHAR(255) when the maximum invariant-text length within the sample is at most 255 UTF-16 code units, otherwise NVARCHAR(MAX); SQLite uses TEXT. Fractional numeric values use FLOAT/REAL. SQL Server pure dates use DATE, dates with time use DATETIME2, and booleans use BIT; SQLite dates use TEXT and booleans use INTEGER. A column named Id (case-insensitive) defaults to BIGINT IDENTITY(1,1) NOT NULL PRIMARY KEY on SQL Server, or INTEGER PRIMARY KEY AUTOINCREMENT on SQLite, regardless of its Excel values. Other columns initially allow NULL. Review the SQL to change this default. Empty or duplicate headers are rejected. Generated SQL never drops or overwrites an existing table. Changing the database connection clears the SQL draft so it can be regenerated for the new provider.

SQLite automatically creates the internal `sqlite_sequence` table when an AUTOINCREMENT table is created; it tracks the largest assigned IDs. This is expected. `INTEGER PRIMARY KEY` also assigns IDs automatically but may reuse deleted IDs; remove AUTOINCREMENT in the SQL editor only if that behavior is acceptable.

### Table design: Drop table

**Table design** has separate **Create table** and **Drop table** tabs. Switching tabs preserves the Excel selection and SQL draft. Import file/folder inputs remain on **Import data**.

**Table design → Drop table** uses a separate table selection. Select the table, click **Drop table**, and confirm the database and table in the final dialog. Execution timing starts after that confirmation. The operation permanently removes the table and all its data; cancellation leaves it intact. SQL Server enforces its dependency restrictions. SQLite incoming foreign keys are checked before deletion to prevent cascading data loss. After deletion the shared table lists refresh, invalid selections are cleared, and the query preview is discarded. Close any old result windows containing previously loaded snapshots if necessary.

### Query preview behavior

- Previews are limited to the first 1,000 rows and displayed in pages of 100. Paging and sorting operate on those preview rows only.
- Query preview uses a 30-second command timeout.
- **Copy all preview rows** copies the loaded preview in its current sort order, including column headers.
- Full exports execute the query again and are not limited to the preview rows. Data may have changed since the preview.
- The result table is read-only; SQL entered in the editor can still modify the database.

For operations with a save picker or confirmation dialog, elapsed-time measurement starts after the final confirmation, excluding time spent choosing a destination or waiting to confirm.

Only one operation can run at a time. Navigation and operation buttons are disabled while it runs; conversion cancellation remains available in the status bar. Confirmation dialogs support keyboard focus and Escape to cancel. Native Windows file pickers remain in use for opening files and choosing export destinations.

### Local data and conversion

Opening an existing SQLite database with **Connect and save** saves its configuration. Creating a blank database only creates and connects; it does not add a saved connection. Its destination selector returns a path without creating a placeholder file, and creation refuses to overwrite an existing file.

Saved connections are stored in `%LOCALAPPDATA%\DataPie\connections.db`. Connection strings are stored without application-level encryption and can contain database passwords; treat this file as private configuration.

SUBSTRING conversion uses compact `substr` when the start is a positive integer literal and the length is a nonnegative integer literal. The same-source suffix pattern SUBSTRING(value, n, LEN(value)-(n-1)) uses substr with rtrim to exclude trailing spaces; inputs shorter than the removed prefix return empty text rather than a SQL Server negative-length error. Other dynamic arguments and nonpositive starts retain compatibility handling.

YEAR/MONTH/DAY convert directly to integer casts of SQLite strftime. NULL or unparseable dates return NULL using SQLite behavior rather than forcing an overflow error; this differs from SQL Server error handling for invalid date text. LEFT with a nonnegative integer literal length uses compact substr.

When distributing a converted SQLite database, keep its generated `StoredProcedures` folder alongside it. SQLite procedures are handled as converted scripts; unsupported SQL is reported in warnings. A canceled or failed conversion may leave an incomplete output database, which should be regenerated before use.

## Publish for Windows x64

```powershell
dotnet publish DataPieMaui\DataPieMaui.csproj `
  -c Release `
  -f net11.0-windows10.0.19041.0 `
  -r win-x64 `
  --self-contained false `
  -p:WindowsAppSDKSelfContained=false `
  -o publish\win-x64
```

Launch `publish\win-x64\DataPieMaui.exe`. Distribute the **entire folder**, including `wwwroot` and the dependency files, rather than the executable alone.

This produces a framework-dependent, unpackaged application. The destination computer must have the matching x64 .NET 11 runtime, Windows App Runtime, and WebView2 Runtime installed. Application libraries (including MAUI, database/Excel libraries, and Windows API projections) still ship with the application. Publish to a new empty folder when switching from self-contained deployment so old runtime files are not left behind.

### Publish from Visual Studio

Open `DataPieMaui.sln` in Visual Studio with .NET 11 RC1 and the .NET MAUI workload installed. Stop debugging, set `DataPieMaui` as the startup project, and select **Release** and **Windows Machine**. Right-click the **DataPieMaui project** and select **Publish**, then use `FolderProfile`.

The project explicitly declares `Publish` and `FolderPublish` capabilities for its unpackaged Windows configuration. After changing the project file, reload the project (or reopen the solution) so Visual Studio refreshes the commands. The profile publishes a framework-dependent Windows x64 application to the destination configured in `FolderProfile.pubxml`; distribute the entire directory. The matching x64 .NET 11 runtime, Windows App Runtime, and WebView2 Runtime must already be installed on the destination computer.

If the MAUI tooling still disables the Publish command, the same profile can be run from Visual Studio's terminal:

```powershell
dotnet publish DataPieMaui/DataPieMaui.csproj -c Release -p:PublishProfile=FolderProfile
```

Microsoft's standard MAUI Windows Publish wizard creates MSIX packages. This project uses folder deployment, so do not enable MSIX packaging solely to activate that wizard.

### Self-contained publishing

To include the .NET and Windows App SDK runtimes, publish to a separate directory:

```powershell
dotnet publish DataPieMaui/DataPieMaui.csproj -c Release -r win-x64 --self-contained true -p:WindowsAppSDKSelfContained=true -o publish/win-x64-self-contained
```

WebView2 Runtime is still required. Close running copies of the app before overwriting their files. `FolderProfile` currently uses framework-dependent deployment and contains an absolute destination path; override it with `-o publish/win-x64` when publishing from a different checkout.

## Regression checks

The test project is a console-based regression runner. Run it with:

```powershell
dotnet run --project DataPieMaui.Tests\DataPieMaui.Tests.csproj -c Release
```

Checks cover SQLite access, connection management, query previews, import/export and length validation, Excel-based table creation and Id identity defaults, table deletion and foreign-key protection, procedure parameters, definition editing, conversion, cancellation, result snapshots, typed sorting, copy formatting, result-component updates, and desktop clipboard delegation. Workspace checks also exercise confirmation and cancellation, concurrent-operation protection, state retention, and rendering of the actual Razor pages. These checks use a test desktop adapter; they do not automate native window/file-picker interactions or require a live SQL Server instance.

To also write static page previews to the ignored `tmp/ui-review/` directory, run:

```powershell
dotnet run --project DataPieMaui.Tests\DataPieMaui.Tests.csproj -c Release -- --render-ui
```

These previews are render snapshots, not an interactive web version of the desktop application.

## UI architecture

- `Controls/QueryResultsHostPage.cs` hosts the independent result window; `Components/QueryResults.razor` owns result rendering and interactions. Clipboard access is provided by `IDesktopServices.CopyTextAsync`, so the result component can be rendered and tested without MAUI.
- `MainPage.cs` hosts the main `BlazorWebView` and provides a native fallback when WebView2 is missing.
- `Components/Workspace.razor` provides navigation and the status bar; `Components/Pages/` contains the business pages, and `Components/Shared/` contains shared controls and dialogs.
- All business pages, including login and connection management, keep their markup and page-specific workflows together in a single `.razor` file using an `@code` block. The static About page needs no code block.
- `WorkspaceController` composes shared services and coordinates navigation and native-window lifecycle. Pages use `WorkspacePageBase` to access shared state and the operation runner. The connection page initializes saved connections once per workspace; revisiting the page preserves loaded configuration and operation status.
- `Services/Shared/WorkspaceContext` owns session state and the shared capabilities for one workspace. Shared services survive navigation; `WorkspaceForm` retains input values when page components are recreated. `DatabaseConversionService` owns conversion execution, progress, and cancellation so the status bar and native close handler do not depend on a page instance.
- `Services/Shared/WorkspaceOperationRunner` provides the single operation gate, error handling, and timing contract used by every page. `QueryPreviewService` shares table/view and SQL result loading, `WorkspaceContext.RefreshSchemaAsync` refreshes table lists after creation/deletion, and `FileNames` shares export filename normalization. `ExcelTableSql` reads worksheets and generates provider-specific table definitions.
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
