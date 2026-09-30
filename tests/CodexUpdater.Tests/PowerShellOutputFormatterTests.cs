using CodexUpdater.Core;

namespace CodexUpdater.Tests;

public sealed class PowerShellOutputFormatterTests
{
    [Fact]
    public void Clean_RemovesProgressAndDecodesOnlyTheErrorText()
    {
        var raw = """
            #< CLIXML
            <Objs xmlns="http://schemas.microsoft.com/powershell/2004/04">
              <Obj S="progress"><MS><S>正在安装...</S></MS></Obj>
              <S S="Error">错误 0x80073D02_x000D__x000A_请关闭应用。</S>
            </Objs>
            """;
        var cleaned = PowerShellOutputFormatter.Clean(raw);
        Assert.Equal("错误 0x80073D02\r\n请关闭应用。", cleaned);
        Assert.DoesNotContain("<Objs", cleaned);
        Assert.DoesNotContain("正在安装", cleaned);
    }

    [Fact]
    public void Clean_DoesNotDecodeEscapedLiteralTwice()
    {
        const string raw = "#< CLIXML\n<Objs><S S=\"Error\">file_x005F_x0041_.msix</S></Objs>";
        Assert.Equal("file_x0041_.msix", PowerShellOutputFormatter.Clean(raw));
    }

    [Fact]
    public void Clean_RejectsExternalEntities()
    {
        const string raw = "#< CLIXML\n<!DOCTYPE x [<!ENTITY probe SYSTEM 'file:///nonexistent'>]><Objs><S>&probe;</S></Objs>";
        Assert.DoesNotContain("<!DOCTYPE", PowerShellOutputFormatter.Clean(raw));
    }

    [Fact]
    public void Clean_PreservesPlainChineseText() =>
        Assert.Equal("安装包未找到。", PowerShellOutputFormatter.Clean("安装包未找到。\r\n"));

    [Fact]
    public void Clean_PreservesPlainErrorMixedWithProgressXml()
    {
        const string raw = "#< CLIXML\n错误 0x80073D02：请关闭应用。\n<Objs><Obj S=\"progress\"><S>progress</S></Obj></Objs>";
        Assert.Equal("错误 0x80073D02：请关闭应用。", PowerShellOutputFormatter.Clean(raw));
    }

    [Theory]
    [InlineData("0x80073D02", "应用仍在运行", true)]
    [InlineData("0x80070490", "包存储库注册失败", false)]
    [InlineData("0x80073CF3", "依赖或兼容性", false)]
    [InlineData("0x80073D06", "更高版本", false)]
    [InlineData("0x80070005", "拒绝访问", false)]
    public void Failure_ProvidesActionableSummary(string code, string expected, bool inUse)
    {
        var failure = InstallationFailure.FromOutput($"安装失败 HRESULT: {code}");
        Assert.Contains(expected, failure.Summary);
        Assert.Equal(inUse, failure.IsPackageInUse);
        Assert.Contains(code, failure.Detail);
    }
}
