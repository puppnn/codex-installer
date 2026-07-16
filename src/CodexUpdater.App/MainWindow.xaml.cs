using System.IO;
using System.Windows;
using System.Windows.Controls;
using CodexUpdater.Core;
using WinForms = System.Windows.Forms;

namespace CodexUpdater.App;

public partial class MainWindow : Window
{
    private readonly AppCommandLine _commandLine;
    private BrowserWindow? _browserWindow;
    private UserSettings _settings;
    private InstalledCodex? _installedCodex;
    private PackageCandidate? _candidate;
    private string? _downloadedPackagePath;

    public MainWindow(AppCommandLine commandLine)
    {
        _commandLine = commandLine;
        _settings = UserSettings.Load();
        InitializeComponent();
        Loaded += MainWindow_Loaded;
        Closed += MainWindow_Closed;
    }

    private async void MainWindow_Loaded(object sender, RoutedEventArgs e)
    {
        SetSelectedArchitecture(_commandLine.Architecture);
        UpdateArchitectureText();
        UpdateDownloadDirectoryText();
        await RefreshInstalledVersionAsync();

        if (!string.IsNullOrWhiteSpace(_commandLine.InstallPath))
        {
            _downloadedPackagePath = _commandLine.InstallPath;
            InstallButton.IsEnabled = false;
            DownloadButton.IsEnabled = false;
            CheckButton.IsEnabled = false;
            ChooseDownloadFolderButton.IsEnabled = false;
            await InstallDownloadedPackageAsync(_downloadedPackagePath);
        }
    }

    private void MainWindow_Closed(object? sender, EventArgs e)
    {
        _browserWindow?.CloseForShutdown();
    }

    private void AdvancedOptionsButton_Click(object sender, RoutedEventArgs e)
    {
        var window = new AdvancedStoreWindow
        {
            Owner = this,
        };
        window.ShowDialog();

        _settings = UserSettings.Load();
        UpdateDownloadDirectoryText();
    }

    private void ChooseDownloadFolderButton_Click(object sender, RoutedEventArgs e)
    {
        using var dialog = new WinForms.FolderBrowserDialog
        {
            Description = "选择 Codex MSIX 安装包的下载位置",
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
        UpdateDownloadDirectoryText();
        SetStatus($"安装包下载位置已设置为：{_settings.DownloadDirectory}");
    }

    private void ArchitectureRadio_Checked(object sender, RoutedEventArgs e)
    {
        if (CandidateText is null ||
            CandidateUrlText is null ||
            ComparisonText is null ||
            DownloadButton is null ||
            InstallButton is null ||
            SubtitleText is null ||
            FooterText is null)
        {
            return;
        }

        _candidate = null;
        _downloadedPackagePath = null;
        CandidateText.Text = "还没有检查更新";
        CandidateUrlText.Text = "";
        ComparisonText.Text = "检查更新后会在这里显示版本对比。";
        DownloadButton.IsEnabled = false;
        InstallButton.IsEnabled = false;
        UpdateArchitectureText();
    }

    private async Task RefreshInstalledVersionAsync()
    {
        try
        {
            _installedCodex = await CodexSystemService.GetInstalledAsync();
            if (_installedCodex is null)
            {
                InstalledVersionText.Text = "未安装";
                InstalledPackageText.Text = "本机没有检测到 OpenAI.Codex，检查更新后可直接全新安装。";
                return;
            }

            InstalledVersionText.Text = $"{_installedCodex.Version} - {_installedCodex.Architecture}";
            InstalledPackageText.Text = _installedCodex.PackageFullName;
        }
        catch (Exception ex)
        {
            InstalledVersionText.Text = "读取失败";
            InstalledPackageText.Text = ExceptionMessageFormatter.Format(ex);
        }
    }

    private async void CheckButton_Click(object sender, RoutedEventArgs e)
    {
        await RunUiActionAsync(async () =>
        {
            var progressWindow = new RgAdguardProgressWindow
            {
                Owner = this,
            };
            progressWindow.Show();
            try
            {
                _candidate = null;
                _downloadedPackagePath = null;
                InstallButton.IsEnabled = false;
                DownloadButton.IsEnabled = false;
                Progress.Value = 0;
                CandidateText.Text = "正在生成临时链接...";
                CandidateUrlText.Text = "";
                ComparisonText.Text = "正在等待远程版本信息...";

                var browserWindow = await GetBrowserWindowAsync();
                await browserWindow.EnsureReadyAsync(this);
                browserWindow.HideAfterSuccess();
                var candidate = await GenerateCandidateFromBrowserAsync();
                browserWindow.HideAfterSuccess();

                _candidate = candidate;
                CandidateText.Text = candidate.FileName;
                CandidateUrlText.Text = $"远程版本 {candidate.Version} - {candidate.Architecture}";
                UpdateVersionComparison(candidate);
                DownloadButton.IsEnabled = true;
            }
            finally
            {
                progressWindow.Close();
            }
        });
    }

    private async Task<PackageCandidate> GenerateCandidateFromBrowserAsync()
    {
        var submitted = false;
        var unresolvedAttempts = 0;
        var deadline = DateTimeOffset.UtcNow.AddMinutes(4);
        var targetArchitecture = GetSelectedArchitecture();

        while (DateTimeOffset.UtcNow < deadline)
        {
            var browserWindow = await GetBrowserWindowAsync();
            var links = await browserWindow.ExtractLinksAsync();
            var candidate = CodexPackage.SelectNewest(links, targetArchitecture);
            if (candidate is not null)
            {
                return candidate;
            }
            unresolvedAttempts++;

            if (!submitted)
            {
                var state = await browserWindow.FillAndSubmitRgAdguardAsync(CodexPackage.ProductId);
                if (state == "submitted")
                {
                    submitted = true;
                    unresolvedAttempts = 0;
                    SetStatus("已提交 ProductId，正在等待 rg-adguard 返回 MSIX 链接...");
                }
                else if (state == "challenge")
                {
                    browserWindow.ShowForAttention("请在这里完成验证");
                    SetStatus("rg-adguard 正在显示验证页。请在链接浏览器弹窗中完成验证，工具会继续等待。");
                }
                else
                {
                    SetStatus("正在等待 rg-adguard 页面加载...");
                    if (unresolvedAttempts >= 2)
                    {
                        browserWindow.ShowForAttention("请检查页面并手动完成可能出现的验证");
                    }
                }
            }
            else
            {
                SetStatus($"正在扫描结果中的 OpenAI.Codex {targetArchitecture} MSIX 链接...");
                if (unresolvedAttempts >= 2)
                {
                    browserWindow.ShowForAttention("请检查页面并手动完成可能出现的验证");
                }
            }

            await Task.Delay(1800);
        }

        throw new TimeoutException($"没有在 rg-adguard 页面中找到 OpenAI.Codex {targetArchitecture} MSIX 链接。");
    }

    private async Task<BrowserWindow> GetBrowserWindowAsync()
    {
        await WebView2RuntimeService.EnsureInstalledAsync(this);
        _browserWindow ??= new BrowserWindow();
        return _browserWindow;
    }

    private async void DownloadButton_Click(object sender, RoutedEventArgs e)
    {
        if (_candidate is null) return;

        await RunUiActionAsync(async () =>
        {
            SetStatus("正在下载 MSIX...");
            Progress.Value = 0;

            var progress = new Progress<double>(value => Progress.Value = Math.Clamp(value, 0, 100));
            var result = await PackageDownloadService.DownloadAsync(
                _candidate,
                _settings.DownloadDirectory,
                GetSelectedArchitecture(),
                progress);
            _downloadedPackagePath = result.FilePath;

            SetStatus(result.ReusedExistingFile
                ? $"检测到完全相同且验证有效的安装包，已跳过下载：{_downloadedPackagePath}"
                : $"下载完成：{_downloadedPackagePath}");
            InstallButton.IsEnabled = true;
        });
    }

    private async void InstallButton_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(_downloadedPackagePath)) return;
        await InstallDownloadedPackageAsync(_downloadedPackagePath);
    }

    private async Task InstallDownloadedPackageAsync(string packagePath)
    {
        await RunUiActionAsync(async () =>
        {
            var targetArchitecture = GetSelectedArchitecture();
            using var packageLock = OpenAndValidatePackageBeforeInstall(packagePath, targetArchitecture);

            var runningCodex = CodexSystemService.FindRunningCodexProcesses();
            if (runningCodex.Count > 0)
            {
                var answer = System.Windows.MessageBox.Show(
                    $"检测到 Codex 正在运行，共 {runningCodex.Count} 个进程。\n\n请先保存当前工作。点击“是”后将关闭 Codex 并继续安装。",
                    "需要关闭 Codex",
                    MessageBoxButton.YesNo,
                    MessageBoxImage.Warning);

                if (answer != MessageBoxResult.Yes)
                {
                    SetStatus("已取消安装。");
                    return;
                }

                SetStatus("正在关闭 Codex...");
                await CodexSystemService.CloseCodexAsync(runningCodex);
            }

            Progress.Value = 0;
            SetStatus("正在安装 MSIX...");
            var result = await CodexSystemService.InstallPackageAsync(packagePath);

            if (!result.Succeeded)
            {
                var message = string.IsNullOrWhiteSpace(result.StandardError)
                    ? result.StandardOutput
                    : result.StandardError;

                SetStatus("安装失败。请关闭 Codex 后重试。");
                System.Windows.MessageBox.Show(message, "Add-AppxPackage 失败", MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }

            Progress.Value = 100;
            await RefreshInstalledVersionAsync();
            SetStatus("安装完成。");
            System.Windows.MessageBox.Show("Codex 更新完成。", "完成", MessageBoxButton.OK, MessageBoxImage.Information);
        });
    }

    private void UpdateVersionComparison(PackageCandidate candidate)
    {
        if (_installedCodex is null)
        {
            ComparisonText.Text = $"本机未安装 Codex。将安装远程版本 {candidate.Version}。";
            SetStatus($"检测到远程版本 {candidate.Version}，可执行全新安装。");
            return;
        }

        var selectedArchitecture = GetSelectedArchitecture();
        var installedArchitecture = _installedCodex.Architecture;
        var architectureNote = installedArchitecture.Equals(selectedArchitecture, StringComparison.OrdinalIgnoreCase)
            ? ""
            : $" 本机当前架构为 {installedArchitecture}，当前选择 {selectedArchitecture}，请确认是否符合你的电脑。";

        var comparison = candidate.Version.CompareTo(_installedCodex.Version);
        if (comparison > 0)
        {
            ComparisonText.Text = $"本地版本 {_installedCodex.Version}，远程版本 {candidate.Version}，可以更新。{architectureNote}";
            SetStatus($"找到新版本 {candidate.Version}，当前本地版本为 {_installedCodex.Version}。");
        }
        else if (comparison == 0)
        {
            ComparisonText.Text = $"本地版本和远程版本都是 {candidate.Version}，可按需重新安装。{architectureNote}";
            SetStatus("远程版本与本地版本相同。");
        }
        else
        {
            ComparisonText.Text = $"本地版本 {_installedCodex.Version} 高于远程版本 {candidate.Version}，不建议安装这个包。{architectureNote}";
            SetStatus("远程版本低于本地版本，请谨慎安装。");
        }
    }

    private static FileStream OpenAndValidatePackageBeforeInstall(
        string packagePath,
        string targetArchitecture)
    {
        var fileName = Path.GetFileName(packagePath);
        if (!CodexPackage.TryParse(fileName, packagePath, targetArchitecture, out var candidate))
        {
            throw new InvalidOperationException($"安装包名称不是 OpenAI.Codex {targetArchitecture} MSIX。");
        }

        var file = new FileInfo(packagePath);
        if (!file.Exists || file.Length == 0)
        {
            throw new FileNotFoundException("安装包不存在或为空。", packagePath);
        }

        var packageLock = new FileStream(
            packagePath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read);
        try
        {
            WindowsTrustVerifier.EnsureValidSignature(packagePath);
            MsixPackageInspector.ValidateCodexPackage(packagePath, candidate, targetArchitecture);
            return packageLock;
        }
        catch
        {
            packageLock.Dispose();
            throw;
        }
    }

    private async Task RunUiActionAsync(Func<Task> action)
    {
        SetBusy(true);
        try
        {
            await action();
        }
        catch (Exception ex)
        {
            var message = ExceptionMessageFormatter.Format(ex);
            SetStatus(message);
            System.Windows.MessageBox.Show(message, "Codex 更新器", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            SetBusy(false);
        }
    }

    private void SetBusy(bool busy)
    {
        X64RadioButton.IsEnabled = !busy;
        Arm64RadioButton.IsEnabled = !busy;
        ChooseDownloadFolderButton.IsEnabled = !busy;
        CheckButton.IsEnabled = !busy;
        DownloadButton.IsEnabled = !busy && _candidate is not null;
        InstallButton.IsEnabled = !busy && !string.IsNullOrWhiteSpace(_downloadedPackagePath);
    }

    private void SetStatus(string message)
    {
        StatusText.Text = message;
    }

    private void UpdateDownloadDirectoryText()
    {
        DownloadDirectoryText.Text = _settings.DownloadDirectory;
    }

    private void SetSelectedArchitecture(string architecture)
    {
        var normalized = architecture.Equals("arm64", StringComparison.OrdinalIgnoreCase)
            ? "arm64"
            : "x64";

        if (normalized == "arm64")
        {
            Arm64RadioButton.IsChecked = true;
            return;
        }

        X64RadioButton.IsChecked = true;
    }

    private string GetSelectedArchitecture()
    {
        return Arm64RadioButton.IsChecked == true ? "arm64" : CodexPackage.DefaultArchitecture;
    }

    private void UpdateArchitectureText()
    {
        if (SubtitleText is null || FooterText is null)
        {
            return;
        }

        var architecture = GetSelectedArchitecture();
        SubtitleText.Text = $"绕过 Microsoft Store，下载并安装 {architecture} MSIX";
        FooterText.Text = $"ProductId: {CodexPackage.ProductId} - {architecture} - 仅 MSIX";
    }
}
