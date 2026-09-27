using System;
using System.Collections.Generic;
using System.Globalization;
using System.Net;
using System.Text.RegularExpressions;
using PiWeb_Launcher.Models;

namespace PiWeb_Launcher.Services.Packages
{
    /// <summary>
    /// pi 包目录(https://pi.dev/packages)的网页解析。
    /// <para>
    /// 为什么是**网页**而不是接口:目录站点没有给第三方用的 JSON 接口
    /// (<c>/api/packages</c> 明确回 <c>{"ok":false,"error":"API routes are reserved for future features."}</c>,
    /// <c>/packages.json</c> 与 <c>/_next/data/…</c> 都是 404)。页面是服务端渲染的,
    /// 结构化信息全部落在每张卡片的 <c>data-*</c> 属性上 —— 比去刨正文的 <c>&lt;span&gt;</c> 稳得多,
    /// 所以这里**只读 data-* 属性**(见 <see cref="ParseCard"/>)。
    /// </para>
    /// <para>
    /// 本类只做"HTML → 模型"的纯映射:不联网、不缓存、不落盘。抓取时机与缓存策略在
    /// <see cref="PiPackageService"/> 那边。这样解析规则能被单元测试用真页面固定住
    /// (测试数据就在 <c>tests/…/Fixtures</c> 里)。
    /// </para>
    /// </summary>
    public static partial class PiCatalogReader
    {
        /// <summary>目录每页的条数(站点固定 50;解析出来的区间用于判断还有没有下一页)。</summary>
        public const int PageSize = 50;

        /// <summary>目录首页地址(也是"All packages"那一节的地址)。</summary>
        public const string CatalogBaseUrl = "https://pi.dev/packages";

        /// <summary>条目详情页地址前缀。</summary>
        public const string PackagePageBaseUrl = "https://pi.dev/packages/";

        /// <summary>
        /// 一次目录查询:与站点表单的字段一一对应。
        /// <list type="bullet">
        /// <item><c>name</c> —— 按名称/描述/作者过滤(<c>?name=…</c>)。</item>
        /// <item><c>type</c> —— 类型筛选(<c>?type=extension</c>;留空为全部)。</item>
        /// <item><c>sort</c> —— 排序(<c>?sort=downloads|recent|name</c>)。</item>
        /// <item><c>page</c> —— 页码,从 1 开始。</item>
        /// </list>
        /// </summary>
        public readonly record struct CatalogQuery(string Name, PackageCatalogType Type, PackageCatalogSort Sort, int Page)
        {
            /// <summary>拼出请求地址(参数顺序与站点表单一致,便于人工对照)。</summary>
            public string ToUrl()
            {
                var parameters = new List<string>();

                if (this.Name.Length > 0)
                {
                    parameters.Add("name=" + Uri.EscapeDataString(this.Name));
                }

                if (ToTypeParameter(this.Type) is { Length: > 0 } type)
                {
                    parameters.Add("type=" + type);
                }

                parameters.Add("sort=" + ToSortParameter(this.Sort));

                if (this.Page > 1)
                {
                    parameters.Add("page=" + this.Page.ToString(CultureInfo.InvariantCulture));
                }

                return parameters.Count == 0
                    ? CatalogBaseUrl
                    : CatalogBaseUrl + "?" + string.Join("&", parameters);
            }
        }

        /// <summary>类型筛选对应的站点参数值。</summary>
        internal static string ToTypeParameter(PackageCatalogType type) => type switch
        {
            PackageCatalogType.Extension => "extension",
            PackageCatalogType.Skill => "skill",
            PackageCatalogType.Theme => "theme",
            PackageCatalogType.Prompt => "prompt",
            _ => string.Empty,
        };

        /// <summary>排序对应的站点参数值(<c>recent</c> 而不是 <c>recentlyPublished</c>,照站点表单抄)。</summary>
        internal static string ToSortParameter(PackageCatalogSort sort) => sort switch
        {
            PackageCatalogSort.RecentlyPublished => "recent",
            PackageCatalogSort.Name => "name",
            _ => "downloads",
        };

        /// <summary>
        /// 解析目录页 HTML。
        /// 认不出任何卡片时返回**空目录**(而不是抛异常):页面结构变了的表现应该是
        /// "列表空了 + 界面提示可刷新",而不是整个包页面崩掉。
        /// </summary>
        public static PiPackageCatalog Parse(string html, string sourceUrl)
        {
            var entries = new List<PiPackageEntry>();
            if (string.IsNullOrEmpty(html))
            {
                return new PiPackageCatalog { SourceUrl = sourceUrl };
            }

            foreach (Match card in CardRegex().Matches(html))
            {
                var entry = ParseCard(card.Value);
                if (entry is not null)
                {
                    entries.Add(entry);
                }
            }

            var (rangeStart, rangeEnd, total) = ParseRange(html);

            return new PiPackageCatalog
            {
                Entries = entries,
                RangeStart = rangeStart,
                RangeEnd = rangeEnd,
                Total = total,
                TotalPages = ParseTotalPages(html),
                LastPage = ParsePageNumber(sourceUrl),
                SourceUrl = sourceUrl,
                FetchedAtLocal = DateTime.Now,
            };
        }

        /// <summary>解析单张卡片(一个 <c>&lt;article data-package-card="true"&gt;</c>)。</summary>
        private static PiPackageEntry? ParseCard(string card)
        {
            var name = Attribute(card, "data-package-name");
            if (name.Length == 0)
            {
                return null;
            }

            var types = Attribute(card, "data-package-types");
            var downloadsRaw = Attribute(card, "data-package-downloads");

            // 相对时间("5h ago")只在正文里,而正文的其余部分不可靠;取 packages-meta 的三个 <span>
            var meta = MetaBlockRegex().Match(card);
            var author = meta.Success ? CleanText(FirstSpan(meta.Value, 0)) : string.Empty;
            var downloadsLabel = meta.Success ? CleanText(FirstSpan(meta.Value, 1)) : string.Empty;
            var publishedLabel = meta.Success ? CleanText(FirstSpan(meta.Value, 2)) : string.Empty;

            // 描述与作者/时间同在一个 packages-meta 之前,单独取 <p class="packages-desc">
            var description = CleanText(DescriptionRegex().Match(card) is { Success: true } descriptionMatch
                ? descriptionMatch.Groups["text"].Value
                : string.Empty);

            // 版本号只在 report 链接的 package-version 查询参数里(卡片上不单独显示)
            var version = string.Empty;
            var versionMatch = VersionRegex().Match(card);
            if (versionMatch.Success)
            {
                version = WebUtility.UrlDecode(versionMatch.Groups["version"].Value);
            }

            // npm / repo 两个链接(顺序固定:npm 在前,repo 在后;没有 repo 时只有 npm)
            var links = LinksBlockRegex().Match(card);
            var repoUrl = string.Empty;
            if (links.Success)
            {
                var repoMatch = RepoLinkRegex().Match(links.Value);
                if (repoMatch.Success)
                {
                    repoUrl = WebUtility.HtmlDecode(repoMatch.Groups["url"].Value);
                }
            }

            return new PiPackageEntry
            {
                Name = WebUtility.HtmlDecode(name),
                Description = description,
                Author = author,
                Types = types.Trim(),
                Downloads = PackageCountParser.TryParse(downloadsRaw) ?? 0,
                DownloadsText = downloadsLabel.Length > 0 ? downloadsLabel : downloadsRaw,
                Version = version,
                PublishedText = publishedLabel,
                PublishedUnixMs = ParseLong(Attribute(card, "data-package-date")),
                RepoUrl = repoUrl,
                PageUrl = PackagePageBaseUrl + Uri.EscapeDataString(name),
            };
        }

        /// <summary>取 <c>packages-meta</c> 里的第 <paramref name="index"/> 个 <c>&lt;span&gt;</c> 内容。</summary>
        private static string FirstSpan(string metaBlock, int index)
        {
            var spans = SpanRegex().Matches(metaBlock);
            return index < spans.Count ? spans[index].Groups["text"].Value : string.Empty;
        }

        /// <summary>
        /// 解析"1-50 / 5360"里的三个数(区间起点、区间终点、总数)。
        /// 过滤后站点会写成"1-10 / 10 (of 5360)":**括号里的 5360 是全部包数,括号外的 10 是命中数**。
        /// 界面上的"已加载 10 / 10"要用命中数,"加载更多"要不要出现也看命中数排不排得满一页 ——
        /// 所以这里返回的是**命中数**(没有括号时就是斜杠后面那个数本身)。
        /// </summary>
        internal static (int RangeStart, int RangeEnd, int Total) ParseRange(string html)
        {
            var count = CountRegex().Match(html);
            if (!count.Success)
            {
                return (0, 0, 0);
            }

            var start = ParseInt(count.Groups["start"].Value);
            var end = ParseInt(count.Groups["end"].Value);
            var raw = count.Groups["total"].Value;

            var filtered = FilteredCountRegex().Match(raw);
            var total = filtered.Success
                ? ParseInt(filtered.Groups["hits"].Value)
                : ParseInt(raw);

            return (start, end, total);
        }

        /// <summary>
        /// 从分页控件里取**总页数**(<c>href="/packages?page=108"</c> 里最大的那个页码)。
        /// 取不到时返回 0:界面据此不显示"加载更多",但已经拿到的条目照常显示。
        /// <para>
        /// ⚠ 不要用"总条数 / 50"来推:总条数在一个小时内见过 5360 与 5364 两个值
        /// (上游边抓边发),拿它推算会推出一个不存在的页码。
        /// </para>
        /// </summary>
        internal static int ParseTotalPages(string html)
        {
            var last = 0;
            foreach (Match page in PageLinkRegex().Matches(html))
            {
                var value = ParseInt(page.Groups["page"].Value);
                if (value > last)
                {
                    last = value;
                }
            }

            return last;
        }

        /// <summary>从请求地址里取出页码(没有 <c>page</c> 参数就是第 1 页)。</summary>
        internal static int ParsePageNumber(string url)
        {
            var match = PageQueryRegex().Match(url);
            if (!match.Success)
            {
                return 1;
            }

            var value = ParseInt(match.Groups["page"].Value);
            return value > 0 ? value : 1;
        }

        private static string Attribute(string html, string name)
        {
            var match = Regex.Match(html, Regex.Escape(name) + """="(?<value>[^"]*)""" + "\"");
            return match.Success ? WebUtility.HtmlDecode(match.Groups["value"].Value) : string.Empty;
        }

        /// <summary>去掉标签、压缩空白、解 HTML 实体(卡片的描述与作者是纯文本,可能带实体)。</summary>
        internal static string CleanText(string value)
        {
            if (value.Length == 0)
            {
                return string.Empty;
            }

            var text = TagRegex().Replace(value, string.Empty);
            text = WebUtility.HtmlDecode(text);
            return WhitespaceRegex().Replace(text, " ").Trim();
        }

        private static int ParseInt(string value)
            => int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) ? parsed : 0;

        private static long ParseLong(string value)
            => long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) ? parsed : 0;

        /// <summary>一张包卡片。</summary>
        [GeneratedRegex("""<article\b[^>]*data-package-card="true"[^>]*>.*?</article>""", RegexOptions.Singleline)]
        private static partial Regex CardRegex();

        /// <summary>作者 · 下载 · 发布时间 那一行。</summary>
        [GeneratedRegex("""<div class="packages-meta">(?<text>.*?)</div>""", RegexOptions.Singleline)]
        private static partial Regex MetaBlockRegex();

        /// <summary>meta 行里的各个 span。</summary>
        [GeneratedRegex("""<span[^>]*>(?<text>.*?)</span>""", RegexOptions.Singleline)]
        private static partial Regex SpanRegex();

        /// <summary>卡片描述。</summary>
        [GeneratedRegex("""<p class="packages-desc">(?<text>.*?)</p>""", RegexOptions.Singleline)]
        private static partial Regex DescriptionRegex();

        /// <summary>report 链接里的 package-version 查询参数。</summary>
        [GeneratedRegex("""package-version=(?<version>[^"&]*)""")]
        private static partial Regex VersionRegex();

        /// <summary>卡片底部的链接区(内含 npm / repo / report 三个链接)。</summary>
        [GeneratedRegex("""<div class="packages-links"[^>]*>(?<text>.*?)</div>""", RegexOptions.Singleline)]
        private static partial Regex LinksBlockRegex();

        /// <summary>repo 链接(github.com / codeberg.org 等任意仓库站点)。</summary>
        [GeneratedRegex("""<a href="(?<url>https?://(?:www\.)?(?:github|gitlab|codeberg|bitbucket)\.[^"]+)"[^>]*>""")]
        private static partial Regex RepoLinkRegex();

        /// <summary>“1-50 / 5360”或“1-10 / 10 (of 5360)”。</summary>
        [GeneratedRegex("""packages-count">\s*(?<start>\d+)\s*-\s*(?<end>\d+)\s*/\s*(?<total>[^<]*)<""")]
        private static partial Regex CountRegex();

        /// <summary>“10 (of 5360)”里的命中数与总数。</summary>
        [GeneratedRegex("""(?<hits>\d+)\s*\(of\s*(?<all>\d+)\)""")]
        private static partial Regex FilteredCountRegex();
        /// <summary>分页链接里的页码。</summary>
        [GeneratedRegex("""href="/packages\?[^"]*page=(?<page>\d+)""")]
        private static partial Regex PageLinkRegex();

        /// <summary>请求地址里的 page 参数。</summary>
        [GeneratedRegex(@"[?&]page=(?<page>\d+)")]
        private static partial Regex PageQueryRegex();

        /// <summary>任意开始/结束标签(用于把片段洗成纯文本)。</summary>
        [GeneratedRegex("<[^>]+>")]
        private static partial Regex TagRegex();

        /// <summary>连续空白。</summary>
        [GeneratedRegex(@"\s+")]
        private static partial Regex WhitespaceRegex();
    }
}
