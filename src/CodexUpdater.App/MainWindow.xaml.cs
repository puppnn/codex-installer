using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using CodexUpdater.Core;
using WinForms = System.Windows.Forms;

namespace CodexUpdater.App;

public partial class MainWindow : Window
{
    private static readonly StoreProductMetadata CodexProduct = new(
        CodexPackage.ProductId, "Codex", "WindowsUpdate",
        [$"{CodexPackage.PackagePrefix}_{CodexPackage.PublisherId}"], ["Windows.Desktop"], "Application", true);
    private readonly AppCommandLine _commandLine;
    private BrowserWindow? _browserWindow;
    private UserSettings _settings;
    private InstalledCodex? _installedCodex;
    private bool _installedReadSucceeded;
    private StorePackageCandidate? _candidate;
    private IReadOnlyList<StorePackageCandidate> _candidates = [];
    private PackageInstallationPlan? _downloadedPlan;
    private CancellationTokenSource? _operation;
    private bool _isInstalling;
    private bool _closeAfterOperation;
    private bool? _wideLayout;
    private bool? _shortLayout;
    private readonly Queue<string> _operationHistory = new();
    private string? _lastRecordedStatus;

    public MainWindow(AppCommandLine commandLine)
    {
        _commandLine = commandLine;
        _settings = UserSettings.Load();
        InitializeComponent();
        WindowLayout.FitToWorkArea(this);
        Loaded += MainWindow_Loaded;
        Closing += MainWindow_Closing;
        LayoutUpdated += MainWindow_LayoutUpdated;
        Closed += (_, _) =>
        {
            LayoutUpdated -= MainWindow_LayoutUpdated;
            _browserWindow?.CloseForShutdown();
        };
        RecordStatus("准备就绪。");
    }

    private void MainViewport_SizeChanged(object sender, SizeChangedEventArgs e)
        => UpdateResponsiveLayout();

    private void MainWindow_LayoutUpdated(object? sender, EventArgs e)
        => UpdateResponsiveLayout();

    private void UpdateResponsiveLayout()
    {
        if (FooterCommands is null || OperationLogText is null) return;
        var wide = MainViewport.ActualWidth >= 760;
        var star = new GridLength(1, GridUnitType.Star);
        if (_wideLayout != wide)
        {
            _wideLayout = wide;
            InformationLeadingColumn.Width = star;
            InformationGapColumn.Width = new GridLength(wide ? 20 : 0);
            InformationTrailingColumn.Width = wide ? star : new GridLength(0);
            Grid.SetColumnSpan(VersionSection, wide ? 1 : 3);
            Grid.SetRow(SettingsSection, wide ? 0 : 1);
            Grid.SetColumn(SettingsSection, wide ? 2 : 0);
            Grid.SetColumnSpan(SettingsSection, wide ? 1 : 3);
            SettingsSection.Margin = new Thickness(0, wide ? 0 : 12, 0, 0);

            CommandsLeadingColumn.Width = wide ? GridLength.Auto : star;
            CommandsGapColumn.Width = new GridLength(wide ? 20 : 0);
            CommandsTrailingColumn.Width = wide ? star : new GridLength(0);
            Grid.SetColumn(PrimaryActions, wide ? 2 : 0);
            Grid.SetColumnSpan(PrimaryActions, wide ? 1 : 3);
            Grid.SetRow(SecondaryActions, wide ? 0 : 1);
            Grid.SetColumnSpan(SecondaryActions, wide ? 1 : 3);
            SecondaryActions.Margin = new Thickness(0, wide ? 0 : 8, 0, 0);
            PrimaryThirdGap.Width = new GridLength(wide ? 10 : 0);
            PrimaryThirdColumn.Width = wide ? star : new GridLength(0);
            Grid.SetRow(InstallButton, wide ? 0 : 1);
            Grid.SetColumn(InstallButton, wide ? 4 : 0);
            Grid.SetColumnSpan(InstallButton, wide ? 1 : 5);
            InstallButton.Margin = new Thickness(0, wide ? 0 : 8, 0, 0);
        }

        var extraHeight = BodyArea.ActualHeight - InformationGrid.DesiredSize.Height;
        var compactHeight = extraHeight < 34;
        if (_shortLayout != compactHeight)
        {
            _shortLayout = compactHeight;
            InformationRow.Height = compactHeight ? star : GridLength.Auto;
            HistoryRow.Height = compactHeight ? GridLength.Auto : star;
            HistorySection.Visibility = compactHeight ? Visibility.Collapsed : Visibility.Visible;
        }
        HistoryHeading.Visibility = extraHeight >= 72 ? Visibility.Visible : Visibility.Collapsed;
    }

    private async void MainWindow_Loaded(object sender, RoutedEventArgs e)
    {
        Arm64RadioButton.IsChecked = _commandLine.Architecture.Equals("arm64", StringComparison.OrdinalIgnoreCase);
        X64RadioButton.IsChecked = !Arm64RadioButton.IsChecked;
        UpdateArchitectureText();
        UpdateDownloadDirectoryText();
        SetStatus("正在读取本机版本...");
        await RunUiActionAsync(RefreshInstalledVersionAsync);
        SetStatus(_installedReadSucceeded
            ? _installedCodex is null ? "本机未安装 Codex。" : $"当前已安装 Codex {_installedCodex.Version}。"
            : "本机版本读取失败，安装前会重新检查。");
        if (!string.IsNullOrWhiteSpace(_commandLine.InstallPath) && !_closeAfterOperation)
            await ShowLocalPackageWindowAsync(_commandLine.InstallPath);
    }

    private void MainWindow_Closing(object? sender, CancelEventArgs e)
    {
        if (_operation is null) return;
        e.Cancel = true;
        _closeAfterOperation = true;
        if (!_isInstalling) _operation.Cancel();
        SetStatus(_isInstalling ? "Windows 正在安装，完成后将关闭窗口。" : "正在取消并清理临时文件...");
    }

    private async void AdvancedOptionsButton_Click(object sender, RoutedEventArgs e)
    {
        if (_operation is not null) return;
        new AdvancedStoreWindow { Owner = this }.ShowDialog();
        _settings = UserSettings.Load();
        UpdateDownloadDirectoryText();
        await RunUiActionAsync(RefreshInstalledVersionAsync);
    }

    private async void LocalPackageButton_Click(object sender, RoutedEventArgs e) => await ShowLocalPackageWindowAsync(null);

    private async Task ShowLocalPackageWindowAsync(string? path)
    {
        if (_operation is not null) return;
        new LocalPackageWindow(path) { Owner = this }.ShowDialog();
        await RunUiActionAsync(RefreshInstalledVersionAsync);
    }

    private void OpenDownloadFolderButton_Click(object sender, RoutedEventArgs e)
    {
        try { DownloadFolderService.Open(_settings.DownloadDirectory); }
        catch (Exception ex) { ErrorDetailsWindow.ShowError(this, "无法打开下载目录", ex.Message, ex.Message); }
    }

    private void ChooseDownloadFolderButton_Click(object sender, RoutedEventArgs e)
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
            _downloadedPlan = null;
            UpdateDownloadDirectoryText();
            UpdateActionButtons();
        }
        catch (Exception ex) { ErrorDetailsWindow.ShowError(this, "无法保存下载位置", ex.Message, ex.Message); }
    }

    private void ArchitectureRadio_Checked(object sender, RoutedEventArgs e)
    {
        if (CandidateText is null || InstallButton is null) return;
        _candidate = null;
        _downloadedPlan = null;
        CandidateText.Text = "还没有检查更新";
        CandidateUrlText.Text = "";
        ComparisonText.Text = "检查更新后显示版本对比。";
        UpdateArchitectureText();
        UpdateActionButtons();
    }

    private async Task RefreshInstalledVersionAsync(CancellationToken cancellationToken)
    {
        try
        {
            _installedCodex = await CodexSystemService.GetInstalledAsync(cancellationToken);
            _installedReadSucceeded = true;
            InstalledVersionText.Text = _installedCodex?.Version.ToString() ?? "未安装";
            InstalledPackageText.Text = _installedCodex?.Architecture ?? "";
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            _installedReadSucceeded = false;
            InstalledVersionText.Text = "读取失败";
            InstalledPackageText.Text = "安装前将重新检查";
            InstalledPackageText.ToolTip = ExceptionMessageFormatter.Format(ex);
        }
        UpdateVersionComparison();
    }

    private async void CheckButton_Click(object sender, RoutedEventArgs e)
    {
        await RunUiActionAsync(async cancellationToken =>
        {
            _candidate = null;
            _downloadedPlan = null;
            CandidateText.Text = "正在查询...";
            CandidateUrlText.Text = "";
            ComparisonText.Text = "正在读取本地和远程版本...";
            var progressWindow = new RgAdguardProgressWindow { Owner = this };
            progressWindow.CancelRequested += (_, _) => _operation?.Cancel();
            progressWindow.Show();
            try
            {
                await RefreshInstalledVersionAsync(cancellationToken);
                _candidates = await QueryCandidatesAsync(cancellationToken);
                _candidate = _candidates.Where(candidate => candidate.Role == StorePackageRole.Main &&
                        candidate.Format == StorePackageFormat.Msix && !candidate.IsExpired &&
                        candidate.Architecture.Equals(GetSelectedArchitecture(), StringComparison.OrdinalIgnoreCase))
                    .OrderByDescending(candidate => candidate.Version).FirstOrDefault()
                    ?? throw new InvalidOperationException($"没有找到 Codex {GetSelectedArchitecture()} MSIX 安装包。");
                CandidateText.Text = _candidate.Version.ToString();
                CandidateUrlText.Text = $"{_candidate.Architecture} / MSIX";
                CandidateText.ToolTip = _candidate.FileName;
                UpdateVersionComparison();
                SetStatus("检查完成。");
            }
            finally { progressWindow.Close(); }
        });
    }

    private async Task<IReadOnlyList<StorePackageCandidate>> QueryCandidatesAsync(CancellationToken cancellationToken)
    {
        await WebView2RuntimeService.EnsureInstalledAsync(this);
        cancellationToken.ThrowIfCancellationRequested();
        _browserWindow ??= new BrowserWindow();
        await _browserWindow.EnsureReadyAsync(this);
        var rows = await _browserWindow.QueryPackagesAsync(CodexProduct.ProductId,
            links => StorePackageCandidateParser.Parse(links, CodexProduct.PackageFamilyNames)
                .Any(candidate => candidate.Role == StorePackageRole.Main && candidate.Architecture == GetSelectedArchitecture()),
            new Progress<string>(SetStatus), cancellationToken);
        return StorePackageCandidateParser.Parse(rows, CodexProduct.PackageFamilyNames);
    }

    private async void DownloadButton_Click(object sender, RoutedEventArgs e)
    {
        if (_candidate is null) return;
        await RunUiActionAsync(async cancellationToken =>
        {
            _downloadedPlan = await PackagePlanService.DownloadAsync(
                CodexProduct, _candidate, _candidates, _settings.DownloadDirectory, GetSelectedArchitecture(),
                includeInstalledDependencies: false, QueryCandidatesAsync,
                new Progress<PackageOperationProgress>(item =>
                {
                    if (_operation is null || _operation.IsCancellationRequested) return;
                    SetStatus(item.Message);
                    if (item.Percentage is { } percentage) Progress.Value = Math.Clamp(percentage, 0, 100);
                }), cancellationToken);
            SetStatus("安装包已下载并通过校验，可以安装。");
        });
    }

    private async void InstallButton_Click(object sender, RoutedEventArgs e)
    {
        if (_downloadedPlan is null) return;
        await RunUiActionAsync(async cancellationToken =>
        {
            var plan = _downloadedPlan;
            await PackageInstallationService.ValidateForInstallAsync(plan.MainPackage, cancellationToken);
            var installed = await CodexSystemService.GetInstalledAsync(cancellationToken);
            var processes = CodexSystemService.FindRunningCodexProcesses(installed?.InstallLocation);
            try
            {
                var message = $"即将安装 Codex {plan.MainPackage.Identity.ApplicationVersion}。";
                if (processes.Count > 0)
                    message += "\n\n检测到应用正在运行。请先保存工作，确认后将尝试关闭应用；8 秒后仍未退出的应用进程会被强制结束。";
                if (System.Windows.MessageBox.Show(this, message, "确认安装", MessageBoxButton.YesNo,
                    MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
                cancellationToken.ThrowIfCancellationRequested();
                if (processes.Count > 0) await CodexSystemService.CloseCodexAsync(processes, cancellationToken);
            }
            finally { foreach (var process in processes) process.Dispose(); }

            cancellationToken.ThrowIfCancellationRequested();
            _isInstalling = true;
            UpdateActionButtons();
            Progress.IsIndeterminate = true;
            SetStatus("Windows 正在安装...");
            var result = await PackageInstallationService.InstallAsync(plan, cancellationToken);
            if (!result.Succeeded)
            {
                var failure = InstallationFailure.FromOutput(result.ErrorMessage);
                SetStatus(failure.Summary);
                ErrorDetailsWindow.ShowError(this, "安装未完成", failure.Detail, failure.Summary);
                return;
            }
            Progress.IsIndeterminate = false;
            Progress.Value = 100;
            await RefreshInstalledVersionAsync(CancellationToken.None);
            SetStatus("安装完成。");
        });
    }

    private void CancelButton_Click(object sender, RoutedEventArgs e)
    {
        if (!_isInstalling) _operation?.Cancel();
    }

    private void UpdateVersionComparison()
    {
        if (_candidate is null) return;
        ComparisonText.Text = !_installedReadSucceeded
            ? "暂时无法读取本地版本，安装前会重新检查。"
            : _installedCodex is null
                ? $"本机未安装，可安装 {_candidate.Version}。"
                : _candidate.Version > _installedCodex.Version
                    ? $"可更新：{_installedCodex.Version} → {_candidate.Version}"
                    : _candidate.Version == _installedCodex.Version
                        ? "已是相同版本，可按需重新安装。"
                        : $"本地 {_installedCodex.Version} 更新，仅允许下载此旧版本。";
    }

    private async Task RunUiActionAsync(Func<CancellationToken, Task> action)
    {
        if (_operation is not null) return;
        using var operation = new CancellationTokenSource();
        _operation = operation;
        Progress.Value = 0;
        UpdateActionButtons();
        try { await action(operation.Token); }
        catch (OperationCanceledException) { SetStatus("操作已取消。"); }
        catch (Exception ex)
        {
            var message = ExceptionMessageFormatter.Format(ex);
            SetStatus(ex.Message);
            if (!_closeAfterOperation) ErrorDetailsWindow.ShowError(this, "操作未完成", message, ex.Message);
        }
        finally
        {
            _operation = null;
            _isInstalling = false;
            Progress.IsIndeterminate = false;
            UpdateActionButtons();
            if (_closeAfterOperation) Close();
        }
    }

    private void UpdateActionButtons()
    {
        if (CheckButton is null || LocalPackageButton is null) return;
        var busy = _operation is not null;
        X64RadioButton.IsEnabled = Arm64RadioButton.IsEnabled = !busy;
        ChooseDownloadFolderButton.IsEnabled = CheckButton.IsEnabled = !busy;
        AdvancedOptionsButton.IsEnabled = LocalPackageButton.IsEnabled = !busy;
        DownloadButton.IsEnabled = !busy && _candidate is not null;
        InstallButton.IsEnabled = !busy && _downloadedPlan is not null &&
            PackageInstallationPolicy.GetBlockReason(_downloadedPlan.MainPackage.Identity,
                RuntimeInformation.OSArchitecture, Environment.OSVersion.Version, _installedCodex?.Version) is null;
        CancelButton.IsEnabled = busy && !_isInstalling;
    }

    private void SetStatus(string message)
    {
        StatusText.Text = message;
        StatusText.ToolTip = message;
        RecordStatus(message);
    }

    private void RecordStatus(string message)
    {
        if (OperationLogText is null || string.IsNullOrWhiteSpace(message) || message == _lastRecordedStatus) return;
        _lastRecordedStatus = message;
        _operationHistory.Enqueue($"{DateTime.Now:HH:mm:ss}  {message}");
        while (_operationHistory.Count > 200) _operationHistory.Dequeue();
        var followLatest = OperationLogText.VerticalOffset >= OperationLogText.ExtentHeight - OperationLogText.ViewportHeight - 2;
        OperationLogText.Text = string.Join(Environment.NewLine, _operationHistory);
        if (followLatest) OperationLogText.ScrollToEnd();
    }

    private void UpdateDownloadDirectoryText()
    {
        DownloadDirectoryText.Text = _settings.DownloadDirectory;
        DownloadDirectoryText.ToolTip = _settings.DownloadDirectory;
    }

    private string GetSelectedArchitecture() => Arm64RadioButton.IsChecked == true ? "arm64" : "x64";

    private void UpdateArchitectureText()
    {
        if (SubtitleText is null || FooterText is null) return;
        SubtitleText.Text = "Microsoft Store 安装包下载与安装";
        FooterText.Text = $"{GetSelectedArchitecture()} · Retail · 当前用户安装";
    }
}
