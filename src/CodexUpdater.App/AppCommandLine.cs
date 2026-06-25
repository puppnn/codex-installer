namespace CodexUpdater.App;

public sealed record AppCommandLine(string? InstallPath, string Architecture)
{
    public static AppCommandLine Parse(string[] args)
    {
        string? installPath = null;
        var architecture = "x64";

        for (var i = 0; i < args.Length; i++)
        {
            if (string.Equals(args[i], "--install", StringComparison.OrdinalIgnoreCase) &&
                i + 1 < args.Length)
            {
                installPath = args[i + 1];
                i++;
                continue;
            }

            if (string.Equals(args[i], "--arch", StringComparison.OrdinalIgnoreCase) &&
                i + 1 < args.Length)
            {
                architecture = args[i + 1];
                i++;
            }
        }

        return new AppCommandLine(installPath, architecture);
    }
}
