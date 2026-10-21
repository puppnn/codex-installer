using System.Text;
using System.Text.RegularExpressions;

namespace CodexUpdater.Core;

public sealed record DeploymentLogEntry(int Id, int Level, string Message);

// Add-AppxPackage only reports the final HRESULT; the AppX deployment log behind its ActivityId holds the
// actual reason, e.g. event 9641 "deployment aborted because of active service", which is logged as Verbose.
public static partial class DeploymentLog
{
    public const int ActiveServiceAbortEventId = 9641;
    private const int MaxMessageLength = 400;

    [GeneratedRegex(@"ActivityId\]?\s*[:：]?\s*([0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12})", RegexOptions.IgnoreCase)]
    private static partial Regex ActivityIdPattern();

    [GeneratedRegex(@"0x8[0-9a-f]{7}", RegexOptions.IgnoreCase)]
    private static partial Regex FailureCodePattern();

    [GeneratedRegex(@"\s+")]
    private static partial Regex WhitespacePattern();

    public static string? FindActivityId(string output)
    {
        var match = ActivityIdPattern().Match(output);
        return match.Success ? match.Groups[1].Value.ToLowerInvariant() : null;
    }

    // Errors and warnings, plus any record carrying a failure HRESULT regardless of its level.
    public static IReadOnlyList<DeploymentLogEntry> SelectKeyEntries(IEnumerable<DeploymentLogEntry> entries) =>
        entries
            .Where(entry => entry.Id == ActiveServiceAbortEventId || entry.Level is >= 1 and <= 3 || FailureCodePattern().IsMatch(entry.Message))
            .DistinctBy(entry => (entry.Id, entry.Message))
            .ToArray();

    public static string Format(string activityId, IReadOnlyList<DeploymentLogEntry> keyEntries)
    {
        var builder = new StringBuilder($"Windows 部署日志（ActivityId {activityId}）关键记录：");
        foreach (var entry in keyEntries)
        {
            var message = WhitespacePattern().Replace(entry.Message, " ").Trim();
            if (message.Length > MaxMessageLength) message = message[..MaxMessageLength] + "…";
            builder.Append('\n').Append($"[{entry.Id}] {message}");
        }
        return builder.ToString();
    }

    public static bool ReportsActiveService(string detail) =>
        detail.Contains($"[{ActiveServiceAbortEventId}] ", StringComparison.Ordinal);
}
