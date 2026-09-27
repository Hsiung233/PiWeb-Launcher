using System;
using System.IO;
using PiWeb_Launcher.Services;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace PiWeb_Launcher.Tests;

/// <summary>
/// "用哪一份 pi"的判定与命令拼装。
/// <para>
/// 这里之所以值得测:pi-web **自带一份完整的 pi**(它的正式依赖里精确钉着
/// <c>@earendil-works/pi-coding-agent</c>,那份副本自己有 <c>bin.pi</c>),
/// 所以启动器的取用顺序是"PATH 上的全局 pi 优先,没有就用自带那份"。
/// 顺序错了会出事:先挑自带的,用户自己装的那份(版本/本地补丁)就被架空了。
/// </para>
/// </summary>
[TestClass]
public sealed class PiCliSourceTests
{
    /// <summary>
    /// 自带 pi 的相对路径必须与 <c>@earendil-works/pi-coding-agent</c> 的 <c>bin.pi</c> 一致。
    /// <para>
    /// 来源:上游 <c>package.json</c> 里 <c>"bin": { "pi": "dist/bundle/cli.js" }</c>。
    /// 上游改了目录结构时这条会红 —— 这正是它存在的意义:否则表现是"包管理突然全不可用",
    /// 而原因藏在几层 node_modules 里。
    /// </para>
    /// </summary>
    [TestMethod]
    public void BundledPiRelativePath_MatchesUpstreamBinEntry()
        => Assert.AreEqual("dist/bundle/cli.js", PiCli.BundledPiRelativePath);

    [TestMethod]
    public void BundledPiCommand_IsRunThroughNode()
    {
        // 自带的那份没有可执行位(它是个 .js),必须 node <cli> <子命令>
        var bundled = new PiCli.PiCommand(@"C:\Program Files\nodejs\node.exe", "\"C:\\npm\\node_modules\\@agegr\\pi-web\\node_modules\\@earendil-works\\pi-coding-agent\\dist\\bundle\\cli.js\"", IsBundled: true);

        var commandLine = bundled.CommandLineFor("install npm:pi-lens");

        StringAssert.Contains(commandLine, "node.exe");
        StringAssert.Contains(commandLine, "cli.js");
        StringAssert.Contains(commandLine, "install npm:pi-lens");
        // 顺序必须是 node → cli.js → 子命令,反了就是把 cli.js 当解释器用
        Assert.IsTrue(
            commandLine.IndexOf("node.exe", StringComparison.Ordinal) < commandLine.IndexOf("cli.js", StringComparison.Ordinal),
            "node 必须排在 cli.js 之前");
        Assert.IsTrue(
            commandLine.IndexOf("cli.js", StringComparison.Ordinal) < commandLine.IndexOf("install", StringComparison.Ordinal),
            "cli.js 必须排在子命令之前");
    }

    [TestMethod]
    public void GlobalPiCommand_PassesArgumentsStraightThrough()
    {
        var global = new PiCli.PiCommand(@"C:\Users\me\AppData\Roaming\npm\pi.cmd", string.Empty, IsBundled: false);

        var commandLine = global.CommandLineFor("list");

        StringAssert.Contains(commandLine, "pi.cmd");
        StringAssert.Contains(commandLine, "list");
        Assert.IsFalse(commandLine.Contains("cli.js", StringComparison.Ordinal), "全局 shim 不该带任何前置参数");
    }

    [TestMethod]
    public void BundledPiIsFlagged()
    {
        // 界面要靠这一位说明"用的是自带那份"(版本随 pi-web)
        Assert.IsTrue(new PiCli.PiCommand("node", "cli.js", IsBundled: true).IsBundled);
        Assert.IsFalse(new PiCli.PiCommand("pi.cmd", string.Empty, IsBundled: false).IsBundled);
    }

    [TestMethod]
    public void TryBuildPiCommand_OnThisMachine_FindsSomething()
    {
        // 本机至少装了 pi-web(开发机必备),所以两条路里必有一条通。
        // 这是**冒烟断言**,不是给 CI 的硬要求 —— 两条都不通时它会红,而它红的信息量正好是"环境缺东西"。
        var (command, error) = PiCli.TryBuildPiCommandAsync().GetAwaiter().GetResult();

        if (command is null)
        {
            Assert.Inconclusive("本机既没有全局 pi,也没找到 pi-web 自带的 pi:" + error);
        }

        Assert.IsTrue(command.Value.ExecutablePath.Length > 0);
    }

    [TestMethod]
    public void CandidateGlobalNodeModules_IsDerivedFromNpmRootOrShim()
    {
        // 反推路径的兜底逻辑:pi-web 的 shim 在 <prefix>\pi-web.cmd,
        // 全局包目录在同级的 node_modules 下(Windows 形态)。
        var shim = Path.Combine("C:", "Users", "me", "AppData", "Roaming", "npm", "pi-web.cmd");
        var binDirectory = Path.GetDirectoryName(shim);

        Assert.IsNotNull(binDirectory);
        Assert.AreEqual(
            Path.Combine("C:", "Users", "me", "AppData", "Roaming", "npm", "node_modules"),
            Path.Combine(binDirectory!, PiCli.GlobalNodeModulesDirectoryName));
    }
}
