namespace CodexUpdater.Core;

public sealed record PackageServiceInfo(string Name, string DisplayName, string State, string PathName)
{
    public bool IsStopped => State.Equals("Stopped", StringComparison.OrdinalIgnoreCase);

    public string Label => string.IsNullOrWhiteSpace(DisplayName) || DisplayName.Equals(Name, StringComparison.OrdinalIgnoreCase)
        ? Name
        : $"{DisplayName}（{Name}）";

    public static string GetExecutablePath(string pathName)
    {
        var text = pathName.Trim();
        if (text.StartsWith('"'))
        {
            var end = text.IndexOf('"', 1);
            return end > 1 ? text[1..end] : text.Trim('"');
        }

        var exe = text.IndexOf(".exe", StringComparison.OrdinalIgnoreCase);
        return exe >= 0 ? text[..(exe + 4)] : text;
    }

    public static bool IsInsidePackage(string pathName, string installLocation)
    {
        if (string.IsNullOrWhiteSpace(pathName) || string.IsNullOrWhiteSpace(installLocation)) return false;
        var root = installLocation.Trim().TrimEnd('\\', '/') + "\\";
        var executable = GetExecutablePath(pathName).Replace('/', '\\');
        return executable.StartsWith(root, StringComparison.OrdinalIgnoreCase);
    }
}
