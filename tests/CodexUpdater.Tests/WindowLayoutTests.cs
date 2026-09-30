using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using CodexUpdater.App;

namespace CodexUpdater.Tests;

public sealed class WindowLayoutTests
{
    [Fact]
    public void MainAndLocalWindows_KeepPrimaryActionsVisible()
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            CodexUpdater.App.App? app = null;
            try
            {
                app = new CodexUpdater.App.App { ShutdownMode = ShutdownMode.OnExplicitShutdown };
                app.InitializeComponent();
                var main = new MainWindow(AppCommandLine.Parse([]));
                ((TextBlock)main.FindName("InstalledVersionText")).Text = "26.915.4065.0";
                ((TextBlock)main.FindName("InstalledPackageText")).Text = "x64";
                ((TextBlock)main.FindName("CandidateText")).Text = "26.928.1915.0";
                ((TextBlock)main.FindName("CandidateUrlText")).Text = "x64 / MSIX";
                ((TextBlock)main.FindName("DownloadDirectoryText")).Text = @"C:\Packages\Codex";
                var mainActions = new[] { "CheckButton", "DownloadButton", "InstallButton", "LocalPackageButton", "AdvancedOptionsButton", "CancelButton", "FooterText", "StatusText" };
                Render(main, 560, 600, "main", [.. mainActions, "OpenDownloadFolderButton"]);
                AssertMainReflow(main, wide: false, historyVisible: true);
                Render(main, 420, 420, "main-short", mainActions);
                AssertMainReflow(main, wide: false, historyVisible: false);
                Render(main, 900, 600, "main-wide", [.. mainActions, "OpenDownloadFolderButton"]);
                AssertMainReflow(main, wide: true, historyVisible: true);
                Render(main, 900, 360, "main-wide-short", mainActions);
                AssertMainReflow(main, wide: true, historyVisible: false);
                Render(main, 800, 460, "main-breakpoint", mainActions);
                AssertMainReflow(main, wide: true, historyVisible: true);
                Render(main, 1200, 780, "main-expanded", [.. mainActions, "OpenDownloadFolderButton"]);
                AssertMainReflow(main, wide: true, historyVisible: true);
                ((TextBlock)main.FindName("DownloadDirectoryText")).Text = @"C:\VeryLongDirectoryName\Packages\Downloads\WindowsApplications\Codex\InstallationPackages";
                ((TextBlock)main.FindName("ComparisonText")).Text = "本地版本高于远程安装包版本，仅允许下载；安装前会再次检查实际应用版本、系统版本和依赖。";
                Render(main, 900, 600, "main-long-content", [.. mainActions, "OpenDownloadFolderButton"]);
                AssertMainReflow(main, wide: true, historyVisible: true);
                ((TextBlock)main.FindName("DownloadDirectoryText")).Text = @"C:\Packages\Codex";
                ((TextBlock)main.FindName("ComparisonText")).Text = "检查更新后显示版本对比。";
                Render(main, 480, 560, "main-compact", [.. mainActions, "OpenDownloadFolderButton"]);
                AssertMainReflow(main, wide: false, historyVisible: false);
                Assert.Equal("26.928.1915.0", ((TextBlock)main.FindName("CandidateText")).Text);
                Assert.True(((RadioButton)main.FindName("X64RadioButton")).IsChecked);
                main.Close();
                var local = new LocalPackageWindow();
                Render(local, 720, 580, "local", ["SelectPackageButton", "InstallButton", "CancelOperationButton"]);
                local.Close();
                var advanced = new AdvancedStoreWindow();
                Render(advanced, 1000, 660, "advanced", ["SearchButton", "DownloadOnlyButton", "DownloadInstallButton", "OpenDownloadFolderButton"]);
                advanced.Close();
            }
            catch (Exception ex) { failure = ex; }
            finally { app?.Shutdown(); }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(30)), "WPF layout verification timed out.");
        Assert.Null(failure);
    }

    private static void AssertMainReflow(MainWindow window, bool wide, bool historyVisible)
    {
        var root = (FrameworkElement)window.Content;
        Assert.Equal(wide ? 2 : 0, Grid.GetColumn((UIElement)window.FindName("SettingsSection")));
        Assert.Equal(wide ? 0 : 1, Grid.GetRow((UIElement)window.FindName("InstallButton")));
        Assert.Equal(historyVisible ? Visibility.Visible : Visibility.Collapsed,
            ((UIElement)window.FindName("HistorySection")).Visibility);

        var names = new[] { "CheckButton", "DownloadButton", "InstallButton", "LocalPackageButton", "AdvancedOptionsButton" };
        var bounds = names.Select(name =>
        {
            var element = (FrameworkElement)window.FindName(name);
            return element.TransformToAncestor(root).TransformBounds(new Rect(element.RenderSize));
        }).ToArray();
        for (var i = 0; i < bounds.Length; i++)
            for (var j = i + 1; j < bounds.Length; j++)
            {
                var overlap = Rect.Intersect(bounds[i], bounds[j]);
                Assert.True(overlap.IsEmpty || overlap.Width <= 0 || overlap.Height <= 0,
                    $"Actions overlap after resize: {names[i]} / {names[j]}");
            }

        if (historyVisible)
        {
            var history = (FrameworkElement)window.FindName("OperationLogText");
            Assert.True(history.ActualHeight >= 20, $"History has no usable height: {history.ActualHeight}");
        }
    }

    private static void Render(Window window, double width, double height, string name, string[] buttonNames)
    {
        var root = (FrameworkElement)window.Content;
        root.Measure(new System.Windows.Size(width, height));
        root.Arrange(new Rect(0, 0, width, height));
        root.UpdateLayout();
        foreach (var buttonName in buttonNames)
        {
            var button = (FrameworkElement)window.FindName(buttonName);
            var bounds = button.TransformToAncestor(root).TransformBounds(new Rect(button.RenderSize));
            Assert.True(bounds.Width > 0 && bounds.Height > 0 && bounds.Left >= 0 && bounds.Top >= 0 &&
                bounds.Right <= root.ActualWidth + 1 && bounds.Bottom <= root.ActualHeight + 1,
                $"{name}/{buttonName}: {bounds}; root {root.ActualWidth}x{root.ActualHeight}");
        }

        var artifactDirectory = Environment.GetEnvironmentVariable("CODEX_UI_ARTIFACTS");
        if (string.IsNullOrWhiteSpace(artifactDirectory)) return;
        Directory.CreateDirectory(artifactDirectory);
        var bitmap = new RenderTargetBitmap((int)width, (int)height, 96, 96, PixelFormats.Pbgra32);
        var background = new DrawingVisual();
        using (var drawing = background.RenderOpen())
            drawing.DrawRectangle(window.Background, null, new Rect(0, 0, width, height));
        bitmap.Render(background);
        bitmap.Render(root);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = File.Create(Path.Combine(artifactDirectory, name + ".png"));
        encoder.Save(stream);
    }
}
