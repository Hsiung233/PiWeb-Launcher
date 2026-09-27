using System;
using System.IO;
using System.Linq;
using PiWeb_Launcher.Models;
using PiWeb_Launcher.Services.Packages;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace PiWeb_Launcher.Tests;

/// <summary>
/// pi.dev/packages 的网页解析。
/// <para>
/// ⚠ 这里用的是**真实抓下来的页面**(<c>Fixtures/catalog-page1.html</c> 与
/// <c>Fixtures/catalog-filtered.html</c>,2026-09 抓取)而不是手写的小样本:
/// 站点是唯一的数据源(没有给第三方用的 JSON 接口),所以"页面结构一变,
/// 解析器就静默返回空列表"是本功能最大的风险点 —— 拿真页面测,改版当天就能发现。
/// </para>
/// <para>
/// 断言刻意**不写死总条数**(上游边抓边发,一小时内见过 5360 与 5364 两个值),
/// 只断言结构与不变量。
/// </para>
/// </summary>
[TestClass]
public sealed class PiCatalogReaderTests
{
    private static string FixturePath(string name)
        => Path.Combine(AppContext.BaseDirectory, "Fixtures", name);

    private static string LoadFixture(string name) => File.ReadAllText(FixturePath(name));

    [TestMethod]
    public void Parse_RealCatalogPage_ReadsEveryCard()
    {
        var catalog = PiCatalogReader.Parse(LoadFixture("catalog-page1.html"), PiCatalogReader.CatalogBaseUrl);

        Assert.AreEqual(PiCatalogReader.PageSize, catalog.Entries.Count, "首页应有 50 条");
        Assert.AreEqual(1, catalog.RangeStart);
        Assert.AreEqual(PiCatalogReader.PageSize, catalog.RangeEnd);
        CollectionAssert.AllItemsAreUnique(catalog.Entries.Select(item => item.Name).ToList());
        Assert.IsTrue(catalog.Entries.All(item => item.Name.Length > 0), "每条都必须有包名");
    }

    [TestMethod]
    public void Parse_RealCatalogPage_ReadsTheStructuredCardFields()
    {
        var catalog = PiCatalogReader.Parse(LoadFixture("catalog-page1.html"), PiCatalogReader.CatalogBaseUrl);
        var entry = catalog.Entries.Single(item => item.Name == "pi-mcp-adapter");

        Assert.AreEqual("nicopreme", entry.Author);
        Assert.AreEqual("extension", entry.Types);
        Assert.AreEqual("2.38.0", entry.Version);
        Assert.IsTrue(entry.Downloads > 1_000_000, $"下载量应解析成整数,实际 {entry.Downloads}");
        Assert.AreEqual("1.1M/mo", entry.DownloadsText);
        Assert.AreEqual("5h ago", entry.PublishedText);
        Assert.AreEqual("https://github.com/nicobailon/pi-mcp-adapter", entry.RepoUrl);
        Assert.AreEqual("https://pi.dev/packages/pi-mcp-adapter", entry.PageUrl);
        StringAssert.Contains(entry.Description, "Model Context Protocol");
    }

    [TestMethod]
    public void Parse_ScopedPackageName_IsKeptVerbatim()
    {
        var catalog = PiCatalogReader.Parse(LoadFixture("catalog-page1.html"), PiCatalogReader.CatalogBaseUrl);

        // scope 里的 @ 与 / 是最容易在解析里被吃掉的东西
        Assert.IsTrue(
            catalog.Entries.Any(item => item.Name == "@juicesharp/rpiv-ask-user-question"),
            "scoped 包名必须原样保留");
    }

    [TestMethod]
    public void Parse_InstalledOrNot_IsNotTheCatalogsBusiness()
    {
        var catalog = PiCatalogReader.Parse(LoadFixture("catalog-page1.html"), PiCatalogReader.CatalogBaseUrl);

        // 目录页不知道自己装了哪些包 —— 这一位必须由服务层填(见 PiPackageService.Decorate)
        Assert.IsTrue(catalog.Entries.All(item => !item.IsInstalled));
    }

    [TestMethod]
    public void Parse_TotalPagesComesFromThePaginator_NotFromTheCount()
    {
        var html = LoadFixture("catalog-page1.html");
        var catalog = PiCatalogReader.Parse(html, PiCatalogReader.CatalogBaseUrl);

        // 分页控件里最大的页码(108)才是"还有没有下一页"的判据;总条数会随请求变化
        Assert.AreEqual(PiCatalogReader.ParseTotalPages(html), catalog.TotalPages);
        Assert.IsTrue(catalog.TotalPages > 100, $"目录有 100+ 页,实际解析到 {catalog.TotalPages}");
        Assert.IsTrue(catalog.HasMore, "第一页后面当然还有");
        Assert.AreEqual(1, catalog.LastPage);
    }

    [TestMethod]
    public void Parse_FilteredPage_ReportsHitCountNotTheWholeCatalog()
    {
        var html = LoadFixture("catalog-filtered.html");
        var catalog = PiCatalogReader.Parse(html, "https://pi.dev/packages?name=pi-mcp-adapter&type=extension&sort=name");

        // 站点把它写成 "1-10 / 10 (of 5360)":括号里的 5360 是全部包数,
        // 而界面上的"已加载 N / M"要用**命中数**,否则会说"已加载 10 / 5360"
        Assert.AreEqual(10, catalog.Total);
        Assert.AreEqual(10, catalog.Entries.Count);
        Assert.IsTrue(catalog.Entries.Any(item => item.Name == "pi-mcp-adapter"));
        StringAssert.Contains(catalog.CountText, "10 / 10");
        Assert.IsFalse(catalog.HasMore, "过滤结果只有一页");
    }

    [TestMethod]
    public void Query_MatchesTheSitesOwnFormParameters()
    {
        // 参数名取自站点表单(name / type / sort / page)—— 改这里等于改请求
        Assert.AreEqual(
            "https://pi.dev/packages?sort=downloads",
            new PiCatalogReader.CatalogQuery(string.Empty, PackageCatalogType.All, PackageCatalogSort.Downloads, 1).ToUrl());

        Assert.AreEqual(
            "https://pi.dev/packages?name=pi%20mcp&type=extension&sort=name&page=3",
            new PiCatalogReader.CatalogQuery("pi mcp", PackageCatalogType.Extension, PackageCatalogSort.Name, 3).ToUrl());

        Assert.AreEqual("recent", PiCatalogReader.ToSortParameter(PackageCatalogSort.RecentlyPublished));
        Assert.AreEqual(string.Empty, PiCatalogReader.ToTypeParameter(PackageCatalogType.All));
        Assert.AreEqual("prompt", PiCatalogReader.ToTypeParameter(PackageCatalogType.Prompt));
    }

    [TestMethod]
    public void ParsePageNumber_DefaultsToFirstPage()
    {
        Assert.AreEqual(1, PiCatalogReader.ParsePageNumber("https://pi.dev/packages"));
        Assert.AreEqual(1, PiCatalogReader.ParsePageNumber("https://pi.dev/packages?sort=downloads"));
        Assert.AreEqual(29, PiCatalogReader.ParsePageNumber("https://pi.dev/packages?page=29"));
        Assert.AreEqual(29, PiCatalogReader.ParsePageNumber("https://pi.dev/packages?sort=name&page=29"));
    }

    [TestMethod]
    public void CleanText_StripsTagsAndDecodesEntities()
    {
        Assert.AreEqual("a & b", PiCatalogReader.CleanText("<b>a</b> &amp; <i>b</i>"));
        Assert.AreEqual("hello world", PiCatalogReader.CleanText("  hello\n\t world  "));
    }

    [DataTestMethod]
    [DataRow("1.1M", 1100000)]
    [DataRow("484.5K", 484500)]
    [DataRow("1,298", 1298)]
    [DataRow("835", 835)]
    [DataRow("377/mo", 377)]
    [DataRow("", null)]
    [DataRow("n/a", null)]
    public void PackageCountParser_ReadsTheCardFormats(string text, int? expected)
        => Assert.AreEqual(expected, PackageCountParser.TryParse(text));

    [TestMethod]
    public void Merge_AppendsPagesAndDropsDuplicates()
    {
        var first = PiCatalogReader.Parse(LoadFixture("catalog-filtered.html"), "https://pi.dev/packages?page=1");
        var second = PiCatalogReader.Parse(LoadFixture("catalog-filtered.html"), "https://pi.dev/packages?page=2");

        var merged = first.Merge(second);

        Assert.AreEqual(first.Entries.Count, merged.Entries.Count, "同一份内容合并后不该翻倍");
        Assert.AreEqual(2, merged.LastPage, "页码要取两者中更靠后的那个");
    }

    [TestMethod]
    public void Parse_GarbageInput_ReturnsEmptyCatalogInsteadOfThrowing()
    {
        // 站点改版/被代理塞了一张登录页:表现应该是"列表空了 + 界面提示可刷新",而不是整个页面崩掉
        var catalog = PiCatalogReader.Parse("<html><body>not a catalog</body></html>", "https://pi.dev/packages");

        Assert.AreEqual(0, catalog.Entries.Count);
        Assert.AreEqual(0, catalog.Total);
        Assert.IsFalse(catalog.HasMore);
        Assert.AreEqual(string.Empty, catalog.CountText);
        Assert.AreEqual("https://pi.dev/packages", catalog.SourceUrl);
    }

    [TestMethod]
    public void Parse_EmptyInput_DoesNotThrow()
    {
        var catalog = PiCatalogReader.Parse(string.Empty, PiCatalogReader.CatalogBaseUrl);

        Assert.AreEqual(0, catalog.Entries.Count);
    }
}
