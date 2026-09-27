using System.Linq;
using PiWeb_Launcher.Models;
using PiWeb_Launcher.Services.Packages;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace PiWeb_Launcher.Tests;

/// <summary>
/// <c>pi list</c> 输出的解析。
/// <para>
/// 样本照抄**上游实现的实际输出形态**(见 pi-coding-agent 里的 <c>case "list"</c> 分支):
/// 分节标题 + 两空格缩进的源规格 + 四空格缩进的已安装路径,过滤过的条目带 <c>(filtered)</c>。
/// 这是升级 pi 之后最容易静默失效的地方 —— 解析不出来不会报错,只会少显示几个包。
/// </para>
/// </summary>
[TestClass]
public sealed class PiListReaderTests
{
    /// <summary>一条都没装时上游只打印这一行。</summary>
    [TestMethod]
    public void Parse_EmptyMarker_YieldsNothing()
    {
        var entries = PiListReader.Parse("No packages installed.\n");

        Assert.AreEqual(0, entries.Count);
    }

    [TestMethod]
    public void Parse_UserAndProjectSections_AreSeparated()
    {
        const string Output = """
            User packages:
              npm:pi-mcp-adapter
                C:\Users\me\.pi\agent\npm\node_modules\pi-mcp-adapter
              npm:@juicesharp/rpiv-todo (filtered)
            Project packages:
              git:github.com/example/tools@v1
            """;

        var entries = PiListReader.Parse(Output);

        Assert.AreEqual(3, entries.Count);

        var adapter = entries.Single(item => item.Name == "pi-mcp-adapter");
        Assert.AreEqual("npm:pi-mcp-adapter", adapter.Source);
        Assert.AreEqual(PackageSourceKind.Npm, adapter.Kind);
        Assert.IsFalse(adapter.IsProjectScope);
        Assert.AreEqual(@"C:\Users\me\.pi\agent\npm\node_modules\pi-mcp-adapter", adapter.InstalledPath);
        Assert.IsFalse(adapter.IsFiltered);

        var todo = entries.Single(item => item.Name == "@juicesharp/rpiv-todo");
        Assert.AreEqual("npm:@juicesharp/rpiv-todo", todo.Source, "过滤标记不属于源规格本身");
        Assert.IsTrue(todo.IsFiltered);
        Assert.AreEqual(string.Empty, todo.InstalledPath, "没有路径行时不该把上一条的路径接过来");

        var project = entries.Single(item => item.Name == "tools");
        Assert.IsTrue(project.IsProjectScope);
        Assert.AreEqual(PackageSourceKind.Git, project.Kind);
    }

    [TestMethod]
    public void Parse_IgnoresNoiseOutsideTheSections()
    {
        // 版本检查、更新提示、颜色控制字符都可能出现在同一份输出里
        const string Output = "\u001b[2mChecking for updates…\u001b[0m\nUpdate available: 0.9.4\n"
            + "User packages:\n  npm:pi-lens\n";

        var entries = PiListReader.Parse(Output);

        Assert.AreEqual(1, entries.Count);
        Assert.AreEqual("pi-lens", entries[0].Name);
    }

    [TestMethod]
    public void Parse_EntriesBeforeAnySectionHeader_AreStillShown()
    {
        // 上游换了分节标题文案时,宁可按"用户级"收下也不要整页空掉
        var entries = PiListReader.Parse("  npm:pi-lens\n");

        Assert.AreEqual(1, entries.Count);
        Assert.IsFalse(entries[0].IsProjectScope);
    }

    [DataTestMethod]
    [DataRow("npm:pi-lens", PackageSourceKind.Npm)]
    [DataRow("npm:@scope/tool@1.2.3", PackageSourceKind.Npm)]
    [DataRow("git:github.com/a/b@v1", PackageSourceKind.Git)]
    [DataRow("https://github.com/a/b", PackageSourceKind.Git)]
    [DataRow("git@github.com:a/b.git", PackageSourceKind.Git)]
    [DataRow("./local-package", PackageSourceKind.Local)]
    [DataRow(@"C:\work\pkg", PackageSourceKind.Local)]
    public void ClassifySource_RecognizesEveryDocumentedPrefix(string source, PackageSourceKind expected)
        => Assert.AreEqual(expected, PiListReader.ClassifySource(source));

    [DataTestMethod]
    [DataRow("npm:pi-lens", "pi-lens")]
    [DataRow("npm:pi-lens@1.2.3", "pi-lens")]
    [DataRow("npm:@scope/tool@^1.0.0", "@scope/tool")]
    [DataRow("git:github.com/a/b@v1", "b")]
    [DataRow("git:github.com/a/b", "b")]
    [DataRow("https://github.com/a/b.git", "b")]
    [DataRow("./local-package", "local-package")]
    [DataRow(@"C:\work\my-pkg", "my-pkg")]
    public void ExtractName_StripsPrefixVersionAndRef(string source, string expected)
        => Assert.AreEqual(expected, PiListReader.ExtractName(source, PiListReader.ClassifySource(source)));

    [TestMethod]
    public void ExtractName_SshForm_KeepsTheUserNameOutOfTheRefStrip()
    {
        // git@github.com:a/b 里的 @ 是用户名分隔符,不是 ref —— 从末尾往前找会把它当成 ref
        Assert.AreEqual("b", PiListReader.ExtractName("git:git@github.com:a/b", PackageSourceKind.Git));
    }

    [TestMethod]
    public void StripControl_RemovesAnsiSequences()
    {
        Assert.AreEqual("User packages:", PiListReader.StripControl("\u001b[1mUser packages:\u001b[0m"));
        Assert.AreEqual("plain", PiListReader.StripControl("plain"));
    }

    [TestMethod]
    public void Parse_EmptyOrNullOutput_YieldsNothing()
    {
        Assert.AreEqual(0, PiListReader.Parse(string.Empty).Count);
        Assert.AreEqual(0, PiListReader.Parse("\n\n").Count);
    }
}
