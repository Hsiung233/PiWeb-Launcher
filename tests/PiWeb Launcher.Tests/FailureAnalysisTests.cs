using PiWeb_Launcher.Services;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace PiWeb_Launcher.Tests;

/// <summary>
/// 启动失败的输出分析。
/// 这是"用户看到失败弹窗之后能不能自己解决"的全部依据:识别错了不会崩,
/// 只会把人引向错误的排查方向(例如明明是端口占用却提示重装全局包)。
/// </summary>
[TestClass]
public sealed class FailureAnalysisTests
{
    [TestMethod]
    public void Describe_AddressInUse_TellsUserHowToFindTheProcess()
    {
        var hints = StartFailureDiagnostics.Describe(
            1, "Error: listen EADDRINUSE: address already in use 127.0.0.1:30141", "--no-open");

        StringAssert.Contains(hints, "端口已被占用");
        StringAssert.Contains(hints, "设置页换一个端口");
    }

    [TestMethod]
    public void Describe_ChineseAddressInUseMessage_AlsoMatches()
    {
        var hints = StartFailureDiagnostics.Describe(1, "通常每个套接字地址 30141 只允许使用一次", string.Empty);

        StringAssert.Contains(hints, "端口已被占用");
    }

    [TestMethod]
    public void Describe_MissingBuildArtifacts_SuggestsReinstall()
    {
        var hints = StartFailureDiagnostics.Describe(
            1, "Build artifacts not found. Please report this issue.", string.Empty);

        StringAssert.Contains(hints, "安装可能不完整");
        StringAssert.Contains(hints, "@agegr/pi-web");
    }

    [TestMethod]
    public void Describe_UnsupportedNodeVersion_MentionsRequiredVersion()
    {
        var hints = StartFailureDiagnostics.Describe(
            1, "Unsupported Node.js version. pi-web requires Node 22.19.0 or newer.", string.Empty);

        StringAssert.Contains(hints, "Node.js 版本过旧");
    }

    [TestMethod]
    public void Describe_AccessDenied_MentionsPermissions()
    {
        StringAssert.Contains(
            StartFailureDiagnostics.Describe(1, "Error: EACCES: permission denied", string.Empty),
            "权限不足");
        StringAssert.Contains(
            StartFailureDiagnostics.Describe(1, "拒绝访问。", string.Empty),
            "权限不足");
    }

    [TestMethod]
    public void Describe_UnrecognizedOutput_StillPointsAtTheLogAndKeepsTheArguments()
    {
        var hints = StartFailureDiagnostics.Describe(
            3, "something nobody has seen before", "--port 30141 --no-open");

        StringAssert.Contains(hints, "展开日志");
        // 参数与退出码必须带上:用户拿同样的参数在终端里跑一次就能自己复现
        StringAssert.Contains(hints, "--port 30141 --no-open");
        StringAssert.Contains(hints, "3");
    }

    [TestMethod]
    public void Describe_EmptyArguments_SaysDefaultInsteadOfPrintingNothing()
    {
        var hints = StartFailureDiagnostics.Describe(1, "boom", string.Empty);

        StringAssert.Contains(hints, "(默认参数)");
    }

    [DataTestMethod]
    [DataRow("Error: listen EADDRINUSE 30141", "EADDRINUSE", true)]
    [DataRow("", "EADDRINUSE", false)]
    [DataRow("cannot find module", "Cannot find module", true)]
    [DataRow("CANNOT FIND MODULE", "cannot find module", true)]
    public void ContainsAny_IsCaseInsensitiveAndSafeOnEmpty(string text, string keyword, bool expected)
        => Assert.AreEqual(expected, StartFailureDiagnostics.ContainsAny(text, keyword));

    [TestMethod]
    public void TryGetErrorLine_PicksTheFirstErrorLine()
    {
        var output = "some noise\nError: listen EADDRINUSE\n    at Server.listen\nError: second one";

        Assert.AreEqual("Error: listen EADDRINUSE", StartFailureDiagnostics.TryGetErrorLine(output));
        Assert.IsNull(StartFailureDiagnostics.TryGetErrorLine(string.Empty));
    }

    [TestMethod]
    public void TryGetGlobalPackageDirectory_ReturnsNullForEmptyShim()
    {
        // 未定位到命令时不能凭空编一个路径出来
        Assert.IsNull(StartFailureDiagnostics.TryGetGlobalPackageDirectory(string.Empty, "@agegr/pi-web"));
    }
}
