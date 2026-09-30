using System.Text.Json;
using System.IO;
using System.Windows;
using CodexUpdater.Core;
using Microsoft.Web.WebView2.Core;

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
                var userDataFolder = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "CodexUpdater", "WebView2");
                var environment = await CoreWebView2Environment.CreateAsync(userDataFolder: userDataFolder);
                await Browser.EnsureCoreWebView2Async(environment);
                Browser.CoreWebView2.Settings.AreDefaultContextMenusEnabled = true;
                Browser.CoreWebView2.Settings.AreDevToolsEnabled = false;
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
            Activate();
        }
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
        Browser.Dispose();
        Close();
    }

    public async Task<IReadOnlyList<PackageLinkRow>> QueryPackagesAsync(
        string productId,
        Func<IReadOnlyList<PackageLinkRow>, bool> hasExpectedPackages,
        IProgress<string> status,
        CancellationToken cancellationToken)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromMinutes(4));
        var token = deadline.Token;
        try
        {
            await NavigateAndWaitAsync("about:blank", token);
            await NavigateAndWaitAsync(CodexPackage.RgAdguardUrl, token);
            var submitted = false;
            var attempts = 0;
            while (true)
            {
                token.ThrowIfCancellationRequested();
                if (!submitted)
                {
                    var state = await FillAndSubmitRgAdguardAsync(productId).WaitAsync(token);
                    submitted = state == "submitted";
                    if (state == "challenge")
                    {
                        ShowForAttention("请完成验证，完成后会自动继续");
                        status.Report("请在链接浏览器中完成验证。");
                    }
                    else
                    {
                        status.Report(submitted ? "已提交查询，正在等待安装包列表..." : "正在等待查询页面加载...");
                    }
                }
                else
                {
                    var rows = await ExtractPackageRowsAsync().WaitAsync(token);
                    if (hasExpectedPackages(rows))
                    {
                        HideAfterSuccess();
                        return rows;
                    }
                }

                if (++attempts >= 5) ShowForAttention("请检查页面并完成可能出现的验证");
                await Task.Delay(1200, token);
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException("等待 rg-adguard 安装包列表超时，请检查网络或手动完成页面验证后重试。");
        }
        finally
        {
            Browser.CoreWebView2?.Stop();
            HideAfterSuccess();
        }
    }

    private async Task NavigateAndWaitAsync(string address, CancellationToken cancellationToken)
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        void Completed(object? sender, CoreWebView2NavigationCompletedEventArgs e)
        {
            if (e.IsSuccess || e.HttpStatusCode is 403 or 429 or 503) completion.TrySetResult();
            else completion.TrySetException(new InvalidOperationException($"链接页面加载失败：{e.WebErrorStatus}。"));
        }

        Browser.CoreWebView2.NavigationCompleted += Completed;
        try
        {
            Browser.CoreWebView2.Navigate(address);
            await completion.Task.WaitAsync(cancellationToken);
        }
        finally
        {
            Browser.CoreWebView2.NavigationCompleted -= Completed;
        }
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
              if (location.hostname !== "store.rg-adguard.net") return "loading";
              const bodyText = document.body?.innerText || "";
              const challengeResponse = document.querySelector('[name="cf-turnstile-response"]');
              const verificationComplete = Boolean(challengeResponse?.value?.trim());
              const challengeControl = document.querySelector(
                'iframe[src*="challenges.cloudflare.com"], iframe[title*="challenge" i], .cf-turnstile, [name="cf-turnstile-response"]');
              if ((!verificationComplete && challengeControl) || /Just a moment|Enable JavaScript|Checking your browser/i.test(bodyText)) {
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
              document.querySelectorAll("tr").forEach(row => {
                if (Array.from(row.querySelectorAll("a")).some(anchor => {
                  try { return new URL(anchor.href).hostname.endsWith(".delivery.mp.microsoft.com"); }
                  catch { return false; }
                })) row.remove();
              });
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
