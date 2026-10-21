using System.Windows.Controls;

namespace CodexUpdater.App;

public partial class DownloadRouteSelector : System.Windows.Controls.UserControl
{
    private bool _initializing;
    public event EventHandler? RouteChanged;
    internal DownloadRoute SelectedRoute => (DownloadRoute)Math.Max(0, RouteBox.SelectedIndex);

    public DownloadRouteSelector()
    {
        InitializeComponent();
        SetRoute(DownloadRoute.Auto);
    }

    internal void SetRoute(DownloadRoute route)
    {
        _initializing = true;
        RouteBox.SelectedIndex = (int)route;
        _initializing = false;
    }

    private void RouteBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_initializing) RouteChanged?.Invoke(this, EventArgs.Empty);
    }
}
