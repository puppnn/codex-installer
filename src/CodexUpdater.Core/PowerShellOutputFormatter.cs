using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;

namespace CodexUpdater.Core;

public static partial class PowerShellOutputFormatter
{
    public static string Clean(string output)
    {
        var marker = output.IndexOf("#< CLIXML", StringComparison.Ordinal);
        if (marker < 0) return output.Trim();

        var text = output.Replace("#< CLIXML", "", StringComparison.Ordinal).Trim();
        var xmlStart = text.IndexOf("<Objs", StringComparison.Ordinal);
        if (xmlStart < 0) return text;
        var prefix = text[..xmlStart].Trim();
        if (prefix.Contains("<!DOCTYPE", StringComparison.OrdinalIgnoreCase)) prefix = "";
        var xmlEnd = text.IndexOf("</Objs>", xmlStart, StringComparison.Ordinal);
        var xml = xmlEnd < 0 ? text[xmlStart..] : text[xmlStart..(xmlEnd + "</Objs>".Length)];
        var suffix = xmlEnd < 0 ? "" : text[(xmlEnd + "</Objs>".Length)..].Trim();
        try
        {
            using var reader = XmlReader.Create(new StringReader(xml), new XmlReaderSettings
            {
                DtdProcessing = DtdProcessing.Prohibit,
                XmlResolver = null,
                MaxCharactersInDocument = 8 * 1024 * 1024,
            });
            var document = XDocument.Load(reader);
            var messages = document.Root?.Elements()
                .Where(element => element.Name.LocalName == "S" &&
                    !string.Equals((string?)element.Attribute("S"), "progress", StringComparison.OrdinalIgnoreCase))
                .Select(element => Decode(element.Value).Trim())
                .Where(value => value.Length > 0)
                .Distinct(StringComparer.Ordinal)
                .ToArray() ?? [];
            return string.Join(Environment.NewLine,
                new[] { prefix }.Concat(messages).Append(Clean(suffix)).Where(value => value.Length > 0));
        }
        catch (XmlException)
        {
            return string.IsNullOrEmpty(prefix)
                ? "PowerShell 返回了无法解析的错误数据，请重试并查看 Windows 应用部署日志。"
                : prefix;
        }
    }

    private static string Decode(string value) => EncodedCharacterRegex().Replace(
        value,
        match => ((char)Convert.ToInt32(match.Groups[1].Value, 16)).ToString());

    [GeneratedRegex("_x([0-9A-Fa-f]{4})_", RegexOptions.CultureInvariant)]
    private static partial Regex EncodedCharacterRegex();
}
