using System.Collections.ObjectModel;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using CodexUpdater.Core;
using WinForms = System.Windows.Forms;

namespace CodexUpdater.App;

public partial class AdvancedStoreWindow : Window
{
    private readonly ObservableCollection<CandidateRow> _visibleCandidates = [];
    private readonly ObservableCollection<string> _dependencyPreview = [];
    private readonly Dictionary<string, InstalledStorePackage?> _installedPackages =
        new(StringComparer.OrdinalIgnoreCase);
    private BrowserWindow? _browserWindow;
    private UserSettings _settings;
    private StoreProductMetadata? _product;
    private IReadOnlyList<StorePackageCandidate> _allCandidates = [];
    private CancellationTokenSource? _operationCancellation;
    private PackageInstallationPlan? _lastPlan;

    public AdvancedStoreWindow()
    {
        InitializeComponent();
        _settings = UserSettings.Load();
        CandidatesGrid.ItemsSource = _visibleCandidates;
        DependenciesList.ItemsSource = _dependencyPreview;
        DownloadDirectoryText.Text = _settings.DownloadDirectory;
        _dependencyPreview.Add("选择主包后，工具会在下载主包后解析并预览依赖。");
        Closed += AdvancedStoreWindow_Closed;
    }

    private void AdvancedStoreWindow_Closed(object? sender, EventArgs e)
    {
        _operationCancellation?.Cancel();
        _browserWindow?.CloseForShutdown();
    }

    private async void SearchButton_Click(object sender, RoutedEventArgs e)
    {
        await RunOperationAsync(SearchAsync);
    }

    private void CancelButton_Click(object sender, RoutedEventArgs e)
    {
        _operationCancellation?.Cancel();
    }

    private void ChooseFolderButton_Click(object sender, RoutedEventArgs e)
    {
        using var dialog = new WinForms.FolderBrowserDialog
        {
            Description = "选择 Microsoft Store 安装包下载位置",
            SelectedPath = Directory.Exists(_settings.DownloadDirectory)
                ? _settings.DownloadDirectory
                : Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            UseDescriptionForTitle = true,
        };
        if (dialog.ShowDialog() != WinForms.DialogResult.OK ||
            string.IsNullOrWhiteSpace(dialog.SelectedPath))
        {
            return;
        }

        _settings = _settings with { DownloadDirectory = dialog.SelectedPath };
        _settings.Save();
        _lastPlan = null;
        DownloadDirectoryText.Text = _settings.DownloadDirectory;
        SetStatus($"安装包下载位置已设置为：{_settings.DownloadDirectory}");
    }

    private void ArchitectureFilterComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (CandidatesGrid is null) return;
        ApplyCandidateFilter();
    }

    private void CandidatesGrid_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        _lastPlan = null;
        DownloadInstallButton.Content = "下载并安装";
        _dependencyPreview.Clear();
        _dependencyPreview.Add("下载主包后将自动解析并匹配依赖。");
        UpdateActionButtons();
    }

    private async void DownloadOnlyButton_Click(object sender, RoutedEventArgs e)
    {
        await RunOperationAsync(cancellationToken => DownloadAndOptionallyInstallAsync(false, cancellationToken));
    }

    private async void DownloadInstallButton_Click(object sender, RoutedEventArgs e)
    {
        await RunOperationAsync(cancellationToken => DownloadAndOptionallyInstallAsync(true, cancellationToken));
    }

    private async Task SearchAsync(CancellationToken cancellationToken)
    {
        ResetSearchResults();
        var productId = StoreProductInputParser.Parse(ProductInputTextBox.Text);
        SetStatus("正在读取 Microsoft Store 应用元数据...");
        var product = await MicrosoftStoreMetadataService.ResolveAsync(productId, cancellationToken);
        _product = product;
        ProductNameText.Text = product.DisplayName;
        ProductIdText.Text = product.ProductId;
        PackageFamilyText.Text = string.Join(", ", product.PackageFamilyNames);

        SetStatus("正在通过 rg-adguard 获取 Retail 安装包列表...");
        _allCandidates = await QueryCandidatesAsync(product, cancellationToken);
        await LoadInstalledPackagesAsync(cancellationToken);
        ApplyCandidateFilter();
        if (_visibleCandidates.Count == 0)
        {
            throw new InvalidOperationException("没有找到与 Store 包族和当前架构筛选匹配的主应用包。");
        }

        var licenseNote = product.IsFree == false ? " 此应用可能需要有效的 Microsoft Store 许可。" : "";
        SetStatus($"找到 {_visibleCandidates.Count} 个主包候选。请选择一个版本。{licenseNote}");
    }

    private async Task<IReadOnlyList<StorePackageCandidate>> QueryCandidatesAsync(
        StoreProductMetadata product,
        CancellationToken cancellationToken)
    {
        var browser = await GetBrowserWindowAsync();
        await browser.EnsureReadyAsync(this);
        browser.NavigateToGenerator();
        browser.HideAfterSuccess();

        var submitted = false;
        var unresolvedAttempts = 0;
        var deadline = DateTimeOffset.UtcNow.AddMinutes(4);
        while (DateTimeOffset.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!submitted)
            {
                var state = await browser.FillAndSubmitRgAdguardAsync(product.ProductId);
                if (state == "submitted")
                {
                    submitted = true;
                    unresolvedAttempts = 0;
                    SetStatus("已提交 ProductId，正在等待 rg-adguard 返回安装包链接...");
                }
                else if (state == "challenge")
                {
                    browser.ShowForAttention("请完成 Cloudflare 验证");
                    SetStatus("请在链接浏览器中完成验证，工具会继续等待。");
                }
                else if (++unresolvedAttempts >= 2)
                {
                    browser.ShowForAttention("请检查页面并手动完成可能出现的验证");
                }

                await Task.Delay(1200, cancellationToken);
                continue;
            }

            var rows = await browser.ExtractPackageRowsAsync();
            var candidates = StorePackageCandidateParser.Parse(rows, product.PackageFamilyNames);
            if (candidates.Any(candidate => candidate.Role == StorePackageRole.Main))
            {
                browser.HideAfterSuccess();
                return candidates;
            }
            unresolvedAttempts++;
            if (unresolvedAttempts >= 2)
            {
                browser.ShowForAttention("请检查页面并手动完成可能出现的验证");
            }

            SetStatus("正在扫描 rg-adguard 返回的主包和依赖包...");
            await Task.Delay(1500, cancellationToken);
        }

        throw new TimeoutException("等待 rg-adguard 安装包列表超时。");
    }

    private async Task LoadInstalledPackagesAsync(CancellationToken cancellationToken)
    {
        _installedPackages.Clear();
        if (_product is null) return;

        var expectedFamilies = new HashSet<string>(
            _product.PackageFamilyNames,
            StringComparer.OrdinalIgnoreCase);
        foreach (var identityName in _allCandidates
            .Where(candidate => candidate.Role == StorePackageRole.Main)
            .Select(candidate => candidate.IdentityName)
            .Distinct(StringComparer.OrdinalIgnoreCase))
        {
            cancellationToken.ThrowIfCancellationRequested();
            _installedPackages[identityName] = (await CodexSystemService.GetInstalledPackagesAsync(identityName))
                .Where(package => expectedFamilies.Contains(package.PackageFamilyName))
                .OrderByDescending(package => package.Version)
                .FirstOrDefault();
        }
    }

    private void ApplyCandidateFilter()
    {
        if (_allCandidates.Count == 0 || CandidatesGrid is null) return;
        var filter = GetArchitectureFilter();
        var hostArchitecture = RuntimeInformation.OSArchitecture;
        var rows = _allCandidates
            .Where(candidate => candidate.Role == StorePackageRole.Main)
            .Where(candidate => filter == "all" ||
                filter == "compatible" && PackageDependencyResolver.IsCompatibleWithHost(candidate.Architecture, hostArchitecture) ||
                candidate.Architecture.Equals(filter, StringComparison.OrdinalIgnoreCase) ||
                candidate.Architecture.Equals("neutral", StringComparison.OrdinalIgnoreCase))
            .OrderBy(candidate => candidate.IsExpired)
            .ThenByDescending(candidate => PackageDependencyResolver.IsCompatibleWithHost(candidate.Architecture, hostArchitecture))
            .ThenByDescending(candidate => candidate.Version)
            .Select(candidate => new CandidateRow(candidate, InstalledFor(candidate)))
            .ToArray();

        _visibleCandidates.Clear();
        foreach (var row in rows) _visibleCandidates.Add(row);
        CandidatesGrid.SelectedItem = _visibleCandidates.FirstOrDefault(row => !row.IsExpired);
        UpdateActionButtons();
    }

    private InstalledStorePackage? InstalledFor(StorePackageCandidate candidate)
    {
        return _installedPackages.GetValueOrDefault(candidate.IdentityName);
    }

    private async Task DownloadAndOptionallyInstallAsync(bool install, CancellationToken cancellationToken)
    {
        if (_product is null || CandidatesGrid.SelectedItem is not CandidateRow selected)
        {
            throw new InvalidOperationException("请先搜索并选择一个主应用包。");
        }

        if (selected.IsExpired)
        {
            throw new InvalidOperationException("所选临时链接已经过期，请重新搜索。");
        }

        if (install && selected.IsDowngrade)
        {
            throw new InvalidOperationException("所选版本低于本机已安装版本，仅允许下载，不允许自动降级。");
        }

        if (install && !selected.IsHostCompatible)
        {
            throw new InvalidOperationException("所选安装包架构与当前 Windows 不兼容，仅允许下载。");
        }

        var plan = _lastPlan is not null &&
            _lastPlan.MainPackage.Candidate.FileName.Equals(selected.Candidate.FileName, StringComparison.OrdinalIgnoreCase)
            ? _lastPlan
            : await BuildInstallationPlanAsync(_product, selected.Candidate, cancellationToken);
        _lastPlan = plan;

        if (!install)
        {
            SetStatus($"下载完成：{Path.GetDirectoryName(plan.MainPackage.FilePath)}");
            System.Windows.MessageBox.Show(
                this,
                $"主包和 {plan.Dependencies.Count} 个依赖包已下载完成。",
                "下载完成",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            return;
        }

        var licenseText = plan.Product.IsFree == false
            ? "\n\n此应用可能需要有效的 Microsoft Store 购买或许可；本工具不会绕过许可。"
            : "";
        var answer = System.Windows.MessageBox.Show(
            this,
            $"即将安装：{plan.Product.DisplayName}\n版本：{plan.MainPackage.Identity.Version}\n依赖：{plan.Dependencies.Count} 个{licenseText}",
            "确认安装",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning);
        if (answer != MessageBoxResult.Yes)
        {
            SetStatus("安装已取消，下载文件已保留。");
            return;
        }

        await InstallPlanAsync(plan);
    }

    private async Task<PackageInstallationPlan> BuildInstallationPlanAsync(
        StoreProductMetadata product,
        StorePackageCandidate selected,
        CancellationToken cancellationToken)
    {
        var targetArchitecture = EffectiveTargetArchitecture(selected);
        var directory = Path.Combine(
            _settings.DownloadDirectory,
            product.ProductId,
            selected.Version.ToString(),
            targetArchitecture);
        Directory.CreateDirectory(directory);
        _dependencyPreview.Clear();
        _dependencyPreview.Add("正在下载并验证主包...");

        var main = await DownloadMainWithRetryAsync(
            selected,
            product,
            directory,
            targetArchitecture,
            cancellationToken);
        var totalBytes = new FileInfo(main.FilePath).Length;
        var dependencyArchitecture = main.Identity.Architecture.Equals("neutral", StringComparison.OrdinalIgnoreCase)
            ? targetArchitecture
            : main.Identity.Architecture;

        var downloadedDependencies = new Dictionary<string, DownloadedStorePackage>(StringComparer.OrdinalIgnoreCase);
        var handledRequirements = new Dictionary<string, Version>(StringComparer.OrdinalIgnoreCase);
        var queue = new Queue<PackageDependencyRequirement>(main.Identity.Dependencies);
        _dependencyPreview.Clear();
        if (queue.Count == 0) _dependencyPreview.Add("主包没有外部依赖。");

        while (queue.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var requirement = queue.Dequeue();
            requirement = requirement with
            {
                PublisherId = WindowsPackageIdentityService.GetPublisherId(
                    requirement.Name,
                    requirement.Publisher),
            };
            var key = $"{requirement.Name}_{requirement.PublisherId}";
            if (handledRequirements.TryGetValue(key, out var handledVersion) &&
                handledVersion >= requirement.MinimumVersion)
            {
                continue;
            }

            var requiredFamilyName = $"{requirement.Name}_{requirement.PublisherId}";
            var installed = (await CodexSystemService.GetInstalledPackagesAsync(requirement.Name))
                .Where(package =>
                    package.PackageFamilyName.Equals(requiredFamilyName, StringComparison.OrdinalIgnoreCase) &&
                    package.Publisher.Equals(requirement.Publisher, StringComparison.OrdinalIgnoreCase) &&
                    package.Version >= requirement.MinimumVersion &&
                    IsDependencyArchitectureCompatible(package.Architecture, dependencyArchitecture))
                .OrderByDescending(package => package.Version)
                .FirstOrDefault();
            if (installed is not null)
            {
                handledRequirements[key] = installed.Version;
                _dependencyPreview.Add($"已安装：{requirement.Name} {installed.Version}");
                continue;
            }

            var resolved = PackageDependencyResolver.Resolve(
                [requirement],
                _allCandidates,
                dependencyArchitecture)[0];
            _dependencyPreview.Add($"下载：{resolved.Candidate.FileName}");
            var dependency = await DownloadDependencyWithRetryAsync(
                resolved.Candidate,
                requirement,
                directory,
                dependencyArchitecture,
                cancellationToken);
            downloadedDependencies[key] = dependency;
            handledRequirements[key] = dependency.Identity.Version;
            totalBytes += new FileInfo(dependency.FilePath).Length;
            if (totalBytes > PackageDownloadService.MaxInstallationPlanBytes)
            {
                throw new InvalidOperationException("主包和依赖包合计超过 4 GB 上限。");
            }

            foreach (var transitive in dependency.Identity.Dependencies) queue.Enqueue(transitive);
        }

        _dependencyPreview.Clear();
        foreach (var dependency in downloadedDependencies.Values)
        {
            _dependencyPreview.Add($"{dependency.Identity.Name} {dependency.Identity.Version} ({dependency.Identity.Architecture})");
        }
        if (downloadedDependencies.Count == 0) _dependencyPreview.Add("无需额外下载依赖包。");
        return new PackageInstallationPlan(product, main, downloadedDependencies.Values.ToArray());
    }

    private async Task<DownloadedStorePackage> DownloadMainWithRetryAsync(
        StorePackageCandidate candidate,
        StoreProductMetadata product,
        string directory,
        string targetArchitecture,
        CancellationToken cancellationToken)
    {
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                SetStatus($"正在下载主包：{candidate.FileName}");
                return await PackageDownloadService.DownloadMainPackageAsync(
                    candidate,
                    product,
                    directory,
                    targetArchitecture,
                    new Progress<double>(value => Progress.Value = value),
                    cancellationToken);
            }
            catch (HttpRequestException ex) when (attempt == 0 && IsExpiredLinkStatus(ex.StatusCode))
            {
                candidate = await RefreshAndRematchAsync(candidate, product, cancellationToken);
            }
        }
    }

    private async Task<DownloadedStorePackage> DownloadDependencyWithRetryAsync(
        StorePackageCandidate candidate,
        PackageDependencyRequirement requirement,
        string directory,
        string targetArchitecture,
        CancellationToken cancellationToken)
    {
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                SetStatus($"正在下载依赖：{candidate.FileName}");
                return await PackageDownloadService.DownloadDependencyPackageAsync(
                    candidate,
                    requirement,
                    directory,
                    targetArchitecture,
                    new Progress<double>(value => Progress.Value = value),
                    cancellationToken);
            }
            catch (HttpRequestException ex) when (attempt == 0 && IsExpiredLinkStatus(ex.StatusCode))
            {
                candidate = await RefreshAndRematchAsync(candidate, _product!, cancellationToken);
            }
        }
    }

    private async Task<StorePackageCandidate> RefreshAndRematchAsync(
        StorePackageCandidate original,
        StoreProductMetadata product,
        CancellationToken cancellationToken)
    {
        SetStatus("临时链接已过期，正在自动重新查询一次...");
        _allCandidates = await QueryCandidatesAsync(product, cancellationToken);
        return _allCandidates.FirstOrDefault(candidate =>
            candidate.PackageFamilyName.Equals(original.PackageFamilyName, StringComparison.OrdinalIgnoreCase) &&
            candidate.Version == original.Version &&
            candidate.Architecture.Equals(original.Architecture, StringComparison.OrdinalIgnoreCase) &&
            candidate.Format == original.Format &&
            candidate.ResourceId.Equals(original.ResourceId, StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidOperationException("重新查询后没有找到与原选择完全一致的安装包。");
    }

    private async Task InstallPlanAsync(PackageInstallationPlan plan)
    {
        var locks = new List<FileStream>();
        try
        {
            locks.Add(PackageDownloadService.OpenAndRevalidateForInstall(plan.MainPackage));
            locks.AddRange(plan.Dependencies.Select(PackageDownloadService.OpenAndRevalidateForInstall));
            SetStatus("正在通过 Add-AppxPackage 安装主包和依赖...");
            Progress.Value = 0;
            var result = await CodexSystemService.InstallPackageAsync(
                plan.MainPackage.FilePath,
                plan.Dependencies.Select(item => item.FilePath).ToArray());
            if (!result.Succeeded)
            {
                var message = string.IsNullOrWhiteSpace(result.StandardError)
                    ? result.StandardOutput
                    : result.StandardError;
                SetStatus("安装失败。请关闭相关应用后点击“下载并安装”重试。");
                DownloadInstallButton.Content = "关闭应用后重试";
                System.Windows.MessageBox.Show(
                    this,
                    message,
                    "Add-AppxPackage 失败",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
                return;
            }

            Progress.Value = 100;
            DownloadInstallButton.Content = "下载并安装";
            SetStatus("安装完成。");
            System.Windows.MessageBox.Show(this, "Microsoft Store 应用安装完成。", "完成", MessageBoxButton.OK, MessageBoxImage.Information);
            await LoadInstalledPackagesAsync(CancellationToken.None);
            ApplyCandidateFilter();
        }
        finally
        {
            foreach (var packageLock in locks) packageLock.Dispose();
        }
    }

    private async Task<BrowserWindow> GetBrowserWindowAsync()
    {
        await WebView2RuntimeService.EnsureInstalledAsync(this);
        _browserWindow ??= new BrowserWindow();
        return _browserWindow;
    }

    private async Task RunOperationAsync(Func<CancellationToken, Task> operation)
    {
        _operationCancellation?.Dispose();
        _operationCancellation = new CancellationTokenSource();
        SetBusy(true);
        try
        {
            await operation(_operationCancellation.Token);
        }
        catch (OperationCanceledException)
        {
            SetStatus("操作已取消，临时文件已清理。");
        }
        catch (Exception ex)
        {
            var message = ExceptionMessageFormatter.Format(ex);
            SetStatus(message);
            System.Windows.MessageBox.Show(this, message, "高级下载安装", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            SetBusy(false);
        }
    }

    private void SetBusy(bool busy)
    {
        SearchButton.IsEnabled = !busy;
        ChooseFolderButton.IsEnabled = !busy;
        ArchitectureFilterComboBox.IsEnabled = !busy;
        CandidatesGrid.IsEnabled = !busy;
        CancelButton.IsEnabled = busy;
        UpdateActionButtons(busy);
    }

    private void UpdateActionButtons(bool busy = false)
    {
        var selected = CandidatesGrid?.SelectedItem as CandidateRow;
        DownloadOnlyButton.IsEnabled = !busy && selected is { IsExpired: false };
        DownloadInstallButton.IsEnabled = !busy && selected is
        { IsExpired: false, IsDowngrade: false, IsHostCompatible: true };
    }

    private void ResetSearchResults()
    {
        _product = null;
        _allCandidates = [];
        _lastPlan = null;
        _visibleCandidates.Clear();
        _dependencyPreview.Clear();
        _dependencyPreview.Add("选择主包后，工具会在下载主包后解析并预览依赖。");
        ProductNameText.Text = "正在解析...";
        ProductIdText.Text = "-";
        PackageFamilyText.Text = "-";
        DownloadInstallButton.Content = "下载并安装";
        Progress.Value = 0;
    }

    private string GetArchitectureFilter()
    {
        return (ArchitectureFilterComboBox.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "compatible";
    }

    private string EffectiveTargetArchitecture(StorePackageCandidate candidate)
    {
        if (!candidate.Architecture.Equals("neutral", StringComparison.OrdinalIgnoreCase))
        {
            return candidate.Architecture;
        }

        var filter = GetArchitectureFilter();
        if (filter is "x64" or "x86" or "arm64") return filter;
        return RuntimeInformation.OSArchitecture switch
        {
            Architecture.Arm64 => "arm64",
            Architecture.X86 => "x86",
            _ => "x64",
        };
    }

    private static bool IsExpiredLinkStatus(HttpStatusCode? statusCode)
    {
        return statusCode is HttpStatusCode.Forbidden or HttpStatusCode.Gone;
    }

    private static bool IsDependencyArchitectureCompatible(string architecture, string targetArchitecture)
    {
        return architecture.Equals(targetArchitecture, StringComparison.OrdinalIgnoreCase) ||
            architecture.Equals("neutral", StringComparison.OrdinalIgnoreCase);
    }

    private void SetStatus(string message)
    {
        StatusText.Text = message;
    }

    private sealed class CandidateRow
    {
        public CandidateRow(StorePackageCandidate candidate, InstalledStorePackage? installed)
        {
            Candidate = candidate;
            Installed = installed;
        }

        public StorePackageCandidate Candidate { get; }
        public InstalledStorePackage? Installed { get; }
        public string FileName => Candidate.FileName;
        public string Version => Candidate.Version.ToString();
        public string Architecture => Candidate.Architecture;
        public string Format => Candidate.Format.ToString();
        public string PageHash => Candidate.PageHash ?? "-";
        public string Expiration => Candidate.ExpiresAt?.ToLocalTime().ToString("yyyy-MM-dd HH:mm") ?? "未知";
        public bool IsExpired => Candidate.IsExpired;
        public bool IsHostCompatible => PackageDependencyResolver.IsCompatibleWithHost(
            Candidate.Architecture,
            RuntimeInformation.OSArchitecture);
        public bool IsDowngrade => Installed is not null && Candidate.Version < Installed.Version;
        public string InstallStatus => IsExpired
            ? "链接已过期"
            : !IsHostCompatible
                ? "与本机不兼容"
            : Installed is null
                ? "未安装"
                : IsDowngrade
                    ? $"已装 {Installed.Version}"
                    : Candidate.Version == Installed.Version
                        ? "版本相同"
                        : $"可更新 {Installed.Version}";
    }
}
