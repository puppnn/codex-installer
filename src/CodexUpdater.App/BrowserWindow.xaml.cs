using System.Text.Json;
using System.Windows;
using CodexUpdater.Core;

namespace CodexUpdater.App;

public partial class BrowserWindow : Window
{
    private bool _initialized;
    private bool _forceClose;

    public BrowserWindow()
    {
        InitializeComponent();
        Closing += BrowserWindow_Closing;
    }

    public async Task EnsureReadyAsync(Window owner)
    {
        if (!_initialized)
        {
            Owner = owner;
            Show();
            await Browser.EnsureCoreWebView2Async();
            Browser.CoreWebView2.Settings.AreDefaultContextMenusEnabled = true;
            Browser.CoreWebView2.Settings.AreDevToolsEnabled = false;
            Browser.CoreWebView2.Navigate(CodexPackage.RgAdguardUrl);
            _initialized = true;
            return;
        }

        if (!IsVisible)
        {
            Show();
        }
    }

    public void ShowForAttention(string hint)
    {
        HintText.Text = hint;
        if (!IsVisible)
        {
            Show();
        }

        Activate();
    }

    public void HideAfterSuccess()
    {
        if (IsVisible)
        {
            Hide();
        }
    }

    public void CloseForShutdown()
    {
        _forceClose = true;
        Close();
    }

    public async Task<string> FillAndSubmitRgAdguardAsync()
    {
        const string script = """
            (() => {
              const bodyText = document.body?.innerText || "";
              if (/Just a moment|Enable JavaScript|Checking your browser|Cloudflare/i.test(bodyText)) {
                return "challenge";
              }

              const selects = Array.from(document.querySelectorAll("select"));
              const inputs = Array.from(document.querySelectorAll("input"));
              const textInput = inputs.find(input => {
                const type = (input.getAttribute("type") || "text").toLowerCase();
                return ["text", "search", "url"].includes(type);
              });

              if (!textInput || selects.length < 1) return "loading";

              const setValue = (element, value) => {
                element.value = value;
                element.dispatchEvent(new Event("input", { bubbles: true }));
                element.dispatchEvent(new Event("change", { bubbles: true }));
              };

              const setSelect = (select, wanted) => {
                const option = Array.from(select.options).find(item =>
                  item.value.toLowerCase() === wanted.toLowerCase() ||
                  item.textContent.toLowerCase().includes(wanted.toLowerCase()));
                setValue(select, option ? option.value : wanted);
              };

              setSelect(selects[0], "ProductId");
              setValue(textInput, "9PLM9XGG6VKS");
              if (selects.length > 1) setSelect(selects[1], "RP");

              const controls = Array.from(document.querySelectorAll("button,input[type=submit],input[type=button]"));
              const submit = controls.find(control => {
                const text = `${control.innerText || ""} ${control.value || ""} ${control.title || ""}`;
                return /check|generate|temporary|link/i.test(text);
              }) || controls[controls.length - 1];

              if (!submit) return "loading";
              submit.click();
              return "submitted";
            })();
            """;

        var json = await Browser.CoreWebView2.ExecuteScriptAsync(script);
        return JsonSerializer.Deserialize<string>(json) ?? "loading";
    }

    public async Task<IReadOnlyList<PackageLink>> ExtractLinksAsync()
    {
        const string script = """
            (() => Array.from(document.links).map(anchor => ({
              href: anchor.href || "",
              text: (anchor.textContent || "").trim()
            })))();
            """;

        var json = await Browser.CoreWebView2.ExecuteScriptAsync(script);
        var links = JsonSerializer.Deserialize<List<BrowserLink>>(
            json,
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

        return links?
            .Where(link => !string.IsNullOrWhiteSpace(link.Href) || !string.IsNullOrWhiteSpace(link.Text))
            .Select(link => new PackageLink(link.Href, link.Text))
            .ToArray() ?? Array.Empty<PackageLink>();
    }

    private void BrowserWindow_Closing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        if (_forceClose) return;

        e.Cancel = true;
        Hide();
    }

    private sealed record BrowserLink(string Href, string Text);
}
