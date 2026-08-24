# Agents.md — open-calendar-desktop（日程桌面）

面向 AI 编码代理与维护者的项目要点摘要。重点是本仓库的约定、架构与易踩坑之处，不想重复读全部源码时可先看这里。

## 这是什么

「日程桌面」：Windows 桌面端日程/待办应用，主界面是一个常驻桌面右下角的**半透明玻璃面板**（类似 Rainmeter 皮肤、Fences 壁纸模糊）。支持日历月视图、事项列表、农历显示、Markdown 事项备注、开机自启、系统托盘。

全中文 UI（界面文案、命名文件/菜单均是中文），代码注释多为中文，夹杂英文技术说明。

## 技术栈

- .NET：`net10.0-windows`（WinExe），`UseWPF=true`
- 解决方案文件：`open-calendar-desktop.slnx`（新版 XML 语法；`dotnet build` / `dotnet test` 在根目录直接可用）
- MVVM：`CommunityToolkit.Mvvm` 8.4.2（`[ObservableProperty]` / `[RelayCommand]` 源生成器）
- 系统托盘：`H.NotifyIcon.Wpf` 2.4.1（`TaskbarIcon`）
- 无 DI 容器：`AppServices` 为手写单例组合根
- **已移除**：遗留 SQLite 兼容层（`Microsoft.Data.Sqlite`、`ScheduleDatabase`/`ScheduleRepository`）已从项目删除，文件夹存储是唯一数据来源。
- 无第三方 Markdown 库：内置 `SimpleMarkdown`（用 `ChineseLunisolarCalendar`，同样无第三方依赖）

## 代码结构

```
open-calendar-desktop.slnx
src/CalendarApp/
  App.xaml / App.xaml.cs      # 组合根：单实例、托盘、窗口生命周期、几何持久化
  AppServices.cs              # 手写服务单例（Settings / Items）
  Theme.cs                    # 强调色画刷工厂
  Models/                     # ScheduleEvent, LunarCalendar
  Data/                       # 文件夹存储（唯一数据源）
  Platform/                   # Win32 桌面玻璃层、模糊、窗口几何、开机启动
  ViewModels/                 # MainViewModel, SettingsViewModel, ViewModelBase
  Views/                      # MainWindow, SettingsWindow, ItemEditorWindow, DateJumpWindow, SimpleMarkdown
  Converters/                 # InverseBooleanToVisibilityConverter
  Themes/GlassStyles.xaml     # 全局玻璃样式资源
tests/CalendarApp.Tests/      # xUnit 测试（文件夹存储、农历、设置）
```

## 关键架构决策

### 数据存储：文件夹 Markdown（唯一数据源）
- **唯一存储是 `FolderItemStore`**：一个普通文件夹里放 `items/*.md`，文件名即数据，格式
  `items/yyyyMMdd-HHmmss_yyyyMMdd-HHmmss_P<级别>_<净化标题>[_<标签>].md`
  （字段用 `_` 分隔、字段内用 `-`；`P1..P4`；规则见仓库根目录 `存储目录设计.md`）。
  `FileSystemWatcher` 实时监听，300ms 去抖后触发 `ItemsChanged`。
- **归档**：截止时间已过的事项由 `ArchiveExpiredItems()` 移入 `archive/YYYY/`（YYYY=开始/创建年份），
  `MainViewModel.Refresh`/`ReloadItems` 每次刷新都会执行（幂等）。`Update(old, new)` 按新截止时间决定落点：
  截止仍过期 → 留在 archive/；截止改为将来 → 移回 items/（编辑已完成事项即走此路径）。
  `MoveToDeleted` 也能从 archive/ 里把文件删进 `deleted/`。
- **查询边界**：`GetItems`（事项列表）只读 items/（未过期项）；`GetUpcoming`/`GetEventsOnDay`（日历圆点、
  选中日日程）同时读 items/ + archive/，所以已完成事项仍显示在它开始的那天，可继续编辑/设为长期/删除。
- 遗留 SQLite 兼容层（`ScheduleDatabase`/`ScheduleRepository`）已整体移除，**不要**再依赖任何 sqlite 概念。

### 设置存放
- `~/.scheduleapp/settings.json`（`UserSettings`）：上次打开文件夹、外观（`BackgroundColor`/`BackgroundOpacity`/`AccentColor`）、窗口几何、开机启动。
- `~/.scheduleapp/lastWindow.json`（`LastViewSettings`）：记住上次是「日历」还是「事项」子视图。
- 均为写入后再 `File.Move` 的原子写。

### 桌面玻璃层（本应用核心特色，最易踩坑）
`Platform/DesktopGlassBehavior.cs` 是 **Rainmeter 三窗口 z-order 系统的忠实移植**（System 窗口 + Helper 窗口 + 主窗口，HwndSource.AddHook 拦截 z-order 变化与最小化/关闭）。要点：
- WPF 化改动：不用 `SetWindowSubclass`，改 `HwndSource.AddHook`（委托由 HwndSource 保活）。**四边拖拽缩放是窗口级的位置检测**（`Platform/EdgeResizer.cs` + `MainWindow.xaml`）：主面板是 `AllowsTransparency` 分层窗口（+`WS_EX_NOACTIVATE`），`WindowChrome.ResizeBorderThickness`、`WM_NCHITTEST` 边界命中、乃至 `WM_SYSCOMMAND SC_SIZE` 缩放循环在分层窗口上都不可靠（左/上边缘不跟手、单个抓手元素命中也有不对称问题）。做法：整窗铺一层 `#01000000`（1/255 alpha）底衬让透明边缘全部可命中，在窗口 `PreviewMouseLeftButtonDown/Move` 里用 `GetWindowRect`+`GetCursorPos` 按光标位置判定边缘（10 DIP 带，即面板外边距），拖动时用一次 `SetWindowPos`（物理像素、`SWP_NOZORDER|SWP_NOACTIVATE`）同时改位置和尺寸，最小尺寸按 `MinWidth/MinHeight` 钳制。
- `RegisterClassEx` 的 WndProc 委托**必须是 static readonly 字段保活**，否则 GC 回收后窗口消息会触发 `Environment.FailFast` 崩溃（代码里已注明）。
- `WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE` 让面板不抢占焦点、不进任务栏。

### 模糊/背板：不要用 DWM 合成
- `BlurBackdrop`：Rainmeter 式 `DwmEnableBlurBehindWindow` + `DWM_BB_BLURREGION`，DPI 感知，resize 时重建 region。
- `WallpaperBackdrop`：Fences 式壁纸快照模糊（读取 `SPI_GETDESKWALLPAPER` 壁纸位图，降采样再放大≈高斯模糊），**纯客户端内容，RDP/软件渲染下输出一致**。
- **重要约定：不要给主面板加 `DwmSetWindowAttribute`/`SetWindowCompositionAttribute` 的 DWM 合成**——在 RDP/软件合成器下会渲染成不透明黑块（App.xaml.cs 里有专门注释）。模糊优先走 `WallpaperBackdrop`（RDP 安全），本机走 `BlurBackdrop`。

### 系统托盘（已修）
`App.xaml.cs` 的 `CreateTrayIcon()` 里，`TaskbarIcon`（H.NotifyIcon.Wpf）作为一个纯代码字段创建、**未加入任何可视化树**，因此它的 `Loaded` 事件不会触发、`TrayIcon.Create()`（Shell_NotifyIcon NIM_ADD）不会自动执行——必须显式调用 `_tray.ForceCreate(enablesEfficiencyMode: false)` 注册。
- 传 `false` 是刻意为之：不开启 Windows 11 效率模式，保持现有前台/后台行为。
- 图标资产 `Assets/app.ico` 是 **PNG 压缩帧**，`BitmapImage` 能解码（已实测），但注意托盘图标注册依赖上面那句 `ForceCreate`。

### 窗口几何持久化
`Platform/WindowGeometry.cs`：位置存**物理像素**（`GetWindowRect`/`SetWindowPos`），尺寸存 **DIP**（`ActualWidth/Height`），跨 DPI 显示器拖动时位置不受缩放影响。位置超出 `[200,4000]` 视为无效，回退到右下角贴边。
- **分辨率自适应尺寸**：`ComputeAdaptiveSize` 根据当前显示器工作区按比例算尺寸（宽≈工作区 18%、高≈50%，除以 DPI 得 DIP，并在窗口自身 Min 值以上）；`ApplyDefaultPlacement` 同时设尺寸并贴右下角。托盘「重置窗口位置」与首次运行（无几何记录）都走它。

### 视图
`MainWindow` 有日历/事项两个子视图（顶栏分段按钮切换，用 `LastViewSettings` 恢复）。另有 `SettingsWindow`、`ItemEditorWindow`（含 SimpleMarkdown 预览）、`DateJumpWindow`（点击月标题弹出的日期跳转浮层，涉及 `NotifyTransientWindow` 保持 Show Desktop z-order）。

## 常用命令

- 构建：`dotnet build`（根目录，走 slnx）
- 测试：`dotnet test`（xUnit 测试，全部通过）
- 主项目：`src/CalendarApp/CalendarApp.csproj`

## 约定与注意

- UI 文案、错误消息用中文；标识符用英文。
- 对齐/缩进、命名风格跟随现有代码（`ScheduleEvent`、`ScheduleEventItem`、`CalendarDayCell` 等）。
- 数据层只有 `FolderItemStore`（文件夹存储），移除了 SQLite 相关代码后没有其他存储路径。
- 桌面玻璃层改动风险高：涉及 Win32 HWND、z-order、委托保活、DPI，改后需在本机实际运行验证（Show Desktop、拖动多显示器、RDP）。
- `.gitignore` 忽略 `bin/ obj/ publish/ .omo/` 等；`.freebuff/` 未跟踪（本工具状态目录，勿提交）。
