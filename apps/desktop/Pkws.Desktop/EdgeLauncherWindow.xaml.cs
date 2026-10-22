using System.Windows;
using System.Windows.Threading;

namespace Pkws.Desktop;

public partial class EdgeLauncherWindow : Window
{
    private const double CollapsedWidth = 6;
    private const double ExpandedWidth = 54;
    private readonly DispatcherTimer _collapseTimer;
    private readonly DispatcherTimer _screenFollowTimer;

    public EdgeLauncherWindow()
    {
        InitializeComponent();
        _collapseTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(450) };
        _collapseTimer.Tick += (_, _) =>
        {
            _collapseTimer.Stop();
            if (!IsMouseOver)
            {
                Collapse();
            }
        };
        _screenFollowTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
        _screenFollowTimer.Tick += (_, _) =>
        {
            if (IsVisible && !IsMouseOver && OpenButton.Visibility != Visibility.Visible)
            {
                WindowPositioner.PlaceLauncherAtCurrentScreenRight(this, CollapsedWidth);
            }
        };
        _screenFollowTimer.Start();
    }

    public event EventHandler? RevealRequested;

    public void ShowCollapsed()
    {
        if (!IsVisible)
        {
            Show();
        }

        Collapse();
    }

    private void Expand()
    {
        _collapseTimer.Stop();
        Handle.Visibility = Visibility.Collapsed;
        OpenButton.Visibility = Visibility.Visible;
        WindowPositioner.PlaceLauncherAtCurrentScreenRight(this, ExpandedWidth);
    }

    private void Collapse()
    {
        OpenButton.Visibility = Visibility.Collapsed;
        Handle.Visibility = Visibility.Visible;
        WindowPositioner.PlaceLauncherAtCurrentScreenRight(this, CollapsedWidth);
    }

    private void Window_MouseEnter(object sender, System.Windows.Input.MouseEventArgs e) => Expand();

    private void Window_MouseLeave(object sender, System.Windows.Input.MouseEventArgs e)
    {
        _collapseTimer.Stop();
        _collapseTimer.Start();
    }

    private void OpenButton_Click(object sender, RoutedEventArgs e)
    {
        Hide();
        RevealRequested?.Invoke(this, EventArgs.Empty);
    }
}
