using CodexUpdater.Core;

namespace CodexUpdater.Tests;

public sealed class DeploymentLogTests
{
    private const string ActivityId = "3e8f1e44-5e28-0008-a4a5-ed42285edd01";

    [Theory]
    [InlineData("注意: 有关其他信息，请在事件日志中查找 [ActivityId] 3E8F1E44-5E28-0008-A4A5-ED42285EDD01，或使用命令行")]
    [InlineData("或使用命令行 Get-AppPackageLog -ActivityID 3e8f1e44-5e28-0008-a4a5-ed42285edd01")]
    [InlineData("NOTE: For additional information, look for [ActivityId] 3e8f1e44-5e28-0008-a4a5-ed42285edd01 in the Event Log")]
    public void FindActivityId_ReadsLocalizedAndCommandForms(string output) =>
        Assert.Equal(ActivityId, DeploymentLog.FindActivityId(output));

    [Fact]
    public void FindActivityId_ReturnsNullWithoutId() =>
        Assert.Null(DeploymentLog.FindActivityId("部署失败，原因是 HRESULT: 0x80073D02"));

    [Fact]
    public void SelectKeyEntries_KeepsErrorsWarningsAndVerboseFailureCodes()
    {
        DeploymentLogEntry[] entries =
        [
            new(603, 4, "已使用主参数 OpenAI.Codex.msix 开始对程序包执行部署 Add 操作。"),
            new(9645, 5, "0x0: 程序包 OpenAI.Codex 的 GetActiveAumidsInPackage 成功。"),
            new(9641, 5, "0x80004004: 由于存在活动服务 OpenAI.Codex_2p2nqsd0c76g0!App，因此中止了部署。"),
            new(1230, 3, "这些硬链接在存储库中没有程序包。"),
            new(419, 2, "错误 0x80073D02: 无法安装，因为需要关闭以下应用。"),
            new(419, 2, "错误 0x80073D02: 无法安装，因为需要关闭以下应用。"),
        ];
        Assert.Equal([9641, 1230, 419], DeploymentLog.SelectKeyEntries(entries).Select(entry => entry.Id));
    }

    [Fact]
    public void ActiveServiceRecord_DrivesFailureSummary()
    {
        var log = DeploymentLog.Format(ActivityId,
            [new DeploymentLogEntry(9641, 5, "0x80004004: 由于存在活动服务\r\n  OpenAI.Codex_2p2nqsd0c76g0!App，因此中止了部署。")]);
        Assert.Equal("[9641] 0x80004004: 由于存在活动服务 OpenAI.Codex_2p2nqsd0c76g0!App，因此中止了部署。", log.Split('\n')[1]);

        var failure = InstallationFailure.FromOutput("错误 0x80073D02: 无法安装。\n\n" + log);
        Assert.True(failure.IsPackageInUse);
        Assert.Contains("后台服务", failure.Summary);
        Assert.Contains("[9641]", failure.Detail);
    }

    [Fact]
    public void PackageInUseWithoutServiceRecord_KeepsGenericSummary()
    {
        var log = DeploymentLog.Format(ActivityId, [new DeploymentLogEntry(419, 2, "错误 0x80073D02: 无法安装。")]);
        var failure = InstallationFailure.FromOutput("错误 0x80073D02\n\n" + log);
        Assert.StartsWith("应用仍在运行", failure.Summary);
    }

    [Fact]
    public void Format_TruncatesVeryLongMessages()
    {
        var log = DeploymentLog.Format(ActivityId, [new DeploymentLogEntry(404, 2, new string('x', 1000))]);
        Assert.True(log.Split('\n')[1].Length < 420);
        Assert.EndsWith("…", log);
    }
}
