using System.Text.Json;
using PiWeb_Launcher.Models;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace PiWeb_Launcher.Tests;

/// <summary>
/// 设置的 JSON 读写与**宽容枚举解析**。
/// 动机写在 <see cref="LenientEnumConverter{T}"/> 的注释里:属性上直接用 JsonStringEnumConverter 时,
/// 遇到已删除的枚举名会抛异常,而加载是整体回退 —— 结果是**所有设置静默重置为默认**(已实测)。
/// </summary>
[TestClass]
public sealed class SettingsJsonTests
{
    [TestMethod]
    public void Deserialize_KnownEnumNames_AreMapped()
    {
        var settings = JsonSerializer.Deserialize<AppSettings>(
            """{ "TraySingleClick": "Browser", "PackageCatalogSort": "RecentlyPublished", "PackageCatalogType": "Skill", "NpmRegistry": "Npmmirror" }""");

        Assert.IsNotNull(settings);
        Assert.AreEqual(WebOpenAction.Browser, settings.TraySingleClick);
        Assert.AreEqual(PackageCatalogSort.RecentlyPublished, settings.PackageCatalogSort);
        Assert.AreEqual(PackageCatalogType.Skill, settings.PackageCatalogType);
        Assert.AreEqual(NpmRegistrySource.Npmmirror, settings.NpmRegistry);
    }

    [TestMethod]
    public void Deserialize_UnknownEnumName_FallsBackToDefaultWithoutLosingOtherSettings()
    {
        var settings = JsonSerializer.Deserialize<AppSettings>(
            """{ "TraySingleClick": "SomeRemovedMember", "ListenPort": 30141, "ProxyUrl": "http://127.0.0.1:7890" }""");

        Assert.IsNotNull(settings);
        Assert.AreEqual(WebOpenAction.None, settings.TraySingleClick);   // 回退到 default,而不是抛异常
        Assert.AreEqual(30141, settings.ListenPort);                     // 其余设置必须原样保留
        Assert.AreEqual("http://127.0.0.1:7890", settings.ProxyUrl);
    }

    [TestMethod]
    public void Deserialize_NumericEnum_IsAcceptedWhenDefined()
    {
        var settings = JsonSerializer.Deserialize<AppSettings>("""{ "TraySingleClick": 2 }""");

        Assert.IsNotNull(settings);
        Assert.AreEqual(WebOpenAction.Browser, settings.TraySingleClick);
    }

    [TestMethod]
    public void Deserialize_NumericEnum_OutOfRangeFallsBackToDefault()
    {
        var settings = JsonSerializer.Deserialize<AppSettings>("""{ "TraySingleClick": 99 }""");

        Assert.IsNotNull(settings);
        Assert.AreEqual(WebOpenAction.None, settings.TraySingleClick);
    }

    [TestMethod]
    public void Deserialize_MissingFields_KeepDocumentedDefaults()
    {
        var settings = JsonSerializer.Deserialize<AppSettings>("{}");

        Assert.IsNotNull(settings);
        Assert.AreEqual(WebOpenAction.WebView, settings.TraySingleClick);
        Assert.AreEqual(WebOpenAction.Browser, settings.TrayDoubleClick);
        Assert.AreEqual(WebOpenAction.None, settings.AfterServiceStarted);
        Assert.AreEqual(WebOpenAction.MainWindow, settings.RepeatLaunchAction);   // 与加入本设置之前的行为一致
        Assert.IsTrue(settings.ShowMainWindowOnStartup);
        Assert.IsFalse(settings.RunServiceOnStartup);
        Assert.IsFalse(settings.AutoStartOnLogon);   // 开机自启动默认关闭
        Assert.AreEqual(0, settings.ListenPort);
        Assert.AreEqual(string.Empty, settings.ListenHostname);   // 空 = pi-web 默认的 127.0.0.1
        Assert.AreEqual(string.Empty, settings.WebPassword);      // 空 = 不启用认证
        Assert.IsFalse(settings.LetServiceOpenBrowser);           // 默认给 pi-web 传 --no-open
        Assert.IsTrue(settings.KeepWebViewAlive);
        Assert.AreEqual(PackageCatalogSort.Downloads, settings.PackageCatalogSort);
        Assert.AreEqual(PackageCatalogType.All, settings.PackageCatalogType);
        Assert.AreEqual(NpmRegistrySource.Config, settings.NpmRegistry);
    }

    [TestMethod]
    public void Serialize_WritesEnumNamesNotNumbers()
    {
        var json = JsonSerializer.Serialize(new AppSettings { TraySingleClick = WebOpenAction.MainWindow });

        StringAssert.Contains(json, "\"TraySingleClick\":\"MainWindow\"");
    }

    [TestMethod]
    public void RoundTrip_PreservesEveryField()
    {
        var original = new AppSettings
        {
            TraySingleClick = WebOpenAction.MainWindow,
            TrayDoubleClick = WebOpenAction.None,
            RunServiceOnStartup = true,
            AutoStartOnLogon = true,
            AfterServiceStarted = WebOpenAction.Browser,
            RepeatLaunchAction = WebOpenAction.None,
            ShowMainWindowOnStartup = false,
            ListenPort = 3100,
            ListenHostname = "0.0.0.0",
            WebPassword = "s3cret-with-符号",
            LetServiceOpenBrowser = true,
            WebViewLink = WebViewLinkTarget.AppWebView,
            KeepWebViewAlive = false,
            WebViewIdleTimeoutMinutes = 30,
            PackageCatalogSort = PackageCatalogSort.Name,
            PackageCatalogType = PackageCatalogType.Theme,
            NpmRegistry = NpmRegistrySource.TencentCloud,
            ProxyUrl = "http://127.0.0.1:7890",
            NoProxy = "localhost,.corp.com",
        };

        var restored = JsonSerializer.Deserialize<AppSettings>(JsonSerializer.Serialize(original));

        Assert.IsNotNull(restored);
        Assert.AreEqual(original.TraySingleClick, restored.TraySingleClick);
        Assert.AreEqual(original.TrayDoubleClick, restored.TrayDoubleClick);
        Assert.AreEqual(original.RunServiceOnStartup, restored.RunServiceOnStartup);
        Assert.AreEqual(original.AutoStartOnLogon, restored.AutoStartOnLogon);
        Assert.AreEqual(original.AfterServiceStarted, restored.AfterServiceStarted);
        Assert.AreEqual(original.RepeatLaunchAction, restored.RepeatLaunchAction);
        Assert.AreEqual(original.ShowMainWindowOnStartup, restored.ShowMainWindowOnStartup);
        Assert.AreEqual(original.ListenPort, restored.ListenPort);
        Assert.AreEqual(original.ListenHostname, restored.ListenHostname);
        Assert.AreEqual(original.WebPassword, restored.WebPassword);
        Assert.AreEqual(original.LetServiceOpenBrowser, restored.LetServiceOpenBrowser);
        Assert.AreEqual(original.WebViewLink, restored.WebViewLink);
        Assert.AreEqual(original.KeepWebViewAlive, restored.KeepWebViewAlive);
        Assert.AreEqual(original.WebViewIdleTimeoutMinutes, restored.WebViewIdleTimeoutMinutes);
        Assert.AreEqual(original.PackageCatalogSort, restored.PackageCatalogSort);
        Assert.AreEqual(original.PackageCatalogType, restored.PackageCatalogType);
        Assert.AreEqual(original.NpmRegistry, restored.NpmRegistry);
        Assert.AreEqual(original.ProxyUrl, restored.ProxyUrl);
        Assert.AreEqual(original.NoProxy, restored.NoProxy);
    }
}
