using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace PiWeb_Launcher.TestRunner;

/// <summary>
/// 极简 MSTest 运行器:反射找出 <c>[TestClass]</c> 里的 <c>[TestMethod]</c> / <c>[DataTestMethod]</c> 并执行。
/// <para>
/// 为什么不直接用 <c>dotnet test</c>:VSTest 的 testhost 会 OpenProcess 监视父进程,
/// 在受限环境里被拒绝访问而整体中止(见 csproj 注释)。CI 上仍走标准的 <c>dotnet test</c>。
/// </para>
/// <para>
/// 只支持本项目实际用到的特性集:<c>[DataRow]</c>、<c>[TestInitialize]</c> / <c>[TestCleanup]</c>、
/// <c>[ExpectedException]</c> 不支持(本项目没用)。断言仍用 MSTest 的 Assert —— 失败信息与 CI 上一致。
/// </para>
/// </summary>
internal static class Program
{
    private static int Main(string[] args)
    {
        var filter = args.FirstOrDefault(argument => !argument.StartsWith('-'));
        var assembly = typeof(PiWeb_Launcher.Tests.SettingsJsonTests).Assembly;

        var passed = 0;
        var failed = new List<(string Name, string Message)>();

        foreach (var type in assembly.GetTypes().Where(t => t.GetCustomAttribute<TestClassAttribute>() is not null).OrderBy(t => t.FullName))
        {
            var testInitialize = type.GetMethods().FirstOrDefault(m => m.GetCustomAttribute<TestInitializeAttribute>() is not null);
            var testCleanup = type.GetMethods().FirstOrDefault(m => m.GetCustomAttribute<TestCleanupAttribute>() is not null);

            foreach (var method in type.GetMethods().Where(m => m.GetCustomAttribute<TestMethodAttribute>() is not null).OrderBy(m => m.Name))
            {
                var cases = method.GetCustomAttributes<DataRowAttribute>().ToList();
                if (cases.Count == 0)
                {
                    Run(type, method, null, filter, ref passed, failed, testInitialize, testCleanup);
                    continue;
                }

                foreach (var dataRow in cases)
                {
                    Run(type, method, dataRow.Data, filter, ref passed, failed, testInitialize, testCleanup);
                }
            }
        }

        Console.WriteLine();
        Console.WriteLine($"通过 {passed}，失败 {failed.Count}");
        foreach (var (name, message) in failed)
        {
            Console.WriteLine();
            Console.WriteLine("失败: " + name);
            Console.WriteLine(message);
        }

        return failed.Count == 0 ? 0 : 1;
    }

    private static void Run(
        Type type,
        MethodInfo method,
        object?[]? arguments,
        string? filter,
        ref int passed,
        List<(string, string)> failed,
        MethodInfo? testInitialize,
        MethodInfo? testCleanup)
    {
        var name = type.FullName + "." + method.Name
            + (arguments is null ? string.Empty : "(" + string.Join(", ", arguments.Select(a => a?.ToString() ?? "null")) + ")");

        if (filter is not null && !name.Contains(filter, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        object? instance = null;
        try
        {
            instance = Activator.CreateInstance(type);
            testInitialize?.Invoke(instance, null);
            method.Invoke(instance, arguments);
            passed++;
            Console.WriteLine("  通过 " + name);
        }
        catch (Exception ex)
        {
            var actual = ex is TargetInvocationException { InnerException: { } inner } ? inner : ex;
            failed.Add((name, actual.ToString()));
            Console.WriteLine("  失败 " + name + " —— " + actual.Message);
        }
        finally
        {
            try
            {
                testCleanup?.Invoke(instance, null);
            }
            catch (Exception)
            {
                // 清理失败不影响结果统计
            }
        }
    }
}
