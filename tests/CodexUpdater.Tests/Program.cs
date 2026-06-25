using CodexUpdater.Core;

var links = new[]
{
    new PackageLink("https://example.test/OpenAI.Codex_26.616.10790.0_arm64__2p2nqsd0c76g0.msix", "OpenAI.Codex_26.616.10790.0_arm64__2p2nqsd0c76g0.msix"),
    new PackageLink("https://example.test/OpenAI.Codex_26.616.10790.0_x64__2p2nqsd0c76g0.BlockMap", "OpenAI.Codex_26.616.10790.0_x64__2p2nqsd0c76g0.BlockMap"),
    new PackageLink("https://example.test/OpenAI.Codex_26.616.9593.0_x64__2p2nqsd0c76g0.msix", "OpenAI.Codex_26.616.9593.0_x64__2p2nqsd0c76g0.Msix"),
    new PackageLink("https://example.test/OpenAI.Codex_26.616.10790.0_x64__2p2nqsd0c76g0.msix", "OpenAI.Codex_26.616.10790.0_x64__2p2nqsd0c76g0.msix"),
};

var candidate = CodexPackage.SelectNewest(links);
var arm64Candidate = CodexPackage.SelectNewest(links, "arm64");

Assert(candidate is not null, "expected a candidate");
Assert(candidate!.Architecture == "x64", "expected x64 architecture");
Assert(candidate.FileName == "OpenAI.Codex_26.616.10790.0_x64__2p2nqsd0c76g0.msix", "expected newest x64 MSIX");
Assert(candidate.Version == new Version(26, 616, 10790, 0), "expected parsed newest version");
Assert(!CodexPackage.IsExpectedFileName("OpenAI.Codex_26.616.10790.0_arm64__2p2nqsd0c76g0.msix"), "arm64 must be ignored");
Assert(CodexPackage.IsExpectedFileName("OpenAI.Codex_26.616.10790.0_arm64__2p2nqsd0c76g0.msix", "arm64"), "arm64 must be accepted when selected");
Assert(arm64Candidate is not null, "expected an arm64 candidate when selected");
Assert(arm64Candidate!.Architecture == "arm64", "expected selected arm64 architecture");
Assert(!CodexPackage.IsExpectedFileName("OpenAI.Codex_26.616.10790.0_x64__2p2nqsd0c76g0.BlockMap"), "BlockMap must be ignored");

Console.WriteLine("CodexUpdater parser tests passed.");

static void Assert(bool condition, string message)
{
    if (!condition)
    {
        throw new InvalidOperationException(message);
    }
}
