# DataPieMaui

English | [简体中文](README.zh-CN.md)

A Windows desktop workspace for querying, importing, exporting, designing tables, and converting SQL Server and SQLite data. Built with .NET 11 RC1 and .NET MAUI Blazor Hybrid, with a Blazor interface throughout the workspace, a unified Table editor page for read-only query previews and independent editable table windows.

<img width="1262" height="937" alt="image" src="https://github.com/user-attachments/assets/b3ca694d-e905-4772-8ec2-1fb8b9aeb46b" />

<img width="1260" height="938" alt="image" src="https://github.com/user-attachments/assets/9efb7965-92d6-4c95-9f90-fb3be5076f4e" />


## Features

- Connect to SQL Server using Windows or SQL Server authentication, open a local SQLite database, or create a blank SQLite database and connect automatically without saving its connection configuration.
- Navigate between Blazor pages without losing SQL or form inputs; review operations in shared confirmation dialogs and a persistent status bar.
- Save and manage database connections.
- Browse tables, views, and stored procedures in a dedicated Database schema page, with search, type filtering, and column details.
- Generate SELECT, UPDATE, and DELETE statements and execute SQL.
- Preview query results in a separate, resizable Blazor window with dynamic columns, typed sorting, pagination, and copying.
- Export queries and tables to Excel or CSV; export multiple tables or views to worksheets or separate CSV files.
- Import Excel, CSV, or a matching table from a SQLite database through Import a file; use Batch import for CSV/XLSX folders. Optional single-file and automatic batch length validation report SQL Server field-length problems.
- Design tables from Excel worksheets, review CREATE TABLE SQL, and open a separate table data editor from SQL query, Import data or Database schema.
- Add, edit, and delete table records in independent editor windows, with full-table paging, current-page selection, modal dialogs, and conflict checks; drop entire tables separately through Table design.
- Run selected procedures without parameters, or run SQL Server procedures individually with typed parameter inputs, review, OUTPUT values, and RETURN codes.
- View and edit supported SQL Server and SQLite view/procedure definitions with diff review and explicit save confirmation.
- Convert SQL Server databases to SQLite, with warnings for unsupported view or procedure SQL.
- Export SQL Server schemas, supported tables, primary keys, defaults, views and stored procedures to a creation script, with skipped-object and dependency warnings.
- Generate a SQL Server schema/procedure script from SQLite without copying rows or executing it against a server.

## Requirements

- Windows x64. The project declares Windows 10 version 1809 as its minimum OS version.
- The .NET SDK `11.0.100-rc.1.26425.128` selected in `global.json` and the matching `maui-windows` workload for development. The project currently targets `net11.0-windows10.0.19041.0`.
- Microsoft Edge WebView2 Runtime for the entire application interface, including when running a published copy. If it is missing, the native host displays an installation message instead of opening the workspace.
- Access to NuGet when restoring dependencies for the first time.

The current project targets Windows only.

The SDK is pinned to .NET 11 RC1; MAUI Controls and Blazor WebView packages are `11.0.0-rc.2.26505.5`, and Microsoft.Extensions.Logging.Debug is `11.0.0-rc.2.26504.105`. Install the SDK selected by `global.json` and a compatible MAUI workload. Visual Studio must support that SDK and allow preview SDK usage where required.

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

Use **View definition** beside a view on **Batch export**, or beside a procedure on **Stored procedures**. **Database schema** also offers a read-only definition viewer; the editing workflow described below is available through the first two pages. Only clicking this button reads the selected definition; initialization does not read SQL bodies. Viewing shows the original SQL and supports **Copy SQL**, **Edit** for supported objects, and **Close**. **Format SQL** is available only while editing the draft; it changes whitespace and indentation locally, adds a single blank line between statements and after completed blocks, preserves SQL tokens, and never saves automatically. Reformatting does not accumulate blank lines. Failures retain the draft. Saving still requires **Save changes…** and **Confirm save**; the save review has no formatting controls. Closing releases the text; reopening reads the latest definition. SQL Server permissions or encryption may prevent viewing a definition. SQLite shows converted procedure scripts. Database conversion still loads the definitions it needs.

### Editing SQL Server definitions

For ordinary SQL Server views and T-SQL procedures, **Edit** opens a draft. **Save changes…** validates one schema-qualified definition and displays a text diff and the full SQL to submit. Only **Confirm save** changes the database. Canceling or closing a modified draft prompts before discarding it.

The editor accepts CREATE, ALTER, or CREATE OR ALTER for the selected object and submits ALTER without executing the procedure. It rejects extra batches, object renames, encrypted definitions, and CLR/native procedures. A lightweight declaration scanner checks the exact target, quoted identifiers, comments, batch separators and unsupported options without a parser package. Complete syntax validation is performed by SQL Server during the save transaction, so some syntax errors appear only after Confirm save. The scanner conservatively rejects extra view commands and bare GO/ENCRYPTION/NATIVE_COMPILATION/EXTERNAL keywords; quote identifiers that use these names. This is not a full T-SQL grammar parser. Indexed views and signed modules remain read-only to avoid losing indexes or signatures. ALTER permission is required. Definitions lacking explicit schema qualification must be qualified before saving.

Saving rechecks the original definition, modification version, and persisted SET options inside a transaction, preserves QUOTED_IDENTIFIER / ANSI_NULLS, and reloads the result before commit. Errors keep the draft for correction. Saves from this editor use a transaction-scoped application lock; external tools such as SSMS do not share that lock, so the optimistic check is not a guarantee against a simultaneous external DDL change between the check and ALTER. Coordinate concurrent schema changes. If a connection fails during commit, reopen the object to verify its actual state before retrying.

### Editing SQLite views and procedure scripts

SQLite uses the same draft, diff, confirmation and unsaved-change controls. Views accept one `CREATE VIEW` statement for the selected `main` view. Saving reserves a database write transaction, checks the original text and schema version, and recreates the view and its triggers atomically. Validation prepares SQL without evaluating it and checks that previously valid views still compile. A view with triggers must retain its column names and order. Failures roll back the replacement and retain the draft. Column compatibility does not guarantee unchanged business semantics of triggers; review those when changing a view's logic.

Converted procedures edit only the SQL body in their existing `StoredProcedures/*.txt` file. Saving checks the active database export, full-file hash, SQL syntax, object references and existing parameter/local names. SELECT/INSERT/UPDATE/DELETE/REPLACE and WITH statements are supported; DDL, transaction and connection commands are rejected. Validation uses EXPLAIN and does not run the procedure. The original metadata header is retained, including parameter defaults, local initializers and conversion warnings. New parameters and changes to conversion metadata are outside this editor's scope.

Each successful script save atomically replaces the file and retains the original as a uniquely named sibling `.bak` file. File permissions must allow this. In-process saves are serialized and the file hash is rechecked immediately before replacement; an external editor can still race that final check, so avoid simultaneous external edits. Backups are retained for manual recovery. Saved scripts are used on the next explicit procedure execution.

## Using the app

The sidebar follows this order. Database operations become available after connecting.

| Menu | Purpose |
| --- | --- |
| Database connection | Connect, create a blank SQLite database, or manage saved connections |
| Database schema | Browse object categories and column metadata; view definitions on demand |
| SQL query | Generate SQL, preview results, run statements, and export |
| Import data | Import a file/folder, export a template, or clear table data |
| Batch export | Export selected tables/views to Excel or CSV |
| Stored procedures | Run procedures and inspect/edit supported definitions |
| Database migration | Convert SQL Server to SQLite, export SQL Server object scripts, or generate SQL Server scripts from SQLite |
| Table design | Create table structure from Excel or drop an entire table |
| About | Application information |

1. Open **Database connection** and connect to SQL Server or SQLite. In the **SQLite** tab, **Create blank database and connect** opens a save picker, creates a valid empty `.db` file and connects without saving its connection configuration. Existing files are never overwritten; choose a new filename. Canceling the picker preserves the current connection. Use **Table design** afterward to create tables.
2. Open **SQL query**, select a table to generate SQL or enter your own statement, and click **Preview query**.
3. View results in the separate window. Click column headers to sort, select a row to copy it, or copy all preview rows. Subsequent queries update the same window. Use **Open Table editor** to reopen the read-only preview without rerunning the query.
4. Use the export buttons for the full query result, or **Batch export** for selected tables and views.
5. Use **Import data** for its **Import a file** and **Batch import** tabs; **Clear data** empties the target table while keeping its structure.
6. Use **Stored procedures** to run procedures or inspect definitions, and **Database migration** to export a SQL Server database.
7. Open **Table design**, immediately after **Database migration**, and choose **Create table** or **Drop table**. **Database schema** follows **Database connection**. Creation defaults to the Excel sheet name; open a table editor from **Import data → Target table → Query&Edit** or **Database schema → select a table → Query&Edit**.

On **Import data**, select a **Target table** and click **Query&Edit** to open an independent Table editor, with add/edit/delete dialogs and full-table paging. The import inputs and existing query preview are preserved. Tables without primary keys can also be edited when a record can be located safely; SQL Server target choices display schema-qualified names, so same-named tables in different schemas can be selected independently. On **Batch export**, each table/view retains its own **Query data** button, independent of the export checkboxes. Export object previews reuse the results window and have the same 1,000-row limit, sorting, paging, and copying as SQL previews.

**Saved connections** retains its connection selector, **Connect**, and **Delete** buttons. **Connection details** shows a read-only, selectable connection string; it does not offer manual name/type/string editing, Test, Save as new, or Update selected. Removing a saved connection removes its configuration, not the database.

Successful exports append the output file's full path to the bottom status message alongside the completion information. Batch CSV export lists the generated file paths. Shared workspace failures display the full exception, including inner exceptions and stack traces, and send it to Trace; this does not create a dedicated on-disk log file.

On **SQL query**, **Query&Edit** beside the table selector opens the selected table in the same independent editor. It uses the selected table, not the SQL text or query result, and preserves the SQL draft, preview and selection. SQL Server choices distinguish schemas; generated SELECT/UPDATE/DELETE statements and whole-table exports use the selected schema-qualified table.

### Import data layout and validation

The top card contains **Target table**, **Query&Edit**, **Export template**, and **Clear data**. The selected SQL Server schema/table identity is used consistently for editing, file/folder import, template export and clearing data. Excel worksheets and SQLite source tables still match the original unqualified table name. Below it, **Import a file** and **Batch import** switch the content inside one shared white card. The target table and file/folder paths remain available when switching tabs or navigating away.

**Import a file** supports XLSX, XLS, CSV, and a SQLite database containing the matching source table. SQL Server import normalizes blank strings and whitespace to NULL for numeric, date/time, and bit destination columns in both normal and length-validation modes; text columns retain their contents. NOT NULL constraints without a usable default can still reject missing values. SQLite import resets parameter types for each row. Blank strings and whitespace in numeric/date destination columns become NULL; text columns retain empty strings and whitespace. NOT NULL constraints still apply. A failed SQLite file import is rolled back, and its errors identify the target table and data-row number. **Validate lengths and import** additionally checks SQL Server destination field lengths. **Batch import** appends supported CSV/XLSX files sequentially and enables length validation. A failed batch retains data already imported; the status identifies the failing file. **Export template** writes the selected table's columns to an empty Excel workbook. **Clear data** requires confirmation and retains the table structure.

The two single-file buttons are distinct: **Import file** uses the normal import path, while **Validate lengths and import** checks SQL Server `nvarchar`/`nchar` lengths as rows are read. A length failure reports the source file, data-row number excluding the header, column, actual length, and allowed length in UTF-16 units. Values are not truncated. SQL Server bulk import may retain records written before a failure; SQLite file imports use a rollback transaction.

### Running SQL Server procedures with parameters

On **Stored procedures**, SQL Server procedures are grouped into **Without parameters** and **With parameters** tabs. Only the first tab offers selection and batch execution; the second offers **Run with parameters** per procedure. Classification reads only parameter existence alongside object names, excluding RETURN values. Defaulted and OUTPUT parameters count as parameters. Detailed parameter metadata loads only when the execution window is opened; definitions and parameter lists are not fetched at connection time. Reconnect after external signature changes to refresh classification. Enter values, select **NULL**, or choose **Omit** to let SQL Server use a default if one exists. An omitted required parameter produces a server error. Empty text remains an empty string. OUTPUT parameters support initial values; omitting one also means its output is not collected.

**Review and run…** displays the server/database, schema-qualified procedure and submitted values. Only **Confirm run** executes the command. The application passes typed SqlParameters, rechecks parameter metadata before execution, blocks duplicate submissions and window closure while running, and displays elapsed time, OUTPUT values and the procedure RETURN code separately from affected rows. Affected rows may be unavailable with NOCOUNT. Execution errors retain input; a failed or timed-out procedure may already have modified data, so inspect the database before retrying. Timeout is 120 seconds; execution is not automatically wrapped in an application transaction.

Supported inputs include common numeric, string, date/time, GUID and XML types. Decimal input currently uses .NET decimal's range/precision. Table-valued, binary, CLR/UDT and other unsupported parameter types disable this execution window. Complete value constraints and EXECUTE/metadata permissions are enforced by SQL Server. Result sets are not displayed in the parameter execution dialog. The existing batch button remains available without parameter configuration and now uses schema-qualified SQL Server names. SQLite converted scripts use their existing execution flow.

### SQL Server driver and dialog reliability

SQL Server uses Microsoft.Data.SqlClient 7.0.3. All application SQL Server connections use Encrypt=Mandatory and TrustServerCertificate=True by default, including saved connections with older settings. Traffic stays encrypted while server certificate validation is skipped. There is no certificate selection control. Updated saved settings are persisted after a successful connection. The Azure authentication extension is not included; the application supports SQL login and Windows authentication.

If opening a modal via JavaScript fails or times out, its controls remain available in a plain open dialog, preserving drafts. When the WebView disappears, pending confirmations are canceled. An executing procedure or definition save retains its operation lock until completion before the unavailable dialog is dismissed. SQLite errors retain their original stack trace.

### Database schema

**Database schema** is an independent sidebar page immediately after **Database connection**. Its compact toolbar places search, object-type filtering, and **Refresh schema** on one row; narrow windows wrap the controls. The top area does not repeat the database name or overall object totals.

Objects are grouped into **Tables**, **Views**, and **Stored procedures**, with a matching-object count for each category. Names include the schema where available and sort by schema and name within each category. Search matches object names and schemas; the type filter limits the categories shown.

Select a table to inspect column names, types, primary keys, auto-generated IDs, nullability, computed fields, and defaults. SQL Server character lengths, decimal precision/scale, and temporal scale are formatted from metadata; spatial columns are included. SQLite defaults, generated columns, and rowid identity details load only when a table is selected. Indexes, unique/check constraints, and foreign key details are explicitly marked as not loaded, rather than absent.

Table structure is read-only here: column renaming, type changes, and constraint editing are not implemented. Select a table and click **Query&Edit** to open its data editor; this is also available from Import data. **View definition** loads one view or procedure definition into a read-only dialog on request; opening this page does not load SQL bodies. For supported definition editing, use the definition actions on **Batch export** or **Stored procedures**. SQLite procedure parameter metadata is shown as not loaded. **Refresh schema** reloads the cached object list and clears the selected details.

The table action bar shows **Import file → Export Excel → Query&Edit → Data analysis → Clear Data → DROP TABLE**. Import opens a dialog bound to the selected table and appends `.xlsx`, `.xls`, `.csv`, or `.db` data using the existing import pipeline; length validation is optional and off by default. SQLite source databases must contain a table with the same name. Export Excel exports all table rows using MiniExcel and reports the full output path in the status bar. Clear Data requires confirmation and removes all rows while keeping the table structure, using the same operation as Import data. DROP TABLE requires confirmation and removes both structure and data; SQL Server targets are schema-qualified, and SQLite tables referenced by foreign keys are rejected. Successful deletion clears the selected details and stale query results and refreshes the schema. Shared table actions and import/button components are reused by the existing import, query export and table-design deletion workflows.

View details also provide **Query data**, reusing Table editor in read-only mode with a 1,000-row preview limit and truncation notice. SQL Server views are addressed by schema and name, including from Batch export. Procedure details provide **Run** for the current procedure only: SQL Server procedures with parameters use the existing parameter input, review and confirmation dialog; other procedures require execution confirmation. SQLite conversion warnings are retained. These actions share the existing query and procedure execution services and operation gate; running a procedure here does not change the Stored procedures page's checked items. Parameters are loaded only when Run is clicked.

The import dialog supports **Import a file** and **Batch import** tabs. Each source input and browse button share one row; switching tabs retains both paths and the target table. Batch import appends the folder's CSV and XLSX files using the same sequential importer, progress and partial-failure reporting as Import data. Length validation in this dialog remains optional and off by default for both modes; the existing Import data batch action retains its length validation.

### Table design: Create table

Connect to SQL Server or SQLite, open **Table design → Create table**, and select an XLSX or XLS file and worksheet. **Generate SQL** samples at most the first 1,000 data rows after the header (all rows if fewer) and uses the worksheet name as the table name and the first row as column names. Review the editable SQL, including the table name, then select **Create table** and confirm execution. The shared table lists refresh after successful execution. This creates structure only; use **Import data** to load rows afterward.

Empty cells, empty strings, and whitespace-only strings are ignored during inference; blank rows still count toward the 1,000-row limit. Native Excel dates and valid year-first date text (yyyy-M-d, yyyy/M/d, yyyy年M月d日, and supported year-first date/time forms) infer DATE on SQL Server when the sample contains only dates; any nonzero native time or explicitly written time in recognized date text promotes the column to DATETIME2. Date cells and recognized date text can coexist in one date column. Ambiguous day/month-first dates, numeric date codes, invalid dates, and columns containing other text remain text; SQLite dates use TEXT. Other nonblank text retains its text type, including leading-zero identifiers. Values beyond the sample may require wider numeric types or longer text columns; review the generated SQL before importing. SQL Server whole numeric columns use INT within its range and BIGINT when any value exceeds that range; SQLite uses INTEGER. Text, mixed-type, and empty columns use NVARCHAR(255) when the maximum invariant-text length within the sample is at most 255 UTF-16 code units, otherwise NVARCHAR(MAX); SQLite uses TEXT. Fractional numeric values use FLOAT/REAL. SQL Server pure dates use DATE, dates with time use DATETIME2, and booleans use BIT; SQLite dates use TEXT and booleans use INTEGER. A column named Id (case-insensitive) defaults to BIGINT IDENTITY(1,1) NOT NULL PRIMARY KEY on SQL Server, or INTEGER PRIMARY KEY AUTOINCREMENT on SQLite, regardless of its Excel values. Other columns initially allow NULL. Review the SQL to change this default. Empty or duplicate headers are rejected. Generated SQL never drops or overwrites an existing table. Changing the database connection clears the SQL draft so it can be regenerated for the new provider.

SQLite automatically creates the internal `sqlite_sequence` table when an AUTOINCREMENT table is created; it tracks the largest assigned IDs. This is expected. `INTEGER PRIMARY KEY` also assigns IDs automatically but may reuse deleted IDs; remove AUTOINCREMENT in the SQL editor only if that behavior is acceptable.

### Table design: Drop table

**Table design → Drop table** retains its separate table selection and **Drop table** button. Confirm before permanently deleting the table structure and all data. Cancel leaves it intact. SQLite incoming foreign keys block deletion; SQL Server enforces database dependencies. Successful deletion refreshes the table lists and clears stale selections and query previews. The Table editor window remains a separate feature for editing records.

### Data analysis

Choose **Database schema → select a table → Data analysis** to open an independent read-only window bound to that database and schema-qualified table. Opening the window loads field metadata only. **Group by** and **Calculations** open separate settings dialogs; the main page shows a compact summary. The grouping dialog searches and selects text, numeric, date/time, boolean and other supported scalar fields, preserves selection order, and lets you move fields up/down. Binary and unsupported fields are visible but disabled with an explanation. Dates group by their original values. The calculation dialog adds named **Sum**, **Average**, **Min** and/or **Max** aggregates for numeric fields. Both dialogs isolate drafts until **Apply**; **Cancel** or Escape leaves the applied configuration intact. **Record count** counts all matching source records, including records with NULL measures. Leave grouping empty to calculate a total. The shared **Search conditions (AND)** component supports **Add condition**, independent field/value rows, removing rows and **Clear search**. For example, Customer contains A AND Product contains P must both match. Each text condition treats wildcard characters literally; optional All text fields searches any text column for that individual condition, while conditions are still combined with AND. Empty values are ignored. The same conditions filter source rows before grouping and apply to results, overall totals and complete Excel export. No active conditions includes all source rows. Text case matching follows the database rules. Date and boolean fields can group but cannot be numeric measures; binary and unsupported types cannot group.

Aggregation runs against all matching database rows. Up to 1,000 result groups are previewed, with sorting, 100-group pages and clipboard copying through the shared table grid. This limit does not restrict source rows participating in calculations. **Export Excel** reruns the last analyzed settings against the current source and exports all groups, showing the full file path. Changed settings must be analyzed before exporting; the source may change between preview and export. **Reset** clears settings and results. Export uses a temporary file before replacing the destination.

Grouped results include a sticky **Total** row aligned with aggregate, record-count and formula columns. It is calculated by a separate ungrouped query using the same source filter, across all matching records, including groups outside the 1,000-group preview. SUM and record count show overall totals; AVG/MIN/MAX are recomputed over the original matching records; formulas use those overall aggregates (for example, total revenue divided by total quantity). Numeric totals use thousands separators and two decimal places, and NULL stays NULL. Pagination and sorting do not change totals. Changed/unapplied settings hide the previous total row; reset and successful template application clear it with the old results. Ungrouped analysis already represents the overall result and does not repeat a Total row. The Total row is a preview summary and is not appended to copied preview rows or the existing grouped Excel export.

Named aggregate formulas support two aggregates, or an aggregate and a decimal constant, using +, −, × or ÷. For weighted average price, add SUM(Revenue) as Total revenue and SUM(Quantity) as Total quantity, then define Average price = Total revenue ÷ Total quantity. Filtering happens first, grouping/aggregation second, and formulas last. Formula references survive aggregate renaming; missing references, duplicate names and unsupported operators are rejected. Formula-to-formula references and arbitrary SQL expressions are not supported. Division by zero or NULL returns NULL, and integer division retains fractional results. Preview and Excel export execute the same aggregate/formula query. Numeric cells display thousands separators and two decimal places without rounding the stored results or exported values.

Analysis templates are stored in a separate `AnalysisTemplates` table in the application's existing `%LocalAppData%\DataPie\connections.db`; saved connections are unaffected. The **Template** list is filtered by the original table name (without SQL Server schema/database), so compatible same-named tables across databases can reuse settings. **Save template** saves the currently applied grouping order, aggregate names/identifiers, formulas/constants and record-count option under the entered name. Different names create separate templates; the same table and template name overwrites the existing settings immediately, preserving its ID. Names are trimmed and limited to 100 characters, with SQLite NOCASE comparisons for uniqueness. No source connection strings, result rows or search conditions are saved. Settings JSON includes a format version.

Select a template and click **Apply template** to validate fields, numeric roles and formula references before replacing the current settings. Missing/incompatible fields are listed, and failed validation preserves the current analysis. Successful application retains the current search conditions and immediately runs **Analyze**. The main page provides template selection, **Apply template**, **Save**, and **Manage templates**. **Save** updates the selected template or prompts for a name when no template is selected. The reusable template manager displays the saved Group by, aggregate, formula and record-count settings, supports editing them with the existing settings dialogs, and saves changes with **Update selected**. It also supports confirmed deletion. Managing saved templates does not change the active analysis settings or results. **Delete template** requires confirmation and removes only the saved template, retaining current analysis settings and source data. **Reset** clears the current template selection along with analysis settings/results. Invalid or unsupported-version payloads are rejected without partially applying them.

Data analysis Excel exports use a dedicated streaming writer, leaving shared ExcelIO exports unchanged. Workbooks have a bold colored header, frozen first row, filtering and sampled column widths (first 200 rows, capped at 45 characters). Numeric calculation cells use thousands separators and two decimals; numeric columns whose header contains `%` use one decimal percentage formatting. Grouping fields retain their value types. All matching groups are exported, with no extra Total row. Export uses a temporary file and replaces the destination only after success. Excel row, column and cell-text limits are reported as errors.

Numeric aggregates ignore NULL, and an all-NULL sum/average/min/max stays NULL. SQL Server integer sums are widened to DECIMAL(38,0), decimal/money scales are retained, fractional averages are preserved, and counts use COUNT_BIG. Finite numeric ranges can still overflow and are reported as errors. SQLite uses native numeric precision; nonnumeric values in selected numeric fields of matching rows are rejected instead of being silently treated as zero. SQLite integer sum overflow is reported rather than converted to an imprecise floating total. Unsupported types cannot be chosen; grouping lists show them disabled with reasons. SQL names are quoted from metadata and search values use bound parameters. Analysis and export do not modify the source database. Windows stay bound to their source even if the main workspace changes connections; active analysis/export operations block closing their window or the application.

### Table editor

All query and table data windows use this page and host. SQL queries and Query data open read-only previews holding up to 1,000 rows (plus one reader probe to detect truncation), without loading table editing metadata or counting the full result. Sorting, searching and copying operate only on that snapshot; paging does not read more database rows. The preview window updates for subsequent queries and clears when the workspace clears its result. It has no add/edit/delete actions. Query&Edit opens a separate table session with full-table database paging, search, row counts and existing edit dialogs. Editing sessions remain bound to their original database and are not replaced by a new query preview.

There is no standalone Table Edit menu. Open a **Table editor** window using **SQL query → Table → Query&Edit**, **Import data → Target table → Query&Edit**, or select a table on **Database schema** and click **Query&Edit**. SQL Server table choices include the schema. Each window stays bound to its original database and table, even if the main workspace changes connections.

The editor reads 100 records per page and can page through the entire table; it does not share the query preview's 1,000-row limit. Rows are ordered by the primary key or discovered unique identifier when available. Composite primary keys are supported. SQL Server tables without primary keys use an enabled, unfiltered unique index with nonnullable key columns when available. Ordinary keyless SQLite tables use an unshadowed internal rowid, which is kept out of the grid and export. Otherwise original comparable column values locate records; zero or multiple affected rows roll back the whole edit/delete transaction. Unsupported fields remain read-only without disabling other editable fields. If no usable comparison columns or rowid exist, editing remains unavailable. The editor does not offer arbitrary SQL. Its shared AND search component accepts multiple independent conditions: text contains, numbers/booleans match exactly, and yyyy-MM-dd matches a date range for DateTime columns. Search runs against the entire table before counts/paging; paging, refresh and saving preserve applied conditions. Invalid values retain the prior results and active filters. Clear search returns to the full table. The same component in read-only mode filters only the loaded preview, retaining sorting and copying of matches.

#### Add records

Click **Add record** to open a new-record dialog. Auto-generated identities, computed columns, rowversion fields, and other read-only fields cannot be entered; manual primary keys can be supplied for new records. Required fields must be supplied. Untouched optional fields are omitted so the database applies their default or NULL. Entering and then clearing an optional text field explicitly submits an empty string.

**Save** performs a parameterized insert in a transaction. Failed inserts retain the input and show the error inside the dialog. Success closes the dialog and reloads the first page; the new record may be on a later page depending on its key. Closing a modified draft asks whether to discard it.

#### Edit records

Select one record and click **EDIT** to open a field-editing dialog. Primary keys, selected unique-index keys, generated fields, binary fields and unsupported fields remain read-only. Fields scroll while **Save** and **Cancel** stay visible. Both Edit record and New record dialogs support dragging the title bar, resizing from the lower-right corner, and maximize/restore. Position and size are remembered within the current Table editor window and clamped when the window shrinks; dialogs cannot move outside it. Focus the title bar or resize handle and use arrow keys for keyboard movement/resizing (Shift uses larger steps). Saving disables these controls. Delete confirmation remains centered and compact. Dates use yyyy-MM-dd and decimals use a dot. Save failures retain input and display the error in the dialog.

There is no **Set to NULL** checkbox. An untouched NULL field stays NULL, and entering a value replaces it. Clearing an existing text value saves an empty string; clearing numeric/date fields does not automatically set NULL and may fail validation. Existing non-NULL fields cannot explicitly be set to NULL through this editor.

#### Select and delete records

Use the checkbox at the top of the Select column to select or clear the **current page**. A partial selection shows a mixed checkbox state. Individual row selection is also available; loading another page clears the selection.

**Delete selected** opens a confirmation dialog with the database/table, record count, and primary-key summary. Confirming deletes records, not the table structure; database cascades and triggers still apply. A successful delete refreshes the page and moves back one page if the current page becomes empty.

#### Drafts and concurrent changes

Updates and deletes compare original row values to detect conflicts, and a conflict rolls back the operation. Cancel the draft and refresh after another tool changes the record. Dialogs prevent background interaction; clicking the backdrop does not close them. Escape follows the same close checks as Cancel. Saving disables duplicate submissions and closing until the operation finishes.

Drafts block paging and refresh. Closing an editor with a modified draft asks whether to discard it; finish or cancel drafts before closing the main application. Existing query-preview snapshots and other editor windows are not automatically refreshed after a write.

### Query preview behavior

- Previews are limited to the first 1,000 rows and displayed in pages of 100. Paging and sorting operate on those preview rows only.
- Query preview uses a 30-second command timeout.
- **Copy all preview rows** copies the loaded preview in its current sort order, including column headers.
- Full exports execute the query again and are not limited to the preview rows. Data may have changed since the preview.
- The result table is read-only; SQL entered in the editor can still modify the database.

For operations with a save picker or confirmation dialog, elapsed-time measurement starts after the final confirmation, excluding time spent choosing a destination or waiting to confirm.

The main workspace serializes its operations and disables navigation and operation buttons while one runs; conversion cancellation remains available in the status bar. Each independent table editor has its own operation guard and remains bound to its original connection. Confirmation dialogs support keyboard focus and Escape to cancel. Native Windows file pickers remain in use for opening files and choosing export destinations.

### Local data and conversion

Opening an existing SQLite database with **Connect and save** saves its configuration. Creating a blank database only creates and connects; it does not add a saved connection. Its destination selector returns a path without creating a placeholder file, and creation refuses to overwrite an existing file.

Saved connections are stored in `%LOCALAPPDATA%\DataPie\connections.db`. Connection strings are stored without application-level encryption and can contain database passwords; treat this file as private configuration.

SUBSTRING conversion uses compact `substr` when the start is a positive integer literal and the length is a nonnegative integer literal. The same-source suffix pattern SUBSTRING(value, n, LEN(value)-(n-1)) uses substr with rtrim to exclude trailing spaces; inputs shorter than the removed prefix return empty text rather than a SQL Server negative-length error. Other dynamic arguments and nonpositive starts retain compatibility handling.

YEAR/MONTH/DAY convert directly to integer casts of SQLite strftime. NULL or unparseable dates return NULL using SQLite behavior rather than forcing an overflow error; this differs from SQL Server error handling for invalid date text. LEFT with a nonnegative integer literal length uses compact substr.

When distributing a converted SQLite database, keep its generated `StoredProcedures` folder alongside it. SQLite procedures are handled as converted scripts; unsupported SQL is reported in warnings. A canceled or failed conversion may leave an incomplete output database, which should be regenerated before use.

### Database migration: SQL Server object script

When connected to SQL Server, **Database migration** retains **Choose destination and convert** for SQLite conversion and adds a separate **Generate SQL Server script** button. Choose a `.sql` destination to export object creation SQL from the current source database. Export requires SQL Server 2016 or later and database-level `VIEW DEFINITION` permission. Metadata is read on demand, without selecting table rows, changing source objects or executing the generated script.

The script includes non-system schemas and supported tables with field types, lengths, precision/scale, nullability, identity seed/increment, and computed expressions. Column COLLATE is emitted only when it differs from the source database default; other text columns inherit the target database default collation. Primary keys preserve their name, column order, ascending/descending keys and clustered/nonclustered form; creating a primary key necessarily creates its backing index. Defaults preserve their constraint name and expression. Views and procedures preserve their SQL bodies and persisted ANSI_NULLS/QUOTED_IDENTIFIER options; declarations become schema-qualified CREATE statements using current catalog names. Schema ownership is normalized to dbo rather than exporting source principals.

Foreign keys, independent UNIQUE/CHECK constraints, non-primary indexes, functions, triggers, user-defined types, sequences, permissions and data are excluded. Alias column types are expanded to base types with a warning. CLR user-defined/typed XML/encrypted/hidden/generated-always/FILESTREAM/column-set columns, legacy bound defaults and special tables such as temporal, memory-optimized and FileTable are not fully supported; affected tables are skipped with warnings. Encrypted, inaccessible, CLR or natively compiled module definitions are also skipped. EXECUTE AS/signature dependencies are reported; principals and signatures are not recreated.

Included objects are ordered using catalog dependencies. Excluded or unresolved dependencies, cross-database references and dependency cycles produce warnings for manual review; dynamic SQL dependencies may not be represented in the catalog. The script is not a complete database backup or version-downgrade tool and may require excluded objects to be created separately. Use a compatible target SQL Server and avoid concurrent source DDL while exporting.

SQL Server object scripts are saved as UTF-16 LE with a BOM, matching the supplied SSMS script encoding. Output contains no generated DROP statements or source-database USE directive. Review it before executing in the intended empty target database, using a GO-aware client such as SSMS. The application generates the complete text before replacing the destination through a sibling temporary file; a failed replacement preserves the previous file. Completion shows exported table/view/procedure counts, warning count, duration and full path. Warnings also appear as script comments and in a results dialog. No SMO dependency or server-side export procedure is installed.

### Converted procedure index

Each `.datapie-<hash>.json` in StoredProcedures now uses a version-2 object with DatabaseFile, GeneratedUtc, and Procedures. Each entry records Name, ScriptFile, Source, Status, Success, Warnings, and Error. Status is Success (fully converted and compiled), Partial (some statements skipped), or Failed. Success is true only for complete conversions; it does not prove business-semantic equivalence. Old name-array indexes remain readable. Regenerate conversion output to upgrade an existing index.

SQLite → SQL Server translates the current StoredProcedures/*.txt bodies rather than restoring original T-SQL. Indexed scripts require Status = Success; independent scripts without an index are also supported. Failed, partial and unsupported scripts are skipped with warnings. SQL Server → SQLite no longer stores original T-SQL.

### Database migration: SQLite → SQL Server

Connect to SQLite, select **Database migration → SQLite → SQL Server**, and choose a `.sql` destination. The export reads schema metadata and samples up to 1,000 returned rows per table for text column lengths and generates SQL Server table columns, primary keys, AUTOINCREMENT identity columns and supported foreign keys under dbo. It does not copy rows, connect to SQL Server, execute DDL, drop target tables or modify the source database.

Review all mappings: INTEGER becomes BIGINT, TEXT/CHAR/CLOB uses NVARCHAR(255) when the sampled maximum length is <=255 UTF-16 code units, otherwise NVARCHAR(MAX). NULL, empty and whitespace-only values are ignored; mixed values remain text. Empty tables/columns default to NVARCHAR(255). Sampling has no explicit row ordering, and later rows may require a wider type, BLOB becomes VARBINARY(MAX), REAL becomes FLOAT, date/time declarations become DATETIME2 and other numeric declarations use DECIMAL(38,10). SQLite defaults, CHECK/UNIQUE constraints, cascading actions, views, triggers and additional indexes require manual conversion and are reported; virtual/generated-column tables are skipped. The generated script uses GO batch separators and CREATE OR ALTER procedures. Execute it manually in the intended SQL Server database after reviewing warnings, schemas, permissions and object dependencies.

Supported procedure bodies include SELECT/INSERT/UPDATE/DELETE/WITH, common string and date functions, and DataPie parameter/local metadata. Dynamic SUBSTR bounds, SQLite-specific options and unknown functions require manual conversion. Review generated SQL for type, collation and NULL semantics. Only a .sql schema script is saved; no data is copied and no generated SQL is executed.

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

The repository also includes `SelfContainedProfile` for Visual Studio or command-line publishing:

```powershell
dotnet publish DataPieMaui/DataPieMaui.csproj -c Release -p:PublishProfile=SelfContainedProfile
```

This profile writes to `publish/win-x64-self-contained` under the repository root. Both profiles produce folder deployments, with trimming and single-file publishing disabled for the self-contained profile. Copy the complete output folder, including the local web assets. Updating source files does not regenerate a previously published copy; publish again to distribute new features.

## Regression checks

The test project is a console-based regression runner. Run it with:

```powershell
dotnet run --project DataPieMaui.Tests\DataPieMaui.Tests.csproj -c Release
```

Checks cover SQL Server script generation from metadata fixtures (types, defaults, primary keys, declaration normalization, dependency ordering, warnings and atomic file replacement), SQLite access, connection management, schema browsing and type formatting, table-editor insertion/defaults, editing, page selection, confirmed deletion and conflict rollback, query previews, import/export and length validation, Excel-based table creation and Id identity defaults, table deletion and foreign-key protection, procedure parameters, definition editing, conversion, cancellation, result snapshots, typed sorting, copy formatting, result-component updates, and desktop clipboard delegation. Workspace checks also exercise confirmation and cancellation, concurrent-operation protection, state retention, and rendering of the actual Razor pages. These checks use a test desktop adapter; they do not automate native window/file-picker interactions or require a live SQL Server instance.

To also write static page previews to the ignored `tmp/ui-review/` directory, run:

```powershell
dotnet run --project DataPieMaui.Tests\DataPieMaui.Tests.csproj -c Release -- --render-ui
```

These previews are render snapshots, not an interactive web version of the desktop application.

With Node.js available, run the dialog geometry interaction checks separately:

```powershell
node DataPieMaui.Tests/table-editor-window-checks.cjs
```

These checks exercise dragging, resizing, viewport bounds, maximize/restore, busy guards, keyboard controls and remembered geometry using a simulated DOM; they do not automate the native desktop UI.

## UI architecture

- `Controls/TableEditorHostPage.cs` hosts each independent table-editor window; `Components/TableEditor.razor` provides paging, page selection, and add/edit/delete dialogs. `DataPieCore/TableEditorSession.cs` owns the bound connection/table, metadata, validation, parameterized writes, and conflict checks.
- `Components/Pages/DatabaseSchemaView.razor` is the independent schema-browser page. It uses cached object lists, selected-table SQLite detail reads, and single-object definition loading; table-structure editing is not implemented.
- `Controls/TableEditorHostPage.cs` and `Components/TableEditor.razor` host both read-only previews and editable table sessions. `Components/Shared/TableDataGrid.razor` shares grid rendering across both modes. The old Query results page and host are removed. `IDesktopServices.ShowTablePreviewAsync` reuses the preview window; `CopyTextAsync` supplies clipboard access.
- `MainPage.cs` hosts the main `BlazorWebView` and provides a native fallback when WebView2 is missing.
- `Components/Workspace.razor` provides navigation and the status bar; `Components/Pages/` contains the business pages, and `Components/Shared/` contains shared controls and dialogs.
- All business pages, including login and connection management, keep their markup and page-specific workflows together in a single `.razor` file using an `@code` block. The static About page needs no code block.
- `WorkspaceController` composes shared services and coordinates navigation and native-window lifecycle. Pages use `WorkspacePageBase` to access shared state and the operation runner. The connection page initializes saved connections once per workspace; revisiting the page preserves loaded configuration and operation status.
- `Services/Shared/WorkspaceContext` owns session state and the shared capabilities for one workspace. Shared services survive navigation; `WorkspaceForm` retains input values when page components are recreated. `DatabaseConversionService` owns conversion execution, progress, and cancellation so the status bar and native close handler do not depend on a page instance.
- `Services/Shared/WorkspaceOperationRunner` provides the single operation gate, error handling, and timing contract used by main-workspace pages. Table-editor windows maintain their own busy and draft state. `QueryPreviewService` shares table/view and SQL result loading, `WorkspaceContext.RefreshSchemaAsync` refreshes table lists after creation/deletion, and `FileNames` shares export filename normalization. `TableSelection` carries schema-qualified import targets separately from source worksheet/table names, and `WorkspacePageBase.OpenTableEditorAsync` is shared by SQL query, Import data and Database schema. `ExcelTableSql` reads worksheets and generates provider-specific table definitions.
- `IDesktopServices` separates the workspace from Windows-specific operations. `WindowsDesktopServices` dispatches native work to the UI thread, binds file pickers to their owner, and manages result and table-editor windows.
- `DataPieCore/SqlServerScriptExporter.cs` reads fresh SQL Server catalog metadata and generates scoped object-creation scripts independently of the lightweight connection-time `DbSchema` cache.
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
