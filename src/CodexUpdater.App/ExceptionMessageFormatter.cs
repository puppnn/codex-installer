namespace CodexUpdater.App;

internal static class ExceptionMessageFormatter
{
    public static string Format(Exception exception)
    {
        var messages = new List<string>();
        for (Exception? current = exception; current is not null; current = current.InnerException)
        {
            var message = current.Message.Trim();
            if (message.Length > 0 && !messages.Contains(message, StringComparer.Ordinal))
            {
                messages.Add(message);
            }
        }

        return string.Join(Environment.NewLine + "原因：", messages);
    }
}
