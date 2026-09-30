using System.Windows;

namespace CodexUpdater.App;

public partial class RgAdguardProgressWindow : Window
{
    private bool _ownerEventsAttached;
    public event EventHandler? CancelRequested;

    private void CancelButton_Click(object sender, RoutedEventArgs e)
    {
        CancelRequested?.Invoke(this, EventArgs.Empty);
    }

    public RgAdguardProgressWindow()
    {
        InitializeComponent();
    }

    protected override void OnContentRendered(EventArgs e)
    {
        base.OnContentRendered(e);
        AttachOwnerEvents();
        CenterOnOwner();
    }

    protected override void OnClosed(EventArgs e)
    {
        DetachOwnerEvents();
        base.OnClosed(e);
    }

    private void AttachOwnerEvents()
    {
        if (_ownerEventsAttached || Owner is null) return;

        Owner.LocationChanged += Owner_PositionChanged;
        Owner.SizeChanged += Owner_PositionChanged;
        _ownerEventsAttached = true;
    }

    private void DetachOwnerEvents()
    {
        if (!_ownerEventsAttached || Owner is null) return;

        Owner.LocationChanged -= Owner_PositionChanged;
        Owner.SizeChanged -= Owner_PositionChanged;
        _ownerEventsAttached = false;
    }

    private void Owner_PositionChanged(object? sender, EventArgs e)
    {
        CenterOnOwner();
    }

    private void CenterOnOwner()
    {
        if (Owner is null) return;

        Left = Owner.Left + Math.Max(0, (Owner.ActualWidth - ActualWidth) / 2);
        Top = Owner.Top + Math.Max(0, (Owner.ActualHeight - ActualHeight) / 2);
    }
}
