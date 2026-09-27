using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

namespace PiWeb_Launcher.Services
{
    /// <summary>
    /// pi 命令行的定位与执行。
    /// <para>
    /// 三个命令都收在这里(<see cref="PiCommandName"/> / <see cref="PiWebCommandName"/> / 内置的 pi),因为它们
    /// 是同一件事的三半:pi 是 Coding Agent 本体(包管理与会话都在它里面),pi-web 是它的浏览器界面,
    /// 而 <b>pi-web 里还自带了一份完整的 pi</b>(见 <see cref="TryLocateBundledPiAsync"/>)。
    /// 定位与执行的写法完全一样,分成几个类只会让"改了一份忘了另一份"(编码判定、call 前缀这些坑)。
    /// </para>
    /// <para>
    /// 为什么包管理走这里而不是自己读写 <c>settings.json</c>:pi 把包的声明放在
    /// <c>~/.pi/agent/settings.json</c> 的 <c>packages</c> 字段里,但格式可能随版本变化
    /// (字符串 / 带 <c>source</c> 的对象 / 带资源过滤的对象),还要处理项目级 <c>.pi/settings.json</c>
    /// 与锁定、去重。这些都是 pi 自己的知识 —— 启动器只调 <c>pi install</c> / <c>pi list</c> / <c>pi remove</c>,
    /// 不猜它的文件格式(唯一的例外是**只读地**扫一遍 pi 的 npm 目录补版本号,见 <c>PiNpmDirectoryReader</c>)。
    /// </para>
    /// <para>
    /// 输出一律经 <see cref="ChildProcessRunner"/>(按原样字节收下再逐行判定编码、
    /// 并注入 npm 源/代理)。
    /// </para>
    /// </summary>
    internal static class PiCli
    {
        /// <summary>pi 本体的命令名(npm 全局 bin 下就是这个名字的 shim,npm 包是 <c>@earendil-works/pi-coding-agent</c>)。</summary>
        internal const string PiCommandName = "pi";

        /// <summary>Pi Web 的命令名(npm 包 <c>@agegr/pi-web</c>)。</summary>
        internal const string PiWebCommandName = "pi-web";

        /// <summary>pi 本体的 npm 包名(安装/更新/版本查询都用它)。</summary>
        internal const string PiPackageName = "@earendil-works/pi-coding-agent";

        /// <summary>Pi Web 的 npm 包名。</summary>
        internal const string PiWebPackageName = "@agegr/pi-web";

        /// <summary>npm 全局根目录(全局装的包都在这下面)。</summary>
        internal const string GlobalNodeModulesDirectoryName = "node_modules";

        /// <summary>
        /// 执行一条 pi 子命令并捕获输出(全局 <c>pi</c> 优先,找不到就用 pi-web 自带的那份)。
        /// 返回码 -1 表示两者都定位不到(原因见 <c>Stderr</c>,可直接展示给用户)。
        /// </summary>
        public static async Task<(int ExitCode, string Stdout, string Stderr)> RunCaptureAsync(string arguments)
        {
            var (command, locateError) = await TryBuildPiCommandAsync();
            if (command is not { } pi)
            {
                return (-1, string.Empty, locateError);
            }

            var result = await ChildProcessRunner.CaptureAsync(pi.CommandLineFor(arguments));
            return (result.ExitCode, result.Stdout, result.Stderr);
        }

        /// <summary>
        /// 执行一条 pi 子命令,输出按行流式回传(用于需要实时反馈的操作,如安装/卸载包)。
        /// 返回码 -1 表示两者都定位不到。
        /// </summary>
        public static async Task<int> RunPiStreamingAsync(string arguments, Action<string> onOutput)
        {
            var (command, locateError) = await TryBuildPiCommandAsync();
            if (command is not { } pi)
            {
                onOutput(locateError);
                return -1;
            }

            return await ChildProcessRunner.StreamAsync(pi.CommandLineFor(arguments), onOutput);
        }

        /// <summary>执行指定命令(pi-web),输出按行流式回传。</summary>
        public static async Task<int> RunStreamingAsync(
            string commandName, string arguments, Action<string> onOutput)
        {
            var (shimPath, locateError) = await TryLocateAsync(commandName);
            if (shimPath.Length == 0)
            {
                onOutput(locateError);
                return -1;
            }

            return await ChildProcessRunner.StreamAsync(
                PlatformProcess.ShellCommandForExecutable(shimPath, arguments), onOutput);
        }

        /// <summary>定位 pi 命令;失败时返回空串与可直接展示的错误说明。</summary>
        public static async Task<(string ShimPath, string Error)> TryLocateAsync()
            => await TryLocateAsync(PiCommandName);

        /// <summary>定位指定命令(pi-web);失败时返回空串与可直接展示的错误说明。</summary>
        public static async Task<(string ShimPath, string Error)> TryLocateAsync(string commandName)
        {
            try
            {
                var (found, probeOutput) = await FindAsync(commandName);
                return found.Length > 0
                    ? (found, string.Empty)
                    : (string.Empty, $"未找到“{commandName}”命令。npm 全局 bin 目录可能不在 PATH 中,或包未正确安装。\r\n"
                        + $"{PlatformProcess.LocateCommandLine(commandName)} 输出:\r\n{probeOutput}");
            }
            catch (Exception ex)
            {
                return (string.Empty, $"定位 {commandName} 命令失败: {ex.Message}");
            }
        }

        /// <summary>一条"可执行的 pi"——要么是 PATH 上的 shim,要么是 pi-web 自带的那份。</summary>
        /// <param name="ExecutablePath">可执行文件(全局 shim,或 <c>node</c> 本身)。</param>
        /// <param name="LeadingArguments">
        /// 排在用户参数**之前**的固定参数。自带 pi 时是那个 <c>cli.js</c> 的路径
        /// (即 <c>node &lt;cli.js&gt; install …</c>);全局 shim 时为空。
        /// </param>
        /// <param name="IsBundled">true = 用的是 pi-web 自带的那份(界面/日志里要说明,便于解释版本差异)。</param>
        internal readonly record struct PiCommand(string ExecutablePath, string LeadingArguments, bool IsBundled)
        {
            public string CommandLineFor(string arguments) => PlatformProcess.ShellCommandForExecutable(
                this.ExecutablePath,
                this.LeadingArguments.Length == 0 ? arguments : this.LeadingArguments + " " + arguments);
        }

        /// <summary>
        /// 找一条可用的 pi:**PATH 上的全局 <c>pi</c> 优先**,没有就退到 **pi-web 自带的那份**。
        /// <para>
        /// 为什么全局优先:用户自己装过 pi 时,他期望启动器用的是**那一个**
        /// (版本、配置、可能还有本地补丁)。只有在他没装 pi 时,用 pi-web 自带的才是"雪中送炭"。
        /// </para>
        /// <para>
        /// 为什么可以退到自带的:pi-web 把 <c>@earendil-works/pi-coding-agent</c> 作为**正式依赖**
        /// 精确钉住(它要用 pi 的 SDK 跑 Agent),所以那份副本是**完整包** —— 它的 <c>package.json</c>
        /// 里就有 <c>"bin": { "pi": "dist/bundle/cli.js" }</c>。用 <c>node &lt;cli.js&gt; &lt;子命令&gt;</c>
        /// 调用它,行为与全局 <c>pi</c> 一致(<c>--version</c>/<c>list</c> 都实测过)。
        /// 于是"只装了 pi-web 没装 pi"的用户也能在这里装卸包,不必为了包管理再装一份 pi。
        /// </para>
        /// <para>
        /// ⚠ 自带的 pi 跑的是 <c>node</c>,所以**没有 node 就用不了这条退路** —— 这是必然的,
        /// pi 本身也是个 Node 程序。
        /// </para>
        /// </summary>
        public static async Task<(PiCommand? Command, string Error)> TryBuildPiCommandAsync()
        {
            var (shimPath, shimError) = await TryLocateAsync(PiCommandName);
            if (shimPath.Length > 0)
            {
                return (new PiCommand(shimPath, string.Empty, IsBundled: false), string.Empty);
            }

            var (bundled, bundledError) = await TryLocateBundledPiAsync();
            if (bundled is { } command)
            {
                return (command, string.Empty);
            }

            // 两条路都不通:把**两条**的原因都给出来,否则用户会以为"装了 pi-web 也没用"
            return (null,
                "未找到可用的 pi 命令,包管理无法使用。\r\n"
                + $"— PATH 上没找到:{shimError}\r\n"
                + $"— pi-web 自带的那份也没找到:{bundledError}\r\n"
                + "请任选一种:npm install -g @earendil-works/pi-coding-agent,或安装/重装 Pi Web(它自带一份完整的 pi)。");
        }

        /// <summary>
        /// 找 pi-web 自带的那份 pi(<c>&lt;npm 全局根&gt;/@agegr/pi-web/node_modules/.../cli.js</c>)。
        /// <para>
        /// 定位方式:先问 <c>npm root -g</c>(一条命令,稳),失败再退到"由 pi-web 的 shim 反推"。
        /// <c>npm root -g</c> 比反推可靠 —— 全局 bin 目录与全局包目录**不一定同级**
        /// (Windows 上 npm 的 prefix 是 <c>%APPDATA%\npm</c>,而某些配置下两者会分开)。
        /// </para>
        /// </summary>
        public static async Task<(PiCommand? Command, string Error)> TryLocateBundledPiAsync()
        {
            try
            {
                var (nodePath, nodeError) = await TryLocateAsync("node");
                if (nodePath.Length == 0)
                {
                    return (null, "未找到 node 命令(自带的 pi 也要靠 node 运行):" + nodeError);
                }

                string? cliPath = null;
                var searched = new List<string>();

                foreach (var root in await CandidateGlobalNodeModulesAsync())
                {
                    var candidate = Path.Combine(
                        root,
                        PiWebPackageName.Replace('/', Path.DirectorySeparatorChar),
                        GlobalNodeModulesDirectoryName,
                        PiPackageName.Replace('/', Path.DirectorySeparatorChar),
                        BundledPiRelativePath.Replace('/', Path.DirectorySeparatorChar));

                    searched.Add(candidate);
                    if (File.Exists(candidate))
                    {
                        cliPath = candidate;
                        break;
                    }
                }

                if (cliPath is null)
                {
                    return (null, "已在下列位置查找 pi-web 自带的 pi,都没有找到:\r\n  " + string.Join("\r\n  ", searched));
                }

                // 自带的那份要用 node 跑:固定参数是 cli.js 的路径,用户参数接在后面
                return (new PiCommand(nodePath, PlatformProcess.Quote(cliPath), IsBundled: true), string.Empty);
            }
            catch (Exception ex)
            {
                return (null, $"定位 pi-web 自带的 pi 失败: {ex.Message}");
            }
        }

        /// <summary>自带 pi 在 pi-web 包内的相对路径(与 <c>@earendil-works/pi-coding-agent</c> 的 <c>bin.pi</c> 一致)。</summary>
        internal const string BundledPiRelativePath = "dist/bundle/cli.js";

        /// <summary>
        /// 候选的 npm 全局包目录:先问 <c>npm root -g</c>,再按 pi-web 的 shim 位置反推。
        /// 去重且保持顺序(先问出来的更可信)。
        /// </summary>
        private static async Task<IReadOnlyList<string>> CandidateGlobalNodeModulesAsync()
        {
            var candidates = new List<string>();

            try
            {
                var probe = await ChildProcessRunner.CaptureAsync("npm root -g");
                if (probe.ExitCode == 0)
                {
                    var line = probe.Stdout
                        .Split('\n', StringSplitOptions.RemoveEmptyEntries)
                        .Select(item => item.Trim())
                        .FirstOrDefault(item => item.Length > 0 && !item.Contains('\r'));

                    if (!string.IsNullOrEmpty(line) && Directory.Exists(line))
                    {
                        candidates.Add(line);
                    }
                }
            }
            catch (Exception)
            {
                // 问不出来就只靠反推(下面那条)
            }

            // 反推:pi-web.cmd 所在目录的 node_modules(Windows 上两者同级;其它平台多半是 bin/../lib/node_modules)
            var (shimPath, _) = await FindAsync(PiWebCommandName);
            if (shimPath.Length > 0)
            {
                try
                {
                    var binDirectory = Path.GetDirectoryName(shimPath);
                    if (!string.IsNullOrEmpty(binDirectory))
                    {
                        candidates.Add(Path.Combine(binDirectory, GlobalNodeModulesDirectoryName));
                        candidates.Add(Path.Combine(binDirectory, "..", "lib", GlobalNodeModulesDirectoryName));
                    }
                }
                catch (Exception)
                {
                    // 路径拼不出来就少一个候选,不影响 npm root -g 那条
                }
            }

            return candidates
                .Select(Path.GetFullPath)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        /// <summary>
        /// 定位 npm 全局 bin 下的命令。
        /// Windows:where &lt;cmd&gt; → 取 .cmd/.exe(npm 装出来的是 &lt;cmd&gt;.cmd);
        /// 类 Unix(macOS/Linux):command -v &lt;cmd&gt; → 取第一条(npm 装出来的是**无扩展名**的
        /// 符号链接,例如 /usr/local/bin/pi,按 .cmd/.exe 过滤只会得出“未找到”)。
        /// 返回空串表示未找到,同时返回探测命令的原始输出以便在错误信息里展示。
        /// </summary>
        public static async Task<(string ShimPath, string ProbeOutput)> FindAsync(string commandName)
        {
            var probeCommand = PlatformProcess.LocateCommandLine(commandName);
            var probe = await ChildProcessRunner.CaptureAsync(probeCommand);

            var candidates = probe.Stdout
                .Split('\n', StringSplitOptions.RemoveEmptyEntries)
                .Select(line => line.Trim())
                .Where(line => line.Length > 0)
                .ToList();

            var shimPath = PlatformProcess.IsWindows
                ? candidates.FirstOrDefault(line => line.EndsWith(".cmd", StringComparison.OrdinalIgnoreCase)
                                                    || line.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) ?? string.Empty
                : candidates.FirstOrDefault() ?? string.Empty;

            return (shimPath, (probe.Stdout + probe.Stderr).TrimEnd());
        }
    }
}
