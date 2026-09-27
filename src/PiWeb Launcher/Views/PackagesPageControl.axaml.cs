using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Threading;
using FluentAvalonia.UI.Controls;
using PiWeb_Launcher.Models;
using PiWeb_Launcher.Services;
using PiWeb_Launcher.Views.Shared;

namespace PiWeb_Launcher.Views
{
    /// <summary>
    /// 包页面:管理 pi 的包(扩展 / 技能 / 主题 / 提示词模板)。
    /// <para>
    /// 数据来源三分,各自独立(改的时候别把它们搅在一起):
    /// <list type="bullet">
    /// <item><b>已安装</b> —— <c>pi list</c> 的输出(<see cref="PiPackageService.RefreshInstalledAsync"/>),
    /// 版本号由启动器读 pi 的 npm 目录补上。</item>
    /// <item><b>目录</b> —— https://pi.dev/packages 的网页(<see cref="PiPackageService.LoadCatalogFirstPageAsync"/>),
    /// 增量加载(每次若干页)。</item>
    /// <item><b>操作输出</b> —— <c>pi install</c> / <c>pi remove</c> / <c>pi update</c> 的实时输出。</item>
    /// </list>
    /// </para>
    /// <para>
    /// 为什么服务进程在跑的时候装完包要提示重启:pi 的扩展是在**会话启动时**加载的,
    /// 而 <c>pi install</c> 只改设置与 node_modules —— 运行中的 pi-web 不会自动装载新扩展。
    /// 这一点由服务侧记账(<see cref="PiInstalledPackage.AwaitingRestart"/>),本页只负责显示与提示。
    /// </para>
    /// </summary>
    public partial class PackagesPageControl : UserControl
    {
        private readonly PiPackageService _packages = PiPackageService.Instance;
        private readonly PiWebService _service = PiWebService.Instance;

        /// <summary>卸载按钮「再点一次确认」的有效期(超过则自动退回,避免一直挂着等误触)。</summary>
        private static readonly TimeSpan UninstallConfirmWindow = TimeSpan.FromSeconds(6);

        /// <summary>已安装列表的筛选与显示(整批替换,刷新后是新对象 —— 界面状态挂在对象上,不缓存)。</summary>
        private IReadOnlyList<PiInstalledPackage> _installedView = [];

        /// <summary>界面是否已挂到可视树上(拦截构造期的控件事件)。</summary>
        private bool _pageReady;

        /// <summary>回填控件期间置位,避免把"回填"当成用户操作。</summary>
        private bool _initializing;

        /// <summary>目录查询等待期间,用户又改了筛选条件 —— 结果回来时按最新条件决定要不要显示。</summary>
        private int _catalogRequestId;

        private readonly LogAppendThrottle _operationThrottle;
        private readonly CopyFeedback _operationLogCopy;
        private readonly CopyFeedback _warningCopy;

        /// <summary>装卸操作的输出全文(服务只把新行推过来,这里自己累积)。</summary>
        private readonly System.Text.StringBuilder _operationLog = new();

        public PackagesPageControl()
        {
            InitializeComponent();

            this._operationThrottle = new LogAppendThrottle(this.UpdateOperationLog);
            this._operationLogCopy = new CopyFeedback(this.CopyOperationLogButtonText, "复制");
            this._warningCopy = new CopyFeedback(this.WarningActionText, "复制安装命令");

            AttachedToVisualTree += OnPageAttached;
            DetachedFromVisualTree += OnPageDetached;

            // Esc 关输出浮窗:用 Tunnel 抢在控件之前处理(与 DSH 插件页同一做法)
            this.AddHandler(KeyDownEvent, this.OnPagePreviewKeyDown, RoutingStrategies.Tunnel);
        }

        /// <summary>
        /// 浮窗开着时按 Esc 关掉它,并阻止事件继续传播(否则会冒泡到导航控件)。
        /// 浮窗没开时**不处理**,保持应用原有的 Esc 行为不变。
        /// </summary>
        private void OnPagePreviewKeyDown(object? sender, KeyEventArgs e)
        {
            if (e.Key != Key.Escape || !this.OperationLogFlyout.IsVisible)
            {
                return;
            }

            this.SetOperationLogVisible(false);
            e.Handled = true;
        }

        private async void OnPageAttached(object? sender, EventArgs e)
        {
            this._packages.CatalogUpdated += this.Packages_CatalogUpdated;
            this._packages.InstalledUpdated += this.Packages_InstalledUpdated;
            this._packages.OperationOutput += this.Packages_OperationOutput;
            this._packages.OperationStateChanged += this.Packages_OperationStateChanged;
            this._service.StateChanged += this.Service_StateChanged;

            this._initializing = true;
            try
            {
                // 首选/排序的当前值来自设置(与目录站点上的表单参数同一套)
                var settings = SettingsService.Instance.Settings;
                this.CatalogTypeCombo.SelectedIndex = CatalogTypeOptions.ToIndex(settings.PackageCatalogType);
                this.CatalogSortCombo.SelectedIndex = CatalogSortOptions.ToIndex(settings.PackageCatalogSort);
                this.LoadMoreButton.IsVisible = false;
            }
            finally
            {
                this._initializing = false;
            }

            this._pageReady = true;

            // 先盘点已安装(快),再取目录第一页(慢);目录回来时就能带上"已安装"徽标
            await this.RefreshInstalledAsync();
            await this.ReloadCatalogAsync();
            this.UpdateWarningBanner();
        }

        private void OnPageDetached(object? sender, EventArgs e)
        {
            this._packages.CatalogUpdated -= this.Packages_CatalogUpdated;
            this._packages.InstalledUpdated -= this.Packages_InstalledUpdated;
            this._packages.OperationOutput -= this.Packages_OperationOutput;
            this._packages.OperationStateChanged -= this.Packages_OperationStateChanged;
            this._service.StateChanged -= this.Service_StateChanged;
        }

        // ---- 顶部提示条 ----

        /// <summary>
        /// 顶部提示条:只在**确实缺东西**时出现(pi 命令两条来源都没有 / pi-web 没装 / 读取失败)。
        /// 三种情况的操作入口不同,所以按钮文案与动作都跟着变。
        /// </summary>
        private async void UpdateWarningBanner()
        {
            var snapshot = this._packages.Installed;

            // 一条 pi 都没有:整页的读写都做不了,这是最需要说清楚的一种情况。
            // ⚠ 注意这只在"PATH 上没有 pi **且** pi-web 自带的那份也找不到"时才成立 ——
            // 装了 Pi Web 的用户本来就有自带的 pi,不该再让他去装一个(见 PiCli.TryBuildPiCommandAsync)。
            if (!snapshot.PiCliAvailable)
            {
                this.ShowWarning(
                    snapshot.Warning is { Length: > 0 } locateFailure
                        ? "没有可用的 pi 命令,无法读写包设置。" + locateFailure
                        : "没有可用的 pi 命令。pi 的包管理(安装/卸载/清单)全部经 pi 命令行完成;"
                            + "装好 Pi Web 即可(它自带一份完整的 pi),或单独安装:"
                            + "npm install -g @earendil-works/pi-coding-agent",
                    "复制安装命令",
                    () => _ = this._warningCopy.CopyAsync(this, "npm install -g @earendil-works/pi-coding-agent"));
                return;
            }

            // pi 在跑,但读取过程本身有抱怨(最常见:pi 自己的设置文件被锁/无写权限)。
            // ⚠ 必须单独说这一句:这种失败下 pi **照样返回 0** 并打印 "No packages installed.",
            // 不提示的话界面会理直气壮地显示"你没有装任何包"。
            // 原因已经压成一行(见 PiPackageService.DescribeFailure),横幅是单行省略的。
            if (snapshot.Warning is { Length: > 0 } warning && snapshot.Installed.Count == 0)
            {
                this.ShowWarning(
                    "无法读取 pi 的包清单,列表可能不完整。pi 报告的原因:" + warning,
                    "重新读取",
                    () => _ = this.RefreshInstalledAsync());
                return;
            }

            if (this._service.InstalledVersion is null)
            {
                await this._service.GetInstalledVersionAsync();
            }

            if (this._service.InstalledVersion is null)
            {
                this.ShowWarning(
                    "尚未安装 Pi Web(@agegr/pi-web):包页面读取 pi 的设置与目录不依赖它,但装好的包要在 Web 界面里生效就需要它。",
                    "去首页安装",
                    NavigateToHome);
                return;
            }

            // 一切正常时不再展示横幅 —— 但"用的是 pi-web 自带的 pi"这件事必须留在界面上,
            // 否则用户会奇怪"我明明没装 pi,这里怎么在装包"。放在左栏的说明行里(常驻,不占横幅)。
            this.HideWarning();
            this.UpdatePiSourceHint();
        }

        /// <summary>
        /// 左栏计数行下方的"用的哪一份 pi"说明。
        /// 只在用自带那份时出现 —— 用 PATH 上的全局 pi 是默认情况,不必每次都念叨。
        /// </summary>
        private void UpdatePiSourceHint()
        {
            var bundled = this._packages.IsUsingBundledPi;
            this.PiSourceText.IsVisible = bundled;
            this.PiSourceText.Text = bundled
                ? "本机 PATH 上没有 pi 命令,正在使用 Pi Web 自带的那份 pi(版本随 pi-web)。"
                : string.Empty;
        }

        private void ShowWarning(string text, string actionText, Action action)
        {
            this.WarningText.Text = text;
            this.WarningActionText.Text = actionText;
            this._warningAction = action;
            this.WarningActionButton.IsVisible = true;
            this.WarningBanner.IsVisible = true;
        }

        private void HideWarning()
        {
            this.WarningBanner.IsVisible = false;
            this._warningAction = null;
        }

        private Action? _warningAction;

        private void OnWarningActionClick(object? sender, RoutedEventArgs e) => this._warningAction?.Invoke();

        /// <summary>把用户送到首页(未安装 pi-web 时提示条上的"去首页安装")。导航由主窗口统一负责。</summary>
        private static void NavigateToHome() => MainWindow.NavigateTo("home");

        // ---- 已安装 ----

        private async Task RefreshInstalledAsync()
        {
            this.InstalledProgress.IsActive = true;
            try
            {
                await this._packages.RefreshInstalledAsync();
            }
            catch (Exception ex)
            {
                PiWebService.Instance.AppendSystemLog($"[包] 读取已安装包失败: {ex.Message}");
            }
            finally
            {
                this.InstalledProgress.IsActive = false;
            }

            this.UpdateInstalledView();
            this.UpdateWarningBanner();
        }

        private void Packages_InstalledUpdated(PiPackageSnapshot snapshot)
            => Dispatcher.UIThread.Post(this.UpdateInstalledView);

        private void UpdateInstalledView()
        {
            var all = this._packages.Installed.Installed;
            var filter = this.InstalledSearchBox.Text?.Trim() ?? string.Empty;

            IEnumerable<PiInstalledPackage> query = all;
            if (filter.Length > 0)
            {
                query = query.Where(item =>
                    item.Name.Contains(filter, StringComparison.OrdinalIgnoreCase)
                    || item.Source.Contains(filter, StringComparison.OrdinalIgnoreCase)
                    || item.AuthorTextOrEmpty().Contains(filter, StringComparison.OrdinalIgnoreCase));
            }

            this._installedView = query as IReadOnlyList<PiInstalledPackage> ?? query.ToList();

            // 列表整体替换:先摘掉旧对象的订阅再挂新的(界面状态挂对象上,旧对象作废)
            this.InstalledList.ItemsSource = null;
            foreach (var item in this._observedInstalled)
            {
                item.PropertyChanged -= this.Installed_PropertyChanged;
            }

            this._observedInstalled.Clear();
            foreach (var item in this._installedView)
            {
                item.PropertyChanged += this.Installed_PropertyChanged;
                this._observedInstalled.Add(item);
            }

            this.InstalledList.ItemsSource = this._installedView;

            var total = all.Count;
            var shown = this._installedView.Count;
            this.InstalledCountText.Text = filter.Length == 0
                ? $"共 {total} 个"
                : $"匹配 {shown} / {total} 个";

            var empty = this._installedView.Count == 0;
            this.InstalledScroll.IsVisible = !empty;
            this.InstalledEmptyText.Text = total == 0
                ? "还没有安装任何 pi 包。切到右边「目录」里挑一个装上,或用上面的「手动安装」。"
                : "没有匹配的包。";
            this.InstalledEmptyText.IsVisible = empty;

            this.UpdateAllButton.IsEnabled = total > 0 && !this._packages.IsBusy;
        }

        /// <summary>正在观察的已安装条目(用于刷新时退订,避免旧对象继续往界面推通知)。</summary>
        private readonly List<PiInstalledPackage> _observedInstalled = [];

        private void Installed_PropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
        {
            // 「确认卸载」有有效期:超时自动退回,免得一直挂着一个"点一下就删"的按钮
            if (e.PropertyName == nameof(PiInstalledPackage.IsPendingUninstall)
                && sender is PiInstalledPackage { IsPendingUninstall: true } entry)
            {
                var token = ++this._uninstallArmId;
                _ = this.DisarmAfterDelayAsync(entry, token);
            }
        }

        private int _uninstallArmId;

        private async Task DisarmAfterDelayAsync(PiInstalledPackage entry, int token)
        {
            await Task.Delay(UninstallConfirmWindow);
            if (token == this._uninstallArmId && entry.IsPendingUninstall)
            {
                Dispatcher.UIThread.Post(() => entry.IsPendingUninstall = false);
            }
        }

        private void OnInstalledSearchChanged(object? sender, TextChangedEventArgs e)
        {
            if (!this._pageReady || this._initializing)
            {
                return;
            }

            this.UpdateInstalledView();
        }

        private async void OnRefreshInstalledClick(object? sender, RoutedEventArgs e)
        {
            await this.RefreshInstalledAsync();
        }

        /// <summary>
        /// 卸载:第一次点击只是把按钮变成「确认卸载」,第二次才真的执行 ——
        /// pi 的包是**能执行代码**的,误点一下就删掉不该发生。
        /// </summary>
        private async void OnUninstallClick(object? sender, RoutedEventArgs e)
        {
            if (sender is not Button { DataContext: PiInstalledPackage entry })
            {
                return;
            }

            if (!entry.IsPendingUninstall)
            {
                entry.IsPendingUninstall = true;
                return;
            }

            entry.IsPendingUninstall = false;
            this.ShowOperationLog();
            this.AppendOperationLine($"[卸载] {entry.Source}");

            var ok = await this._packages.UninstallAsync(entry.Source);
            if (ok && this._service.IsRunning)
            {
                await this.AskRestartAsync($"已卸载 {entry.Name}。运行中的 Pi Web 服务要重启后才会移除它。");
            }
        }

        private async void OnUpdateAllClick(object? sender, RoutedEventArgs e)
        {
            this.ShowOperationLog();
            await this._packages.UpdateAllAsync();
        }

        // ---- 目录 ----

        /// <summary>按当前搜索/类型/排序重新取第一页。</summary>
        private async Task ReloadCatalogAsync()
        {
            var requestId = ++this._catalogRequestId;
            this.CatalogProgress.IsActive = true;
            this.LoadMoreButton.IsEnabled = false;

            try
            {
                await this._packages.LoadCatalogFirstPageAsync(
                    this.CatalogSearchBox.Text ?? string.Empty,
                    CatalogTypeOptions.FromIndex(this.CatalogTypeCombo.SelectedIndex),
                    CatalogSortOptions.FromIndex(this.CatalogSortCombo.SelectedIndex));
            }
            catch (Exception ex)
            {
                if (requestId == this._catalogRequestId)
                {
                    this.ShowCatalogError(ex);
                }
            }
            finally
            {
                if (requestId == this._catalogRequestId)
                {
                    this.CatalogProgress.IsActive = false;
                }
            }
        }

        private async Task LoadMoreCatalogAsync()
        {
            var requestId = ++this._catalogRequestId;
            this.CatalogProgress.IsActive = true;
            this.LoadMoreButton.IsEnabled = false;

            try
            {
                await this._packages.LoadMoreAsync();
            }
            catch (Exception ex)
            {
                if (requestId == this._catalogRequestId)
                {
                    this.ShowCatalogError(ex);
                }
            }
            finally
            {
                if (requestId == this._catalogRequestId)
                {
                    this.CatalogProgress.IsActive = false;
                }
            }
        }

        private void ShowCatalogError(Exception ex)
        {
            this.CatalogScroll.IsVisible = false;
            this.CatalogEmptyText.IsVisible = true;
            this.LoadMoreButton.IsVisible = false;
            this.CatalogEndText.IsVisible = false;
            this.CatalogCountText.Text = string.Empty;
            this.CatalogEmptyText.Text =
                "读取 pi.dev/packages 失败:" + ex.Message + "\r\n\r\n"
                + "可以点「刷新」重试;若本机需要代理才能访问外网,请在设置页填写代理地址。"
                + "也可以直接点「打开目录站点」到浏览器里自己看。";
            AppLogService.Write($"[目录] 读取失败: {ex}");
        }

        private void Packages_CatalogUpdated(PiPackageCatalog catalog)
            => Dispatcher.UIThread.Post(() => this.ApplyCatalog(catalog));

        private void ApplyCatalog(PiPackageCatalog catalog)
        {
            this.CatalogList.ItemsSource = catalog.Entries;

            var empty = catalog.Entries.Count == 0;
            this.CatalogScroll.IsVisible = !empty;
            this.CatalogEmptyText.IsVisible = empty;
            if (empty)
            {
                this.CatalogEmptyText.Text = (this.CatalogSearchBox.Text ?? string.Empty).Length > 0
                    ? "目录里没有匹配的包,换个关键词或把类型筛选改回「全部类型」。"
                    : "目录返回了空列表。点「刷新」重试,或到浏览器里打开目录站点确认。";
            }

            this.CatalogCountText.Text = catalog.CountText;
            this.CatalogSourceText.Text = catalog.SourceUrl.Length == 0
                ? string.Empty
                : $"取自 {catalog.SourceUrl}({catalog.FetchedAtLocal:HH:mm:ss})";

            // “加载更多”只在确实还有下一页时出现;取不到总页数时也不显示(总条数会随请求变化)
            var more = catalog.HasMore;
            this.LoadMoreButton.IsVisible = more;
            this.LoadMoreButton.IsEnabled = more;
            this.CatalogEndText.IsVisible = !more && !empty;
        }

        private async void OnCatalogRefreshClick(object? sender, RoutedEventArgs e) => await this.ReloadCatalogAsync();

        private async void OnLoadMoreClick(object? sender, RoutedEventArgs e) => await this.LoadMoreCatalogAsync();

        private async void OnCatalogSearchKeyDown(object? sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter)
            {
                await this.ReloadCatalogAsync();
            }
        }

        private async void OnCatalogTypeChanged(object? sender, SelectionChangedEventArgs e)
        {
            if (!this._pageReady || this._initializing || this.CatalogTypeCombo.SelectedIndex < 0)
            {
                return;
            }

            var type = CatalogTypeOptions.FromIndex(this.CatalogTypeCombo.SelectedIndex);
            if (SettingsService.Instance.Settings.PackageCatalogType != type)
            {
                SettingsService.Instance.Update(s => s.PackageCatalogType = type);
            }

            await this.ReloadCatalogAsync();
        }

        private async void OnCatalogSortChanged(object? sender, SelectionChangedEventArgs e)
        {
            if (!this._pageReady || this._initializing || this.CatalogSortCombo.SelectedIndex < 0)
            {
                return;
            }

            var sort = CatalogSortOptions.FromIndex(this.CatalogSortCombo.SelectedIndex);
            if (SettingsService.Instance.Settings.PackageCatalogSort != sort)
            {
                SettingsService.Instance.Update(s => s.PackageCatalogSort = sort);
            }

            await this.ReloadCatalogAsync();
        }

        private void OnOpenCatalogSiteClick(object? sender, RoutedEventArgs e)
            => BrowserLauncher.OpenInBrowser(PiPackageService.CatalogSiteUrl);

        /// <summary>安装目录里的一条包。</summary>
        private async void OnInstallFromCatalogClick(object? sender, RoutedEventArgs e)
        {
            if (sender is not Button { DataContext: PiPackageEntry entry })
            {
                return;
            }

            this.ShowOperationLog();
            this.AppendOperationLine($"[安装] {entry.InstallSpec}");

            var ok = await this._packages.InstallAsync(entry.InstallSpec);
            if (ok && this._service.IsRunning)
            {
                await this.AskRestartAsync(
                    $"已安装 {entry.Name}。Pi Web 的扩展在**会话启动时**加载,运行中的服务要重启后才会装载它。");
            }
        }

        /// <summary>打开目录详情页(在浏览器里 —— 详情页里有 README 与 manifest,不是这个窗口该渲染的东西)。</summary>
        private void OnOpenPackagePageClick(object? sender, RoutedEventArgs e)
        {
            if (sender is Button { DataContext: PiPackageEntry entry } && entry.PageUrl.Length > 0)
            {
                BrowserLauncher.OpenInBrowser(entry.PageUrl);
            }
        }

        /// <summary>
        /// 手动安装:输入源规格(<c>npm:…</c> / <c>git:…</c> / 本地路径)。
        /// 为什么保留这个入口:目录只收录声明了 <c>pi-package</c> 关键字、且被站点抓到的包,
        /// 私有包与本地开发的包只能手输。
        /// </summary>
        private async void OnManualInstallClick(object? sender, RoutedEventArgs e)
        {
            var topLevel = TopLevel.GetTopLevel(this);
            if (topLevel is not Window ownerWindow)
            {
                return;
            }

            var box = new TextBox
            {
                PlaceholderText = "npm:pi-lens 或 git:github.com/owner/repo@v1 或 ./local-package",
                Width = 460,
            };

            var dialog = new FAContentDialog
            {
                Title = "手动安装 pi 包",
                Content = new StackPanel
                {
                    Spacing = 8,
                    Children =
                    {
                        new TextBlock
                        {
                            Text = "填入 pi install 接受的源规格(与在终端里执行 pi install <规格> 等价)。",
                            TextWrapping = TextWrapping.Wrap,
                            FontSize = 12,
                        },
                        box,
                    },
                },
                PrimaryButtonText = "安装",
                CloseButtonText = "取消",
                DefaultButton = FAContentDialogButton.Primary,
            };

            var result = await dialog.ShowAsync(ownerWindow);
            var spec = box.Text?.Trim() ?? string.Empty;
            if (result != FAContentDialogResult.Primary || spec.Length == 0)
            {
                return;
            }

            this.ShowOperationLog();
            var ok = await this._packages.InstallAsync(spec);
            if (ok && this._service.IsRunning)
            {
                await this.AskRestartAsync($"已安装 {spec}。运行中的 Pi Web 服务要重启后才会装载它。");
            }
        }

        // ---- 操作日志 ----

        private void Packages_OperationOutput(string line)
        {
            lock (this._operationLog)
            {
                this._operationLog.Append(line).Append("\r\n");
            }

            // 浮窗关着时用小圆点提示有新输出(开着时用户自己看得到,不必提示)
            if (!this.OperationLogFlyout.IsVisible)
            {
                Dispatcher.UIThread.Post(() => this.OperationLogUnreadDot.IsVisible = true);
            }

            if (Dispatcher.UIThread.CheckAccess())
            {
                this._operationThrottle.Schedule();
            }
            else
            {
                Dispatcher.UIThread.Post(this._operationThrottle.Schedule);
            }
        }

        private void Packages_OperationStateChanged()
            => Dispatcher.UIThread.Post(() =>
            {
                var busy = this._packages.IsBusy;
                this.OperationProgress.IsActive = busy;
                this.UpdateAllButton.IsEnabled = !busy && this._packages.Installed.Installed.Count > 0;
                this.RefreshInstalledButton.IsEnabled = !busy;
                this.InstalledProgress.IsActive = busy;

                // 装卸一开始就把输出浮窗弹出来:这类操作要等网络,用户需要立刻看到进度与报错。
                // 用户手动关掉之后仍可自己再开(与 DSH 插件页同一套行为)。
                if (busy && !this.OperationLogFlyout.IsVisible)
                {
                    this.SetOperationLogVisible(true);
                }
            });

        /// <summary>开/关输出浮窗(筛选栏那个「输出」按钮)。</summary>
        private void OnOperationLogToggleClick(object? sender, RoutedEventArgs e)
            => this.SetOperationLogVisible(!this.OperationLogFlyout.IsVisible);

        /// <summary>点了浮窗之外的空白处:关闭浮窗(点击不再往下传递,与 light dismiss 一致)。</summary>
        private void OnOperationLogDismissPressed(object? sender, PointerPressedEventArgs e)
        {
            this.SetOperationLogVisible(false);
            e.Handled = true;
        }

        private void OnCloseOperationLogClick(object? sender, RoutedEventArgs e)
            => this.SetOperationLogVisible(false);

        /// <summary>显示/隐藏输出浮窗。打开时清掉未读小圆点并把日志滚到末尾。</summary>
        private void SetOperationLogVisible(bool visible)
        {
            this.OperationLogFlyout.IsVisible = visible;

            // 命中层与浮窗同进同出:只有浮窗开着时才拦截"点空白处"
            this.OperationLogDismissLayer.IsVisible = visible;
            this.OperationLogUnreadDot.IsVisible = false;

            if (visible)
            {
                this.OperationLogScroll.ScrollToEnd();
            }
        }

        private void Service_StateChanged()
            => Dispatcher.UIThread.Post(() =>
            {
                // 服务停下/起来之后，"重启后生效"标注要么已经兑现、要么已经无意义
                if (!this._service.IsRunning)
                {
                    this._packages.ClearAwaitingRestart();
                }
            });

        private void UpdateOperationLog()
        {
            string text;
            lock (this._operationLog)
            {
                text = this._operationLog.ToString();
            }

            this.OperationLogTextBlock.Text = text;
            Dispatcher.UIThread.Post(
                () => this.OperationLogScroll.ScrollToEnd(),
                DispatcherPriority.Background);
        }

        private void AppendOperationLine(string line)
        {
            lock (this._operationLog)
            {
                this._operationLog.Append(line).Append("\r\n");
            }

            this.UpdateOperationLog();
        }

        /// <summary>确保输出浮窗是打开的(用户点了安装/卸载/更新这类动作时当作明确意图)。</summary>
        private void ShowOperationLog() => this.SetOperationLogVisible(true);

        private void OnClearOperationLogClick(object? sender, RoutedEventArgs e)
        {
            lock (this._operationLog)
            {
                this._operationLog.Clear();
            }

            this.UpdateOperationLog();
        }

        private async void OnCopyOperationLogClick(object? sender, RoutedEventArgs e)
        {
            string text;
            lock (this._operationLog)
            {
                text = this._operationLog.ToString();
            }

            await this._operationLogCopy.CopyAsync(this, text);
        }

        /// <summary>
        /// 装/卸完成后,若服务正在运行就问一句要不要现在重启 ——
        /// pi 的扩展要到服务下次启动才装载,不提示的话用户会以为装失败了。
        /// </summary>
        private async Task AskRestartAsync(string message)
        {
            this._service.AppendSystemLog("[包] " + message);

            var topLevel = TopLevel.GetTopLevel(this);
            if (topLevel is not Window ownerWindow)
            {
                return;
            }

            var dialog = new FAContentDialog
            {
                Title = "重启 Pi Web 服务?",
                Content = new SelectableTextBlock
                {
                    Text = message + "\r\n\r\n要现在重启服务让变更生效吗?(重启会结束当前正在进行的会话)",
                    TextWrapping = TextWrapping.Wrap,
                },
                PrimaryButtonText = "立即重启",
                CloseButtonText = "稍后",
            };

            var result = await dialog.ShowAsync(ownerWindow);
            if (result != FAContentDialogResult.Primary)
            {
                this._service.AppendSystemLog("[包] 变更将在下次启动 Pi Web 服务时生效。");
                return;
            }

            var ok = await this._service.RestartAsync();
            this._service.AppendSystemLog(ok
                ? "[包] 已重启 Pi Web 服务,变更已生效。"
                : "[包] 重启 Pi Web 服务失败,请到首页查看启动日志。");
        }
    }

    /// <summary>
    /// 类型筛选下拉框的**界面顺序**与 <see cref="PackageCatalogType"/> 值的互转。
    /// <para>
    /// 为什么需要这张表(与 <see cref="RepeatLaunchOptions"/> 同一理由):下拉框按使用习惯排,
    /// 与枚举值顺序不同;若把映射留在界面里,顺序与设置值的对应关系就只存在于 XAML 与事件处理器的
    /// 对照中,改一处会静默错位。放在这里既被单元测试盯住,界面也只剩两行转调。
    /// </para>
    /// </summary>
    internal static class CatalogTypeOptions
    {
        private static readonly PackageCatalogType[] Items =
        [
            PackageCatalogType.All,
            PackageCatalogType.Extension,
            PackageCatalogType.Skill,
            PackageCatalogType.Theme,
            PackageCatalogType.Prompt,
        ];

        public static int ToIndex(PackageCatalogType type)
        {
            var index = Array.IndexOf(Items, type);
            return index >= 0 ? index : 0;
        }

        public static PackageCatalogType FromIndex(int index)
            => index >= 0 && index < Items.Length ? Items[index] : PackageCatalogType.All;
    }

    /// <summary>
    /// 排序下拉框的界面顺序与 <see cref="PackageCatalogSort"/> 值的互转。
    /// 取值必须与 https://pi.dev/packages 的 <c>sort</c> 参数一致(见 <c>PiCatalogReader.ToSortParameter</c>)。
    /// </summary>
    internal static class CatalogSortOptions
    {
        private static readonly PackageCatalogSort[] Items =
        [
            PackageCatalogSort.Downloads,
            PackageCatalogSort.RecentlyPublished,
            PackageCatalogSort.Name,
        ];

        public static int ToIndex(PackageCatalogSort sort)
        {
            var index = Array.IndexOf(Items, sort);
            return index >= 0 ? index : 0;
        }

        public static PackageCatalogSort FromIndex(int index)
            => index >= 0 && index < Items.Length ? Items[index] : PackageCatalogSort.Downloads;
    }

    /// <summary>已安装条目上那些不是模型属性的小工具(避免为了界面便利把模型撑大)。</summary>
    internal static class InstalledPackageDisplayExtensions
    {
        /// <summary>作者信息不在 <c>pi list</c> 的输出里,这里只回退成源规格的 scope 段。</summary>
        public static string AuthorTextOrEmpty(this PiInstalledPackage package)
            => package.Name.StartsWith('@') && package.Name.Contains('/')
                ? package.Name[1..package.Name.IndexOf('/', StringComparison.Ordinal)]
                : string.Empty;
    }
}
