using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace PiWeb_Launcher.Services.Packages
{
    /// <summary>
    /// 只读地扫一遍 pi 自己的安装目录,给界面补上"版本号"这类 <c>pi list</c> 不打印的信息。
    /// <para>
    /// 为什么需要它:<c>pi list</c> 只给"源规格"和"已安装路径",**不给版本**。
    /// 而 pi 装 npm 包的位置是固定的(<c>&lt;agent&gt;/npm</c>,里面有一份普通的
    /// <c>package.json</c> + <c>node_modules</c>),版本号就在每个包的 <c>package.json</c> 里。
    /// </para>
    /// <para>
    /// ⚠ 这是本项目**唯一**直接读 pi 私有目录结构的地方,而且刻意止步于"读" ——
    /// 任何写操作(装/卸/改)都必须经 <c>pi</c> 命令(见 <see cref="PiCli"/>),
    /// 因为 <c>packages</c> 字段的形态可能随版本变化,猜格式写文件迟早会写坏用户的设置。
    /// </para>
    /// </summary>
    internal static class PiNpmDirectoryReader
    {
        /// <summary>pi 存放 npm 包的位置(相对 agent 目录)。</summary>
        internal const string NpmSubDirectory = "npm";

        /// <summary>
        /// 读出"包名 → 版本"。读不到任何东西时返回空字典(调用方据此退化为不显示版本)。
        /// <para>
        /// 版本优先取 <c>node_modules/&lt;包名&gt;/package.json</c> 的 <c>version</c>(真实安装的版本),
        /// 取不到再退回依赖声明里的范围(如 <c>^2.38.0</c>)—— 后者带 <c>^</c>,界面上会照原样显示,
        /// 这比假装知道版本要诚实:它至少说明"pi 记着的声明是这一段"。
        /// </para>
        /// </summary>
        public static IReadOnlyDictionary<string, string> ReadVersions(string agentDirectory)
        {
            var versions = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            if (string.IsNullOrWhiteSpace(agentDirectory))
            {
                return versions;
            }

            var npmDirectory = Path.Combine(agentDirectory, NpmSubDirectory);
            var manifestPath = Path.Combine(npmDirectory, "package.json");

            try
            {
                if (!File.Exists(manifestPath))
                {
                    return versions;
                }

                using var document = JsonDocument.Parse(File.ReadAllText(manifestPath));
                if (!document.RootElement.TryGetProperty("dependencies", out var dependencies)
                    || dependencies.ValueKind != JsonValueKind.Object)
                {
                    return versions;
                }

                foreach (var dependency in dependencies.EnumerateObject())
                {
                    var declared = dependency.Value.ValueKind == JsonValueKind.String
                        ? dependency.Value.GetString() ?? string.Empty
                        : string.Empty;

                    versions[dependency.Name] = NormalizeDeclaredVersion(declared);
                }

                // 再补一遍真实安装版本(同名覆盖声明里的范围)
                foreach (var name in new List<string>(versions.Keys))
                {
                    var installed = ReadInstalledVersion(npmDirectory, name);
                    if (installed.Length > 0)
                    {
                        versions[name] = installed;
                    }
                }
            }
            catch (Exception)
            {
                // 目录结构或文件内容不认(pi 改过布局):不显示版本,不影响其它功能
            }

            return versions;
        }

        /// <summary>读某个包在 pi 的 npm 目录里的真实安装版本;读不到返回空串。</summary>
        private static string ReadInstalledVersion(string npmDirectory, string packageName)
        {
            try
            {
                var manifestPath = Path.Combine(npmDirectory, "node_modules", packageName.Replace('/', Path.DirectorySeparatorChar), "package.json");
                if (!File.Exists(manifestPath))
                {
                    return string.Empty;
                }

                using var document = JsonDocument.Parse(File.ReadAllText(manifestPath));
                return document.RootElement.TryGetProperty("version", out var version)
                       && version.ValueKind == JsonValueKind.String
                    ? version.GetString() ?? string.Empty
                    : string.Empty;
            }
            catch (Exception)
            {
                return string.Empty;
            }
        }

        /// <summary>是否是"看起来像精确版本"的字符串(数字开头且含点)。</summary>
        private static bool LooksLikeExactVersion(string value)
            => value.Length > 0 && char.IsDigit(value[0]) && value.Contains('.', StringComparison.Ordinal);

        /// <summary>
        /// 归一化依赖声明里的版本:精确版本(<c>x.y.z</c>)去掉可能的前缀后直接用;
        /// 范围(<c>^x.y.z</c>、<c>&gt;=1</c>、<c>latest</c> 等)照原样保留 —— 那本来就是
        /// "pi 记着的声明",界面照实显示比编一个版本号诚实。
        /// </summary>
        internal static string NormalizeDeclaredVersion(string declared)
        {
            var trimmed = declared.TrimStart('^', '~', '=').Trim();
            if (trimmed.Length > 0 && LooksLikeExactVersion(trimmed))
            {
                return trimmed;
            }

            return declared;
        }
    }
}
