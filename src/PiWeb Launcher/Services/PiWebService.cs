using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace PiWeb_Launcher.Services
{
    /// <summary>
    /// 管理 npm 全局包 @agegr/pi-web 与 pi 命令行的检测、安装与运行(单例,跨页面保持状态)。
    /// 所有事件可能在后台线程触发,UI 层需自行调度到 UI 线程。
    /// <para>
    /// 与 Pi 启动器的结构差异(都是"上游本来就不一样",不是简化):
    /// <list type="bullet">
    /// <item>pi-web 通过 <c>npx</c> 或全局安装运行都可以,但它**不是**在命令行上启动 pi 的 Web 服务;
    /// 它是自己的 npm 包(<c>@agegr/pi-web</c>),命令名 <c>pi-web</c>。</item>
    /// <item>不带 token:pi-web 的地址就是 <c>http://host:port</c>(没有 ?token=)。所以
    /// <see cref="WebUrl"/> 是**推导**出来的(监听地址 + 端口),而不是从输出里正则抓出来的;
    /// 输出只用来确认"服务已经就绪"。</item>
    /// <item>pi 包(扩展/技能/主题/提示词)由 <c>pi install</c> 管理,pi-web 只是它们的浏览器界面;
    /// 这部分在 <c>PiPackageService</c> 里,与本类无关。</item>
    /// </list>
    /// </para>
    /// </summary>
    public sealed partial class PiWebService
    {
        public static PiWebService Instance { get; } = new();

        /// <summary>子命令部分;pi-web 没有子命令,启动参数由 <see cref="BuildRunArgs"/> 按设置追加。</summary>
        private const string BaseRunArgs = "";

        /// <summary>pi-web 自身的默认端口(命令行 <c>--help</c> 与文档里都是这个值)。</summary>
        public const int DefaultPort = 30141;

        /// <summary>pi-web 自身的默认监听地址。</summary>
        public const string DefaultHostname = "127.0.0.1";

        /// <summary>pi-web 在 stdout 里打出的就绪标记(Next.js 的 "✓ Ready in 2.3s";应用据此自动开浏览器)。</summary>
        private const string ReadyMarker = "Ready";

        private Process? _process;

        /// <summary>
        /// 日志缓冲区。日志同时被多个线程写:stdout/stderr 的异步回调(线程池)、
        /// RunStreamingAsync 的安装输出回调、UI 线程的 AppendSystemLog;读侧还有 LogText、
        /// TryRecordStartFailure 与 ClearLog —— 线程安全与长度裁剪都由 <see cref="LogBuffer"/> 负责。
        /// </summary>
        /// <remarks>
        /// 上限不是"省内存"的锦上添花,而是**防界面假死**的硬要求:Node 启动失败时会把
        /// 整个依赖栈完整打印(实测 pi 一次吐出 43 万字符),而面板是"整段文本 + 每行追加全量重排"
        /// 的实现,不限长就会把 UI 线程钉死在 Avalonia 文本排版里(窗口"未响应")。
        /// 面板本来就是实时尾部视图,更早的内容另有 app.log 文件可查。
        /// </remarks>
        private readonly LogBuffer _log = new(LogBuffer.DefaultMaxChars);

        /// <summary>本次运行输出的开头(<see cref="CaptureRunHead"/>),供启动失败时写进 app.log。</summary>
        private readonly StringBuilder _runHead = new();

        /// <summary><see cref="_runHead"/> 的锁(日志会被多个线程追加)。</summary>
        private readonly Lock _runHeadLock = new();

        /// <summary>本次运行产生的输出总字符数(含已被面板上限裁掉的部分)。</summary>
        private int _runOutputLength;

        private DateTime _startTimeUtc = DateTime.MinValue;
        private int _lastStartLogMark;
        private volatile bool _stopRequestedByUser;
        private bool _startFailureHandled;
        private volatile bool _readyReported;

        /// <summary>最近一次启动实际使用的参数(错误信息中展示,便于用户复现)。</summary>
        private string _lastRunArgs = BaseRunArgs;

        /// <summary>最近一次启动定位到的 pi-web 命令路径(失败留证时用它反推全局包目录)。</summary>
        private string _lastShimPath = string.Empty;

        /// <summary>服务 URL 变化时触发(启动就绪、停止、重启后端口变化)。</summary>
        public event Action<string>? WebUrlDetected;

        /// <summary>
        /// 服务**非用户主动停止**地退出时触发(启动失败或中途崩溃),界面据此弹一次提示。
        /// 判定与"用户点了停止"分开记:后者是正常路径,弹窗就成了噪音。
        /// </summary>
        public event Action? StartFailed;

        /// <summary>有新日志时触发(<c>detectWebUrl</c> 只在进程输出时为 true)。</summary>
        public event Action<string>? LogAppended;

        /// <summary>日志被清空时触发。</summary>
        public event Action? LogsCleared;

        /// <summary>运行状态(启动/停止)变化时触发。</summary>
        public event Action? StateChanged;

        /// <summary>
        /// Pi Web 的可访问地址(如 <c>http://127.0.0.1:30141</c>)。未运行时为 null。
        /// <para>
        /// 与 Pi 不同,这个地址**不是**从输出里抓的:pi-web 的地址由监听地址与端口唯一决定,
        /// 输出里只有一句 "Ready"。所以这里是推导值,进程退出/停止时清空。
        /// </para>
        /// </summary>
        public string? WebUrl { get; private set; }

        /// <summary>服务是否正在运行。</summary>
        public bool IsRunning => IsAlive(this._process);

        /// <summary>已安装的 pi-web 版本;未安装/查询失败为 null。</summary>
        public string? InstalledVersion { get; private set; }

        /// <summary>是否正在安装/更新(npm 安装期间禁用按钮)。</summary>
        public bool IsInstalling { get; private set; }

        /// <summary>日志全文(供界面显示)。</summary>
        public string LogText => this._log.ToString();

        /// <summary>npm 上的最新版本;查询失败为 null。</summary>
        public string? LatestVersion { get; private set; }

        /// <summary>是否有可用更新。</summary>
        public bool IsUpdateAvailable =>
            this.InstalledVersion is { Length: > 0 } installed
            && this.LatestVersion is { Length: > 0 } latest
            && CompareVersions(latest, installed) > 0;

        private static bool IsAlive(Process? process)
        {
            if (process is null)
            {
                return false;
            }

            try
            {
                return !process.HasExited;
            }
            catch (Exception)
            {
                return false;
            }
        }

        /// <summary>本次运行的启动时刻(本地时间);未运行时为 null。</summary>
        public DateTime? StartedAtLocal =>
            this.IsRunning && this._startTimeUtc != DateTime.MinValue
                ? this._startTimeUtc.ToLocalTime()
                : null;

        /// <summary>进程 PID;未运行时为 null。</summary>
        public int? ProcessId
        {
            get
            {
                try
                {
                    return this.IsRunning ? this._process?.Id : null;
                }
                catch (Exception)
                {
                    return null;
                }
            }
        }

        /// <summary>最近一次启动失败的原因(可直接展示给用户);没有失败时为空。</summary>
        public string LastStartError { get; private set; } = string.Empty;

        /// <summary>
        /// 依据设置拼接 pi-web 启动参数。
        /// <list type="bullet">
        /// <item>端口为 0(未设置)时不追加 <c>--port</c>,沿用 pi-web 自身默认端口 30141。</item>
        /// <item>监听地址为空时不追加 <c>--hostname</c>(默认 127.0.0.1)。
        /// 填非回环地址会让服务暴露在网络上,界面上的提示与 README 都基于这一条。</item>
        /// <item>默认追加 <c>--no-open</c>:启动器自己就是那个"界面",不该再弹一个系统浏览器;
        /// 设置里可以放行(见 <see cref="Models.AppSettings.LetServiceOpenBrowser"/>)。</item>
        /// </list>
        /// </summary>
        private static string BuildRunArgs()
        {
            var settings = SettingsService.Instance.Settings;
            var args = new StringBuilder(BaseRunArgs);

            var port = settings.ListenPort is >= 1 and <= 65535 ? settings.ListenPort : 0;
            if (port > 0)
            {
                args.Append(" --port ").Append(port.ToString(CultureInfo.InvariantCulture));
            }

            var hostname = NormalizeHostname(settings.ListenHostname);
            if (hostname.Length > 0)
            {
                args.Append(" --hostname ").Append(QuoteArg(hostname));
            }

            if (!settings.LetServiceOpenBrowser)
            {
                args.Append(" --no-open");
            }

            return args.ToString().Trim();
        }

        /// <summary>
        /// 监听地址归一化:去掉首尾空白,识别不出的(含空格、引号等)一律当未设置。
        /// <para>
        /// ⚠ 带端口(<c>127.0.0.1:30141</c>)也当未设置:端口是**单独一项设置**,
        /// 而且 <c>--hostname</c> 只接受主机名 —— 把它原样传下去会让 pi-web 监听到一个
        /// 谁也猜不到的地址(或者直接报错),而这属于"用户填错了地方",
        /// 静默退化成默认值比原样透传好排查。
        /// </para>
        /// </summary>
        internal static string NormalizeHostname(string? hostname)
        {
            if (string.IsNullOrWhiteSpace(hostname))
            {
                return string.Empty;
            }

            var value = hostname.Trim();
            if (value.Contains(':', StringComparison.Ordinal) && !value.StartsWith('['))
            {
                // 裸 IPv6 与 host:port 在这里要区分开:前者含两个以上冒号,后者只有一段 "主机:端口"
                var colons = 0;
                foreach (var ch in value)
                {
                    if (ch == ':')
                    {
                        colons++;
                    }
                }

                if (colons == 1)
                {
                    return string.Empty;
                }
            }

            foreach (var ch in value)
            {
                if (!char.IsLetterOrDigit(ch) && ch is not ('.' or '-' or ':' or '[' or ']'))
                {
                    return string.Empty;
                }
            }

            return value;
        }

        /// <summary>给命令行参数加引号(主机名允许出现的字符里没有空格,但保持与端口一致的处理)。</summary>
        private static string QuoteArg(string value)
            => value.Contains(' ', StringComparison.Ordinal) ? "\"" + value + "\"" : value;

        /// <summary>
        /// 按当前设置推导服务的可访问地址。
        /// <para>
        /// ⚠ <c>0.0.0.0</c> / <c>::</c> 是"监听所有网卡",**不是**可以拿来访问的地址;
        /// 而 <c>localhost</c> 与 <c>127.0.0.1</c> 之外的具体地址(如局域网 IP)可以直接用。
        /// </para>
        /// </summary>
        internal static string BuildWebUrl(string? hostname, int port)
        {
            var effectivePort = port is >= 1 and <= 65535 ? port : DefaultPort;
            var host = NormalizeHostname(hostname);
            if (host.Length == 0 || host is "0.0.0.0" or "::" or "[::]" or "*")
            {
                host = DefaultHostname;
            }

            // IPv6 字面量必须带方括号才能拼进 URL
            var hostPart = host.Contains(':', StringComparison.Ordinal) && !host.StartsWith('[')
                ? "[" + host + "]"
                : host;

            return $"http://{hostPart}:{effectivePort}";
        }

        /// <summary>
        /// 追加日志。<paramref name="detectWebUrl"/> 仅在 pi-web 进程输出流调用时为 true
        /// (只有进程输出才谈得上"就绪");其余日志(安装输出、系统信息、启停标记)一律跳过检测。
        /// </summary>
        private void AppendLog(string text, bool detectWebUrl = false)
        {
            // 超长时由 LogBuffer 从头部按整行裁剪(见 LogBuffer.DefaultMaxChars 的说明)
            this._log.Append(text);
            this.CaptureRunHead(text);

            if (detectWebUrl)
            {
                this.TryDetectReady(text);
            }

            // 事件在锁外触发:处理器会 Post 到 UI 线程,放在锁里没有好处,只会扩大临界区
            LogAppended?.Invoke(text);
        }

        /// <summary>失败留证里"本次运行输出的开头"的字符数上限。</summary>
        private const int RunHeadChars = 2_000;

        /// <summary>
        /// 记住本次运行输出的**开头**。
        /// 为什么要单独存:面板正文(<see cref="LogBuffer"/>)有长度上限、且只保留尾部,
        /// 而排查启动失败时最有价值的恰好是开头(第一处报错);拉日志时若直接从缓冲区取开头,
        /// 拿到的其实是被裁剪后的那段(开头已经是"已省略 N 字符"的提示行)。
        /// </summary>
        private void CaptureRunHead(string text)
        {
            lock (this._runHeadLock)
            {
                this._runOutputLength += text.Length;

                var room = RunHeadChars - this._runHead.Length;
                if (room > 0)
                {
                    this._runHead.Append(text.Length <= room ? text : text[..room]);
                }
            }
        }

        /// <summary>记录应用级诊断信息(显示在首页日志面板,同时写入文件日志)。</summary>
        public void AppendSystemLog(string message)
        {
            this.AppendLog($"[应用] {message}\r\n");
            AppLogService.Write(message);
        }

        /// <summary>
        /// 清空日志文本并通知 UI。
        /// 注意:这里**不**置空 <see cref="WebUrl"/> —— 服务仍在运行,地址依然有效,
        /// 清日志不该让"打开/复制地址"的入口消失。该字段只在 Stop/Restart/进程退出时清空。
        /// </summary>
        public void ClearLog()
        {
            this._log.Clear();

            lock (this._runHeadLock)
            {
                this._runHead.Clear();
            }

            // ⚠ 必须一起复位:_runOutputLength 是“本次运行”的累计输出量,不归零时每次重试都会累加,
            // app.log 里会出现成倍增长的假象(Pi 那边实测踩到过)。
            this._runOutputLength = 0;

            LogsCleared?.Invoke();
        }

        /// <summary>
        /// 检测 pi-web 是否已经就绪。就绪后把 <see cref="WebUrl"/> 填好并通知界面。
        /// <para>
        /// 判据是输出里的 <c>Ready</c>(Next.js 的 "✓ Ready in 2.3s"):pi-web 的启动脚本
        /// 正是用它来决定"该开浏览器了",所以这是上游公认的就绪信号,比自己去猜端口通不通可靠。
        /// 地址本身由设置推导(见 <see cref="BuildWebUrl"/>),不从输出里抓 —— pi-web 没有 token。
        /// </para>
        /// </summary>
        private void TryDetectReady(string text)
        {
            if (this._readyReported || !this.IsRunning)
            {
                return;
            }

            if (!text.Contains(ReadyMarker, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            this._readyReported = true;
            var settings = SettingsService.Instance.Settings;
            var url = BuildWebUrl(settings.ListenHostname, settings.ListenPort);

            this.WebUrl = url;
            AppLogService.Write($"[服务] 已就绪: {url}");
            WebUrlDetected?.Invoke(url);
        }

        /// <summary>查询 npm 全局安装的 pi-web 版本号;未安装时返回 null。</summary>
        public async Task<string?> GetInstalledVersionAsync()
        {
            try
            {
                var result = await ChildProcessRunner.CaptureAsync(
                    $"npm ls -g {PiCli.PiWebPackageName} --depth=0");
                var match = Regex.Match(result.Stdout, Regex.Escape(PiCli.PiWebPackageName) + @"@([^\s]+)");
                this.InstalledVersion = match.Success ? match.Groups[1].Value : null;
            }
            catch (Exception ex)
            {
                this.InstalledVersion = null;
                this.AppendLog($"[检查安装状态失败] {ex.Message}\r\n");
            }

            return this.InstalledVersion;
        }

        /// <summary>通过 npm 全局安装 pi-web,输出写入日志。</summary>
        public async Task InstallAsync()
        {
            if (this.IsInstalling)
            {
                return;
            }

            this.IsInstalling = true;
            this.AppendLog($"\r\n[安装] npm install -g {PiCli.PiWebPackageName}\r\n");
            try
            {
                await RunStreamingAsync($"npm install -g {PiCli.PiWebPackageName}");
            }
            finally
            {
                this.IsInstalling = false;
            }

            await this.GetInstalledVersionAsync();
            StateChanged?.Invoke();
        }

        /// <summary>查询 npm 上的最新版本(用于显示"有新版本")。</summary>
        public async Task CheckForUpdateAsync()
        {
            try
            {
                var result = await ChildProcessRunner.CaptureAsync(
                    $"npm view {PiCli.PiWebPackageName} version");
                var version = result.Stdout.Trim();
                this.LatestVersion = version.Length > 0 && !version.Contains('\n') ? version : null;
            }
            catch (Exception)
            {
                this.LatestVersion = null;
            }
        }

        /// <summary>更新 pi-web 到最新版,输出写入日志。</summary>
        public async Task UpdateAsync()
        {
            if (this.IsInstalling)
            {
                return;
            }

            var wasRunning = this.IsRunning;
            if (wasRunning)
            {
                this.Stop();
            }

            this.IsInstalling = true;
            this.AppendLog($"\r\n[更新] npm install -g {PiCli.PiWebPackageName}@latest\r\n");
            try
            {
                await RunStreamingAsync($"npm install -g {PiCli.PiWebPackageName}@latest");
            }
            finally
            {
                this.IsInstalling = false;
            }

            await this.GetInstalledVersionAsync();
            await this.CheckForUpdateAsync();
            StateChanged?.Invoke();

            if (wasRunning)
            {
                await this.StartAsync();
            }
        }

        /// <summary>
        /// 比较两个语义化版本号:前者大于后者返回正数,相等返回 0,小于返回负数。
        /// 逐段比较数字,非数字段按字符串比较;段数不同时短的一方补 0
        /// (所以 "1.2" == "1.2.0")。预发布后缀(如 <c>-beta.1</c>)在数字段相同时算更小。
        /// </summary>
        internal static int CompareVersions(string a, string b)
        {
            static (int[] Numbers, string Pre) Split(string value)
            {
                var text = value.Trim().TrimStart('v', 'V');

                // 构建元数据(+build.5)不参与比较,先切掉
                var plus = text.IndexOf('+', StringComparison.Ordinal);
                if (plus >= 0)
                {
                    text = text[..plus];
                }

                var pre = string.Empty;
                var dash = text.IndexOf('-', StringComparison.Ordinal);
                if (dash >= 0)
                {
                    pre = text[(dash + 1)..];
                    text = text[..dash];
                }

                var parts = text.Split('.', StringSplitOptions.RemoveEmptyEntries);
                var numbers = new int[parts.Length];
                for (var i = 0; i < parts.Length; i++)
                {
                    // "1.2.3+build" 这类元数据不参与比较
                    var head = new string(parts[i].TakeWhile(char.IsDigit).ToArray());
                    numbers[i] = head.Length > 0 && int.TryParse(head, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed)
                        ? parsed
                        : 0;
                }

                return (numbers, pre);
            }

            var left = Split(a);
            var right = Split(b);
            var length = Math.Max(left.Numbers.Length, right.Numbers.Length);
            for (var i = 0; i < length; i++)
            {
                var x = i < left.Numbers.Length ? left.Numbers[i] : 0;
                var y = i < right.Numbers.Length ? right.Numbers[i] : 0;
                if (x != y)
                {
                    return x.CompareTo(y);
                }
            }

            // 数字段相同:无预发布后缀的更大(1.0.0 > 1.0.0-rc.1)
            if (left.Pre.Length == 0 && right.Pre.Length == 0)
            {
                return 0;
            }

            if (left.Pre.Length == 0)
            {
                return 1;
            }

            return right.Pre.Length == 0
                ? -1
                : string.CompareOrdinal(left.Pre, right.Pre);
        }

        /// <summary>启动 Pi Web 服务。返回是否成功进入运行状态。</summary>
        public async Task<bool> StartAsync()
        {
            if (this.IsRunning)
            {
                return true;
            }

            if (this.InstalledVersion is null)
            {
                await this.GetInstalledVersionAsync();
            }

            if (this.InstalledVersion is null)
            {
                this.LastStartError = $"未安装 {PiCli.PiWebPackageName}。请先点击「安装」。";
                this.AppendLog($"[启动失败] {this.LastStartError}\r\n");
                return false;
            }

            return await StartOnceAsync();
        }

        private async Task<bool> StartOnceAsync()
        {
            this.LastStartError = string.Empty;
            this._stopRequestedByUser = false;
            this._startFailureHandled = false;
            this._readyReported = false;
            this.WebUrl = null;
            this._lastStartLogMark = this._log.Length;
            this._lastRunArgs = BuildRunArgs();

            var settings = SettingsService.Instance.Settings;
            var (shimPath, locateError) = await PiCli.TryLocateAsync(PiCli.PiWebCommandName);
            if (shimPath.Length == 0)
            {
                this.LastStartError = locateError;
                this.AppendLog($"[启动失败] {locateError}\r\n");
                return false;
            }

            this._lastShimPath = shimPath;

            // 环境:密码走 PI_WEB_PASSWORD;代理注入给服务端自己的模型/API 请求用
            // (pi-web 也读 HTTP_PROXY / HTTPS_PROXY / NO_PROXY,见其 README 的“HTTP 代理”一节)。
            var startInfo = PlatformProcess.CreateShellStartInfo(
                PlatformProcess.ShellCommandForExecutable(shimPath, this._lastRunArgs),
                redirectOutput: true,
                rawByteOutput: true);

            ChildEnvironment.ApplyServiceOverrides(startInfo);
            if (settings.WebPassword.Length > 0)
            {
                startInfo.Environment["PI_WEB_PASSWORD"] = settings.WebPassword;
            }

            this.AppendLog($"\r\n[启动] {shimPath} {this._lastRunArgs}\r\n");

            Process process;
            try
            {
                process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
                process.OutputDataReceived += (_, e) =>
                {
                    if (e.Data is not null)
                    {
                        this.AppendLog(PlatformProcess.DecodeChildOutputLine(e.Data) + "\r\n", detectWebUrl: true);
                    }
                };
                process.ErrorDataReceived += (_, e) =>
                {
                    if (e.Data is not null)
                    {
                        this.AppendLog(PlatformProcess.DecodeChildOutputLine(e.Data) + "\r\n", detectWebUrl: true);
                    }
                };
                process.Exited += (_, _) => this.OnProcessExited(process);

                if (!process.Start())
                {
                    throw new InvalidOperationException("进程未能启动");
                }

                this._process = process;
                this._startTimeUtc = DateTime.UtcNow;
                process.BeginOutputReadLine();
                process.BeginErrorReadLine();

                // 进程随本进程一起退出(Windows Job Object;其他平台由 Stop 兜底)
                ChildProcessJobObject.Assign(process);

                StateChanged?.Invoke();
                return true;
            }
            catch (Exception ex)
            {
                this.LastStartError = ex.Message;
                this.AppendLog($"[启动失败] {ex.GetType().Name}: {ex.Message}\r\n");
                AppLogService.Write($"[服务] 启动失败: {ex}");
                StateChanged?.Invoke();
                return false;
            }
        }

        /// <summary>进程退出:清状态、通知界面,并在非用户主动停止时给出失败提示。</summary>
        private void OnProcessExited(Process process)
        {
            if (!ReferenceEquals(this._process, process))
            {
                return;
            }

            this._process = null;
            this.WebUrl = null;
            this._startTimeUtc = DateTime.MinValue;

            if (this._stopRequestedByUser)
            {
                this.AppendLog("[服务] 已停止。\r\n");
            }
            else
            {
                this.TryRecordStartFailure(process);
            }

            StateChanged?.Invoke();
        }

        /// <summary>
        /// 记录一次非用户主动的退出(启动失败或中途崩溃)。
        /// <para>
        /// 为什么单独留证:界面上的日志面板有长度上限、只保留尾部,而这里最需要的是**开头**
        /// (第一处报错)与当时的运行参数。两者一起写进 app.log,事后还能复盘。
        /// </para>
        /// </summary>
        private bool TryRecordStartFailure(Process process)
        {
            if (this._startFailureHandled)
            {
                return false;
            }

            this._startFailureHandled = true;

            int exitCode;
            try
            {
                exitCode = process.ExitCode;
            }
            catch (Exception)
            {
                exitCode = -1;
            }

            var summary = StartFailureDiagnostics.Describe(exitCode, this._runHead.ToString(), this._lastRunArgs);
            this.LastStartError = summary;
            this.AppendLog($"[服务] 已退出(退出码 {exitCode})。{summary}\r\n");
            AppLogService.Write($"[服务] 退出码 {exitCode}。{summary}");

            // 留证:把 pi-web 全局包目录、pi agent 目录、扩展安装位当刻的状态写进 app.log。
            // 面板里的输出有长度上限只留尾部,而"某个包半装/被占用"这类问题必须当场看清。
            StartFailureDiagnostics.WriteDependencySnapshot("Pi Web 启动失败", this._lastShimPath);

            // 通知界面弹一次提示(失败原因已经写进 LastStartError)
            StartFailed?.Invoke();
            return true;
        }

        /// <summary>重启服务(先停后启)。</summary>
        public async Task<bool> RestartAsync()
        {
            this.Stop();
            await Task.Delay(300);
            return await this.StartAsync();
        }

        /// <summary>
        /// 停止服务。
        /// <para>
        /// Windows 上子进程挂在 Job Object 里(见 <c>ChildProcessJobObject</c>),但**主动停止时**
        /// 仍要整树杀掉:pi-web 会再拉起一个 Next.js 子进程,只杀父进程会留下一个还在监听端口的孤儿 ——
        /// 下次启动就会因端口被占而失败(实测过)。
        /// </para>
        /// </summary>
        public void Stop()
        {
            var process = this._process;
            if (process is null)
            {
                return;
            }

            this._stopRequestedByUser = true;
            this.AppendLog("[服务] 正在停止…\r\n");

            try
            {
                if (!process.HasExited)
                {
                    process.Kill(entireProcessTree: true);
                }

                process.WaitForExit(5_000);
            }
            catch (Exception ex)
            {
                AppLogService.Write($"[服务] 停止失败: {ex.Message}");
            }
            finally
            {
                ChildProcessJobObject.CloseJobHandle();
                this._process = null;
                this.WebUrl = null;
                this._startTimeUtc = DateTime.MinValue;
                this.StateChanged?.Invoke();
            }
        }

        /// <summary>探测 Node.js 是否可用(pi-web 与 npm 安装都依赖它)。</summary>
        public async Task<bool> HasNodeEnvironmentAsync()
        {
            var result = await ChildProcessRunner.CaptureAsync("node --version");
            return result.ExitCode == 0 && result.Stdout.Trim().Length > 0;
        }

        /// <summary>
        /// 采集环境面板要显示的信息:Node / npm 版本、pi 与 pi-web 的命令路径、pi 数据目录。
        /// <para>
        /// 每次都要起几个子进程(约 1 秒),所以由调用方缓存(见首页的
        /// <c>EnsureEnvironmentLoadedAsync</c>)—— 本方法自己不做缓存,免得"刷新"变成空操作。
        /// </para>
        /// </summary>
        public async Task<(string? Node, string? Npm, string? PiPath, string? PiWebPath)> GetEnvironmentInfoAsync()
        {
            var node = await TryCaptureFirstLineAsync("node --version");
            var npm = await TryCaptureFirstLineAsync("npm --version");

            // pi 那一项返回**实际会被使用的那一份**:PATH 上的全局 pi 优先,没有就退到
            // pi-web 自带的那份(见 PiCli.TryBuildPiCommandAsync)。面板上必须说清是哪一个 ——
            // 否则"我明明没装 pi,这里怎么在装包"就成了一个说不通的现象。
            var (piCommand, _) = await PiCli.TryBuildPiCommandAsync();
            var (piWebPath, _) = await PiCli.FindAsync(PiCli.PiWebCommandName);

            var piPath = piCommand switch
            {
                { IsBundled: true } bundled => $"pi-web 自带的那份(node {bundled.ExecutablePath})",
                { } global => global.ExecutablePath,
                _ => null,
            };

            return (
                node,
                npm,
                piPath,
                piWebPath.Length > 0 ? piWebPath : null);
        }

        /// <summary>捕获一条命令的输出(用于环境面板:node / npm / pi 版本等)。</summary>
        public static async Task<string?> TryCaptureFirstLineAsync(string command)
        {
            try
            {
                var result = await ChildProcessRunner.CaptureAsync(command);
                var line = result.Stdout
                    .Split('\n', StringSplitOptions.RemoveEmptyEntries)
                    .Select(item => item.Trim())
                    .FirstOrDefault(item => item.Length > 0);
                return line ?? (result.ExitCode == 0 ? string.Empty : null);
            }
            catch (Exception)
            {
                return null;
            }
        }

        /// <summary>执行一条命令并把输出逐行写进日志(安装/更新都走这里,保证面板实时可见)。</summary>
        private async Task RunStreamingAsync(string command)
        {
            await ChildProcessRunner.StreamAsync(
                command,
                line => this.AppendLog(PlatformProcess.DecodeChildOutputLine(line) + "\r\n"));
        }

        /// <summary>取走本次运行输出的开头(供 app.log 留证;取一次即清)。</summary>
        internal string TakeRunHead()
        {
            lock (this._runHeadLock)
            {
                var text = this._runHead.ToString();
                this._runHead.Clear();
                return text;
            }
        }

        /// <summary>本次运行输出的总字符数(诊断用)。</summary>
        internal int RunOutputLength => this._runOutputLength;

        /// <summary>pi 数据目录(默认 <c>~/.pi/agent</c>;可用环境变量 PI_CODING_AGENT_DIR 覆盖)。</summary>
        public static string ResolveAgentDirectory()
        {
            var overridden = Environment.GetEnvironmentVariable("PI_CODING_AGENT_DIR");
            if (!string.IsNullOrWhiteSpace(overridden))
            {
                return overridden.Trim();
            }

            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            if (string.IsNullOrEmpty(home))
            {
                home = Environment.GetEnvironmentVariable("HOME") ?? string.Empty;
            }

            return Path.Combine(home, ".pi", "agent");
        }
    }
}
