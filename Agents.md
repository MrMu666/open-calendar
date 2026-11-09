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
- `~/.calendarapp/settings.json`（`UserSettings`）：上次打开文件夹、外观（`BackgroundColor`/`BackgroundOpacity`/`AccentColor`）、开机启动。**不再存窗口几何**——窗口位置/尺寸已移入 lastWindow.json。
- `~/.calendarapp/lastWindow.json`（`LastViewSettings`）：记住上次是「日历」还是「事项」子视图，以及**按显示器分辨率+DPI 分组的窗口几何**（`Geometry` 字典，key 为 `{workW}x{workH}@{dpi}`）。
- 均为写入后再 `File.Move` 的原子写。
- **禁止 `new LastViewSettings{...}.Save()`**：这会用一个只含 `View` 的全新对象覆盖 lastWindow.json，清空 `Geometry` 字典（曾导致窗口位置/大小永远记录不下来）。子视图切换必须改共享实例 `_services.LastView.View` 再 `Save()`（`MainViewModel.SetView` 已有此注释）。
- **分辨率/DPI 切换自动恢复**：`WindowGeometry.OnMovedOrResized` 在每次 `LocationChanged` 时检测当前 key 是否变化——变了就查找该分辨率的历史几何记录并恢复；无记录则自动 dock 右下角。重置窗口位置只清空当前分辨率下的那条记录。
- **RDP / 热插拔 / 会话恢复必须主动对齐**：这些场景不触发窗口的 `LocationChanged/SizeChanged`，但会改变显示器几何。`App` 监听 `SystemEvents.DisplaySettingsChanged` + `SessionSwitch`（RemoteConnect/ConsoleConnect/SessionLogon），去抖 700ms 后再补一次 1600ms 跟进检查，调 `WindowGeometry.Reconcile` 重新对齐（恢复该分辨率记录或 dock 右下角）。

### 桌面玻璃层（本应用核心特色，最易踩坑）
`Platform/DesktopGlassBehavior.cs` 是 **Rainmeter 三窗口 z-order 系统的忠实移植**（System 窗口 + Helper 窗口 + 主窗口，HwndSource.AddHook 拦截 z-order 变化与最小化/关闭）。要点：
- WPF 化改动：不用 `SetWindowSubclass`，改 `HwndSource.AddHook`（委托由 HwndSource 保活）。**四边拖拽缩放是窗口级的位置检测**（`Platform/EdgeResizer.cs` + `MainWindow.xaml`）：主面板是 `AllowsTransparency` 分层窗口（+`WS_EX_NOACTIVATE`），`WindowChrome.ResizeBorderThickness`、`WM_NCHITTEST` 边界命中、乃至 `WM_SYSCOMMAND SC_SIZE` 缩放循环在分层窗口上都不可靠（左/上边缘不跟手、单个抓手元素命中也有不对称问题）。做法：整窗铺一层 `#01000000`（1/255 alpha）底衬让透明边缘全部可命中，在窗口 `PreviewMouseLeftButtonDown/Move` 里用 `GetWindowRect`+`GetCursorPos` 按光标位置判定边缘（10 DIP 带，即面板外边距），拖动时用一次 `SetWindowPos`（物理像素、`SWP_NOZORDER|SWP_NOACTIVATE`）同时改位置和尺寸，最小尺寸按 `MinWidth/MinHeight` 钳制。
- `RegisterClassEx` 的 WndProc 委托**必须是 static readonly 字段保活**，否则 GC 回收后窗口消息会触发 `Environment.FailFast` 崩溃（代码里已注明）。
- `WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE` 让面板不抢占焦点、不进任务栏。

### 模糊/背板：壁纸截取 + WPF BlurEffect（RDP 安全，无 DWM）

背板由四个图层叠加（自下而上）：

1. **整窗 1/255 alpha 底衬** — 命中测试层，让透明边缘全部可命中（缩放用）
2. **壁纸+色调层**（Grid 直接子元素，`Margin=10`，与内容面板同尺寸/同圆角）：
   - **壁纸模糊层** — `WallpaperImage`（`Stretch=Fill`），`WallpaperBackdrop.CropForWindow()`
     截取，`BlurEffect` 模糊。`OpacityMask`（`DrawingBrush` + `RectangleGeometry(16,16)`）
     精确裁剪四个圆角——**不用 Clip 属性**（会膨胀命中几何、阻断 EdgeResizer）
   - **用户颜色层** — `Border`（`CornerRadius=16` 自身裁切圆角），`SolidColorBrush`
3. **面板内容** — 日历/事项等 UI 控件

`WallpaperBackdrop` 要点（v4）：
- **图像源**：总是从 `%AppData%\Microsoft\Windows\Themes\TranscodedWallpaper` 加载——
  Windows 每次换壁纸（手动/幻灯片/主题切换）都重写这个文件，它永远是当前桌面位图。
  **不使用 `IDesktopWallpaper.GetWallpaper` 拿壁纸路径**——幻灯片轮换时 COM 可能返回
  旧路径（写文件是异步的），导致永远显示第一张壁纸
- **WPF 缓存绕行**：`LoadWallpaper` 用 `FileStream` 读入后构造 `BitmapImage`（
  `StreamSource` + `OnLoad`），**不用 `UriSource`**——即使设了 `CacheOption=OnLoad`，
  WPF 内部 URI 缓存也可能在同一个 URI 反复加载时返回已缓存的实例。
  TranscodedWallpaper 是被就地覆盖的，必须每次读盘
- **显示器 RECT**：`IDesktopWallpaper` COM 仍用于按窗口中心点匹配显示器 RECT（只用于
  裁剪原点计算），不再从 COM 拿壁纸路径
- **无 DPI 缩放**：壁纸像素直接按布局模式映射到显示器物理像素。
  WPF 的 `Stretch=Fill` 自动处理 DIP↔物理像素转换——截取尺寸=窗口物理尺寸，
  在任何 DPI 下 GPU 都做 1:1 屏幕像素映射
- **布局感知**：支持 Fill/Fit/Stretch/Center/Tile 五种壁纸布局模式
- **壁纸变化感知**：`SystemEvents.UserPreferenceChanged`（Desktop 类别）+ `FileSystemWatcher`
  监听 `TranscodedWallpaper` 文件。两者都通过 300ms 去抖（`DispatcherTimer`）合并为
  一次 `WallpaperChanged` 事件，且 `FileSystemWatcher` 回调通过 `Dispatcher.BeginInvoke`
  封送到 UI 线程（避免跨线程 COM 死锁）
- **重试机制**：`BlurBackdrop.RefreshBlur` 在 `CropForWindow` 返回 null 时自动重试最多
  3 次（幻灯片轮播瞬间文件可能正在写入）
- **性能**：`BlurBackdrop.ScheduleRefresh` 100ms 去抖（窗口移动/缩放）；
  WPF `BlurEffect` 走 GPU 着色器路径

**重要约定**：
- **不要重新引入 DWM 合成**（`DwmEnableBlurBehindWindow`、`SetWindowCompositionAttribute`、
  `DwmSetWindowAttribute`）——在 RDP/软件合成器下渲染为不透明黑块
- `BlurBackdrop` 已移除所有 DWM/GDI 代码（`CreateRectRgn`、`DeleteObject` 等），只管理 WPF 层
- **不要给壁纸做 DPI 缩放**（`GetWallpaperDpiScale` 已删除）——壁纸像素直接对物理像素，
  Stretch=Fill 自动处理 DIP 转换。`Stretch=None` + 手动 DIP 重缩放是错误的路径
- 壁纸监听必须保留 `FileSystemWatcher` 路径（幻灯片轮播刚需），且必须封送到 UI 线程
- **壁纸图像源永远用 `TranscodedWallpaper`**（不用 `IDesktopWallpaper.GetWallpaper` 路径）——
  后者在幻灯片轮播下返回旧路径，导致永远显示第一张壁纸
- **加载壁纸永远用 `FileStream`（不用 `UriSource`）**——避免 WPF URI 内部缓存
- **壁纸+色调层必须设 `IsHitTestVisible="False"`**——避免阻挡内容面板交互
- **壁纸层圆角裁剪必须用 `OpacityMask`**（`DrawingBrush` + `RectangleGeometry(RadiusX,RadiusY)`），
  **严禁使用 `UIElement.Clip` 属性**——`Clip` 属性会膨胀元素的命中测试几何体，覆盖面板透明
  边缘区域，阻断 `EdgeResizer` 的窗口级 `PreviewMouseLeftButtonDown` 边缘检测
  （`IsHitTestVisible=False` 也无法消除此影响）
- **壁纸层本身不用包装 Border**——直接作为 Grid 子元素（`Margin=10`），避免多余的视觉树
  节点引入意外命中测试行为；色调层用 `Border` 自身 `CornerRadius=16` 裁切即可

### 已知性能债务（可优化但暂不紧急）

当前每次 `CropForWindow` 都 `File.ReadAllBytes` + `BitmapImage` 解码整张 TranscodedWallpaper
（~200KB−1MB，SSD 上 <1ms I/O，1080p 解码约 5−15ms）。窗口缩放/拖动时 `ScheduleRefresh`
100ms 去抖，峰值约 150ms/秒 CPU。**优化方案**：给解码结果加一层进程内缓存——只在
`WallpaperChanged` 事件（壁纸文件真的变了）或首次加载时才重新读盘解码；窗口移动/缩放时
只从已缓存的 `BitmapSource` 重新做 `CroppedBitmap` 裁剪（<1ms）。实施时注意：
- 缓存需感知文件修改时间（`TranscodedWallpaper` 被就地重写）
- 多窗口场景共享同一份缓存

### 系统托盘（已修）
`App.xaml.cs` 的 `CreateTrayIcon()` 里，`TaskbarIcon`（H.NotifyIcon.Wpf）作为一个纯代码字段创建、**未加入任何可视化树**，因此它的 `Loaded` 事件不会触发、`TrayIcon.Create()`（Shell_NotifyIcon NIM_ADD）不会自动执行——必须显式调用 `_tray.ForceCreate(enablesEfficiencyMode: false)` 注册。
- 传 `false` 是刻意为之：不开启 Windows 11 效率模式，保持现有前台/后台行为。
- 图标资产 `Assets/app.ico` 是 **PNG 压缩帧**，`BitmapImage` 能解码（已实测），但注意托盘图标注册依赖上面那句 `ForceCreate`。

### 窗口几何持久化
`Platform/WindowGeometry.cs`：位置存**物理像素**（`GetWindowRect`/`SetWindowPos`），尺寸存 **DIP**（`ActualWidth/Height`），跨 DPI 显示器拖动时位置不受缩放影响。
- **`GetMonitorInfo` 必须走 `TryGetMonitorInfo` 包装**：Win32 要求调用前先填 `MONITORINFO.cbSize = Marshal.SizeOf<MONITORINFO>()`，否则调用失败、所有几何查询静默短路（曾导致 lastWindow.json 的 Geometry 永远为空、重置/自适应都不生效）。不要直接裸调 `GetMonitorInfo`。
- **按分辨率分组**：每个 `{workW}x{workH}@{dpi}` key 独立存一条几何记录于 `LastViewSettings.Geometry`。拖动窗口到不同分辨率/DPI 的显示器时自动恢复该配置下的上次位置/尺寸，无记录则 dock 右下角。
- **分辨率自适应尺寸**：`ComputeAdaptiveSize` 根据当前显示器工作区按比例算尺寸（宽≈工作区 18%、高≈50%，除以 DPI 得 DIP，并在窗口自身 Min 值以上）；`ApplyDefaultPlacement` 同时设尺寸并贴右下角。托盘「重置窗口位置」与首次运行（无几何记录）都走它，且**只清空当前分辨率的那条记录**。
- **位置钳制**：`ClampToWorkArea` 确保恢复的位置至少 80px 留在屏幕内，不会跑到屏幕外。

### 视图
`MainWindow` 有日历/事项两个子视图（顶栏分段按钮切换，用 `LastViewSettings` 恢复）。另有 `SettingsWindow`、`ItemEditorWindow`（含 SimpleMarkdown 预览）、`DateJumpWindow`（点击月标题弹出的日期跳转浮层，涉及 `NotifyTransientWindow` 保持 Show Desktop z-order）。

- **日历选中状态与刷新边界**：`MainViewModel.Refresh(resetSelection)` 带参数——事项数据变化后，`AddItem`/`UpdateItem`/`DeleteItem` 传 `resetSelection: false`，保持用户当前选中的日期/月份（在过去的某天右键「设为已完成/长期事项/删除」后**不会**跳回今天，方便继续处理该天其他事项）；`resetSelection: true`（默认）才重置回今天，用于打开/切换存储文件夹、应用启动。顶栏「事项」按钮（`ShowTasks`）也会主动回到今天并清除日历选中态。
- **日历事项提示点**：日期格子下的小圆点由 `CalendarDayCell.EventDotBrush` 决定颜色——该日有未到期事项（`EndsAt` 为空或未过）= 红色 `#FF4D4D`，事项均已到期 = 绿色 `#03D96B`，色值与事项列表中结束日期（红）/开始日期（绿）文字一致；圆点与农历字样间距由 XAML 中 `Margin="0,3,0,0"` 控制。

- **设置页选色 Popup**：`SettingsWindow.xaml` 的调色板 `Popup` 用 `StaysOpen="True"`（不用 `StaysOpen="False"`），因为点击色块预览触发 `MouseLeftButtonDown` 打开 Popup 的一瞬间，WPF 会判定该点击在 Popup 之外从而立即关闭它。关闭逻辑改为手动管理：
  - 点击色块预览：同目标再次点击关闭（切换），新目标切换并打开
  - 点击色块选色后关闭
  - 点击 Popup 外任意位置关闭（`StackPanel.PreviewMouseLeftButtonDown` 遍历视觉树判定）
- **ItemEditorWindow**：定位在构造函数中设 `Left`/`Top`（不等到 `ContentRendered`，消除首次渲染闪烁），并用 `SystemParameters.WorkArea` 钳制边缘不超出屏幕；标题框回车触发保存；去掉了「P1-P4 仅用于文件名」提示文字
- **应用弹出的临时窗口约定（SettingsWindow / ItemEditorWindow / DateJumpWindow）**：主面板在 Win+D 期间被 `DesktopGlassBehavior` 每 250ms 重申到 TOPMOST 带正下方（即所有普通窗口之上），所以任何从面板打开的临时窗口**必须**：
  1. `Topmost="True"`——进 TOPMOST 带，否则会被 250ms 的 z-order 重申压到面板后面（设置窗口曾因此被遮）；
  2. Show 前调 `DesktopGlassBehavior.NotifyTransientWindow(mainWindow, true)`、Closed 时调 `false`——防止临时窗口持有前台时 Show Desktop 状态被误判翻转。

## 常用命令

- 构建：`dotnet build`（根目录，走 slnx）
- 测试：`dotnet test`（xUnit 测试，全部通过）
- 主项目：`src/CalendarApp/CalendarApp.csproj`

## 约定与注意

- **Git 分支约定**：仓库只有 `master`（默认/主分支）一个分支，**没有 `main`，也没有 `dev`**。说「main」指 `master`；所有提交直接在 `master` 上进行。推送远程、打发布标签（`v*`）都需用户明确指令；标签会触发 `.github/workflows/release.yml` 自动构建 Windows 安装包并创建 Release。
- UI 文案、错误消息用中文；标识符用英文。
- 对齐/缩进、命名风格跟随现有代码（`ScheduleEvent`、`ScheduleEventItem`、`CalendarDayCell` 等）。
- 数据层只有 `FolderItemStore`（文件夹存储），移除了 SQLite 相关代码后没有其他存储路径。
- 桌面玻璃层改动风险高：涉及 Win32 HWND、z-order、委托保活、DPI，改后需在本机实际运行验证（Show Desktop、拖动多显示器、RDP）。
- `.gitignore` 忽略 `bin/ obj/ publish/ .omo/` 等；`.freebuff/` 未跟踪（本工具状态目录，勿提交）。
