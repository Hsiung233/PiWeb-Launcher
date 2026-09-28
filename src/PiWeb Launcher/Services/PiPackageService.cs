using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using PiWeb_Launcher.Models;
using PiWeb_Launcher.Services.Packages;

namespace PiWeb_Launcher.Services
{
    /// <summary>
    /// pi 包(扩展 / 技能 / 主题 / 提示词模板)的管理:目录抓取、安装、卸载、盘点。
    /// <para>
    /// 三件事的边界是刻意划开的,改的时候别把它们搅在一起:
    /// <list type="number">
    /// <item><b>读写 pi 的设置</b> → 一律经 <c>pi install</c> / <c>pi remove</c> / <c>pi list</c>
    /// (见 <see cref="PiCli"/>)。启动器不解析、更不写 <c>settings.json</c> ——
    /// <c>packages</c> 字段有字符串、对象、带过滤数组等多种形态,猜格式迟早写坏用户设置。</item>
    /// <item><b>读 pi 的目录</b> → 只读地扫 <c>&lt;agent&gt;/npm</c> 补版本号(见 <see cref="PiNpmDirectoryReader"/>)。</item>
    /// <item><b>浏览 pi 的包目录</b> → 抓 https://pi.dev/packages 的网页并解析
    /// (见 <see cref="PiCatalogReader"/>)。站点没有给第三方用的 JSON 接口。</item>
    /// </list>
    /// </para>
    /// </summary>
    public sealed partial class PiPackageService
    {
        public static PiPackageService Instance { get; } = new();

        /// <summary>目录站点的地址(界面上会显示,便于用户自己去逛)。</summary>
        public const string CatalogSiteUrl = PiCatalogReader.CatalogBaseUrl;

        /// <summary>
        /// 一次"加载"最多抓几页。目录有 100+ 页(5000+ 条),一次全抓既慢又对站点不礼貌;
        /// 而一页 50 条在这个窗口里两屏就看完了。取 4 页 = 200 条,加载一次约 1-2 秒。
        /// </summary>
        internal const int PagesPerBatch = 4;

        /// <summary>抓取目录页的 HTTP 客户端(带代理与我们的 User-Agent,不复用全局默认)。</summary>
        private HttpClient? _httpClient;

        /// <summary>客户端对应的代理指纹(代理改了要重建,免重启生效)。</summary>
        private string? _httpClientKey;

        /// <summary>目录抓取的串行锁:界面上可能连点"加载更多",不能并发打站点。</summary>
        private readonly SemaphoreSlim _catalogLock = new(1, 1);

        /// <summary>本次运行期间安装的包(源规格)。服务停止/重启后清空,用于"重启后生效"标注。</summary>
        private readonly HashSet<string> _awaitingRestart = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>目录加载完成时触发(参数是加载后的完整目录)。</summary>
        public event Action<PiPackageCatalog>? CatalogUpdated;

        /// <summary>已安装包列表刷新时触发。</summary>
        public event Action<PiPackageSnapshot>? InstalledUpdated;

        /// <summary>安装/卸载等操作的输出行(<paramref name="isError"/> 只用于着色,不影响文案)。</summary>
        public event Action<string>? OperationOutput;

        /// <summary>操作开始/结束时触发(界面据此禁用按钮、显示进度环)。</summary>
        public event Action? OperationStateChanged;

        /// <summary>当前目录(还没加载过时为 null)。</summary>
        public PiPackageCatalog? Catalog { get; private set; }

        /// <summary>是否正在抓目录。</summary>
        public bool IsLoadingCatalog { get; private set; }

        /// <summary>是否正在安装/卸载。</summary>
        public bool IsBusy { get; private set; }

        /// <summary>最后一次已安装包盘点的结果。</summary>
        public PiPackageSnapshot Installed { get; private set; } = new();

        /// <summary>当前目录查询(搜索词 / 类型 / 排序)。</summary>
        public PiCatalogReader.CatalogQuery CatalogQuery { get; private set; }
            = new(string.Empty, PackageCatalogType.All, PackageCatalogSort.Downloads, 1);

        /// <summary>把设置里的排序/类型同步为当前查询(界面初始化时调用一次)。</summary>
        public void ApplySettingsToQuery()
        {
            var settings = SettingsService.Instance.Settings;
            this.CatalogQuery = this.CatalogQuery with
            {
                Type = settings.PackageCatalogType,
                Sort = settings.PackageCatalogSort,
            };
        }

        /// <summary>
        /// 抓取目录的第一批(或换搜索词后重新开始)。
        /// <paramref name="name"/> 为 null 表示沿用当前搜索词。
        /// </summary>
        public async Task<PiPackageCatalog> LoadCatalogFirstPageAsync(
            string? name = null,
            PackageCatalogType? type = null,
            PackageCatalogSort? sort = null,
            CancellationToken cancellationToken = default)
        {
            var query = this.CatalogQuery with
            {
                Name = name ?? this.CatalogQuery.Name,
                Type = type ?? this.CatalogQuery.Type,
                Sort = sort ?? this.CatalogQuery.Sort,
                Page = 1,
            };

            this.CatalogQuery = query;
            this.Catalog = null;
            var catalog = await this.FetchPagesAsync(query, PagesPerBatch, cancellationToken).ConfigureAwait(true);
            this.Catalog = catalog;
            CatalogUpdated?.Invoke(catalog);
            return catalog;
        }

        /// <summary>
        /// 再往后抓一批(界面的"加载更多")。到最后一页后不再发请求,直接返回当前目录。
        /// </summary>
        public async Task<PiPackageCatalog> LoadMoreAsync(CancellationToken cancellationToken = default)
        {
            var current = this.Catalog;
            if (current is null)
            {
                return await this.LoadCatalogFirstPageAsync(cancellationToken: cancellationToken).ConfigureAwait(true);
            }

            if (!current.HasMore)
            {
                return current;
            }

            var query = this.CatalogQuery with { Page = current.LastPage + 1 };
            var next = await this.FetchPagesAsync(query, PagesPerBatch, cancellationToken).ConfigureAwait(true);
            var merged = current.Merge(next);
            this.Catalog = merged;
            this.CatalogQuery = query;
            CatalogUpdated?.Invoke(merged);
            return merged;
        }

        /// <summary>
        /// 从 <paramref name="start"/> 页开始连续抓若干页,合并成一份目录。
        /// 中途某一页失败时**保留已抓到的部分**(并抛出原因)——"拿到一半"比"什么都没有"有用。
        /// </summary>
        private async Task<PiPackageCatalog> FetchPagesAsync(
            PiCatalogReader.CatalogQuery start,
            int pageCount,
            CancellationToken cancellationToken)
        {
            await this._catalogLock.WaitAsync(cancellationToken).ConfigureAwait(true);
            this.IsLoadingCatalog = true;
            OperationStateChanged?.Invoke();

            try
            {
                PiPackageCatalog? merged = null;
                Exception? failure = null;

                for (var offset = 0; offset < pageCount; offset++)
                {
                    var page = start with { Page = start.Page + offset };
                    try
                    {
                        var catalog = await this.FetchPageAsync(page, cancellationToken).ConfigureAwait(true);
                        merged = merged is null ? catalog : merged.Merge(catalog);

                        // 已经到最后一页(或这一页没有条目)就不再往下要
                        if (!catalog.HasMore || catalog.Entries.Count == 0)
                        {
                            break;
                        }
                    }
                    catch (Exception ex)
                    {
                        failure = ex;
                        break;
                    }
                }

                if (merged is null)
                {
                    throw failure ?? new InvalidOperationException("目录返回了空页面。");
                }

                if (failure is not null)
                {
                    AppLogService.Write($"[目录] 部分页面抓取失败: {failure.Message}");
                }

                return merged;
            }
            finally
            {
                this.IsLoadingCatalog = false;
                OperationStateChanged?.Invoke();
                this._catalogLock.Release();
            }
        }

        /// <summary>抓取并解析单页目录。</summary>
        private async Task<PiPackageCatalog> FetchPageAsync(
            PiCatalogReader.CatalogQuery query,
            CancellationToken cancellationToken)
        {
            var url = query.ToUrl();
            AppLogService.Write($"[目录] 抓取 {url}");

            var html = await this.FetchHtmlAsync(url, cancellationToken).ConfigureAwait(true);
            return PiCatalogReader.Parse(html, url);
        }

        /// <summary>
        /// 抓取网页正文;先走 <c>HttpClient</c>,本机 TLS 不可用时退到 Node
        /// (见 <see cref="NodeFetchFallback"/>,那里解释了为什么要这条退路)。
        /// </summary>
        private async Task<string> FetchHtmlAsync(string url, CancellationToken cancellationToken)
        {
            try
            {
                using var response = await this.GetHttpClient()
                    .GetAsync(url, HttpCompletionOption.ResponseContentRead, cancellationToken)
                    .ConfigureAwait(true);
                response.EnsureSuccessStatusCode();
                return await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(true);
            }
            catch (Exception ex) when (NodeFetchFallback.LooksLikeTlsFailure(ex))
            {
                // 兜底也失败时把**原来的** TLS 异常抛出去(它比"抓取失败"更能说明本机的问题)
                return await NodeFetchFallback.TryGetStringAsync(url).ConfigureAwait(true)
                    ?? throw new InvalidOperationException(
                        $"本机 HTTPS 不可用({ex.Message}),Node 兜底抓取也失败。", ex);
            }
        }

        /// <summary>取(必要时重建)目录抓取用的 HTTP 客户端:代理改了免重启即生效。</summary>
        private HttpClient GetHttpClient()
        {
            var key = ChildEnvironment.ProxyKey();
            if (this._httpClient is not null && string.Equals(this._httpClientKey, key, StringComparison.Ordinal))
            {
                return this._httpClient;
            }

            this._httpClient?.Dispose();

            var handler = new HttpClientHandler();
            ChildEnvironment.ApplyProxy(handler);

            var client = new HttpClient(handler)
            {
                Timeout = TimeSpan.FromSeconds(30),
            };

            // 站点对我们的请求没有特殊要求,但报一个诚实的 User-Agent 是基本礼貌
            client.DefaultRequestHeaders.UserAgent.ParseAdd("PiWebLauncher/1.0 (+https://pi.dev/packages)");
            client.DefaultRequestHeaders.Accept.ParseAdd("text/html,application/xhtml+xml");

            this._httpClient = client;
            this._httpClientKey = key;
            return client;
        }

        /// <summary>
        /// 把一次失败的原始输出压成**一行**可读的原因。
        /// <para>
        /// 为什么必须压:Node 的报错是完整栈(实测 <c>pi list</c> 的一次 EPERM 就有十几行
        /// <c>at …chunk-OJP47DM6.js:134:xxxxx</c>),而这句话要塞进界面顶部的一条横幅里 ——
        /// 原样展示会把横幅撑到十几行,把整个页面挤走(实测踩到)。
        /// 完整输出仍在 <c>app.log</c> 与首页日志里,这里只要"哪一步、什么错"。
        /// </para>
        /// </summary>
        internal static string DescribeFailure(string raw)
        {
            if (string.IsNullOrWhiteSpace(raw))
            {
                return string.Empty;
            }

            var lines = raw
                .Split('\n')
                .Select(line => line.TrimEnd('\r').Trim())
                .Where(line => line.Length > 0)
                .ToList();

            // 优先取真正的错误行(Error: xxx),否则取第一行非空的
            var errorLine = StartFailureDiagnostics.TryGetErrorLine(raw);
            var summary = errorLine ?? lines.FirstOrDefault() ?? string.Empty;

            // 去掉 "    at …" 这类栈帧残留(如果错误行本身带上了)
            var atIndex = summary.IndexOf("    at ", StringComparison.Ordinal);
            if (atIndex > 0)
            {
                summary = summary[..atIndex].TrimEnd();
            }

            const int MaxLength = 240;
            return summary.Length > MaxLength ? summary[..MaxLength] + "…" : summary;
        }

        /// <summary>未指定 agent 目录时的默认值(与 <see cref="PiWebService.ResolveAgentDirectory"/> 同一套)。</summary>
        private static string AgentDirectory => PiWebService.ResolveAgentDirectory();

        /// <summary>
        /// 本次用的是**哪一份 pi** —— PATH 上的全局 <c>pi</c>,还是 pi-web 自带的那份。
        /// <c>null</c> = 还没判定过。
        /// </summary>
        private PiCli.PiCommand? _piCommand;

        /// <summary>本次用的 pi 是不是 pi-web 自带的那份(界面据此标注版本来源)。</summary>
        public bool IsUsingBundledPi => this._piCommand is { IsBundled: true };

        /// <summary>本次用的 pi 的可执行路径(环境信息里展示;未判定时为 null)。</summary>
        public string? PiExecutablePath => this._piCommand?.ExecutablePath;

        /// <summary>重新判定"用哪一份 pi",并返回判定所用的那句说明(失败时是原因)。</summary>
        public async Task<string> RefreshPiCliSourceAsync()
        {
            var (command, error) = await PiCli.TryBuildPiCommandAsync().ConfigureAwait(true);
            var previous = this._piCommand;
            this._piCommand = command;

            if (command is not { } pi)
            {
                return error;
            }

            // 只有在**换了来源**时才写日志:这个方法每次盘点都会跑,不然日志会被刷屏
            if (previous is null || previous.Value.IsBundled != pi.IsBundled
                || !string.Equals(previous.Value.ExecutablePath, pi.ExecutablePath, StringComparison.OrdinalIgnoreCase))
            {
                AppLogService.Write(pi.IsBundled
                    ? $"[包] PATH 上没有 pi 命令,改用 pi-web 自带的那份: {pi.ExecutablePath} {PiCli.BundledPiRelativePath}"
                    : $"[包] 使用 PATH 上的 pi 命令: {pi.ExecutablePath}");
            }

            return string.Empty;
        }

        /// <summary>
        /// 盘点已安装的 pi 包:跑一次 <c>pi list</c>,再补上版本号与"重启后生效"标注。
        /// </summary>
        public async Task<PiPackageSnapshot> RefreshInstalledAsync()
        {
            // 每次盘点都重新判定用的是哪一份 pi:PATH 上的全局 pi 可能在两次盘点之间被装上/卸掉
            await this.RefreshPiCliSourceAsync().ConfigureAwait(true);

            var (exitCode, stdout, stderr) = await PiCli.RunCaptureAsync("list").ConfigureAwait(true);

            var entries = PiListReader.Parse(stdout);
            string? warning = null;

            if (exitCode == -1)
            {
                warning = DescribeFailure(stderr);
            }
            else if (exitCode != 0)
            {
                warning = $"pi list 退出码 {exitCode}。" + DescribeFailure(stderr.Length > 0 ? stderr : stdout);
            }
            else if (stderr.Trim().Length > 0 && entries.Count == 0)
            {
                // ⚠ 这一格最容易漏:pi 在读不到自己的设置时**照样返回 0**,只在 stderr 里抱怨一句
                // ("Warning (package command, global settings): EPERM … settings.json.lock"),
                // 然后打印 "No packages installed."。只看退出码就会把它显示成"你没装任何包" ——
                // 而用户明明装了十个。所以"退出码 0 + 有 stderr + 一条都没解析出来"一律当异常上报。
                warning = DescribeFailure(stderr);
            }

            var versions = PiNpmDirectoryReader.ReadVersions(AgentDirectory);

            var installed = entries
                .Select(entry => new PiInstalledPackage
                {
                    Source = entry.Source,
                    Name = entry.Name,
                    Kind = entry.Kind,
                    IsProjectScope = entry.IsProjectScope,
                    InstalledPath = entry.InstalledPath,
                    IsFiltered = entry.IsFiltered,
                    Version = versions.TryGetValue(entry.Name, out var version) ? version : null,
                    AwaitingRestart = this._awaitingRestart.Contains(entry.Source),
                })
                .ToList();

            var snapshot = new PiPackageSnapshot
            {
                Installed = installed,
                PiCliAvailable = exitCode != -1,
                PiCliIsBundled = this.IsUsingBundledPi,
                Warning = warning,
                InstalledSources = new HashSet<string>(installed.Select(item => item.Source), StringComparer.OrdinalIgnoreCase),
                InstalledNames = new HashSet<string>(installed.Select(item => item.Name), StringComparer.OrdinalIgnoreCase),
            };

            this.Installed = snapshot;
            this.Catalog = Decorate(this.Catalog, snapshot);
            InstalledUpdated?.Invoke(snapshot);

            // 目录里的"已安装"徽标来自这次盘点,所以盘点完要一并通知目录已更新
            if (this.Catalog is { } decorated)
            {
                CatalogUpdated?.Invoke(decorated);
            }

            return snapshot;
        }

        /// <summary>
        /// 给目录条目打上"已安装"标记(目录页本身不知道你装了什么)。
        /// <para>
        /// 每次盘点后都重算一遍:装/卸之后徽标必须跟着变,而目录条目是**不可变**的
        /// (全部 init 属性),所以这里是生成一份新的条目列表 —— 条目数最多几百条,代价可以忽略。
        /// 返回 null 表示原值为 null(还没加载过目录),别在这里凭空造一个空目录出来。
        /// </para>
        /// </summary>
        private static PiPackageCatalog? Decorate(PiPackageCatalog? catalog, PiPackageSnapshot snapshot)
        {
            if (catalog is null)
            {
                return null;
            }

            var entries = new List<PiPackageEntry>(catalog.Entries.Count);
            foreach (var entry in catalog.Entries)
            {
                // 目录给的是包名,而 pi 的源规格可能带 scope 之外的写法 —— 优先按名字匹配
                var installed = snapshot.InstalledNames.Contains(entry.Name)
                    || snapshot.InstalledSources.Contains(entry.InstallSpec);

                entries.Add(installed
                    ? new PiPackageEntry
                    {
                        Name = entry.Name,
                        Description = entry.Description,
                        Author = entry.Author,
                        Types = entry.Types,
                        Downloads = entry.Downloads,
                        DownloadsText = entry.DownloadsText,
                        Version = entry.Version,
                        PublishedText = entry.PublishedText,
                        PublishedUnixMs = entry.PublishedUnixMs,
                        RepoUrl = entry.RepoUrl,
                        PageUrl = entry.PageUrl,
                        IsInstalled = true,
                        InstalledVersion = FindInstalledVersion(snapshot, entry.Name),
                    }
                    : entry);
            }

            return new PiPackageCatalog
            {
                Entries = entries,
                Total = catalog.Total,
                TotalPages = catalog.TotalPages,
                LastPage = catalog.LastPage,
                RangeStart = catalog.RangeStart,
                RangeEnd = catalog.RangeEnd,
                SourceUrl = catalog.SourceUrl,
                FetchedAtLocal = catalog.FetchedAtLocal,
            };
        }

        /// <summary>在盘点结果里找某个包名对应的已安装版本;没有则返回 null。</summary>
        private static string? FindInstalledVersion(PiPackageSnapshot snapshot, string name)
            => snapshot.Installed
                .FirstOrDefault(item => string.Equals(item.Name, name, StringComparison.OrdinalIgnoreCase))
                ?.Version;

        /// <summary>
        /// 安装一个包。
        /// <paramref name="spec"/> 是 <c>pi install</c> 接受的规格(如 <c>npm:pi-lens</c>、
        /// <c>git:github.com/a/b@v1</c>、<c>./local</c>)。
        /// </summary>
        public async Task<bool> InstallAsync(string spec)
        {
            if (spec.Length == 0)
            {
                return false;
            }

            this.SetBusy(true);
            try
            {
                this.Emit($"[安装] pi install {spec}");
                var exitCode = await PiCli.RunPiStreamingAsync(
                    "install " + PlatformProcess.Quote(spec),
                    this.Emit).ConfigureAwait(true);

                if (exitCode == 0)
                {
                    // 服务正在运行时装的包:pi 的扩展在会话启动时加载,所以要等重启/新会话才生效
                    if (PiWebService.Instance.IsRunning)
                    {
                        this._awaitingRestart.Add(spec);
                    }

                    this.Emit($"[完成] 已安装 {spec}");
                }
                else
                {
                    this.Emit($"[失败] 安装 {spec} 未成功(退出码 {exitCode})");
                }

                await this.RefreshInstalledAsync().ConfigureAwait(true);
                return exitCode == 0;
            }
            finally
            {
                this.SetBusy(false);
            }
        }

        /// <summary>卸载一个包(按 <c>pi list</c> 给出的**源规格**原文)。</summary>
        public async Task<bool> UninstallAsync(string source)
        {
            if (source.Length == 0)
            {
                return false;
            }

            this.SetBusy(true);
            try
            {
                this.Emit($"[卸载] pi remove {source}");
                var exitCode = await PiCli.RunPiStreamingAsync(
                    "remove " + PlatformProcess.Quote(source),
                    this.Emit).ConfigureAwait(true);

                if (exitCode == 0)
                {
                    this.Emit($"[完成] 已卸载 {source}");
                    this._awaitingRestart.Remove(source);
                }
                else
                {
                    this.Emit($"[失败] 卸载 {source} 未成功(退出码 {exitCode})");
                }

                await this.RefreshInstalledAsync().ConfigureAwait(true);
                return exitCode == 0;
            }
            finally
            {
                this.SetBusy(false);
            }
        }

        /// <summary>
        /// 更新所有已安装的包(<c>pi update --extensions</c>)。
        /// 单独成方法而不是复用 <see cref="InstallAsync"/>:pi 的 update 与 install 是两条命令,
        /// 参数形态也不同(updates 不接受源规格)。
        /// </summary>
        public async Task<bool> UpdateAllAsync()
        {
            this.SetBusy(true);
            try
            {
                this.Emit("[更新] pi update --extensions");
                var exitCode = await PiCli.RunPiStreamingAsync("update --extensions", this.Emit).ConfigureAwait(true);
                this.Emit(exitCode == 0 ? "[完成] 已更新全部包" : $"[失败] 更新未成功(退出码 {exitCode})");

                await this.RefreshInstalledAsync().ConfigureAwait(true);
                return exitCode == 0;
            }
            finally
            {
                this.SetBusy(false);
            }
        }

        // ---- 更新检测 ----

        /// <summary>判定是否需要更新。internal 供单元测试。</summary>
        internal static bool IsUpdateNeeded(string? installedVersion, string? latestVersion)
        {
            if (string.IsNullOrWhiteSpace(installedVersion) || string.IsNullOrWhiteSpace(latestVersion))
            {
                return false;
            }

            // 比较规则与 pi-web 的版本比较同一套(逐段按数字比、预发布 < 正式);
            // 形态怪异的版本号比不出大小就按"无更新"处理 —— 宁可少提示,也不误报
            return PiWebService.CompareVersions(latestVersion.Trim(), installedVersion.Trim()) > 0;
        }

        /// <summary>
        /// 解析 <c>npm view &lt;pkg&gt; version</c> 的输出:取最后一条非空行、剥掉引号;
        /// 空输出(查询失败)返回 null。npm 偶尔会在版本号前后夹告警行或引号,这里一并消化。
        /// </summary>
        internal static string? ParseLatestVersionOutput(string stdout)
        {
            var line = stdout
                .Split('\n', StringSplitOptions.RemoveEmptyEntries)
                .Select(item => item.Trim())
                .LastOrDefault(item => item.Length > 0);
            if (line is null)
            {
                return null;
            }

            line = line.Trim('"').Trim();
            return line.Length > 0 && VersionLineRegex().IsMatch(line) ? line : null;
        }

        /// <summary>
        /// 版本号行须整个长得像版本号。为什么要校验:npm 的告警/错误行可能混进输出
        /// (网络抖动、registry 瞬断、代理失败),而解析取的是"最后一条非空行" ——
        /// 不设防的话,一行错误文本(比如以数字开头的 <c>404 …</c>)会被当成"更新版本",
        /// 界面凭空亮出「可更新」徽标。不像版本号的一律当"查询失败"(null = 无更新):
        /// 宁可漏报一次,也不误报。
        /// </summary>
        [GeneratedRegex(@"^v?\d+(\.\d+)*(?:-[0-9A-Za-z.\-]+)?(?:\+[0-9A-Za-z.\-]+)?$")]
        private static partial Regex VersionLineRegex();

        /// <summary>
        /// 逐个查询一批 npm 包在 registry 上的最新版本(顺序执行,不并发轰炸源站)。
        /// 查不到(404 / 离线 / 超时)记 null,调用方按"无更新"处理;
        /// git/本地源的包没有 registry 版本可查,调用方不应把它们传进来。
        /// </summary>
        /// <param name="onProgress">每查完一个回调一次(已完成数,总数),界面用它显示进度。</param>
        public async Task<Dictionary<string, string?>> FetchLatestVersionsAsync(
            IEnumerable<string> packageNames,
            Action<int, int>? onProgress = null)
        {
            var results = new Dictionary<string, string?>(StringComparer.Ordinal);
            var names = packageNames
                .Where(name => !string.IsNullOrWhiteSpace(name))
                .Distinct(StringComparer.Ordinal)
                .ToList();

            var index = 0;
            foreach (var name in names)
            {
                index++;
                string? latest = null;
                try
                {
                    var result = await ChildProcessRunner.CaptureAsync(
                        $"npm view {PlatformProcess.Quote(name)} version").ConfigureAwait(true);
                    latest = result.ExitCode == 0 ? ParseLatestVersionOutput(result.Stdout) : null;
                }
                catch (Exception)
                {
                    // npm 不在 PATH/进程起不来等:这一个包查不到,不影响其余的
                    latest = null;
                }

                results[name] = latest;
                onProgress?.Invoke(index, names.Count);
            }

            return results;
        }

        /// <summary>
        /// 服务停止或重启后清掉"重启后生效"标注 —— 新起的服务进程会重新加载全部包,
        /// 标注留着就是骗人。
        /// </summary>
        public void ClearAwaitingRestart()
        {
            if (this._awaitingRestart.Count == 0)
            {
                return;
            }

            this._awaitingRestart.Clear();
            _ = this.RefreshInstalledAsync();
        }

        /// <summary>本机是否具备 pi 运行环境(node + pi 命令)。</summary>
        public async Task<(bool Node, bool PiCli, bool PiWeb)> ProbeEnvironmentAsync()
        {
            var (nodePath, _) = await PiCli.FindAsync("node").ConfigureAwait(true);
            var (piCommand, _) = await PiCli.TryBuildPiCommandAsync().ConfigureAwait(true);
            var (piWebPath, _) = await PiCli.FindAsync(PiCli.PiWebCommandName).ConfigureAwait(true);

            // 包管理能用 = 有全局 pi **或** 有 pi-web 自带的那份
            return (nodePath.Length > 0, piCommand is not null, piWebPath.Length > 0);
        }

        private void SetBusy(bool busy)
        {
            this.IsBusy = busy;
            OperationStateChanged?.Invoke();
        }

        private void Emit(string line)
        {
            AppLogService.Write("[包] " + line);
            OperationOutput?.Invoke(line);
        }
    }
}
