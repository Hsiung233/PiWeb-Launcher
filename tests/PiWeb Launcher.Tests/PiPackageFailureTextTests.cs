using System.Linq;
using PiWeb_Launcher.Services;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace PiWeb_Launcher.Tests;

/// <summary>
/// 包页面的失败原因压缩。
/// <para>
/// 存在的理由很具体:Node 的报错是**完整栈**(一次 <c>pi list</c> 的 EPERM 就有十几行
/// <c>at …chunk-OJP47DM6.js:134:xxxxx</c>),而这句话要塞进界面顶部的一条横幅里。
/// 原样展示会把横幅撑到十几行、把整个包页面挤走 —— 这是实测踩到的,不是假想。
/// </para>
/// </summary>
[TestClass]
public sealed class PiPackageFailureTextTests
{
    /// <summary>照抄 `pi list` 在设置文件被锁时真实吐出来的那段 stderr。</summary>
    private const string RealLockFailure = """
        Warning (package command, global settings): EPERM: operation not permitted, mkdir 'C:\Users\me\.pi\agent\settings.json.lock'
        Error: EPERM: operation not permitted, mkdir 'C:\Users\me\.pi\agent\settings.json.lock'
            at Object.mkdirSync (node:fs:1411:26)
            at newFs.<computed> [as mkdir] (file:///C:/Users/me/AppData/Roaming/npm/node_modules/@earendil-works/pi-coding-agent/dist/bundle/chunks/chunk-OJP47DM6.js:134:22120)
            at acquireLock (file:///C:/Users/me/AppData/Roaming/npm/node_modules/@earendil-works/pi-coding-agent/dist/bundle/chunks/chunk-OJP47DM6.js:134:17126)
            at RetryOperation._fn (file:///C:/Users/me/AppData/Roaming/npm/node_modules/@earendil-works/pi-coding-agent/dist/bundle/chunks/chunk-OJP47DM6.js:134:20167)
            at lock.lockSync (file:///C:/Users/me/AppData/Roaming/npm/node_modules/@earendil-works/pi-coding-agent/dist/bundle/chunks/chunk-OJP47DM6.js:134:23280)
        """;

    [TestMethod]
    public void DescribeFailure_CollapsesAStackToASingleLine()
    {
        var summary = PiPackageService.DescribeFailure(RealLockFailure);

        Assert.IsFalse(summary.Contains('\n'), "横幅是单行的,原因里不能再有换行");
        StringAssert.Contains(summary, "EPERM", "要保留真正的错误信息");
        StringAssert.Contains(summary, "settings.json.lock", "要保留出错的对象");
        Assert.IsFalse(summary.Contains("mkdirSync"), "栈帧不该出现在原因里");
    }

    [TestMethod]
    public void DescribeFailure_IsBounded()
    {
        var summary = PiPackageService.DescribeFailure(new string('x', 2000));

        Assert.IsTrue(summary.Length <= 241, $"过长的原因要截断,实际 {summary.Length}");
    }

    [TestMethod]
    public void DescribeFailure_EmptyInputYieldsEmptyText()
    {
        Assert.AreEqual(string.Empty, PiPackageService.DescribeFailure(string.Empty));
        Assert.AreEqual(string.Empty, PiPackageService.DescribeFailure("   \n  \n"));
    }

    [TestMethod]
    public void DescribeFailure_OutputWithoutErrorLine_FallsBackToTheFirstLine()
    {
        var summary = PiPackageService.DescribeFailure("something odd happened\nand more detail here");

        Assert.AreEqual("something odd happened", summary);
    }
}
