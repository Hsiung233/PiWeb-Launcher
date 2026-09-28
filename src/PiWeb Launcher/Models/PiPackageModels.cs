using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using System.Text;
using PiWeb_Launcher.Services;

namespace PiWeb_Launcher.Models
{
    /// <summary>
    /// pi 包目录(https://pi.dev/packages)里的一条包。目录页把每个包渲染成一张卡片,
    /// 所有结构化信息都挂在卡片的 <c>data-*</c> 属性上(见 <c>Services/Packages/PiCatalogReader</c>)。
    /// <para>
    /// 与 Pi 启动器的一个关键差别:**目录条目带版本号**,而 pi 的安装规格(
    /// <c>npm:&lt;包名&gt;</c>)不带 —— 这一点是有意为之(见 <see cref="InstallSpec"/>)。
    /// </para>
    /// </summary>
    public sealed class PiPackageEntry
    {
        /// <summary>包名(含 scope,如 <c>@juicesharp/rpiv-todo</c>)。</summary>
        public string Name { get; init; } = string.Empty;

        /// <summary>目录里的一句话简介。</summary>
        public string Description { get; init; } = string.Empty;

        /// <summary>作者(npm 发布者名)。</summary>
        public string Author { get; init; } = string.Empty;

        /// <summary>
        /// 目录给出的类型标记(可能多个,如 <c>"extension skill"</c>)。没有可识别类型时为空 ——
        /// 目录把这类包显示成 <c>package</c>,而它的 <c>data-package-types</c> 属性确实是空串。
        /// </summary>
        public string Types { get; init; } = string.Empty;

        /// <summary>月下载量(原始整数)。目录卡片上的 <c>1.1M/mo</c> 文本也一并保留在 <see cref="DownloadsText"/>。</summary>
        public int Downloads { get; init; }

        /// <summary>目录卡片上的下载量原始文本(如 <c>1.1M/mo</c>);缺失时为空。</summary>
        public string DownloadsText { get; init; } = string.Empty;

        /// <summary>版本号(从卡片上的 report 链接里取;缺失为空)。</summary>
        public string Version { get; init; } = string.Empty;

        /// <summary>目录卡片上的相对发布时间原文(如 <c>5h ago</c>);缺失时为空。</summary>
        public string PublishedText { get; init; } = string.Empty;

        /// <summary>发布时间(卡片上的毫秒时间戳;缺失为 0)。</summary>
        public long PublishedUnixMs { get; init; }

        /// <summary>仓库地址(目录卡片的 repo 链接);没有仓库时为空。</summary>
        public string RepoUrl { get; init; } = string.Empty;

        /// <summary>目录里这一条的详情页地址(https://pi.dev/packages/&lt;name&gt;)。</summary>
        public string PageUrl { get; init; } = string.Empty;

        /// <summary>
        /// 这个包是否已经装在本机。
        /// <para>
        /// 由 <c>PiPackageService</c> 在把目录条目交给界面时按 <c>pi list</c> 的结果**填**进来
        /// (目录页本身不知道你装了什么)。放在模型上而不是界面里,是因为列表是虚拟化的:
        /// 徽标的状态必须挂数据对象,不能靠容器里的控件。
        /// </para>
        /// </summary>
        public bool IsInstalled { get; init; }

        /// <summary>已安装的版本号(仅 <see cref="IsInstalled"/> 为真时可能有值)。</summary>
        public string? InstalledVersion { get; init; }

        /// <summary>“已安装 v1.2.3”这类徽标文案;未安装时为空。</summary>
        public string InstalledText => !this.IsInstalled
            ? string.Empty
            : this.InstalledVersion is { Length: > 0 } version ? "已安装 v" + version : "已安装";

        /// <summary>
        /// 安装规格,统一是 <c>npm:&lt;包名&gt;</c>。
        /// <para>
        /// 为什么不带版本号:目录的 <c>pi install</c> 命令行里也不带,且 pi 文档明确
        /// “版本化的 npm 规格会被钉住”(pinned),装完就不会再被 <c>pi update</c> 带走。
        /// 装最新版更符合“在目录里挑一个包装上”的意图。
        /// </para>
        /// </summary>
        public string InstallSpec => this.Name.Length == 0 ? string.Empty : "npm:" + this.Name;

        /// <summary>作者标记(<c>@作者</c>);无作者时为空。</summary>
        public string AuthorText => this.Author.Length == 0 ? string.Empty : "@" + this.Author;

        /// <summary>版本标记(<c>v1.2.3</c>);无版本时为空。</summary>
        public string VersionText => this.Version.Length == 0 ? string.Empty : "v" + this.Version;

        /// <summary>
        /// 类型徽标文本。目录对没有类型的包显示 <c>package</c>(下拉框筛选里没有这一项),
        /// 这里照同一套:空 → <c>package</c>,多个类型用 <c>/</c> 连接。
        /// </summary>
        public string TypeText => this.Types.Length == 0
            ? "package"
            : this.Types.Replace(' ', '/');

        /// <summary>卡片上“作者 · 下载 · 发布时间”那一行的合并文本(只显示目录确实提供的项)。</summary>
        public string MetaText
        {
            get
            {
                var parts = new List<string>();
                if (this.AuthorText.Length > 0)
                {
                    parts.Add(this.AuthorText);
                }

                if (this.DownloadsText.Length > 0)
                {
                    parts.Add(this.DownloadsText);
                }

                if (this.PublishedText.Length > 0)
                {
                    parts.Add(this.PublishedText);
                }

                return string.Join("  ·  ", parts);
            }
        }
    }

    /// <summary>
    /// 一次目录抓取的结果(可能是**一页**,也可能是启动器自己合并的若干页)。
    /// <para>
    /// 目录有 5000+ 条、每页 50 条 —— 一次全量抓取要 100 多次请求,既不礼貌也很慢。
    /// 所以启动器是**增量**的:先把第一页拿来显示,用户点“加载更多”再往下取,
    /// 每次取若干页并合并到同一份 <see cref="Entries"/> 里(按包名去重)。
    /// </para>
    /// </summary>
    public sealed class PiPackageCatalog
    {
        /// <summary>已取到的条目(按目录给出的顺序,即排序后的顺序)。</summary>
        public IReadOnlyList<PiPackageEntry> Entries { get; init; } = [];

        /// <summary>目录里符合条件的总条数(卡片头上“1-50 / 5360”里的第二个数);读不到时为 0。</summary>
        public int Total { get; init; }

        /// <summary>
        /// 目录里的**总页数**(从分页控件里读到的最大页码)。
        /// <para>
        /// 为什么要单独存:总条数是**随请求变化**的(“5360”在一个小时内见过 5360 / 5364 两个值 ——
        /// 上游边抓边发),拿它除以每页条数推页数会推出一个不存在的页码来。
        /// 分页控件里的页码是站点自己算的,拿它当"还有没有下一页"的判据才可靠。
        /// </para>
        /// </summary>
        public int TotalPages { get; init; }

        /// <summary>已经取到第几页(从 1 开始;0 = 还没取过)。</summary>
        public int LastPage { get; init; }

        /// <summary>目录页声明的“当前区间起点/终点”(1-50 里的 1 和 50);读不到时为 0。</summary>
        public int RangeStart { get; init; }

        /// <summary>目录页声明的“当前区间终点”。</summary>
        public int RangeEnd { get; init; }

        /// <summary>本次实际请求的地址(诊断用,界面上可展开查看)。</summary>
        public string SourceUrl { get; init; } = string.Empty;

        /// <summary>抓取到本地的时间。</summary>
        public DateTime FetchedAtLocal { get; init; } = DateTime.Now;

        /// <summary>是否还有下一页可拉(已取到最后一页时为 false)。</summary>
        public bool HasMore => this.TotalPages > 0 && this.LastPage > 0 && this.LastPage < this.TotalPages;

        /// <summary>“已加载 120 / 5360”这类摘要文本(总条数读不到时退化为只报已加载数)。</summary>
        public string CountText => this.Total <= 0
            ? (this.Entries.Count == 0 ? string.Empty : $"已加载 {this.Entries.Count} 个包")
            : $"已加载 {this.Entries.Count} / {this.Total}";

        /// <summary>把另一份(下一页)结果合并进来:按包名去重,保持先到的顺序。</summary>
        public PiPackageCatalog Merge(PiPackageCatalog next)
        {
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var merged = new List<PiPackageEntry>(this.Entries.Count + next.Entries.Count);
            foreach (var entry in this.Entries)
            {
                if (entry.Name.Length > 0 && seen.Add(entry.Name))
                {
                    merged.Add(entry);
                }
            }

            foreach (var entry in next.Entries)
            {
                if (entry.Name.Length > 0 && seen.Add(entry.Name))
                {
                    merged.Add(entry);
                }
            }

            return new PiPackageCatalog
            {
                Entries = merged,
                Total = next.Total > 0 ? next.Total : this.Total,
                TotalPages = Math.Max(this.TotalPages, next.TotalPages),
                LastPage = Math.Max(this.LastPage, next.LastPage),
                RangeStart = this.RangeStart > 0 ? this.RangeStart : next.RangeStart,
                RangeEnd = Math.Max(this.RangeEnd, next.RangeEnd),
                SourceUrl = next.SourceUrl.Length > 0 ? next.SourceUrl : this.SourceUrl,
                FetchedAtLocal = next.FetchedAtLocal,
            };
        }
    }

    /// <summary>
    /// 已安装的 pi 包(`pi list` 的结果,外加启动器补充的本地判断)。
    /// <para>
    /// ⚠ 实现 <see cref="INotifyPropertyChanged"/> 是为了 <see cref="IsPendingUninstall"/> 这类
    /// **界面状态**:列表是虚拟化的,状态必须挂数据对象上才能跨容器回收存活。
    /// </para>
    /// </summary>
    public sealed class PiInstalledPackage : INotifyPropertyChanged
    {
        /// <summary>pi 设置里记着的那一行原文(如 <c>npm:pi-mcp-adapter</c>、<c>git:github.com/a/b@v1</c>、<c>./local</c>)。</summary>
        public string Source { get; init; } = string.Empty;

        /// <summary>包名(npm 源去掉 <c>npm:</c> 前缀;git/本地源保留解析出的名称或原样)。</summary>
        public string Name { get; init; } = string.Empty;

        /// <summary>
        /// 来源种类(用于界面上的一行标注与图标)。
        /// 默认 <see cref="PackageSourceKind.Npm"/>:`pi list` 给出的绝大多数条目就是它,
        /// 而且这是"给出中性标注"而不是"标成未知"(见 <see cref="KindText"/>)。
        /// </summary>
        public PackageSourceKind Kind { get; init; } = PackageSourceKind.Npm;

        /// <summary>这一条是否来自项目设置(<c>.pi/settings.json</c>)而不是用户设置。</summary>
        public bool IsProjectScope { get; init; }

        /// <summary>pi 给出的已安装路径(<c>pi list</c> 的第二行);没有时为空的。</summary>
        public string InstalledPath { get; init; } = string.Empty;

        /// <summary>
        /// 这一条在 pi 设置里带了**资源过滤**(<c>pi list</c> 会打印 <c>(filtered)</c>)。
        /// 过滤只决定"这个包装了哪些资源",与能否卸载无关,界面上仅作标注。
        /// </summary>
        public bool IsFiltered { get; init; }

        /// <summary>本地已安装的版本号(从 pi 的 npm 目录里读 <c>package.json</c>);读不到为空。</summary>
        public string? Version { get; init; }

        /// <summary>
        /// 服务正在运行时安装的包:运行中的 pi-web **不会**自动装载它(pi 的扩展在会话启动时加载),
        /// 需要重启服务或新开一个 pi 会话才生效。由 <c>PiPackageService</c> 在安装当刻对账得出。
        /// </summary>
        public bool AwaitingRestart { get; init; }

        /// <summary>来源标签。</summary>
        public string KindText => this.Kind switch
        {
            PackageSourceKind.Npm => "npm",
            PackageSourceKind.Git => "git",
            PackageSourceKind.Local => "本地",
            _ => "未知",
        };

        /// <summary>作用域标签。</summary>
        public string ScopeText => this.IsProjectScope ? "项目" : "用户";

        /// <summary>版本标记(<c>v1.2.3</c>);读不到版本时为空。</summary>
        public string VersionText => string.IsNullOrEmpty(this.Version) ? string.Empty : "v" + this.Version;

        /// <summary>“作用域 · 来源 · 版本”合并成的一行摘要(只显示确实拿到的项)。</summary>
        public string MetaText
        {
            get
            {
                var parts = new List<string> { this.ScopeText, this.KindText };
                if (this.VersionText.Length > 0)
                {
                    parts.Add(this.VersionText);
                }

                if (this.IsFiltered)
                {
                    parts.Add("有资源过滤");
                }

                return string.Join("  ·  ", parts);
            }
        }

        /// <summary>安装命令的提示(把源规格还原成一条可直接粘贴的命令)。</summary>
        public string InstallCommandText => this.Source.Length == 0 ? string.Empty : "pi install " + this.Source;

        /// <summary>状态说明(等待重启生效时给出标注,否则为空)。</summary>
        public string StateText => this.AwaitingRestart ? "重启后生效" : string.Empty;

        /// <summary>界面状态变化时通知绑定(见 <see cref="IsPendingUninstall"/>)。</summary>
        public event PropertyChangedEventHandler? PropertyChanged;

        private bool _isPendingUninstall;

        /// <summary>
        /// 卸载按钮是否处于「再点一次确认」的状态(仅界面状态,不影响 pi 的设置)。
        /// <para>
        /// ⚠ 必须存在**数据对象**上,而不是去改 <c>Button.Content</c>:列表是虚拟化的、容器会被回收重用,
        /// 而模板里的文字是绑定 —— 容器被另一行复用时会把"确认卸载"一起带过去,
        /// 用户滚动后会看到别的包也处于待确认状态。
        /// </para>
        /// </summary>
        public bool IsPendingUninstall
        {
            get => this._isPendingUninstall;
            set
            {
                if (this._isPendingUninstall == value)
                {
                    return;
                }

                this._isPendingUninstall = value;
                this.PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(this.IsPendingUninstall)));
                // 按钮文案是另一个绑定,必须一起通知,否则状态变了文字不变
                this.PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(this.UninstallText)));
            }
        }

        /// <summary>卸载按钮的文案:待确认时变成「确认卸载」。</summary>
        public string UninstallText => this._isPendingUninstall ? "确认卸载" : "卸载";

        private string? _latestVersion;

        /// <summary>
        /// registry 上的最新版本(「检查更新」后回填);未检查、查不到或非 npm 来源为 null。
        /// <para>
        /// ⚠ 必须实现变更通知(与 <see cref="IsPendingUninstall"/> 同一理由):检查更新是
        /// 在列表显示之后**异步**回填的,不通知界面,行内的「可更新」徽标就不会出现。
        /// </para>
        /// </summary>
        public string? LatestVersion
        {
            get => this._latestVersion;
            set
            {
                if (string.Equals(this._latestVersion, value, StringComparison.Ordinal))
                {
                    return;
                }

                this._latestVersion = value;
                this.PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(this.LatestVersion)));
                this.PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(this.IsUpdateAvailable)));
                this.PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(this.CanUpdate)));
                this.PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(this.UpdateBadgeText)));
            }
        }

        /// <summary>
        /// registry 最新版是否比已装版本更新(判定规则见 <see cref="PiPackageService.IsUpdateNeeded"/>)。
        /// 仅 npm 来源参与:git/本地源的包没有 registry 版本,谈不上"有更新"。
        /// </summary>
        public bool IsUpdateAvailable =>
            this.Kind == PackageSourceKind.Npm
            && this.Version is { Length: > 0 }
            && this.LatestVersion is { Length: > 0 }
            && PiPackageService.IsUpdateNeeded(this.Version, this.LatestVersion);

        /// <summary>能否由启动器更新:npm 来源 + registry 上确实查到了更新的版本。</summary>
        public bool CanUpdate => this.Kind == PackageSourceKind.Npm && this.IsUpdateAvailable;

        /// <summary>行内「可更新到 vX」徽标文案;无更新时空串(徽标隐藏)。</summary>
        public string UpdateBadgeText => this.IsUpdateAvailable ? $"可更新到 v{this.LatestVersion}" : string.Empty;
    }

    /// <summary>pi 包的来源种类。</summary>
    public enum PackageSourceKind
    {
        Unknown = 0,

        /// <summary>npm 包(<c>npm:</c> 前缀)。</summary>
        Npm = 1,

        /// <summary>git 源(<c>git:</c> 前缀 / <c>https://…git</c> / <c>ssh://…</c>)。</summary>
        Git = 2,

        /// <summary>本地路径(<c>./…</c>、<c>../…</c>、绝对路径)。</summary>
        Local = 3,
    }

    /// <summary>一次 pi 包盘点的结果。</summary>
    public sealed class PiPackageSnapshot
    {
        /// <summary>已安装的包。</summary>
        public IReadOnlyList<PiInstalledPackage> Installed { get; init; } = [];

        /// <summary>pi 命令是否可用(<c>pi install</c> / <c>pi list</c> 都需要它)。</summary>
        public bool PiCliAvailable { get; init; }

        /// <summary>
        /// 本次用的 pi 是不是 **pi-web 自带的那一份**。
        /// <para>
        /// 界面上要说清楚:PATH 上没有 <c>pi</c> 时启动器会用 pi-web 依赖里那份完整的 pi
        /// (见 <c>PiCli.TryLocateBundledPiAsync</c>),它的版本**跟着 pi-web 走** ——
        /// 与用户自己全局装的那份可能不是同一个版本。"为什么命令行为与我在终端里跑的不一样"
        /// 就要靠这一位解释,所以它得显示出来,不能只写在代码注释里。
        /// </para>
        /// </summary>
        public bool PiCliIsBundled { get; init; }

        /// <summary>盘点过程中的非致命问题(读设置失败、pi list 报错等)。</summary>
        public string? Warning { get; init; }

        /// <summary>已安装包的规格集合(小写比较),用于在目录里给条目打“已安装”标记。</summary>
        public IReadOnlySet<string> InstalledSources { get; init; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        /// <summary>已安装包的名称集合(小写比较),用于在目录里给条目打“已安装”标记。</summary>
        public IReadOnlySet<string> InstalledNames { get; init; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>把目录里的数字文本(如 <c>337.3K/wk</c>)还原成整数。</summary>
    public static class PackageCountParser
    {
        /// <summary>
        /// 解析目录卡片上的下载量文本。识别 <c>K/M</c>(千/百万)与逗号分隔;
        /// 识别不出时返回 null(界面据此退化为只显示原文)。
        /// </summary>
        public static int? TryParse(string text)
        {
            if (string.IsNullOrWhiteSpace(text))
            {
                return null;
            }

            var span = text.Trim();
            var multiplier = 1d;
            var last = span[span.Length - 1];
            if (last is 'K' or 'k')
            {
                multiplier = 1_000d;
                span = span[..^1];
            }
            else if (last is 'M' or 'm')
            {
                multiplier = 1_000_000d;
                span = span[..^1];
            }
            else if (last is 'B' or 'b')
            {
                multiplier = 1_000_000_000d;
                span = span[..^1];
            }

            var builder = new StringBuilder();
            foreach (var ch in span)
            {
                // ⚠ 逗号是**千位分隔符**,必须直接丢掉:早期实现把它换成小数点,
                // 于是 "1,298" 被解析成 1.298 → 取整成 1(界面上的下载量因此少三个数量级)。
                // 目录里的数值只有 "1,298" 与 "1.1M" 两种形态,小数点保留即可。
                if (char.IsDigit(ch) || ch == '.')
                {
                    builder.Append(ch);
                }
                else if (ch == ',')
                {
                    continue;
                }
                else
                {
                    break;
                }
            }

            if (builder.Length == 0
                || !double.TryParse(builder.ToString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var value))
            {
                return null;
            }

            var scaled = value * multiplier;
            return scaled >= int.MaxValue ? int.MaxValue : (int)Math.Round(scaled);
        }
    }
}
