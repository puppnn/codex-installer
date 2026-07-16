using CodexUpdater.Core;

namespace CodexUpdater.Tests;

public sealed class StoreProductInputParserTests
{
    [Theory]
    [InlineData("9plm9xgg6vks", "9PLM9XGG6VKS")]
    [InlineData("XP8BT8DW290MPQ", "XP8BT8DW290MPQ")]
    [InlineData("https://apps.microsoft.com/detail/9PLM9XGG6VKS?hl=zh-CN", "9PLM9XGG6VKS")]
    [InlineData("https://apps.microsoft.com/store/detail/codex/9PLM9XGG6VKS", "9PLM9XGG6VKS")]
    [InlineData("https://apps.microsoft.com/detail/codex?productid=9PLM9XGG6VKS", "9PLM9XGG6VKS")]
    [InlineData("https://www.microsoft.com/en-us/p/codex/9PLM9XGG6VKS", "9PLM9XGG6VKS")]
    [InlineData("https://microsoft.com/store/apps/codex/9PLM9XGG6VKS", "9PLM9XGG6VKS")]
    public void Parse_AcceptsProductIdsAndOfficialStoreUrls(string input, string expected)
    {
        Assert.Equal(expected, StoreProductInputParser.Parse(input));
    }

    [Theory]
    [InlineData("http://apps.microsoft.com/detail/9PLM9XGG6VKS")]
    [InlineData("https://apps.microsoft.com.example.test/detail/9PLM9XGG6VKS")]
    [InlineData("https://apps.microsoft.com@evil.test/detail/9PLM9XGG6VKS")]
    [InlineData("https://microsoft.com/security/9PLM9XGG6VKS")]
    [InlineData("https://example.test/store/9PLM9XGG6VKS")]
    [InlineData("not-a-product-id")]
    [InlineData("ABC!23456789")]
    public void Parse_RejectsNonStoreAndInvalidInputs(string input)
    {
        Assert.Throws<InvalidOperationException>(() => StoreProductInputParser.Parse(input));
    }
}
