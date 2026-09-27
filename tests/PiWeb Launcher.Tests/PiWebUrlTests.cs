using PiWeb_Launcher.Services;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace PiWeb_Launcher.Tests;

/// <summary>
/// Pi Web 启动参数与地址的两块纯逻辑:
/// <list type="bullet">
/// <item><b>监听地址归一化</b> —— 认不出的写法要被当成"未设置"(而不是原样拼进命令行),</item>
/// <item><b>地址推导</b> —— <c>0.0.0.0</c> / <c>::</c> 是"监听所有网卡",**不是**能拿来访问的地址。</item>
/// </list>
/// 这两条都会直接变成"界面上的打开按钮打不开页面",所以逐字固定下来。
/// </summary>
[TestClass]
public sealed class PiWebUrlTests
{
    [DataTestMethod]
    [DataRow("", "")]
    [DataRow("   ", "")]
    [DataRow("127.0.0.1", "127.0.0.1")]
    [DataRow("  localhost  ", "localhost")]
    [DataRow("0.0.0.0", "0.0.0.0")]
    [DataRow("192.168.1.5", "192.168.1.5")]
    [DataRow("my-host.local", "my-host.local")]
    [DataRow("::1", "::1")]
    [DataRow("[::1]", "[::1]")]
    public void NormalizeHostname_KeepsDocumentedForms(string input, string expected)
        => Assert.AreEqual(expected, PiWebService.NormalizeHostname(input));

    [DataTestMethod]
    [DataRow("127.0.0.1:30141", "")]      // 端口是单独一项设置,塞进地址里只会拼出坏命令行
    [DataRow("host name", "")]
    [DataRow("host\"name", "")]
    [DataRow("host;rm -rf", "")]
    [DataRow("host/name", "")]
    public void NormalizeHostname_RejectsAnythingItCannotVouchFor(string input, string expected)
        => Assert.AreEqual(expected, PiWebService.NormalizeHostname(input));

    [TestMethod]
    public void NormalizeHostname_Null_IsTreatedAsUnset()
        => Assert.AreEqual(string.Empty, PiWebService.NormalizeHostname(null));

    [TestMethod]
    public void BuildWebUrl_UsesPiWebDefaultsWhenNothingIsConfigured()
    {
        Assert.AreEqual("http://127.0.0.1:30141", PiWebService.BuildWebUrl(string.Empty, 0));
        Assert.AreEqual("http://127.0.0.1:30141", PiWebService.BuildWebUrl(null, 0));
    }

    [TestMethod]
    public void BuildWebUrl_WildcardBindAddress_BecomesLoopback()
    {
        // 0.0.0.0 与 :: 是"监听所有网卡",不是可访问地址 —— 打开它们只会得到"无法访问此站点"
        Assert.AreEqual("http://127.0.0.1:30141", PiWebService.BuildWebUrl("0.0.0.0", 0));
        Assert.AreEqual("http://127.0.0.1:30141", PiWebService.BuildWebUrl("::", 0));
        Assert.AreEqual("http://127.0.0.1:30141", PiWebService.BuildWebUrl("*", 0));
    }

    [TestMethod]
    public void BuildWebUrl_KeepsConcreteAddresses()
    {
        Assert.AreEqual("http://192.168.1.5:8080", PiWebService.BuildWebUrl("192.168.1.5", 8080));
        Assert.AreEqual("http://my-host.local:30141", PiWebService.BuildWebUrl("my-host.local", 0));
    }

    [TestMethod]
    public void BuildWebUrl_BracketsIpv6Literals()
    {
        Assert.AreEqual("http://[::1]:30141", PiWebService.BuildWebUrl("::1", 0));
        Assert.AreEqual("http://[::1]:30141", PiWebService.BuildWebUrl("[::1]", 0));
    }

    [DataTestMethod]
    [DataRow(0)]
    [DataRow(-1)]
    [DataRow(70000)]
    public void BuildWebUrl_OutOfRangePort_FallsBackToTheDefault(int port)
        => Assert.AreEqual("http://127.0.0.1:30141", PiWebService.BuildWebUrl("127.0.0.1", port));

    [TestMethod]
    public void BuildWebUrl_UnparseableHostname_IsTreatedAsUnset()
        => Assert.AreEqual("http://127.0.0.1:30141", PiWebService.BuildWebUrl("bad host", 0));

    // ---- 版本比较(用于"有新版本"按钮) ----

    [DataTestMethod]
    [DataRow("1.2.3", "1.2.3", 0)]
    [DataRow("1.2.4", "1.2.3", 1)]
    [DataRow("1.2.3", "1.2.4", -1)]
    [DataRow("v1.2.3", "1.2.3", 0)]
    [DataRow("1.2", "1.2.0", 0)]
    [DataRow("1.10.0", "1.9.0", 1)]
    [DataRow("0.9.3", "0.9.10", -1)]
    [DataRow("1.0.0", "1.0.0-rc.1", 1)]
    [DataRow("1.0.0-rc.1", "1.0.0", -1)]
    [DataRow("0.7.15", "0.9.3", -1)]
    public void CompareVersions_OrdersSemanticVersions(string left, string right, int expectedSign)
    {
        var actual = PiWebService.CompareVersions(left, right);

        Assert.AreEqual(expectedSign, actual == 0 ? 0 : (actual > 0 ? 1 : -1), $"{left} vs {right}");
    }

    [TestMethod]
    public void CompareVersions_IgnoresBuildMetadata()
        => Assert.AreEqual(0, PiWebService.CompareVersions("1.2.3+build.5", "1.2.3"));
}
