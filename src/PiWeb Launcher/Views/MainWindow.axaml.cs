using System;
using System.Collections.Generic;
using Avalonia.Controls;
using FluentAvalonia.UI.Controls;
using PiWeb_Launcher.Services;

namespace PiWeb_Launcher.Views
{
    /// <summary>
    /// 主窗口:顶部导航(首页/包/设置),内容区切换(对应 WinUI 版 NavigationView + Frame)。
    /// </summary>
    public partial class MainWindow : Window
    {
        /// <summary>
        /// 当前窗口实例(单实例常驻应用,同一时刻只可能有一个主窗口)。
        /// 存在的理由只有一个:**跨页面导航** —— 包页面在"未安装 Pi Web"时要能把用户送到首页
        /// (见 <see cref="NavigateTo"/>),而它拿不到主窗口的引用。
        /// </summary>
        private static MainWindow? _current;

        private readonly HomePageControl _homePage;
        private readonly PackagesPageControl _packagesPage;
        private readonly SettingsPageControl _settingsPage;
        private readonly WindowStateTracker _windowStateTracker;

        public MainWindow()
        {
            InitializeComponent();

            // 窗口背景材质:Windows 11 用 Mica;其他平台用 Blur(macOS 毛玻璃/Linux blur-behind)。
            // 各平台不支持该级别时会自动回退,不影响功能。
            TransparencyLevelHint = new List<WindowTransparencyLevel>
            {
                OperatingSystem.IsWindows() ? WindowTransparencyLevel.Mica : WindowTransparencyLevel.Blur,
            };

            // 设置窗口/任务栏图标(logo-512.png 已嵌入程序集资源,见 AppIcon.LoadLogo512)
            this.Icon = AppIcon.LoadLogo512();

            // 恢复上次的位置/大小与最大化状态;没有有效记录时保持 XAML 初始尺寸 1280×720
            var restoredBounds = WindowStateService.Instance.RestoreMainWindow(this);
            this._windowStateTracker = WindowStateService.Instance.TrackMainWindow(this, restoredBounds);

            this._homePage = new HomePageControl();
            this._packagesPage = new PackagesPageControl();
            this._settingsPage = new SettingsPageControl();

            _current = this;

            this.NavView.Content = this._homePage;
            if (this.NavView.MenuItems.Count > 0)
            {
                this.NavView.SelectedItem = this.NavView.MenuItems[0];
            }

            Closed += (_, _) =>
            {
                if (ReferenceEquals(_current, this))
                {
                    _current = null;
                }
            };
        }

        /// <summary>保存主窗口位置/大小与最大化状态(隐藏到托盘、退出应用前由 App 调用)。</summary>
        public void SaveWindowState() => this._windowStateTracker.Save();

        /// <summary>
        /// 切到指定的页面标签(见 XAML 里各 <c>FANavigationViewItem</c> 的 <c>Tag</c>)。
        /// 找不到主窗口时静默返回 —— 导航只是便利,不该让调用方(包页面)崩在这里。
        /// </summary>
        internal static void NavigateTo(string tag)
        {
            var window = _current;
            if (window is null)
            {
                return;
            }

            foreach (var item in window.NavView.MenuItems)
            {
                if (item is FANavigationViewItem navigationItem
                    && string.Equals(navigationItem.Tag?.ToString(), tag, StringComparison.Ordinal))
                {
                    window.NavView.SelectedItem = navigationItem;
                    return;
                }
            }
        }

        private void NavView_SelectionChanged(object? sender, FANavigationViewSelectionChangedEventArgs args)
        {
            if (args.SelectedItem is FANavigationViewItem item)
            {
                switch (item.Tag?.ToString())
                {
                    case "packages":
                        this.NavView.Content = this._packagesPage;
                        break;
                    case "settings":
                        this.NavView.Content = this._settingsPage;
                        break;
                    default:
                        this.NavView.Content = this._homePage;
                        break;
                }
            }
        }
    }
}
