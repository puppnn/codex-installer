using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows;
using CodexUpdater.Core;
using OpenFileDialog = Microsoft.Win32.OpenFileDialog;

namespace CodexUpdater.App;

public partial class LocalPackageWindow : Window
{
    private readonly string? _initialPath;
    private readonly ObservableCollection<string> _dependencyRows = [];
    private readonly List<DownloadedStorePackage> _localDependencies = [];
    private DownloadedStorePackage? _mainPackage;
    private LocalDependencySelection? _dependencies;
    private CancellationTokenSource? _operation;
    private bool _canInstall;
    private bool _installing;
    private bool _closeAfterOperation;

    public LocalPackageWindow(string? initialPath = null)
    {
        _initialPath = initialPath;
        InitializeComponent();
        DependenciesList.ItemsSource = _dependencyRows;
        WindowLayout.FitToWorkArea(this);
        Loaded += Window_Loaded;
        Closing += Window_Closing;
    }

    private async void Window_Loaded(object sender, RoutedEventArgs e)
    {
        if (_initialPath is not null) await RunOperationAsync(token => LoadPackageAsync(_initialPath, token));
        else await ChoosePackageAsync();
    }

    private void Window_Closing(object? sender, CancelEventArgs e)
    {
        if (_operation is null) return;
        e.Cancel = true;
        _closeAfterOperation = true;
        if (!_installing) _operation.Cancel();
        StatusText.Text = _installing ? "Windows 正在安装，完成后将关闭窗口。" : "正在取消校验...";
    }

    private static OpenFileDialog CreateFileDialog(bool multiple) => new()
    {
        Title = multiple ? "选择本地依赖包" : "选择本地安装包",
        Filter = "Windows 安装包|*.msix;*.appx;*.msixbundle;*.appxbundle",
        Multiselect = multiple,
        CheckFileExists = true,
        CheckPathExists = true,
    };

    private async void SelectPackageButton_Click(object sender, RoutedEventArgs e) => await ChoosePackageAsync();

    private async Task ChoosePackageAsync()
    {
        if (_operation is not null) return;
        var dialog = CreateFileDialog(false);
        if (dialog.ShowDialog(this) == true) await RunOperationAsync(token => LoadPackageAsync(dialog.FileName, token));
    }

    private async Task LoadPackageAsync(string path, CancellationToken cancellationToken)
    {
        _mainPackage = null;
        _canInstall = false;
        _dependencies = null;
        _localDependencies.Clear();
        _dependencyRows.Clear();
        PackagePathText.Text = path;
        PackageNameText.Text = "正在校验...";
        PackageIdentityText.Text = "";
        PublisherText.Text = "";
        StatusText.Text = "正在验证数字签名和包身份...";
        _mainPackage = await PackageInstallationService.InspectLocalAsync(path, cancellationToken);
        var identity = _mainPackage.Identity;
        PackageNameText.Text = identity.Name;
        PackageIdentityText.Text = $"应用版本：{identity.ApplicationVersion}    架构：{identity.Architecture}\n包族：{_mainPackage.PackageFamilyName}";
        PublisherText.Text = identity.Publisher;
        await RefreshDependenciesAsync(cancellationToken);
    }

    private async void AddDependenciesButton_Click(object sender, RoutedEventArgs e)
    {
        var dialog = CreateFileDialog(true);
        if (dialog.ShowDialog(this) != true) return;
        await RunOperationAsync(async token =>
        {
            _canInstall = false;
            foreach (var path in dialog.FileNames)
            {
                StatusText.Text = "正在校验本地依赖包...";
                var dependency = await PackageInstallationService.InspectLocalAsync(path, token);
                if (!dependency.Identity.IsFramework || dependency.Identity.IsResourcePackage)
                    throw new InvalidOperationException($"{dependency.Candidate.FileName} 不是框架依赖包。");
                _localDependencies.RemoveAll(item => item.FilePath.Equals(dependency.FilePath, StringComparison.OrdinalIgnoreCase));
                _localDependencies.Add(dependency);
            }
            await RefreshDependenciesAsync(token);
        });
    }

    private async void ClearDependenciesButton_Click(object sender, RoutedEventArgs e)
    {
        await RunOperationAsync(async token =>
        {
            _localDependencies.Clear();
            await RefreshDependenciesAsync(token);
        });
    }

    private async Task RefreshDependenciesAsync(CancellationToken cancellationToken)
    {
        _canInstall = false;
        if (_mainPackage is null) return;
        await PackageInstallationService.ValidateForInstallAsync(_mainPackage, cancellationToken);
        _dependencies = await PackageInstallationService.ResolveLocalDependenciesAsync(_mainPackage, _localDependencies, cancellationToken);
        _dependencyRows.Clear();
        foreach (var dependency in _dependencies.Packages)
            _dependencyRows.Add($"已校验：{dependency.Identity.Name} {dependency.Identity.ApplicationVersion} ({dependency.Identity.Architecture})");
        foreach (var missing in _dependencies.Missing) _dependencyRows.Add("缺少：" + missing);
        if (_dependencyRows.Count == 0) _dependencyRows.Add("无需额外依赖，或必需依赖已在本机安装。");
        _canInstall = _dependencies.Missing.Count == 0;
        StatusText.Text = _canInstall ? "签名和兼容性检查通过。" : "请添加上方缺少的本地依赖包后再安装。";
    }

    private async void InstallButton_Click(object sender, RoutedEventArgs e)
    {
        await RunOperationAsync(async token =>
        {
            await RefreshDependenciesAsync(token);
            if (!_canInstall || _mainPackage is null || _dependencies is null) return;
            var identity = _mainPackage.Identity;
            var message = $"即将为当前 Windows 用户安装：\n{identity.Name}\n应用版本：{identity.ApplicationVersion}\n发布者：{identity.Publisher}\n\n文件：{_mainPackage.FilePath}\n\n请保存工作并退出要更新的应用。";
            if (System.Windows.MessageBox.Show(this, message, "确认本地安装", MessageBoxButton.YesNo,
                MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
            token.ThrowIfCancellationRequested();
            _installing = true;
            UpdateButtons();
            StatusText.Text = "Windows 正在安装...";
            var product = new StoreProductMetadata("", identity.Name, "Local", [_mainPackage.PackageFamilyName], ["Windows.Desktop"], "Application", null);
            var plan = new PackageInstallationPlan(product, _mainPackage, _dependencies.Packages);
            var result = await PackageInstallationService.InstallAsync(plan, token);
            if (!result.Succeeded)
            {
                var failure = InstallationFailure.FromOutput(result.ErrorMessage);
                StatusText.Text = failure.Summary;
                InstallButton.Content = failure.IsPackageInUse ? "关闭应用后重试" : "重试安装";
                ErrorDetailsWindow.ShowError(this, "安装未完成", failure.Detail, failure.Summary);
                return;
            }
            StatusText.Text = "安装完成。";
            InstallButton.Content = "安装所选包";
        });
    }

    private void CancelOperationButton_Click(object sender, RoutedEventArgs e)
    {
        if (!_installing) _operation?.Cancel();
    }

    private async Task RunOperationAsync(Func<CancellationToken, Task> action)
    {
        if (_operation is not null) return;
        using var operation = new CancellationTokenSource();
        _operation = operation;
        Progress.IsIndeterminate = true;
        UpdateButtons();
        try { await action(operation.Token); }
        catch (OperationCanceledException) { StatusText.Text = "操作已取消。"; }
        catch (Exception ex)
        {
            StatusText.Text = ex.Message;
            if (!_closeAfterOperation) ErrorDetailsWindow.ShowError(this, "本地安装未完成", ExceptionMessageFormatter.Format(ex), ex.Message);
        }
        finally
        {
            _operation = null;
            _installing = false;
            Progress.IsIndeterminate = false;
            UpdateButtons();
            if (_closeAfterOperation) Close();
        }
    }

    private void UpdateButtons()
    {
        var busy = _operation is not null;
        SelectPackageButton.IsEnabled = !busy;
        AddDependenciesButton.IsEnabled = ClearDependenciesButton.IsEnabled = !busy && _mainPackage is not null;
        InstallButton.IsEnabled = !busy && _canInstall;
        CancelOperationButton.IsEnabled = busy && !_installing;
    }
}
