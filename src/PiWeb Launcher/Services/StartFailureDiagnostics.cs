using System;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;

namespace PiWeb_Launcher.Services
{
    /// <summary>
    /// 启动失败诊断:把子进程的输出与退出码翻译成**可操作的提示**,并把当刻的依赖层快照
    /// 写进 app.log 留证。
    /// <para>
    /// 为什么单独成类:这里是"格式知识"(上游进程自己打印的那几类文本),而且规则表会随
    /// 上游版本变化,放在 <see cref="PiWebService"/> 里会把"启动/停止"的主线淹没在诊断细节中。
    /// 因此是可直接测试的纯函数 + 只读探测(见 PiWeb Launcher.Tests)。
    /// </para>
    /// </summary>
    internal static partial class StartFailureDiagnostics
    {
        /// <summary>
        /// 依据退出码、进程输出与本次运行参数,给出一句(可能多行)可操作的失败说明。
        /// <paramref name="runHead"/> 是本次运行输出的**开头**(见 <c>PiWebService.CaptureRunHead</c>)。
        /// </summary>
        public static string Describe(int exitCode, string runHead, string runArgs)
        {
            var hints = new List<string>();

            // Node 版本不够:pi-web 的 bin 会在启动前就检查,提示语里带版本要求
            if (ContainsAny(runHead, "Unsupported Node", "requires Node", "node --version", "requires at least Node"))
            {
                hints.Add("Node.js 版本过旧:pi-web 要求 Node 22.19.0 或更高版本。"
                    + "请升级 Node.js(https://nodejs.org)后重试。");
            }

            // 端口被占:EADDRINUSE / 中文 Windows 上的几种说法
            if (ContainsAny(runHead, "EADDRINUSE", "address already in use", "only one usage of each socket",
                    "地址已在使用", "端口已被占用", "只允许使用一次", "只允许对它使用一次"))
            {
                hints.Add("端口已被占用:可能已经有一个 Pi Web 在运行,或别的程序占着这个端口。"
                    + "请在设置页换一个端口,或先结束占用它的进程。");
            }

            if (ContainsAny(runHead, "EACCES", "EPERM", "permission denied", "拒绝访问"))
            {
                hints.Add("权限不足:请检查端口是否落在需要特权的范围(1-1023),"
                    + "或以管理员/合适的用户身份运行 PiWeb Launcher。");
            }

            if (ContainsAny(runHead, "ENOENT", "Cannot find module", "Build artifacts not found", "Cannot find package"))
            {
                hints.Add("缺少运行所需的文件:pi-web 的安装可能不完整。"
                    + "请在首页点「重新安装」,或手动执行 npm install -g @agegr/pi-web@latest。");
            }

            if (ContainsAny(runHead, "EAI_AGAIN", "ENOTFOUND", "ETIMEDOUT", "fetch failed"))
            {
                hints.Add("网络不可用:启动过程需要访问网络(检查更新)。"
                    + "若本机需要代理,请在设置页填写代理地址。");
            }

            if (hints.Count == 0)
            {
                hints.Add("请展开日志查看具体报错。若刚装过或更新过 Node.js / pi-web,重启一次本应用通常即可。");
            }

            // 参数一并给出来:用户拿同样的参数在终端里跑一次就能自己复现
            var argsText = runArgs.Length > 0 ? runArgs : "(默认参数)";
            return $"{string.Join("\r\n", hints)}\r\n启动参数:pi-web {argsText}(退出码 {exitCode}）";
        }

        /// <summary>
        /// 失败时把"依赖层快照"写进 app.log 留证。
        /// <para>
        /// 为什么要快照:pi-web 与 pi 装在 npm 全局目录里,而 pi 的扩展装在
        /// <c>&lt;agent&gt;/npm</c> 下。失败时点这两层往往是"某个包半装/被占用/权限不对",
        /// 只留一句报错事后查不出来 —— 当场把两层的可达性与关键文件记下来是唯一定位手段。
        /// </para>
        /// </summary>
        public static void WriteDependencySnapshot(string headline, string piWebShimPath)
        {
            try
            {
                var lines = new List<string>
                {
                    string.Empty,
                    $"[启动诊断] {headline} {DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}",
                    $"pi-web 命令路径:{(piWebShimPath.Length > 0 ? piWebShimPath : "(未定位到)")}",
                };

                var globalPiWeb = TryGetGlobalPackageDirectory(piWebShimPath, PiCli.PiWebPackageName);
                lines.Add($"① pi-web 全局包目录:{DescribeDirectory(globalPiWeb)}");
                if (globalPiWeb is not null)
                {
                    lines.Add($"   入口 bin/pi-web.js:{DescribeFile(Path.Combine(globalPiWeb, "bin", "pi-web.js"))}");
                    lines.Add($"   构建产物 .next:{DescribeDirectory(Path.Combine(globalPiWeb, ".next"))}");
                    lines.Add($"   清单 package.json:{DescribeFile(Path.Combine(globalPiWeb, "package.json"))}");
                }

                var agentDirectory = PiWebService.ResolveAgentDirectory();
                lines.Add($"② pi agent 目录:{DescribeDirectory(agentDirectory)}");
                lines.Add($"   设置文件 settings.json:{DescribeFile(Path.Combine(agentDirectory, "settings.json"))}");

                var npmDirectory = Path.Combine(agentDirectory, "npm");
                lines.Add($"③ pi 的 npm 目录(扩展安装位):{DescribeDirectory(npmDirectory)}");
                lines.Add($"   node_modules:{DescribeDirectory(Path.Combine(npmDirectory, "node_modules"))}");

                lines.Add(string.Empty);
                AppLogService.Write(string.Join("\r\n", lines));
            }
            catch (Exception ex)
            {
                AppLogService.Write($"[启动诊断] 依赖层快照失败: {ex.Message}");
            }
        }

        /// <summary>
        /// 由 npm shim 路径反推全局包目录(&lt;prefix&gt;\node_modules\&lt;包名&gt;);
        /// 仅 Windows 形态(命令是 .cmd)可直接反推,取不到返回 null。
        /// </summary>
        internal static string? TryGetGlobalPackageDirectory(string shimPath, string packageName)
        {
            if (shimPath.Length == 0)
            {
                return null;
            }

            try
            {
                var binDirectory = Path.GetDirectoryName(shimPath);
                if (string.IsNullOrEmpty(binDirectory))
                {
                    return null;
                }

                // <prefix>\node_modules\<包名> —— 包名里的 / 要拆成目录层(scoped 包)
                var candidate = Path.Combine(
                    binDirectory,
                    "node_modules",
                    packageName.Replace('/', Path.DirectorySeparatorChar));

                return Directory.Exists(candidate) ? candidate : null;
            }
            catch (Exception)
            {
                return null;
            }
        }

        private static string DescribeDirectory(string? path)
            => string.IsNullOrEmpty(path)
                ? "(路径未知)"
                : Directory.Exists(path) ? path + "  [存在]" : path + "  [不存在]";

        private static string DescribeFile(string path)
        {
            try
            {
                if (!File.Exists(path))
                {
                    return path + "  [不存在]";
                }

                var info = new FileInfo(path);
                return $"{path}  [存在, {info.Length} 字节]";
            }
            catch (Exception ex)
            {
                return $"{path}  [检查失败: {ex.Message}]";
            }
        }

        /// <summary>输出里是否出现任意一个关键字(大小写不敏感,取"任一命中即算")。</summary>
        internal static bool ContainsAny(string text, params string[] keywords)
        {
            if (string.IsNullOrEmpty(text))
            {
                return false;
            }

            foreach (var keyword in keywords)
            {
                if (text.Contains(keyword, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }

            return false;
        }

        /// <summary>从失败输出里取 <c>Error: ...</c> 那一行(界面上的标题用它,比整段输出好读)。</summary>
        internal static string? TryGetErrorLine(string output)
        {
            if (string.IsNullOrEmpty(output))
            {
                return null;
            }

            var match = ErrorLineRegex().Match(output);
            return match.Success ? match.Groups["text"].Value.Trim() : null;
        }

        /// <summary><c>Error: xxx</c> / <c>Error [CODE]: xxx</c> 形态的一行。</summary>
        [GeneratedRegex(@"(?m)^\s*(?<text>Error(?: \[[^\]]+\])?: .*)$")]
        private static partial Regex ErrorLineRegex();
    }
}
