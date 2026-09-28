using System;
using System.IO;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Input;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;

namespace PiWeb_Launcher.Services
{
    /// <summary>
    /// 系统托盘服务,基于 Avalonia 内置 TrayIcon + NativeMenu。
    /// Avalonia 的 TrayIcon 只提供 Clicked 事件(每次左键点击触发),
    /// 单击/双击区分通过计时器模拟:第一次点击启动双击时长定时器,
    /// 时限内第二次点击判为双击,超时判为单击(与 WinUI 版行为一致)。
    /// 需在 UI 线程创建与使用。
    /// </summary>
    internal sealed class TrayService : IDisposable
    {
        private TrayIcon? _trayIcon;
        private DispatcherTimer? _singleClickTimer;
        private int _lastDoubleClickTickCount = Environment.TickCount;

        // 服务控制菜单项:三个常驻菜单,按服务运行状态切换可见性(见 UpdateServiceMenuState),
        // 状态由 App 在 PiWebService.StateChanged 时同步进来 —— 本类不直接依赖 PiWebService。
        private NativeMenuItem? _serviceStartItem;
        private NativeMenuItem? _serviceStopItem;
        private NativeMenuItem? _serviceRestartItem;

        public event Action? SingleClickRequested;
        public event Action? DoubleClickRequested;

        /// <summary>托盘右键菜单“打开界面”。</summary>
        public event Action? MainWindowRequested;

        /// <summary>托盘右键菜单“WebView中打开”。</summary>
        public event Action? WebViewOpenRequested;

        /// <summary>托盘右键菜单“浏览器中打开”。</summary>
        public event Action? BrowserOpenRequested;

        public event Action? ExitRequested;

        /// <summary>托盘右键菜单“启动服务”(服务未运行时显示)。</summary>
        public event Action? ServiceStartRequested;

        /// <summary>托盘右键菜单“停止服务”(服务运行中时显示)。</summary>
        public event Action? ServiceStopRequested;

        /// <summary>托盘右键菜单“重启服务”(服务运行中时显示)。</summary>
        public event Action? ServiceRestartRequested;

        /// <summary>
        /// 是否启用双击检测(单击延迟判定)。
        /// 双击行为为“无动作”时应置为 false:单击立即触发,不再等待双击间隔。
        /// 仅 UI 线程读写。
        /// </summary>
        public bool EnableDoubleClickDetection { get; set; } = true;

        /// <summary>平台设置取不到时的双击间隔兜底值(毫秒),与 Avalonia.Native 的默认值一致。</summary>
        private const double DefaultDoubleClickTimeMs = 500;

        /// <summary>认为平台返回值可信的上限(毫秒);超出则当作异常值,退回默认值。</summary>
        private const double MaxDoubleClickTimeMs = 5000;

        private TrayService(string tip)
        {
            _trayIcon = new TrayIcon
            {
                Icon = LoadIcon(),
                ToolTipText = tip,
                IsVisible = true,
                Menu = BuildMenu(),
            };

            _trayIcon.Clicked += OnTrayClicked;

            // 把图标挂到 Application 上(必须由 Application 持有才会显示)
            if (Application.Current is not null)
            {
                TrayIcon.SetIcons(Application.Current, new TrayIcons { _trayIcon });
            }

            // 记录实际生效的双击间隔:排查"单击/双击判定"问题时可据此确认平台取值是否合理
            AppLogService.Write($"[托盘] 已创建托盘图标,双击间隔 {GetDoubleClickTime():0.#} 毫秒");
        }

        /// <summary>创建托盘图标;失败时返回 null(不影响主功能)。</summary>
        public static TrayService? TryCreate(string tip)
        {
            try
            {
                return new TrayService(tip);
            }
            catch (Exception)
            {
                return null;
            }
        }

        private NativeMenu BuildMenu()
        {
            var menu = new NativeMenu();
            menu.Items.Add(CreateItem("打开界面", () => MainWindowRequested?.Invoke()));
            menu.Items.Add(CreateItem("WebView中打开", () => WebViewOpenRequested?.Invoke()));
            menu.Items.Add(CreateItem("浏览器中打开", () => BrowserOpenRequested?.Invoke()));
            menu.Items.Add(new NativeMenuItemSeparator());

            // 服务控制:先按“未运行”初始化(只显示“启动服务”),
            // App 在接线完成后会立刻按实际运行状态校正一次
            _serviceStartItem = CreateItem("启动服务", () => ServiceStartRequested?.Invoke());
            _serviceStopItem = CreateItem("停止服务", () => ServiceStopRequested?.Invoke());
            _serviceRestartItem = CreateItem("重启服务", () => ServiceRestartRequested?.Invoke());
            _serviceStopItem.IsVisible = false;
            _serviceRestartItem.IsVisible = false;
            menu.Items.Add(_serviceStartItem);
            menu.Items.Add(_serviceStopItem);
            menu.Items.Add(_serviceRestartItem);

            menu.Items.Add(new NativeMenuItemSeparator());
            menu.Items.Add(CreateItem("退出程序", () => ExitRequested?.Invoke()));
            return menu;
        }

        /// <summary>
        /// 按服务运行状态切换服务控制项的可见性:运行中显示“停止服务/重启服务”,
        /// 未运行时只显示“启动服务”。
        /// <para>
        /// Avalonia 12 的托盘菜单在每次右键时按当前 <see cref="NativeMenuItem.IsVisible"/>
        /// 重建弹层(见 TrayIconImpl.OnRightClicked),因此这里改完,下次打开菜单即生效;
        /// 菜单开着时改动也会经可见性绑定实时反映。仅 UI 线程调用(菜单项是 UI 对象)。
        /// </para>
        /// </summary>
        public void UpdateServiceMenuState(bool isRunning)
        {
            if (_serviceStartItem is not null)
            {
                _serviceStartItem.IsVisible = !isRunning;
            }

            if (_serviceStopItem is not null)
            {
                _serviceStopItem.IsVisible = isRunning;
            }

            if (_serviceRestartItem is not null)
            {
                _serviceRestartItem.IsVisible = isRunning;
            }
        }

        private static NativeMenuItem CreateItem(string header, Action onClick)
        {
            var item = new NativeMenuItem(header);
            item.Click += (_, _) => onClick();
            return item;
        }

        /// <summary>
        /// 处理托盘左键点击:启用双击检测时,第一次点击挂起等待,
        /// 双击时长内的第二次点击触发双击事件,超时则触发单击事件。
        /// </summary>
        private void OnTrayClicked(object? sender, EventArgs e)
        {
            if (!EnableDoubleClickDetection)
            {
                SingleClickRequested?.Invoke();
                return;
            }

            if (_singleClickTimer is not null)
            {
                // 双击时长内第二次点击:取消挂起的单击,触发双击
                _singleClickTimer.Stop();
                _singleClickTimer = null;
                _lastDoubleClickTickCount = Environment.TickCount;
                DoubleClickRequested?.Invoke();
                return;
            }

            // 双击序列结束后紧跟的第三次点击不应被误判(防御性窗口)
            if (unchecked(Environment.TickCount - _lastDoubleClickTickCount) < GetDoubleClickTime())
            {
                return;
            }

            var timer = new DispatcherTimer
            {
                Interval = TimeSpan.FromMilliseconds(GetDoubleClickTime()),
            };
            timer.Tick += (_, _) =>
            {
                timer.Stop();
                if (ReferenceEquals(_singleClickTimer, timer))
                {
                    _singleClickTimer = null;
                    SingleClickRequested?.Invoke();
                }
            };
            _singleClickTimer = timer;
            timer.Start();
        }

        private static WindowIcon? LoadIcon()
        {
            // logo-512.png 已嵌入程序集资源(avares://),见 AppIcon.LoadLogo512;失败时返回 null
            return AppIcon.LoadLogo512();
        }

        public void Dispose()
        {
            _singleClickTimer?.Stop();
            _singleClickTimer = null;

            if (_trayIcon is not null)
            {
                _trayIcon.Clicked -= OnTrayClicked;
                if (Application.Current is not null)
                {
                    TrayIcon.SetIcons(Application.Current, new TrayIcons());
                }

                _trayIcon.Dispose();
                _trayIcon = null;
            }
        }

        /// <summary>
        /// 系统双击间隔(毫秒)。用 Avalonia 的跨平台平台设置,不再直接 P/Invoke user32:
        /// Windows 上 <see cref="IPlatformSettings.GetDoubleTapTime"/> 内部就是 GetDoubleClickTime(),
        /// 因此行为与改造前一致;macOS/Linux 由各自平台实现提供,
        /// 不会因缺少 user32.dll 在点击托盘时抛 DllNotFoundException。
        /// </summary>
        private static double GetDoubleClickTime()
        {
            try
            {
                var time = Application.Current?.PlatformSettings?.GetDoubleTapTime(PointerType.Mouse);
                if (time is { TotalMilliseconds: > 0 and <= MaxDoubleClickTimeMs })
                {
                    return time.Value.TotalMilliseconds;
                }
            }
            catch (Exception)
            {
                // 平台设置不可用(极早期/受限环境)时退回通用默认值
            }

            return DefaultDoubleClickTimeMs;
        }
    }
}
