# DataPieMaui user guide

[简体中文](USER-GUIDE.zh-CN.md) | [Project README](README.md)

This guide covers the Windows desktop application. Switch between English and 简体中文 at the top of the workspace; some settings and diagnostic messages may remain in English. The bottom status bar shows progress, errors, and export paths. Wait for a running operation to finish; database conversion can be canceled from the status bar.

## 1. Connect to a database

### SQL Server

1. Open **Database connection → SQL Server** and enter the server, for example `server\instance`.
2. Choose **Windows** or **SQL Server** authentication. For SQL Server authentication, enter the username and password.
3. Click **Load databases**, select a database, and click **Connect and save**.
4. Later, choose the entry under **Saved connections** and click **Connect**. Deleting an entry removes only its local configuration.

Connection loads an object list first; a table's fields are loaded when needed. The status bar reports connection, object-list, and workspace setup times. SQL Server connections are encrypted and trust the server certificate by default. Saved connection strings can contain passwords, so protect the local user configuration.

### SQLite

1. On the **SQLite** tab, click **Browse file** and choose an existing `.db`, `.sqlite`, or `.sqlite3` file.
2. Leave the password blank for an unencrypted file. For a default-configuration SQLCipher 4 file, enter its password, then click **Connect and save**. The password is kept only for the session and must be re-entered after reconnecting.
3. To create a database, leave the password blank, click **Create blank database and connect**, and choose a new filename. This operation does not automatically save the connection.

## 2. Browse database schema

Open **Database schema**, enter an object name in the search box, and choose from the table, view, and procedure categories. Select an object to see its actions. **Refresh schema** reloads the object list.

- Table: click **Show structure** for columns, types, keys, defaults, and related details. The action area also offers import, export, **Query&Edit**, data analysis, clear data, and drop table.
- View: query data, open data analysis, or view its definition.
- Procedure: run it or view its definition. The schema page's definition viewer is read-only; use the definition viewer in **Batch export** or **Stored procedures** to edit supported views or procedures.

**Clear data** preserves the table structure; **Drop table** deletes the structure and data. Check the destination database and object in the confirmation dialog.

## 3. Query, search, and export

1. Open **SQL query**, select a table, and click **SELECT**, **UPDATE**, or **DELETE** to generate SQL, or enter your own SQL in the editor.
2. Click **Preview query** to see the result. The preview window is read-only, but the SQL itself can modify the database. To run a non-query statement, use **Run SQL** and confirm.
3. The preview displays at most the first 1,000 matching rows, 100 per page. The full result is captured in a temporary snapshot in the background; once capture finishes, search covers the entire returned result. Capture can be canceled. A `TOP` or `LIMIT` in the SQL still limits the result. Column headers sort the displayed rows.
4. For simple search, select a field and enter a value; added conditions use AND. **Advanced filters** supports equals, not-equals, contains, ranges, multiple values, NULL, and other operations, combined with AND. **Clear search** removes filters.
5. On the main page, **Query to Excel** or **Query to CSV** reruns the SQL. Data may have changed since the preview.

**Query&Edit** opens an independent editor for the selected database table; it uses the table itself, rather than the SQL editor's result.

## 4. Edit table data

Select a table on SQL query, Import data, or Database schema, and click **Query&Edit**. The editor displays 100 records per page and can browse the whole table. Search, paging, refresh, and **Export Excel** use the currently applied conditions.

1. **Add record**: enter editable values and save. The database supplies defaults or NULL for omitted optional fields; generated fields cannot be entered.
2. **EDIT**: select one record, edit it in the dialog, and save. Primary keys, generated fields, and unsupported fields are read-only. An unchanged NULL remains NULL; clearing text saves an empty string.
3. **Delete selected**: select one or more rows, optionally use **Select all** for the current page, then review and confirm deletion.
4. Closing a modified draft requires confirmation. Record dialogs support dragging, resizing, and maximizing.

Tables without primary keys can be edited when records can be located safely. Writes are rejected or rolled back if a record cannot be identified uniquely or has changed; refresh and check before retrying. Simple and advanced filters are applied separately in this editor: applying simple search replaces the advanced filters.

## 5. Import data

1. On **Import data**, choose the target table. Optionally click **Export template** for an empty Excel workbook with column headers.
2. **Import a file** supports `.xlsx`, `.xls`, `.csv`, and `.db`. Choose a file and click **Import file**. Multi-sheet workbooks or SQLite sources need a matching worksheet/source-table name.
3. **Batch import** processes the folder's CSV and XLSX files sequentially. Source data is appended; generated IDs are supplied by the database.
4. To check SQL Server `nvarchar` / `nchar` lengths, select **Validate field lengths before importing**. An overlength error identifies the file, data-row number, column, actual length, and allowed length. Leave it unchecked for normal import.

For a SQLite destination, shared columns are matched case-insensitively and extra source columns are ignored. Missing destination columns use database defaults or NULL; a required field with no usable value causes failure. SQL Server bulk writes use a 600-second timeout. In a folder import, previously successful files remain imported after a later failure; check the destination before retrying.

## 6. Data analysis and templates

1. In Database schema, select a table or view and click **Data analysis**. Analysis is read-only; view analysis uses the rows returned by the view.
2. Open **Group by**, choose grouping fields, and apply. Leave it empty for an overall result.
3. Open **Calculations** and use **Add aggregate** for numeric Sum, Average, Min, or Max. Set names, number formats, and optional default sorting. Use **↑** beside an aggregate to change its display order.
4. For a weighted average price, add `SUM(Revenue)` and `SUM(Quantity)`, then use **Add formula** to divide the first aggregate by the second. The right value can be another aggregate or a constant; division by zero or NULL returns NULL.
5. Set simple search or advanced filters and click **Analyze**. Filters are applied before grouping and calculations. At most 1,000 groups are previewed, but the calculations and bottom Total row cover all matching source records. Clicking a column header sorts the loaded preview only.
6. Select **Format** for formatted Excel output, then click **Export** to export all groups. Export reruns the analyzed settings against the current source, so later data changes can affect the workbook. Analyze again after changing settings.

### Save and manage templates

- Click **Save** after configuring the analysis. A selected template is updated; otherwise enter a name for a new template. Multiple differently named templates can exist for the same object name; saving the same name overwrites it.
- Select a template and click **Apply template** to restore it and immediately analyze.
- **New template** copies the selected template into an unsaved draft; adjust it and save with a different name.
- **Manage templates** edits the name, grouping, calculations, and fixed default filters. Click **Update selected** to save; deletion requires confirmation. Management does not automatically change the current analysis results.
- Templates are stored in the application's local SQLite configuration database. Compatible same-named tables or same-named views can reuse templates; tables and views are kept separate. Templates save fixed filter values. Older parameter or relative-date templates may require review and conversion to fixed values in the manager. The current manager does not import or export template files.

## 7. Batch export and stored procedures

Under **Batch export**, select objects on the Tables or Views tab; Select all affects the current category. Choose **Excel (Standard)**, **Excel (MiniExcel)**, or **CSV folder**, then choose a destination. Per-object query and definition buttons do not change export selections.

Under **Stored procedures**, select and run procedures without parameters, or use **Run with parameters** for an individual SQL Server procedure. Enter values and click **Review and run…**; review and confirm. NULL and Omit are available. After a failure or timeout, check the database state before retrying. Some views and procedures support editing, diff review, and confirmed saving from the definition viewer.

## 8. Table design and migration

Under **Table design → Create table**, select an Excel file and worksheet. The first row supplies column names. Click **Generate SQL**; types are inferred from the first 1,000 data rows. Review and edit the SQL as needed, then click **Create table** and confirm. This creates structure only; import records separately. **Drop table** deletes the entire selected table after confirmation.

Under **Database migration**:

- With SQL Server connected, choose a destination to convert to SQLite. Keep the generated `StoredProcedures` folder and review conversion warnings. Cancellation or failure can leave an incomplete file.
- With SQL Server connected, generate a SQL Server object script containing supported schemas, tables, primary keys, defaults, views, and procedures. It excludes data, other indexes and constraints, functions, triggers, and user-defined types. Review warnings and dependencies.
- With SQLite connected, generate a SQL Server structure and supported procedure script. It neither copies data nor executes SQL. Review inferred types, omitted objects, and warnings.

## 9. Troubleshooting

- **Slow connection:** review the connection and object-list timings in the status bar; check the server name, network, and permissions. Table fields load on demand. Refresh schema after external changes.
- **Import timeout:** check file size, SQL Server load, blocking, and network conditions. Verify destination data before retrying. The 600-second setting applies to SQL Server bulk writes.
- **Cannot edit a table:** check permissions and the page's explanation. A keyless table still needs safe record identification; binary and other unsupported fields are read-only.
- **Search misses records:** wait for the preview's full-result capture to finish. Search only covers rows returned by the original SQL; check field, type, and value, then clear and retry.
- **Where is the file?** Check the status bar after completion. If analysis export is unavailable, apply settings and analyze again.
