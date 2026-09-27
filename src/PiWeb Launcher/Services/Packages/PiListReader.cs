using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using PiWeb_Launcher.Models;

namespace PiWeb_Launcher.Services.Packages
{
    /// <summary>
    /// 解析 <c>pi list</c> 的输出。以爬到的实际实现为准,输出长这样:
    /// <code>
    /// User packages:
    ///   npm:pi-mcp-adapter
    ///     C:\Users\me\.pi\agent\npm\node_modules\pi-mcp-adapter
    ///   npm:@foo/bar (filtered)
    /// Project packages:
    ///   git:github.com/a/b@v1
    /// </code>
    /// 一条都没装时只打印一行 <c>No packages installed.</c>。
    /// <para>
    /// 为什么单独成类:这是**纯文本映射**,不碰进程、不碰文件、不碰界面;而它的输入是
    /// 一个不断演进的 CLI 输出 —— 解析规则必须能被单元测试逐字固定下来,否则升级 pi 之后
    /// 界面会静默地少显示几个包(而不是报错)。
    /// </para>
    /// <para>
    /// 解析原则:**宽容**。认不出的行一律跳过(不抛异常、不把半个句子当包名),
    /// 因为输出里混着进度提示、警告与颜色控制字符;而"少一条"的代价远小于"多一条假条目"。
    /// </para>
    /// </summary>
    internal static partial class PiListReader
    {
        /// <summary>没有安装任何包时 pi 打印的那一行(小写比较,可能带终端样式字符)。</summary>
        internal const string EmptyMarker = "No packages installed";

        /// <summary>用户级分节标题(pi 原样输出的英文标题)。</summary>
        internal const string UserSection = "User packages:";

        /// <summary>项目级分节标题。</summary>
        internal const string ProjectSection = "Project packages:";

        /// <summary>条目行的缩进量(pi 用两个空格)。比它更深的是"已安装路径"那一行。</summary>
        private const int ItemIndent = 2;

        /// <summary>已安装路径行的缩进量(pi 用四个空格)。</summary>
        private const int PathIndent = 4;

        /// <summary>资源过滤标记的后缀(pi 对带过滤器的条目打印 <c>npm:foo (filtered)</c>)。</summary>
        private const string FilteredSuffix = "(filtered)";

        /// <summary>
        /// 解析 <c>pi list</c> 的输出为已安装包列表。
        /// 分节标题之前的内容(版本检查、更新提示等)一律忽略。
        /// </summary>
        public static IReadOnlyList<PiInstalledPackage> Parse(string output)
        {
            var entries = new List<PiInstalledPackage>();
            if (string.IsNullOrEmpty(output) || output.Contains(EmptyMarker, StringComparison.OrdinalIgnoreCase))
            {
                return entries;
            }

            var scopeIsProject = false;

            // 上一条条目还没读到路径行时挂在这里(路径行是可选的下一条)
            string? pendingSource = null;
            var pendingFiltered = false;

            void FlushPending(string installedPath)
            {
                if (pendingSource is null)
                {
                    return;
                }

                var source = pendingSource;
                entries.Add(Build(source, scopeIsProject, pendingFiltered, installedPath));
                pendingSource = null;
                pendingFiltered = false;
            }

            foreach (var rawLine in output.Split('\n'))
            {
                var line = StripControl(rawLine).TrimEnd('\r', ' ', '\t');

                // 空行:条目之间可能有空行,但已读到的条目仍归当前分节
                if (line.Trim().Length == 0)
                {
                    FlushPending(string.Empty);
                    continue;
                }

                var indent = CountIndent(line);
                var text = line.Trim();

                if (indent == 0)
                {
                    FlushPending(string.Empty);

                    if (text.Equals(UserSection, StringComparison.OrdinalIgnoreCase))
                    {
                        scopeIsProject = false;
                        continue;
                    }

                    if (text.Equals(ProjectSection, StringComparison.OrdinalIgnoreCase))
                    {
                        scopeIsProject = true;
                        continue;
                    }

                    // 分节之外的顶层行(更新提示、错误摘要等):忽略
                    continue;
                }

                if (indent >= PathIndent)
                {
                    // 已安装路径行:接到上一条条目上
                    FlushPending(text);
                    continue;
                }

                if (indent >= ItemIndent)
                {
                    // 还没进过任何分节时也照收:极可能是新版本换了标题文案 ——
                    // "显示不出来"比"作用域标错"更糟,而作用域在界面上只是一行小字。
                    FlushPending(string.Empty);

                    var isFiltered = text.EndsWith(FilteredSuffix, StringComparison.OrdinalIgnoreCase);
                    pendingSource = isFiltered
                        ? text[..^FilteredSuffix.Length].TrimEnd()
                        : text;
                    pendingFiltered = isFiltered;
                    continue;
                }
            }

            FlushPending(string.Empty);
            return entries;
        }

        /// <summary>由一条源规格与可选路径构造已安装包。</summary>
        private static PiInstalledPackage Build(string source, bool isProjectScope, bool filtered, string installedPath)
        {
            var kind = ClassifySource(source);
            return new PiInstalledPackage
            {
                Source = source,
                Name = ExtractName(source, kind),
                Kind = kind,
                IsProjectScope = isProjectScope,
                // 过滤标记只影响"这个包装了哪些资源",与本页的启停无关,先如实记进日志/工具提示
                AwaitingRestart = false,
                InstalledPath = installedPath,
                IsFiltered = filtered,
            };
        }

        /// <summary>判断一个源规格属于哪一类(pi 的来源前缀只有 npm: / git: / 路径几种)。</summary>
        internal static PackageSourceKind ClassifySource(string source)
        {
            if (source.StartsWith("npm:", StringComparison.OrdinalIgnoreCase))
            {
                return PackageSourceKind.Npm;
            }

            if (source.StartsWith("git:", StringComparison.OrdinalIgnoreCase)
                || source.StartsWith("ssh://", StringComparison.OrdinalIgnoreCase)
                || source.StartsWith("git@", StringComparison.OrdinalIgnoreCase)
                || source.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
                || source.StartsWith("https://", StringComparison.OrdinalIgnoreCase)
                || source.Contains(".git", StringComparison.OrdinalIgnoreCase))
            {
                return PackageSourceKind.Git;
            }

            if (source.StartsWith('.') || source.StartsWith('/') || source.StartsWith('\\')
                || source.Contains(":\\", StringComparison.Ordinal))
            {
                return PackageSourceKind.Local;
            }

            // 兜底:pi 文档里没有裸包名这一形态,真出现了按 npm 处理(它至少是个包名而不是 URL)
            return PackageSourceKind.Npm;
        }

        /// <summary>
        /// 从源规格里取出**包名**:npm 源去掉前缀与版本、git 源取仓库名、本地路径取最后一段。
        /// 用于在目录里给条目打“已安装”标记(目录给的是包名,不是源规格)。
        /// </summary>
        internal static string ExtractName(string source, PackageSourceKind kind)
        {
            var value = source.Trim();
            if (kind == PackageSourceKind.Npm)
            {
                if (value.StartsWith("npm:", StringComparison.OrdinalIgnoreCase))
                {
                    value = value[4..];
                }

                return StripVersion(value);
            }

            if (kind == PackageSourceKind.Git)
            {
                var repo = value.StartsWith("git:", StringComparison.OrdinalIgnoreCase) ? value[4..] : value;
                repo = StripRef(repo);
                repo = repo.TrimEnd('/');
                var slash = repo.LastIndexOf('/');
                var name = slash >= 0 ? repo[(slash + 1)..] : repo;
                return name.EndsWith(".git", StringComparison.OrdinalIgnoreCase) ? name[..^4] : name;
            }

            if (kind == PackageSourceKind.Local)
            {
                var trimmed = value.TrimEnd('/', '\\');
                var slash = trimmed.LastIndexOfAny(['/', '\\']);
                return slash >= 0 ? trimmed[(slash + 1)..] : trimmed;
            }

            return StripVersion(value);
        }

        /// <summary>去掉 npm 规格上的版本段(<c>@1.2.3</c> / <c>@^1.0.0</c>);scoped 包名的第一个 @ 是 scope 分隔符。</summary>
        internal static string StripVersion(string value)
        {
            var searchFrom = value.StartsWith('@') ? value.IndexOf('/', StringComparison.Ordinal) + 1 : 0;
            if (searchFrom < 0)
            {
                return value;
            }

            var at = value.IndexOf('@', searchFrom);
            return at > 0 ? value[..at] : value;
        }

        /// <summary>去掉 git 规格上的 ref 段(<c>@v1</c> / <c>@&lt;sha&gt;</c>)。</summary>
        internal static string StripRef(string value)
        {
            // ⚠ 只能从**路径段**里找 @:SSH 形态 git@github.com:a/b 的 @ 是用户名分隔符,
            // 从末尾往前找 "最后一个 @ 且在第一个 / 之后" 才是 ref。
            var slash = value.IndexOf('/');
            var at = value.LastIndexOf('@');
            return at > slash && slash >= 0 ? value[..at] : value;
        }

        /// <summary>去掉终端颜色/光标控制字符(<c>\u001b[...m</c> 之类)。</summary>
        internal static string StripControl(string line)
        {
            if (line.IndexOf('\u001b') < 0)
            {
                return line;
            }

            return AnsiRegex().Replace(line, string.Empty);
        }

        /// <summary>行首空格数(按空格与制表符计,制表符算两格)。</summary>
        private static int CountIndent(string line)
        {
            var indent = 0;
            foreach (var ch in line)
            {
                if (ch == ' ')
                {
                    indent++;
                }
                else if (ch == '\t')
                {
                    indent += 2;
                }
                else
                {
                    break;
                }
            }

            return indent;
        }

        /// <summary>ANSI 转义序列(CSI 与 OSC)。</summary>
        [GeneratedRegex(@"\u001b(?:\[[0-9;?]*[ -/]*[@-~]|\][^\u0007]*(?:\u0007|\u001b\\)|[@-Z\\-_])")]
        private static partial Regex AnsiRegex();
    }
}
