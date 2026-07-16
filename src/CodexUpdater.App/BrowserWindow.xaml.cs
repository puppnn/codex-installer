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
            ShowActivated = false;
            ShowInTaskbar = false;
            Opacity = 0;
            Show();
            try
            {
                await Browser.EnsureCoreWebView2Async();
                Browser.CoreWebView2.Settings.AreDefaultContextMenusEnabled = true;
                Browser.CoreWebView2.Settings.AreDevToolsEnabled = false;
                Browser.CoreWebView2.Navigate(CodexPackage.RgAdguardUrl);
                _initialized = true;
            }
            finally
            {
                Hide();
                Opacity = 1;
                ShowActivated = true;
                ShowInTaskbar = true;
            }
            return;
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

    public void NavigateToGenerator()
    {
        if (!_initialized)
        {
            return;
        }

        Browser.CoreWebView2.Navigate(CodexPackage.RgAdguardUrl);
    }

    public async Task<string> FillAndSubmitRgAdguardAsync(string productId)
    {
        if (!StoreProductInputParser.TryNormalizeProductId(productId, out var normalizedProductId))
        {
            throw new InvalidOperationException("ProductId 格式无效。");
        }

        var productIdJson = JsonSerializer.Serialize(normalizedProductId);
        var script = $$"""
            (() => {
              const productId = {{productIdJson}};
              const bodyText = document.body?.innerText || "";
              const challengeControl = document.querySelector(
                'iframe[src*="challenges.cloudflare.com"], iframe[title*="challenge" i], .cf-turnstile, [name="cf-turnstile-response"]');
              if (challengeControl || /Just a moment|Enable JavaScript|Checking your browser|Cloudflare/i.test(bodyText)) {
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
              setValue(textInput, productId);
              if (selects.length > 1) setSelect(selects[1], "Retail");

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
        var rows = await ExtractPackageRowsAsync();
        return rows.Select(row => new PackageLink(row.Href, row.Text, row.PageHash)).ToArray();
    }

    public async Task<IReadOnlyList<PackageLinkRow>> ExtractPackageRowsAsync()
    {
        const string script = """
            (() => Array.from(document.links).map(anchor => {
              const row = anchor.closest("tr");
              const cells = row ? Array.from(row.querySelectorAll("td")).map(cell => (cell.textContent || "").trim()) : [];
              return {
                href: anchor.href || "",
                text: (anchor.textContent || "").trim(),
                expires: cells.length > 1 ? cells[1] : "",
                pageHash: cells.length > 2 ? cells[2] : ""
              };
            }))();
            """;

        var json = await Browser.CoreWebView2.ExecuteScriptAsync(script);
        var links = JsonSerializer.Deserialize<List<BrowserLinkRow>>(
            json,
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

        return links?
            .Where(link => !string.IsNullOrWhiteSpace(link.Href) || !string.IsNullOrWhiteSpace(link.Text))
            .Select(link => new PackageLinkRow(link.Href, link.Text, link.Expires, link.PageHash))
            .ToArray() ?? Array.Empty<PackageLinkRow>();
    }

    private void BrowserWindow_Closing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        if (_forceClose) return;

        e.Cancel = true;
        Hide();
    }

    private sealed record BrowserLinkRow(
        string Href,
        string Text,
        string? Expires,
        string? PageHash);
}
