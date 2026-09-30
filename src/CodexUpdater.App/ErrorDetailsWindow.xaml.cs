using System.Windows;
using CodexUpdater.Core;

namespace CodexUpdater.App;

public partial class ErrorDetailsWindow : Window
{
    public ErrorDetailsWindow()
    {
        InitializeComponent();
        Height = Math.Min(Height, SystemParameters.WorkArea.Height - 32);
        Width = Math.Min(Width, SystemParameters.WorkArea.Width - 32);
    }

    internal static void ShowError(Window owner, string title, string detail, string? summary = null)
    {
        if (!owner.IsLoaded) return;
        var failure = InstallationFailure.FromOutput(detail);
        var window = new ErrorDetailsWindow { Owner = owner, Title = title };
        window.SummaryText.Text = summary ?? failure.Summary;
        window.DetailsText.Text = failure.Detail;
        window.ShowDialog();
    }

    private void CopyButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            System.Windows.Clipboard.SetText(SummaryText.Text + Environment.NewLine + DetailsText.Text);
        }
        catch (System.Runtime.InteropServices.ExternalException)
        {
            System.Windows.MessageBox.Show(this, "剪贴板正被其他程序占用，请稍后重试。", "无法复制");
        }
    }
}
