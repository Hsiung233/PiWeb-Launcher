using System;
using System.IO;
using System.Text;
using System.Threading.Tasks;

namespace PiWeb_Launcher.Services
{
    /// <summary>
    /// 用 Node.js 取一个网页的兜底通道。
    /// <para>
    /// 为什么需要它(不是"多此一举"):.NET 的 <c>HttpClient</c> 在 Windows 上走 SChannel,
    /// 一旦当前进程拿不到系统凭据句柄就会以
    /// <c>AuthenticationException: 安全包中没有可用的凭证</c> 失败 —— 现象是**所有** HTTPS 都连不上,
    /// 而同一个进程里 <c>node -e fetch(...)</c> 完全正常(Node 自带 OpenSSL + 内置 CA,
    /// 不走 SChannel)。这种情况在受限的进程沙箱里是常态,而本应用的运行前提里本来就有 Node
    /// (pi-web 与 pi 都要求它),所以拿 Node 当兜底通道不引入任何新依赖。
    /// </para>
    /// <para>
    /// ⚠ 只在 <see cref="LooksLikeTlsFailure"/> 命中时使用:正常情况下走 <c>HttpClient</c>,
    /// 不为每次抓取都拉起一个进程。
    /// </para>
    /// <para>
    /// ⚠ 脚本走**临时 .mjs 文件**、结果写**临时文件**,都不经 stdio 管道:
    /// 沙箱下节点进程常常拿不到管道句柄(<c>spawn EPERM</c>),文件是唯一稳的通道;
    /// 顺带也免掉了把 HTML 拼进命令行时的引号/转义问题。
    /// </para>
    /// </summary>
    internal static class NodeFetchFallback
    {
        /// <summary>Node 侧脚本体。只用 ASCII,不含任何引号,方便直接内联成文件。</summary>
        private const string Script = """
            const fs = require("node:fs");
            const url = process.argv[2];
            const out = process.argv[3];
            const UA = "PiWebLauncher/1.0 (+https://pi.dev/packages)";
            (async () => {
              try {
                const controller = new AbortController();
                const timer = setTimeout(() => controller.abort(), 20000);
                const response = await fetch(url, { headers: { "user-agent": UA, accept: "text/html,application/xhtml+xml" }, signal: controller.signal });
                clearTimeout(timer);
                const body = await response.text();
                fs.writeFileSync(out, JSON.stringify({ ok: response.ok, status: response.status, body }), "utf8");
              } catch (error) {
                fs.writeFileSync(out, JSON.stringify({ ok: false, status: 0, error: String(error && error.message ? error.message : error) }), "utf8");
              }
            })();
            """;

        /// <summary>
        /// 判断一个异常是否属于"本机 TLS 凭据问题"。
        /// 只看异常链上的类型与关键字,不吞别的错误(超时、404、DNS 失败都不该走兜底)。
        /// </summary>
        internal static bool LooksLikeTlsFailure(Exception exception)
        {
            for (var current = exception; current is not null; current = current.InnerException)
            {
                var message = current.Message;
                if (message.Contains("SSL", StringComparison.OrdinalIgnoreCase)
                    || message.Contains("安全包", StringComparison.Ordinal)
                    || message.Contains("Authentication failed", StringComparison.OrdinalIgnoreCase)
                    || message.Contains("SecureConnectionError", StringComparison.OrdinalIgnoreCase)
                    || current is System.Security.Authentication.AuthenticationException)
                {
                    return true;
                }
            }

            return false;
        }

        /// <summary>
        /// 用 Node 抓取网页正文。返回 null 表示兜底也不可用(未装 Node / 脚本执行失败 / 抓取失败),
        /// 调用方应把原来的异常抛给用户。
        /// </summary>
        public static async Task<string?> TryGetStringAsync(string url)
        {
            string? scriptPath = null;
            string? resultPath = null;

            try
            {
                var directory = Path.Combine(Path.GetTempPath(), "PiWebLauncher", "fetch");
                Directory.CreateDirectory(directory);
                scriptPath = Path.Combine(directory, "fetch.cjs");
                await File.WriteAllTextAsync(scriptPath, Script, new UTF8Encoding(false));

                resultPath = Path.Combine(directory, "result-" + Guid.NewGuid().ToString("N") + ".json");

                var (nodePath, _) = await PiCli.FindAsync("node");
                if (nodePath.Length == 0)
                {
                    return null;
                }

                var command = PlatformProcess.ShellCommandForExecutable(
                    nodePath,
                    PlatformProcess.Quote(scriptPath) + " "
                    + PlatformProcess.Quote(url) + " "
                    + PlatformProcess.Quote(resultPath));

                var result = await ChildProcessRunner.CaptureAsync(command);
                if (!File.Exists(resultPath))
                {
                    AppLogService.Write($"[目录] Node 兜底抓取未产出结果(退出码 {result.ExitCode}): {result.Stderr.Trim()}");
                    return null;
                }

                var payload = await File.ReadAllTextAsync(resultPath);
                var envelope = System.Text.Json.JsonDocument.Parse(payload).RootElement;
                if (!envelope.TryGetProperty("ok", out var ok) || !ok.GetBoolean())
                {
                    var reason = envelope.TryGetProperty("error", out var error) ? error.GetString() : null;
                    AppLogService.Write($"[目录] Node 兜底抓取失败: {reason}");
                    return null;
                }

                AppLogService.Write("[目录] HttpClient 的 TLS 不可用,已改用 Node 抓取目录页。");
                return envelope.TryGetProperty("body", out var body) ? body.GetString() : null;
            }
            catch (Exception ex)
            {
                AppLogService.Write($"[目录] Node 兜底抓取异常: {ex.Message}");
                return null;
            }
            finally
            {
                TryDelete(scriptPath);
                TryDelete(resultPath);
            }
        }

        private static void TryDelete(string? path)
        {
            if (path is null)
            {
                return;
            }

            try
            {
                if (File.Exists(path))
                {
                    File.Delete(path);
                }
            }
            catch (Exception)
            {
                // 临时文件删不掉不影响任何功能
            }
        }
    }
}
