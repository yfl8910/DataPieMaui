# DataPieMaui

[English](README.md) | 简体中文

面向 Windows x64 的数据库桌面工具，支持 SQL Server 和 SQLite 的查询、导入、导出、表设计、存储过程及数据库转换。程序使用 .NET 11 RC1、.NET MAUI Blazor Hybrid，工作区界面采用 Blazor，查询结果在独立窗口中显示。

## 功能概览

- 使用 Windows 身份验证或 SQL Server 身份验证连接服务器，打开本地 SQLite 数据库。
- 创建空白 SQLite 数据库并自动连接，不保存该连接配置。
- 保存和管理数据库连接，切换页面时保留输入内容。
- 生成 SELECT、UPDATE、DELETE 语句，预览查询或执行 SQL。
- 查询结果支持动态列、类型排序、分页、行选择和复制。
- 将查询或整张表导出为 Excel、CSV，批量导出多个表或视图。
- 单文件导入 Excel、CSV、SQLite；从文件夹批量导入 CSV 和 XLSX，支持 SQL Server 字段长度校验。
- 从 Excel 工作表生成建表 SQL，审核后创建表；在独立 TAB 中删除表。
- 批量运行无参数存储过程，或填写参数后单独执行 SQL Server 存储过程。
- 按需查看视图和存储过程定义，支持的对象可编辑、查看差异并确认保存。
- 将 SQL Server 数据库转换为 SQLite，显示转换进度并支持取消。

## 开发与运行要求

- Windows x64；项目声明的最低系统版本为 Windows 10 1809。
- `global.json` 指定的 .NET SDK：`11.0.100-rc.1.26425.128`，以及匹配的 `maui-windows` 工作负载。
- 当前目标框架为 `net11.0-windows10.0.19041.0`；MAUI Controls 和 Blazor WebView 包版本为 `11.0.0-rc.1.26451.6`。
- Microsoft Edge WebView2 Runtime。开发和发布版本都需要；缺少时原生宿主会显示安装提示。
- 首次还原依赖需要访问 NuGet。

项目当前仅面向 Windows。.NET 11 RC1 属于预发布版本；Visual Studio 必须支持该 SDK，并在需要时启用预览 SDK。`global.json` 只选择已安装的 SDK，不负责安装。Visual Studio 通常使用系统目录 `C:\Program Files\dotnet` 下的 SDK，临时目录中的独立 SDK 不会自动被发现。

在仓库根目录执行：

```powershell
dotnet workload install maui-windows
dotnet restore DataPieMaui\DataPieMaui.csproj
dotnet build DataPieMaui\DataPieMaui.csproj -c Debug
dotnet build DataPieMaui\DataPieMaui.csproj -t:Run -c Debug
```

也可以在支持 .NET 11 RC1 和 MAUI 的 Visual Studio 中打开 `DataPieMaui.sln`，将 DataPieMaui 设为启动项目，选择 Windows Machine 运行。如果提示无法面向 .NET 11，请检查仓库根目录的 `dotnet --version` 和 `dotnet --list-sdks`，安装指定 SDK 及工作负载后重新启动 Visual Studio。

## 使用说明

### Database connection：数据库连接

页面提供 SQL Server、SQLite、Saved connections 三个 TAB。

SQL Server 支持 Windows 或 SQL Server 身份验证。填写服务器和身份信息，点击 **Load databases** 选择数据库，再点击 **Connect and save**。

SQLite 提供两种操作：

- **Browse file → Connect and save**：打开已有数据库，并保存连接配置。
- **Create blank database and connect**：选择新 `.db` 文件的位置，创建有效的空白数据库并自动连接，不保存连接配置。

创建数据库使用只返回路径的保存对话框，不会提前生成占位文件。真正已存在的文件不会被覆盖；取消选择不影响当前连接。空白数据库创建后，可进入 **Table design** 创建表。

Saved connections 可重新连接或删除配置。删除连接配置不会删除数据库文件。

### SQL query：SQL 查询

选择表后可生成 SELECT、UPDATE、DELETE，或在 **SQL editor** 中输入自己的 SQL。**Preview query** 打开独立结果窗口；**Run SQL** 在确认后执行编辑器中的语句。查询和整表均可导出为 Excel 或 CSV。

结果窗口支持列排序、分页、选择行、复制选中行和复制全部预览行。后续查询更新同一个窗口；**Open results** 可重新打开结果窗口而无需再次查询。

- 预览最多读取 1,000 行，每页显示 100 行；排序和分页只针对已加载的预览。
- 预览查询使用 30 秒命令超时。
- **Copy all preview rows** 按当前排序复制数据，包含列标题。
- 完整导出会重新执行查询，不受预览行数限制；数据可能与先前预览不同。
- 结果表格只读；SQL editor 中的 SQL 可以修改数据库。

### Import data：数据导入

顶部卡片放置 **Target table、Query data、Export template、Clear data**。下方白色卡片包含 **Import a file / Batch import** 两个 TAB，切换时只改变导入内容，并保留目标表、文件及文件夹路径。

**Import a file** 支持 XLSX、XLS、CSV，以及包含对应源表的 SQLite 数据库。**Import file** 执行导入，**Validate lengths and import** 额外检查 SQL Server 目标字段长度。

**Batch import** 顺序导入文件夹中的 CSV 和 XLSX，并启用字段长度校验。如果某个文件失败，已导入的数据会保留，状态信息指出失败文件。

**Query data** 预览目标表，**Export template** 导出只包含列结构的空白 Excel 模板。**Clear data** 经确认后清空数据，保留表结构。永久删除表位于 Table design 页面。

### Batch export：批量导出

通过 **Tables / Views** TAB 选择对象，支持 MiniExcel 或 EPPlus 多工作表导出，以及每个对象一个文件的 CSV 导出。两个 TAB 的选择会保留；Select all 只作用于当前分类。CSV 同名文件覆盖前需要确认。

每个表或视图的 **Query data** 与导出勾选独立，复用查询结果窗口，不改变 SQL editor。视图旁的 **View definition** 按需读取 SQL 定义。

### Stored procedures：存储过程

SQL Server 分为 **Without parameters / With parameters** 两个 TAB。前者支持勾选和批量执行，后者为每个过程提供 **Run with parameters**。带默认值和 OUTPUT 参数也归入有参数分类，RETURN 值不参与分类。连接时只读取参数存在信息，打开执行窗口时再读取详细参数。外部修改签名后，请重新连接刷新分类。

参数窗口支持输入值、选择 NULL，或选择 Omit 让服务器使用默认值。省略必填参数会由服务器报错；空字符串保留为空字符串。OUTPUT 参数可提供初始值，省略的 OUTPUT 参数不收集输出值。

**Review and run…** 展示服务器、数据库、完整过程名称及参数值，只有 **Confirm run** 才会执行。程序使用带类型的参数，执行前复核参数信息，运行期间阻止重复提交和关闭窗口。完成后显示耗时、OUTPUT 值、RETURN 值及受影响行数；NOCOUNT 可能使行数不可用。参数执行窗口不显示结果集。

支持常见数字、字符串、日期时间、GUID 和 XML 类型。.NET decimal 的范围与精度限制适用于 decimal 输入。表值、二进制、CLR/UDT 等不支持的参数会禁用执行窗口。数据库负责最终值约束及权限检查。

执行超时为 120 秒，程序不会自动包裹应用事务。错误保留输入；失败或超时的过程可能已修改数据，重试前请核实状态。SQLite 使用转换后的过程脚本执行流程。

### Convert to SQLite：数据库转换

仅在连接 SQL Server 时可用。选择目标 `.db` 文件后转换数据库，状态栏显示进度并提供取消按钮。无法完整转换的视图或过程会产生警告。

分发转换后的数据库时，应将生成的 `StoredProcedures` 文件夹一并保留在数据库旁。取消或失败可能留下不完整数据库，应重新转换后使用。

### Table design：表设计

该页面位于 Convert to SQLite 之后，使用“表格＋铅笔”图标，包含 **Create table / Drop table** 两个 TAB。切换时保留 Excel 选择和 SQL 草稿。

**Create table**：选择 XLSX 或 XLS 文件和工作表，点击 **Generate SQL**。第一行作为列名，Sheet 名称作为默认表名，无需另填表名。程序只读取表头后的前 1,000 行数据推断字段类型，不足 1,000 行则读取全部；用户可在 SQL editor 中修改表名、类型和约束，审核后点击 Create table 并确认。

| Excel 数据 | SQL Server | SQLite |
| --- | --- | --- |
| 文本、混合类型或空列 | 最大长度 ≤255 个 UTF-16 代码单元：NVARCHAR(255)，否则 NVARCHAR(MAX) | TEXT |
| 整数 | INT；任意值超出 INT 范围时使用 BIGINT | INTEGER |
| 小数 | FLOAT | REAL |
| 纯日期 / 日期含时间 | DATE / DATETIME2 | TEXT |
| 布尔值 | BIT | INTEGER |
| Id 列，不区分大小写 | BIGINT IDENTITY(1,1) NOT NULL PRIMARY KEY | INTEGER PRIMARY KEY AUTOINCREMENT |

真正的 Excel 日期单元格，以及有效的年在前文本日期（如 2026-10-12、2026/10/12、2026年10月12日，或带时间的年在前格式），在 SQL Server 中，样本全为纯日期时推断为 DATE；存在非零日期单元格时间或文本中明确写出的时间时使用 DATETIME2。两者可以混合在同一日期列。日/月顺序不明确的日期、纯数字日期编号、非法日期或含其他文本的混合列保留文本类型，SQLite 日期仍使用 TEXT。其他文本单元格保持文本类型，包括前导零编号。空单元格、空字符串和纯空格在推断时忽略；空数据行仍计入 1,000 行限制。长度按样本内最大值判断；混合列使用固定区域格式转换为文本后计数，空列长度为 0。INT 范围为 −2,147,483,648～2,147,483,647。Id 列优先应用自增主键规则，不受 Excel 中值的类型影响；其余列默认允许 NULL。空列名和重复列名会被拒绝。第 1,001 行之后的值可能需要更大的整数类型或文本长度；请审核推断结果，特别是小数精度、业务编号和主键要求。

SQLite 使用 AUTOINCREMENT 时会自动创建系统表 `sqlite_sequence`，用于记录已使用的最大编号，这是正常现象。`INTEGER PRIMARY KEY` 也能自动分配编号，但可能复用已删除的编号；需要这种行为时，可在 SQL 中移除 AUTOINCREMENT。

建表仅创建结构，不导入数据，不会自动覆盖同名表。成功后刷新共享表列表，再通过 Import data 导入数据。切换数据库连接会清空建表 SQL，便于针对新数据库重新生成。

**Drop table**：选择表，点击 Drop table，在确认弹窗中核对数据库和表名。无需输入表名确认。操作会永久删除表结构及全部数据，取消不会删除。

SQL Server 由数据库检查依赖限制；SQLite 先检查其他表的外键引用，避免级联删除数据。成功后刷新各页面表列表、清理失效选择和当前查询预览；旧结果窗口中的历史快照也应按需关闭。

## 查看及编辑对象定义

在 Batch export 的视图或 Stored procedures 的过程旁点击 **View definition**。仅点击后读取对应定义，连接时不会预先加载 SQL 正文。弹窗支持 Copy SQL、受支持对象的 Edit，以及 Close；关闭释放文本，重新打开读取最新定义。权限不足或 SQL Server 对象加密可能使定义不可读。

### SQL Server 定义编辑

普通视图和 T-SQL 过程可编辑草稿。**Save changes…** 验证单个、带架构限定的对象定义，并展示差异及待提交 SQL；只有 Confirm save 会修改数据库。关闭已修改草稿前需要确认放弃。

编辑器接受所选对象的 CREATE、ALTER、CREATE OR ALTER，保存时提交 ALTER，不执行存储过程。拒绝额外批次、重命名、加密定义、CLR/native 过程。声明扫描器检查目标、引用标识符、注释和不支持选项，完整语法验证由 SQL Server 完成。GO、ENCRYPTION 等关键字作为标识符时需引用。索引视图和签名模块保持只读，避免丢失索引或签名；保存需要 ALTER 权限及显式架构名称。

保存事务内复核原始定义、修改版本及 SET 选项，保留 QUOTED_IDENTIFIER / ANSI_NULLS，并在提交前重新读取结果。失败保留草稿。应用锁只协调本编辑器的保存，不协调 SSMS 等外部工具；避免同时修改相同对象。提交期间断连后，重新打开核实状态再重试。

### SQLite 定义编辑

视图接受所选 main 视图的单条 CREATE VIEW。保存会取得写事务、复核原始文本和架构版本，原子重建视图及触发器。验证只准备 SQL，不执行查询，并检查原先有效的视图仍可编译。有触发器的视图需保留列名及顺序。失败回滚并保留草稿；列兼容不保证触发器业务语义不变。

转换后的过程仅编辑现有 `StoredProcedures/*.txt` 的 SQL 正文。支持 SELECT、INSERT、UPDATE、DELETE、REPLACE、WITH；拒绝 DDL、事务及连接命令。使用 EXPLAIN 校验语法及对象引用，不执行过程。原参数默认值、局部变量初始化和转换警告等元数据头保留，不支持新增参数或修改转换元数据。

保存前检查当前数据库、完整文件哈希及已有参数/局部变量。成功时原子替换文件，并保留唯一命名的 `.bak` 备份。进程内保存会串行化；外部编辑器仍可能竞争，避免同时修改。保存的脚本在下次明确执行时使用。

## 操作计时、连接设置与本地数据

选择保存位置或等待确认的时间不计入执行耗时；计时从最终确认后开始。一次只运行一个操作，运行期间禁用页面导航和其他操作，转换取消仍可用。确认弹窗支持键盘焦点和 Escape 取消。

弹窗 JavaScript 失败或超时时，控件保留在普通打开的对话框中。WebView 消失时取消待确认操作；运行中的过程或定义保存会保留操作锁直至完成。

SQL Server 使用 Microsoft.Data.SqlClient 7.0.3，连接默认 Encrypt=Mandatory、TrustServerCertificate=True：流量加密，但跳过服务器证书验证。旧保存设置在成功连接后更新。支持 Windows 和 SQL 登录；未包含 Azure 身份验证扩展。

连接配置保存于 `%LOCALAPPDATA%\DataPie\connections.db`，未提供应用层加密，连接字符串可能包含数据库密码，请作为私有配置保管。创建空白 SQLite 数据库不会添加保存配置。

## Windows x64 发布

### 依赖运行时的发布

```powershell
dotnet publish DataPieMaui\DataPieMaui.csproj `
  -c Release `
  -f net11.0-windows10.0.19041.0 `
  -r win-x64 `
  --self-contained false `
  -p:WindowsAppSDKSelfContained=false `
  -o publish\win-x64
```

运行 `publish\win-x64\DataPieMaui.exe`。目标电脑需要匹配的 x64 .NET 11、Windows App Runtime 和 WebView2 Runtime。分发整个目录，包含 wwwroot 和依赖文件。由自包含切换到此方式时，应使用新的空目录，避免遗留运行库。

### 自包含发布

```powershell
dotnet publish DataPieMaui/DataPieMaui.csproj -c Release -r win-x64 --self-contained true -p:WindowsAppSDKSelfContained=true -o publish/win-x64-self-contained
```

包含 .NET 和 Windows App SDK 运行库，仍需要 WebView2。发布前关闭正在运行的程序，避免文件占用；分发整个目录。

### Visual Studio 发布

停止调试，将 DataPieMaui 设为启动项目，选择 Release / Windows Machine，右键项目选择 Publish，使用 FolderProfile。修改项目配置后可重新加载项目或解决方案刷新菜单。

FolderProfile 当前是依赖运行时的发布方式，并包含绝对输出路径。在其他目录使用时建议覆盖输出位置：

```powershell
dotnet publish DataPieMaui/DataPieMaui.csproj -c Release -p:PublishProfile=FolderProfile -o publish/win-x64
```

如果 Publish 仍不可用，可在 Visual Studio 终端执行该命令。本项目采用免安装的文件夹部署，MAUI 标准 Windows 发布向导主要面向 MSIX。

## 回归检查

```powershell
dotnet run --project DataPieMaui.Tests/DataPieMaui.Tests.csproj -c Release
```

控制台检查覆盖查询快照、排序和复制、连接、空白数据库创建和文件保护、导入导出及长度校验、建表和 Id 自增、删除表及外键保护、过程参数、定义编辑、转换取消、操作确认、页面输入保留及 Razor 渲染。

生成静态页面预览到忽略目录 `tmp/ui-review`：

```powershell
dotnet run --project DataPieMaui.Tests/DataPieMaui.Tests.csproj -c Release -- --render-ui
```

检查使用测试桌面适配器，不自动操作原生窗口、剪贴板或文件选择框，也不依赖真实 SQL Server。静态预览不是可交互的桌面程序网页版。

## 代码结构

- `MainPage.cs` 承载主 BlazorWebView；`Controls/QueryResultsHostPage.cs` 承载独立结果窗口。
- `Components/Workspace.razor` 管理导航和状态栏，`Components/QueryResults.razor` 管理结果显示及交互。
- 各业务页面的标记和页面专属逻辑放在同一个 `.razor` 文件的 `@code` 中；静态 About 页面无需代码块。
- `WorkspaceController` 协调工作区和窗口生命周期，`WorkspacePageBase` 为页面提供共享状态和操作能力。
- `WorkspaceContext` 与 `WorkspaceForm` 保存跨页面状态；共享架构刷新方法同步建表和删表后的列表。
- `WorkspaceOperationRunner` 统一计时、异常处理和防重复执行；查询预览、定义编辑、过程执行、转换及 Excel 建表推断使用共享服务或核心库。
- `IDesktopServices` 隔离 Windows 文件选择、只选路径的保存窗口、剪贴板及结果窗口操作，便于测试。
- Razor 组件在本机运行，无需 Web 服务器。

| 目录 | 内容 |
| --- | --- |
| `DataPieMaui/Components/Pages/` | 页面及业务逻辑 |
| `DataPieMaui/Components/Shared/` | 共用控件与弹窗 |
| `DataPieMaui/Controls/` | 原生窗口宿主 |
| `DataPieMaui/Services/` | 桌面服务与共享流程 |
| `DataPieMaui/wwwroot/` | HTML、CSS、JavaScript 等源资源 |
| `DataPieCore/` | 数据库、Excel、SQL 生成及转换引擎 |
| `DataPieMaui.Tests/` | 控制台回归检查 |

`publish/`、`.vs/`、`bin/`、`obj/` 和临时目录不提交到 Git；wwwroot 中的源码资源仍受版本管理。
