using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
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
    private readonly Dictionary<string, InstalledStorePackage?> _installedPackages = new(StringComparer.OrdinalIgnoreCase);
    private BrowserWindow? _browserWindow;
    private UserSettings _settings;
    private StoreProductMetadata? _product;
    private IReadOnlyList<StorePackageCandidate> _allCandidates = [];
    private CancellationTokenSource? _operationCancellation;
    private PackageInstallationPlan? _lastPlan;
    private bool _busy;
    private bool _installing;
    private bool _closeAfterOperation;

    public AdvancedStoreWindow()
    {
        InitializeComponent();
        WindowLayout.FitToWorkArea(this);
        _settings = UserSettings.Load();
        CandidatesGrid.ItemsSource = _visibleCandidates;
        DependenciesList.ItemsSource = _dependencyPreview;
        DownloadDirectoryText.Text = _settings.DownloadDirectory;
        _dependencyPreview.Add("选择主包后，下载并校验其依赖。");
        Closing += Window_Closing;
        Closed += (_, _) => _browserWindow?.CloseForShutdown();
    }

    private void Window_Closing(object? sender, CancelEventArgs e)
    {
        if (!_busy) return;
        e.Cancel = true;
        _closeAfterOperation = true;
        if (!_installing) _operationCancellation?.Cancel();
        SetStatus(_installing ? "Windows 正在安装，完成后将关闭窗口。" : "正在取消并清理临时文件...");
    }

    private async void SearchButton_Click(object sender, RoutedEventArgs e) => await RunOperationAsync(SearchAsync);

    private void CancelButton_Click(object sender, RoutedEventArgs e)
    {
        if (!_installing) _operationCancellation?.Cancel();
    }

    private void OpenDownloadFolderButton_Click(object sender, RoutedEventArgs e)
    {
        try { DownloadFolderService.Open(_settings.DownloadDirectory); }
        catch (Exception ex) { ErrorDetailsWindow.ShowError(this, "无法打开下载目录", ex.Message, ex.Message); }
    }

    private void ChooseFolderButton_Click(object sender, RoutedEventArgs e)
    {
        using var dialog = new WinForms.FolderBrowserDialog
        {
            Description = "选择安装包下载位置",
            SelectedPath = Directory.Exists(_settings.DownloadDirectory)
                ? _settings.DownloadDirectory : Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            UseDescriptionForTitle = true,
        };
        if (dialog.ShowDialog() != WinForms.DialogResult.OK || string.IsNullOrWhiteSpace(dialog.SelectedPath)) return;
        try
        {
            var settings = _settings with { DownloadDirectory = dialog.SelectedPath };
            settings.Save();
            _settings = settings;
            _lastPlan = null;
            DownloadDirectoryText.Text = _settings.DownloadDirectory;
            DownloadDirectoryText.ToolTip = _settings.DownloadDirectory;
        }
        catch (Exception ex) { ErrorDetailsWindow.ShowError(this, "无法保存下载位置", ex.Message, ex.Message); }
    }

    private void ArchitectureFilterComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (CandidatesGrid is not null) ApplyCandidateFilter();
    }

    private void CandidatesGrid_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        _lastPlan = null;
        DownloadInstallButton.Content = "下载并安装";
        _dependencyPreview.Clear();
        _dependencyPreview.Add("下载主包后将解析实际应用版本和依赖。");
        UpdateActionButtons();
    }

    private async void DownloadOnlyButton_Click(object sender, RoutedEventArgs e) =>
        await RunOperationAsync(token => DownloadAndOptionallyInstallAsync(false, token));

    private async void DownloadInstallButton_Click(object sender, RoutedEventArgs e) =>
        await RunOperationAsync(token => DownloadAndOptionallyInstallAsync(true, token));

    private async Task SearchAsync(CancellationToken cancellationToken)
    {
        _product = null;
        _allCandidates = [];
        _lastPlan = null;
        _visibleCandidates.Clear();
        _dependencyPreview.Clear();
        ProductNameText.Text = "正在解析...";
        ProductIdText.Text = PackageFamilyText.Text = "-";
        var productId = StoreProductInputParser.Parse(ProductInputTextBox.Text);
        SetStatus("正在读取 Microsoft Store 应用元数据...");
        _product = await MicrosoftStoreMetadataService.ResolveAsync(productId, cancellationToken);
        ProductNameText.Text = _product.DisplayName;
        ProductNameText.ToolTip = _product.DisplayName;
        ProductIdText.Text = _product.ProductId;
        PackageFamilyText.Text = string.Join(", ", _product.PackageFamilyNames);
        PackageFamilyText.ToolTip = PackageFamilyText.Text;
        _allCandidates = await QueryCandidatesAsync(_product, cancellationToken);
        await LoadInstalledPackagesAsync(cancellationToken);
        ApplyCandidateFilter();
        if (_visibleCandidates.Count == 0) throw new InvalidOperationException("没有找到符合当前架构筛选的主应用包。");
        SetStatus($"找到 {_visibleCandidates.Count} 个主包候选。");
    }

    private async Task<IReadOnlyList<StorePackageCandidate>> QueryCandidatesAsync(
        StoreProductMetadata product, CancellationToken cancellationToken)
    {
        await WebView2RuntimeService.EnsureInstalledAsync(this);
        cancellationToken.ThrowIfCancellationRequested();
        _browserWindow ??= new BrowserWindow();
        await _browserWindow.EnsureReadyAsync(this);
        var rows = await _browserWindow.QueryPackagesAsync(product.ProductId,
            links => StorePackageCandidateParser.Parse(links, product.PackageFamilyNames).Any(candidate => candidate.Role == StorePackageRole.Main),
            new Progress<string>(SetStatus), cancellationToken);
        return StorePackageCandidateParser.Parse(rows, product.PackageFamilyNames);
    }

    private async Task LoadInstalledPackagesAsync(CancellationToken cancellationToken)
    {
        _installedPackages.Clear();
        if (_product is null) return;
        var families = new HashSet<string>(_product.PackageFamilyNames, StringComparer.OrdinalIgnoreCase);
        foreach (var name in _allCandidates.Where(candidate => candidate.Role == StorePackageRole.Main)
            .Select(candidate => candidate.IdentityName).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            _installedPackages[name] = (await CodexSystemService.GetInstalledPackagesAsync(name, cancellationToken))
                .Where(package => families.Contains(package.PackageFamilyName))
                .OrderByDescending(package => package.Version).FirstOrDefault();
        }
    }

    private void ApplyCandidateFilter()
    {
        if (_allCandidates.Count == 0) return;
        var filter = GetArchitectureFilter();
        var host = RuntimeInformation.OSArchitecture;
        var rows = _allCandidates.Where(candidate => candidate.Role == StorePackageRole.Main)
            .Where(candidate => filter == "all" ||
                filter == "compatible" && PackageDependencyResolver.IsCompatibleWithHost(candidate.Architecture, host) ||
                candidate.Architecture.Equals(filter, StringComparison.OrdinalIgnoreCase) ||
                candidate.Architecture.Equals("neutral", StringComparison.OrdinalIgnoreCase))
            .OrderBy(candidate => candidate.IsExpired)
            .ThenByDescending(candidate => PackageDependencyResolver.IsCompatibleWithHost(candidate.Architecture, host))
            .ThenByDescending(candidate => candidate.Version)
            .Select(candidate => new CandidateRow(candidate, _installedPackages.GetValueOrDefault(candidate.IdentityName))).ToArray();
        _visibleCandidates.Clear();
        foreach (var row in rows) _visibleCandidates.Add(row);
        CandidatesGrid.SelectedItem = _visibleCandidates.FirstOrDefault();
        UpdateActionButtons();
    }

    private async Task DownloadAndOptionallyInstallAsync(bool install, CancellationToken cancellationToken)
    {
        if (_product is null || CandidatesGrid.SelectedItem is not CandidateRow selected)
            throw new InvalidOperationException("请先搜索并选择主应用包。");
        if (install && (selected.IsDowngrade || !selected.IsHostCompatible))
            throw new InvalidOperationException("所选版本较旧或架构不兼容，仅允许下载。");

        var targetArchitecture = EffectiveTargetArchitecture(selected.Candidate);
        var directory = Path.Combine(_settings.DownloadDirectory, _product.ProductId,
            selected.Candidate.Version.ToString(), targetArchitecture);
        var plan = install && _lastPlan is not null
            ? _lastPlan
            : await PackagePlanService.DownloadAsync(_product, selected.Candidate, _allCandidates, directory,
                targetArchitecture, includeInstalledDependencies: !install,
                async token => _allCandidates = await QueryCandidatesAsync(_product, token),
                new Progress<PackageOperationProgress>(item =>
                {
                    if (!_busy || _operationCancellation?.IsCancellationRequested == true) return;
                    SetStatus(item.Message);
                    if (item.Percentage is { } percentage) Progress.Value = Math.Clamp(percentage, 0, 100);
                }), cancellationToken);
        _lastPlan = plan;
        _dependencyPreview.Clear();
        _dependencyPreview.Add($"应用版本：{plan.MainPackage.Identity.ApplicationVersion} ({plan.MainPackage.Identity.Architecture})");
        foreach (var dependency in plan.Dependencies)
            _dependencyPreview.Add($"{dependency.Identity.Name} {dependency.Identity.ApplicationVersion} ({dependency.Identity.Architecture})");
        if (plan.Dependencies.Count == 0) _dependencyPreview.Add("无需额外下载依赖包。");

        if (!install)
        {
            SetStatus($"下载完成：{directory}");
            return;
        }

        await PackageInstallationService.ValidateForInstallAsync(plan.MainPackage, cancellationToken);
        var license = _product.IsFree == false ? "\n此应用仍需要有效的 Microsoft Store 许可。" : "";
        var message = $"即将安装：{_product.DisplayName}\n应用版本：{plan.MainPackage.Identity.ApplicationVersion}\n依赖：{plan.Dependencies.Count} 个{license}\n\n请保存工作并退出相关应用。";
        if (System.Windows.MessageBox.Show(this, message, "确认安装", MessageBoxButton.YesNo,
            MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
        cancellationToken.ThrowIfCancellationRequested();
        _installing = true;
        UpdateActionButtons();
        CancelButton.IsEnabled = false;
        Progress.IsIndeterminate = true;
        SetStatus("Windows 正在安装...");
        var result = await PackageInstallationService.InstallAsync(plan, cancellationToken);
        if (!result.Succeeded)
        {
            var failure = InstallationFailure.FromOutput(result.ErrorMessage);
            SetStatus(failure.Summary);
            DownloadInstallButton.Content = failure.IsPackageInUse ? "关闭应用后重试" : "重试安装";
            ErrorDetailsWindow.ShowError(this, "安装未完成", failure.Detail, failure.Summary);
            return;
        }

        Progress.IsIndeterminate = false;
        Progress.Value = 100;
        SetStatus("安装完成。");
        await LoadInstalledPackagesAsync(CancellationToken.None);
        ApplyCandidateFilter();
    }

    private string EffectiveTargetArchitecture(StorePackageCandidate candidate)
    {
        // Windows chooses the bundle payload for the host, independently of the table filter.
        if (candidate.Format is StorePackageFormat.AppxBundle or StorePackageFormat.MsixBundle)
            return PackageInstallationService.HostArchitecture;
        if (candidate.Architecture != "neutral") return candidate.Architecture;
        var filter = GetArchitectureFilter();
        return filter is "x64" or "x86" or "arm64" ? filter : PackageInstallationService.HostArchitecture;
    }

    private async Task RunOperationAsync(Func<CancellationToken, Task> operation)
    {
        if (_busy) return;
        using var cancellation = new CancellationTokenSource();
        _operationCancellation = cancellation;
        SetBusy(true);
        Progress.Value = 0;
        try { await operation(cancellation.Token); }
        catch (OperationCanceledException) { SetStatus("操作已取消，临时下载已清理。"); }
        catch (Exception ex)
        {
            SetStatus(ex.Message);
            if (!_closeAfterOperation) ErrorDetailsWindow.ShowError(this, "高级下载安装", ExceptionMessageFormatter.Format(ex), ex.Message);
        }
        finally
        {
            _operationCancellation = null;
            _installing = false;
            Progress.IsIndeterminate = false;
            SetBusy(false);
            if (_closeAfterOperation) Close();
        }
    }

    private void SetBusy(bool busy)
    {
        _busy = busy;
        ProductInputTextBox.IsEnabled = SearchButton.IsEnabled = ChooseFolderButton.IsEnabled = !busy;
        ArchitectureFilterComboBox.IsEnabled = CandidatesGrid.IsEnabled = !busy;
        CancelButton.IsEnabled = busy && !_installing;
        UpdateActionButtons();
    }

    private void UpdateActionButtons()
    {
        var selected = CandidatesGrid?.SelectedItem as CandidateRow;
        DownloadOnlyButton.IsEnabled = !_busy && selected is not null;
        DownloadInstallButton.IsEnabled = !_busy && selected is { IsDowngrade: false, IsHostCompatible: true };
    }

    private string GetArchitectureFilter() => (ArchitectureFilterComboBox.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "compatible";

    private void SetStatus(string message)
    {
        StatusText.Text = message;
        StatusText.ToolTip = message;
    }

    private sealed class CandidateRow(StorePackageCandidate candidate, InstalledStorePackage? installed)
    {
        public StorePackageCandidate Candidate { get; } = candidate;
        public string FileName => Candidate.FileName;
        public string Version => Candidate.Version.ToString();
        public string Architecture => Candidate.Architecture;
        public string Format => Candidate.Format.ToString();
        public string PageHash => Candidate.PageHash ?? "-";
        public string Expiration => Candidate.ExpiresAt?.ToLocalTime().ToString("yyyy-MM-dd HH:mm") ?? "未知";
        public bool IsHostCompatible => PackageDependencyResolver.IsCompatibleWithHost(Candidate.Architecture, RuntimeInformation.OSArchitecture);
        private bool IsBundle => Candidate.Format is StorePackageFormat.MsixBundle or StorePackageFormat.AppxBundle;
        public bool IsDowngrade => !IsBundle && installed is not null && Candidate.Version < installed.Version;
        public string InstallStatus => Candidate.IsExpired ? "下载时刷新链接"
            : !IsHostCompatible ? "与本机不兼容"
            : IsBundle ? "下载后检查应用版本"
            : installed is null ? "未安装"
            : IsDowngrade ? $"已装 {installed.Version}"
            : Candidate.Version == installed.Version ? "版本相同" : $"可更新 {installed.Version}";
    }
}
