using PiWeb_Launcher.Services;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace PiWeb_Launcher.Tests;

/// <summary>
/// pi 包更新检测的纯逻辑:是否需要更新的判定 + <c>npm view</c> 输出的解析。
/// registry/输出来源的版本号形态不受控(告警行、引号、空输出、v 前缀、预发布标记),
/// 判错的表现是"永远提示可更新"或"永远不提示",都不会报错,只能靠测试兜住。
/// </summary>
[TestClass]
public sealed class PackageUpdateTests
{
    [DataTestMethod]
    [DataRow("1.2.3", "1.2.4", true)]
    [DataRow("1.2.4", "1.2.3", false)]   // 本地比 registry 新(本地链接/降级安装)不算可更新
    [DataRow("1.2.3", "1.2.3", false)]
    [DataRow("1.2.3", "1.10.0", true)]   // 按数字比,不是按字符串比
    [DataRow("1.0.0", "1.0.0-beta", false)]  // registry 上的预发布不算更新
    [DataRow("1.0.0-beta", "1.0.0", true)]   // 本地是预发布、registry 有正式版 → 可更新
    [DataRow("v1.2.3", "1.2.4", true)]   // 本地版本带 v 前缀
    [DataRow("1.2.3", "v1.2.4", true)]   // registry 输出带 v 前缀
    public void IsUpdateNeeded_ComparesSemver(string installed, string latest, bool expected)
    {
        Assert.AreEqual(expected, PiPackageService.IsUpdateNeeded(installed, latest), $"{installed} vs {latest}");
    }

    [DataTestMethod]
    [DataRow(null, "1.2.4")]
    [DataRow("1.2.3", null)]
    [DataRow("", "1.2.4")]
    [DataRow("1.2.3", "")]
    [DataRow("   ", "   ")]
    public void IsUpdateNeeded_MissingVersions_ReturnsFalse(string? installed, string? latest)
    {
        // "未知就当作无更新":查不到版本宁可少提示,也不误报
        Assert.IsFalse(PiPackageService.IsUpdateNeeded(installed, latest));
    }

    [TestMethod]
    public void ParseLatestVersionOutput_TakesLastNonEmptyLine()
    {
        var stdout = "npm warn config Some noise\r\n1.2.4\r\n";
        Assert.AreEqual("1.2.4", PiPackageService.ParseLatestVersionOutput(stdout));
    }

    [TestMethod]
    public void ParseLatestVersionOutput_StripsQuotes()
    {
        Assert.AreEqual("1.2.4", PiPackageService.ParseLatestVersionOutput("\"1.2.4\""));
    }

    [DataTestMethod]
    [DataRow("")]
    [DataRow("   \r\n  \n")]
    public void ParseLatestVersionOutput_EmptyOutput_ReturnsNull(string stdout)
    {
        Assert.IsNull(PiPackageService.ParseLatestVersionOutput(stdout));
    }

    /// <summary>
    /// npm 的错误/告警行混进输出(网络抖动、registry 瞬断)时,最后一行可能是任意文本。
    /// 不像版本号的必须当"查询失败"—— 否则一行 "404 …" 会被当成新版本,界面凭空亮「可更新」徽标。
    /// (这是实测踩到的:一次检查把某个瞬态输出判成了新版本,手动复查明明全部是最新。)
    /// </summary>
    [DataTestMethod]
    [DataRow("npm error code E404\r\nnpm error 404 Not Found - GET https://registry.npmjs.org/x\r\n")]
    [DataRow("npm error A complete log of this run can be found in: C:\\Users\\x\\_logs\\2026-09-29T03-36-01-debug-0.log\r\n")]
    [DataRow("npm warn config production Use --omit=dev instead.\r\n2.0.0\r\nnpm notice integrity checksum mismatch\r\n")]
    [DataRow("not a version at all")]
    [DataRow("2026-09-29T03:36:01.000Z")]
    public void ParseLatestVersionOutput_GarbageLines_ReturnsNull(string stdout)
    {
        Assert.IsNull(PiPackageService.ParseLatestVersionOutput(stdout));
    }

    [DataTestMethod]
    [DataRow("1.2.3", "1.2.3")]
    [DataRow("v1.2.3", "v1.2.3")]   // v 前缀保留,由 CompareVersions 统一剥(与 IsUpdateNeeded 的输入约定一致)
    [DataRow("1.2", "1.2")]
    [DataRow("1.2.3-beta.1", "1.2.3-beta.1")]
    [DataRow("1.2.3+build.5", "1.2.3+build.5")]
    [DataRow("  \"1.2.4\"  \r\n", "1.2.4")]
    public void ParseLatestVersionOutput_VersionLikeLines_AreKept(string stdout, string expected)
    {
        Assert.AreEqual(expected, PiPackageService.ParseLatestVersionOutput(stdout));
    }
}
