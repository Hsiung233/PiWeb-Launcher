using System.Collections.Generic;
using System.ComponentModel;
using PiWeb_Launcher.Models;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace PiWeb_Launcher.Tests;

/// <summary>
/// <see cref="PiInstalledPackage"/> 的界面状态与展示文案。
/// 前者存在的唯一理由是**列表是虚拟化的**:容器会被回收复用,
/// 所以"待确认卸载"这类状态必须挂在数据对象上(踩过的坑见属性注释)。
/// </summary>
[TestClass]
public sealed class PiInstalledPackageTests
{
    [TestMethod]
    public void UninstallText_FollowsPendingState()
    {
        var entry = new PiInstalledPackage { Name = "pkg" };

        Assert.AreEqual("卸载", entry.UninstallText);
        entry.IsPendingUninstall = true;
        Assert.AreEqual("确认卸载", entry.UninstallText);
        entry.IsPendingUninstall = false;
        Assert.AreEqual("卸载", entry.UninstallText);
    }

    [TestMethod]
    public void IsPendingUninstall_RaisesChangeForBothStateAndText()
    {
        var entry = new PiInstalledPackage { Name = "pkg" };
        var changed = new List<string?>();
        entry.PropertyChanged += (_, e) => changed.Add(e.PropertyName);

        entry.IsPendingUninstall = true;

        CollectionAssert.Contains(changed, nameof(PiInstalledPackage.IsPendingUninstall));
        CollectionAssert.Contains(
            changed,
            nameof(PiInstalledPackage.UninstallText)); // 只通知前者会让按钮文字不变
    }

    [TestMethod]
    public void IsPendingUninstall_SettingSameValue_DoesNotNotify()
    {
        var entry = new PiInstalledPackage { Name = "pkg" };
        var raised = 0;
        entry.PropertyChanged += (_, _) => raised++;

        entry.IsPendingUninstall = false; // 本来就是 false

        Assert.AreEqual(0, raised);
    }

    [TestMethod]
    public void MetaText_ShowsScopeKindAndVersion_AndSkipsWhatIsUnknown()
    {
        var bare = new PiInstalledPackage { Name = "plain", Source = "npm:plain" };
        Assert.AreEqual("用户  ·  npm", bare.MetaText);   // 版本读不到就一个字都不显示,不编造

        var withVersion = new PiInstalledPackage
        {
            Name = "@scope/tool",
            Source = "npm:@scope/tool",
            Kind = PackageSourceKind.Npm,
            Version = "1.2.3",
            IsProjectScope = true,
        };
        Assert.AreEqual("项目  ·  npm  ·  v1.2.3", withVersion.MetaText);

        var filtered = new PiInstalledPackage
        {
            Name = "filtered",
            Source = "npm:filtered",
            Kind = PackageSourceKind.Npm,
            IsFiltered = true,
        };
        StringAssert.Contains(filtered.MetaText, "有资源过滤");
    }

    [TestMethod]
    public void StateText_OnlyMentionsRestart_WhenPackageAwaitsOne()
    {
        var entry = new PiInstalledPackage { Name = "pkg" };
        Assert.AreEqual(string.Empty, entry.StateText);

        var awaiting = new PiInstalledPackage { Name = "pkg", AwaitingRestart = true };
        Assert.AreEqual("重启后生效", awaiting.StateText);
    }

    [TestMethod]
    public void InstallCommandText_RestoresAPasteableCommand()
    {
        var git = new PiInstalledPackage
        {
            Name = "tools",
            Source = "git:github.com/example/tools@v1",
            Kind = PackageSourceKind.Git,
        };

        Assert.AreEqual("pi install git:github.com/example/tools@v1", git.InstallCommandText);
        Assert.AreEqual("本地", new PiInstalledPackage { Name = "x", Kind = PackageSourceKind.Local }.KindText);
    }
}
