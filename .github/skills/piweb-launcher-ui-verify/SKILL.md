---
name: piweb-launcher-ui-verify
description: 在真实桌面上驱动 PiWeb Launcher 做界面验证(切页、读 UIA 树、截图)。改动 Views/ 下的界面、或需要"确认这个按钮真的在、真的能点"时使用。
---

# PiWeb Launcher 界面验证

用 UI Automation 驱动**真实运行**的应用,而不是靠读 XAML 猜界面对不对。
界面代码(尤其 Avalonia 的样式与模板)改错时编译照样通过、跑起来也可能不报错 ——
只有"去窗口里看一眼"能给出答案。

## 前置

1. 构建并启动(必须有一个**可见的桌面会话**,无头环境跑不了):

   ```powershell
   dotnet build "src/PiWeb Launcher/PiWeb Launcher.csproj" -p:UsedAvaloniaProducts=
   Start-Process "src/PiWeb Launcher/bin/Debug/net10.0/PiWeb Launcher.exe"
   ```

2. 应用是**单实例常驻托盘**程序。若已有实例在跑,新启动的进程会通知它并按“重复启动应用时”
   设置响应后自行退出 —— 因此**不要**用“再启动一个”来确认状态,直接对已有窗口做 UIA 操作。

## ⚠⚠ 结束进程前先认准名字 —— 这一条踩过,代价是打断了用户自己的会话

要重启本应用(重新构建、重新截图都会需要)时,**只能**杀本项目:

```powershell
Get-Process -Name "PiWeb Launcher" -ErrorAction SilentlyContinue | Stop-Process -Force
```

**绝对不要把 `"DSH Launcher"` 写进 `-Name`。** 本仓库与 DSH Launcher 是同一台机器上的两个启动器项目,
而 DSH Launcher 常常正是**当前这个会话的宿主**(它拉起 `dsh web`,会话就在它的 WebView/浏览器里跑)。
实测:一条 `Get-Process -Name "PiWeb Launcher","DSH Launcher" | Stop-Process -Force`
把宿主一起杀掉了,`dsh web` 停止监听 3080,用户不得不手动重启启动器。

它不是"可能有点影响",而是**直接打断用户正在用的东西**:

- 拒绝"顺手清一下相关进程"这种写法 —— 清理探针残留不需要动别的应用。
- 需要按名字批量杀时,先 `Get-Process` **打印出候选**再决定,不要直接管道给 `Stop-Process`。
- 同理:不要 `taskkill /IM node.exe` 之类的宽泛匹配 —— 会话宿主也是 node。

## 让「已安装」列表有内容(否则左栏永远是空的)

沙箱/受限环境里 `pi` 写不了 `~/.pi/agent/settings.json.lock`,于是 `pi list` 只会打印
`No packages installed.` —— 左栏空白,**滚动条相关的布局问题就复现不出来**。
把 agent 目录指到一个可写的位置即可拿到真实列表:

```powershell
$agent = "D:\...\PiWeb Launcher\.probe-agent"
New-Item -ItemType Directory -Force "$agent\npm\node_modules" | Out-Null
Set-Content "$agent\settings.json" -Value '{"packages":["npm:@4fu/pi-pwsh","npm:pi-mcp-adapter"]}'

$env:PI_CODING_AGENT_DIR = $agent         # 子进程要继承它,所以必须在 Start-Process 之前设
Start-Process "src/PiWeb Launcher/bin/Debug/net10.0/PiWeb Launcher.exe"
```

包数给到 10 个以上才会撑出滚动条(这正是"滚动条压住按钮"那类问题的复现条件)。
验证完记得删掉 `.probe-agent`。

## 切页

```powershell
pwsh -NoProfile -File .github/skills/piweb-launcher-ui-verify/scripts/switch-page.ps1 -Page packages
```

支持 `首页` / `包` / `设置`(也可写 `home` / `packages` / `settings`)。
脚本自己处理了这些坑(改动它之前先读文件顶部的清单):

- 导航项只有窗口在前台时才以 `ListItem` 暴露;必须先激活窗口。
- `SelectionItemPattern.Select()` 返回 true 但**页面不切换**,必须真实鼠标点击。
- “Pi Web” WebView 窗口会盖在主窗口上抢走点击,先把它收掉(应用对关闭的处理是 Hide,会话保留)。

> 这个脚本顺带还有个用处:**它能真正把启动器拉到前台** —— 原因见下面「截图」一节。

## 读界面状态(比截图更适合做断言)

```powershell
Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes
$root = [System.Windows.Automation.AutomationElement]::RootElement
$cond = New-Object System.Windows.Automation.PropertyCondition(
    [System.Windows.Automation.AutomationElement]::NameProperty, 'PiWeb Launcher')
$win = $root.FindFirst([System.Windows.Automation.TreeScope]::Children, $cond)

# 按 AutomationId 找控件(x:Name 在 Avalonia 里就是 AutomationId)
$id = New-Object System.Windows.Automation.PropertyCondition(
    [System.Windows.Automation.AutomationElement]::AutomationIdProperty, 'CatalogCountText')
$el = $win.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $id)
$el.Current.Name   # 例如 “已加载 200 / 5371”
```

### 各页可用的常驻 AutomationId(判断"当前在哪一页"就靠它们)

| 页面 | AutomationId | 含义 |
|---|---|---|
| 首页 | `StatusText` | 产品名 + 版本(常驻) |
| 包 | `InstalledCountText` | 左栏“共 N 个”(常驻) |
| 包 | `CatalogCountText` | 右栏“已加载 N / M”(常驻) |
| 设置 | `SettingsScroll` | 设置页的滚动容器(常驻) |

⚠ 别用只在某个状态下才存在的控件判断页面(例如首页的 `PortBox` 只在“已安装且未运行”时存在,
服务运行中就查不到),那会得出"页面没打开"的错误结论。

⚠ `Grid` / `ItemsControl` 这类布局面板**没有 UIA 对等体**,按它们的 `x:Name` 永远查不到;
要断言列表内容,请查列表项(有 `Text` 子元素)或那一行常驻的统计文本。

## 截图

截图是最后手段(人眼确认布局),但对断言没什么用 —— 能读文本就优先读文本。

### 首选:`PrintWindow`(不需要窗口在前台)

**用 `PrintWindow` + `PW_RENDERFULLCONTENT` 而不是 `CopyFromScreen`。** 前者让窗口自己把内容画到
我们给的 DC 上,因此**完全不依赖前台状态、也不会截到别的程序**;后者是从屏幕像素上抠一块,
窗口被遮挡/不在前台时抠到的就是别人。实测:在本机浏览器占着焦点的情况下,
`SetForegroundWindow` 被系统静默拒绝,`CopyFromScreen` 抓出一张"尺寸对、内容全是浏览器"的图,
而同样的窗口用 `PrintWindow` 一次就对。

```powershell
Add-Type -AssemblyName System.Drawing, UIAutomationClient, UIAutomationTypes
Add-Type @'
using System; using System.Runtime.InteropServices;
public class WinCap {
  [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out X r);
  [DllImport("user32.dll")] public static extern IntPtr GetWindowDC(IntPtr h);
  [DllImport("user32.dll")] public static extern int ReleaseDC(IntPtr h, IntPtr dc);
  [DllImport("user32.dll")] public static extern bool PrintWindow(IntPtr h, IntPtr dc, uint flags);
  [StructLayout(LayoutKind.Sequential)] public struct X { public int Left, Top, Right, Bottom; }
  public const uint PW_RENDERFULLCONTENT = 0x00000002;   // 让 DWM 渲染的窗口(含 Mica)也能整幅抓下
}
'@

# 按窗口名找窗口(别用 Get-Process 的 MainWindowHandle,理由见下)
$root = [System.Windows.Automation.AutomationElement]::RootElement
$cond = New-Object System.Windows.Automation.PropertyCondition(
  [System.Windows.Automation.AutomationElement]::NameProperty, 'PiWeb Launcher')
$win = $root.FindFirst([System.Windows.Automation.TreeScope]::Children, $cond)
if (-not $win) { throw '找不到 PiWeb Launcher 窗口(应用没在跑?)' }
$h = [IntPtr]$win.Current.NativeWindowHandle

$r = New-Object WinCap+X; [WinCap]::GetWindowRect($h, [ref]$r) | Out-Null
$bmp = New-Object System.Drawing.Bitmap ($r.Right-$r.Left), ($r.Bottom-$r.Top)
$g = [System.Drawing.Graphics]::FromImage($bmp)
$hdc = $g.GetHdc()
$ok = [WinCap]::PrintWindow($h, $hdc, [WinCap]::PW_RENDERFULLCONTENT)
$g.ReleaseHdc($hdc); $g.Dispose()
if (-not $ok) { throw 'PrintWindow 失败' }
$bmp.Save('docs/screenshots/xxx.png')
```

### ⚠ 两条防守(都实际踩过)

1. **不要用 `Get-Process … .MainWindowHandle` 拿窗口句柄。**
   取到 `MainWindowHandle = 0` 的进程(headless / 刚启动还没建窗口的实例)是常事,
   那时 `GetWindowRect` 返回 `0,0,0,0`,`CopyFromScreen(0,0,0,0, 整屏大小)` 就**把整屏存进了仓库**。
   用 UIA 按窗口名找,拿不到直接失败。
2. **如果非要用 `CopyFromScreen`,必须断言"前台就是启动器"。**
   否则输出是一张"尺寸对但内容全是别人"的图 —— 曾经就这么把一张浏览器截图当成果保存了下来。
   注意 `switch-page.ps1` 的点击**并不是**可靠的"带到前台"手段(浏览器可以抢回去),
   所以才首选 `PrintWindow`。

> 无论用哪种,截完都要**打开图片确认内容**再提交 —— 尺寸正确不代表内容正确。


## 需要联网的页面

「包」页面会去抓 `https://pi.dev/packages`。

- 抓不到时界面会**明确显示原因与重试入口**(`CatalogEmptyText`),不会静默空列表 —— 验证时先看它。
- 网络不可用的环境里,这一页只能验证"空态与错误提示对不对",验证不了列表渲染;
  列表渲染由 `tests/PiWeb Launcher.Tests` 里的真实页面样本(`Fixtures/`)兜住。
- 截图确认列表时**注意别把用户的浏览器窗口截进来**(窗口矩形截图就是为了避免这个)。

## 已验证过的观察结论(2026-09)

- 首页:标题显示 `@agegr/pi-web`,右侧“监听/端口/运行”操作栏与“环境与详情”“标准输出”两处折叠区都在。
- 包页面:左栏“已安装的包”+ 右栏目录两栏并列;统计行会显示
  `已加载 200 / 5384` 与 `取自 https://pi.dev/packages?sort=downloads&page=4` ——
  说明一次加载取的是 4 页(`PiPackageService.PagesPerBatch`)。
- 设置页:四组卡片(服务 / 环境 / 系统托盘 / WebView),“监听地址”“Web 登录密码”
  “允许服务自己打开浏览器”三行在「服务」卡片末尾。
- 窗口图标与任务栏图标都是 pi.dev 官方 favicon 的那个标记(`tools/IconTool` 生成)。
