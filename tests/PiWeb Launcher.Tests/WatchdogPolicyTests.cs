using System;
using PiWeb_Launcher.Services;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace PiWeb_Launcher.Tests;

/// <summary>
/// 看门狗(Pi Web 服务意外退出自动重启)的纯决策规则。
/// 这些规则写错的表现是"该重启时不动 / 不该重启时反复拉起",不会报错,只能靠测试兜住。
/// </summary>
[TestClass]
public sealed class WatchdogPolicyTests
{
    [TestMethod]
    public void ShouldSchedule_EnabledAndNotUserStopped_AndWithinQuota_ReturnsTrue()
    {
        Assert.IsTrue(WatchdogPolicy.ShouldSchedule(autoRestartEnabled: true, stoppedByUser: false, restartsUsed: 0));
        Assert.IsTrue(WatchdogPolicy.ShouldSchedule(autoRestartEnabled: true, stoppedByUser: false, restartsUsed: WatchdogPolicy.MaxRestarts - 1));
    }

    [TestMethod]
    public void ShouldSchedule_Disabled_OrUserStopped_OrQuotaExhausted_ReturnsFalse()
    {
        Assert.IsFalse(WatchdogPolicy.ShouldSchedule(autoRestartEnabled: false, stoppedByUser: false, restartsUsed: 0));
        Assert.IsFalse(WatchdogPolicy.ShouldSchedule(autoRestartEnabled: true, stoppedByUser: true, restartsUsed: 0));
        Assert.IsFalse(WatchdogPolicy.ShouldSchedule(autoRestartEnabled: true, stoppedByUser: false, restartsUsed: WatchdogPolicy.MaxRestarts));
        Assert.IsFalse(WatchdogPolicy.ShouldSchedule(autoRestartEnabled: true, stoppedByUser: false, restartsUsed: WatchdogPolicy.MaxRestarts + 1));
    }

    [TestMethod]
    public void DelayFor_FollowsBackoffSequence()
    {
        Assert.AreEqual(TimeSpan.FromSeconds(5), WatchdogPolicy.DelayFor(1));
        Assert.AreEqual(TimeSpan.FromSeconds(15), WatchdogPolicy.DelayFor(2));
        Assert.AreEqual(TimeSpan.FromSeconds(30), WatchdogPolicy.DelayFor(3));
    }

    /// <summary>越界输入不能抛异常:调用方即使算错次数也只是退回序列端点。</summary>
    [TestMethod]
    public void DelayFor_OutOfRange_ClampsToSequence()
    {
        Assert.AreEqual(TimeSpan.FromSeconds(5), WatchdogPolicy.DelayFor(0));
        Assert.AreEqual(TimeSpan.FromSeconds(30), WatchdogPolicy.DelayFor(99));
    }

    [TestMethod]
    public void ShouldResetAttempts_BeforeThreshold_ReturnsFalse()
    {
        Assert.IsFalse(WatchdogPolicy.ShouldResetAttempts(TimeSpan.Zero));
        Assert.IsFalse(WatchdogPolicy.ShouldResetAttempts(TimeSpan.FromSeconds(30)));
        Assert.IsFalse(WatchdogPolicy.ShouldResetAttempts(TimeSpan.FromMinutes(4)));
    }

    /// <summary>恰好达到阈值就算"稳定运行过":长期运行中偶发的崩溃应从头计数。</summary>
    [TestMethod]
    public void ShouldResetAttempts_AtOrAfterThreshold_ReturnsTrue()
    {
        Assert.IsTrue(WatchdogPolicy.ShouldResetAttempts(WatchdogPolicy.StableRunReset));
        Assert.IsTrue(WatchdogPolicy.ShouldResetAttempts(TimeSpan.FromHours(1)));
    }

    /// <summary>设置页说明文案里的退避间隔必须与实际序列一致(文案写错会误导用户)。</summary>
    [TestMethod]
    public void DescribeBackoff_MatchesActualSequence()
    {
        Assert.AreEqual("5/15/30 秒", WatchdogPolicy.DescribeBackoff());
    }
}
