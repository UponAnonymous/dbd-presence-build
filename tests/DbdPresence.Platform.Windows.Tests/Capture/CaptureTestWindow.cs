using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;

namespace DbdPresence.Platform.Windows.Tests.Capture;

/// <summary>
/// Our own WPF window used as the capture target in C3 and C4. It is borderless and fixed in size so its pixels are known:
/// four colour quadrants, or large black text on white. The tests never capture any other window, and never the game.
/// </summary>
internal sealed class CaptureTestWindow : IDisposable
{
    public static readonly Color TopLeft = Color.FromRgb(255, 0, 0);
    public static readonly Color TopRight = Color.FromRgb(0, 255, 0);
    public static readonly Color BottomLeft = Color.FromRgb(0, 0, 255);
    public static readonly Color BottomRight = Color.FromRgb(255, 255, 255);

    private readonly Thread _thread;
    private Dispatcher? _dispatcher;
    private Window? _window;
    private Border? _topLeftCell;
    private Border? _bottomRightCell;
    private Border? _textBackground;
    private int _repaints;
    private int _redrawTicks;
    private bool _redrawing;

    private CaptureTestWindow(Func<UIElement> content, double width, double height, bool framed = false)
    {
        var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _thread = new Thread(() =>
        {
            try
            {
                _dispatcher = Dispatcher.CurrentDispatcher;
                _window = new Window
                {
                    WindowStyle = framed ? WindowStyle.SingleBorderWindow : WindowStyle.None,
                    ResizeMode = ResizeMode.NoResize,
                    ShowInTaskbar = false,
                    Topmost = true,
                    AllowsTransparency = false,
                    WindowStartupLocation = WindowStartupLocation.Manual,
                    Left = 40,
                    Top = 40,
                    Width = width,
                    Height = height,
                    Background = Brushes.Black,
                    Content = content(),
                };
                _window.ContentRendered += (_, _) => ready.TrySetResult();
                _window.Show();
                Dispatcher.Run();
            }
#pragma warning disable CA1031 // The failure is handed to the test thread that waits on 'ready', which rethrows it.
            catch (Exception ex)
#pragma warning restore CA1031
            {
                ready.TrySetException(ex);
            }
        })
        {
            IsBackground = true,
            Name = "capture-test-window",
        };
        _thread.SetApartmentState(ApartmentState.STA);
        _thread.Start();
        ready.Task.GetAwaiter().GetResult();
    }

    /// <summary>The window's own process id (the capture source finds the window by it).</summary>
    public static int ProcessId => Environment.ProcessId;

    public static CaptureTestWindow ShowQuadrants(double width = 800, double height = 600)
    {
        // The content is built on the window thread before the constructor returns, so the cell is handed over afterwards.
        Border? topLeft = null;
        Border? bottomRight = null;
        var created = new CaptureTestWindow(() => Quadrants((tl, br) => (topLeft, bottomRight) = (tl, br)), width, height);
        created._topLeftCell = topLeft;
        created._bottomRightCell = bottomRight;
        return created;
    }

    /// <summary>
    /// The same four quadrants inside an ordinary window with a title bar and a border (the outer size is <paramref name="width"/> by <paramref name="height"/>),
    /// like a windowed game. The client area is the quadrants only.
    /// </summary>
    public static CaptureTestWindow ShowFramedQuadrants(double width = 800, double height = 600)
    {
        Border? topLeft = null;
        Border? bottomRight = null;
        var created = new CaptureTestWindow(() => Quadrants((tl, br) => (topLeft, bottomRight) = (tl, br)), width, height, framed: true);
        created._topLeftCell = topLeft;
        created._bottomRightCell = bottomRight;
        return created;
    }

    public static CaptureTestWindow ShowText(string text, double width = 900, double height = 300, double fontSize = 72)
    {
        Border? background = null;
        var created = new CaptureTestWindow(
            () =>
            {
                background = new Border
                {
                    Background = Brushes.White,
                    Child = new TextBlock
                    {
                        Text = text,
                        FontFamily = new FontFamily("Arial"),
                        FontSize = fontSize,
                        Foreground = Brushes.Black,
                        HorizontalAlignment = HorizontalAlignment.Center,
                        VerticalAlignment = VerticalAlignment.Center,
                    },
                };
                return background;
            },
            width,
            height);
        created._textBackground = background;
        return created;
    }

    public void Resize(double width, double height) =>
        _dispatcher!.Invoke(() =>
        {
            _window!.Width = width;
            _window.Height = height;
        });

    /// <summary>
    /// Makes the window present a new frame, as a running game does all the time: the top-left cell alternates between red 255 and
    /// 254, well inside the colour tolerance of the tests. Windows delivers capture frames only when the window presents, so a test of
    /// a static window would otherwise wait for a frame that never comes. The text window alternates its white background between 255 and
    /// 254 in the red channel, which black text on white recognition does not notice.
    /// </summary>
    public void Repaint() =>
        _dispatcher!.Invoke(() =>
        {
            _repaints++;
            var even = _repaints % 2 == 0;
            if (_topLeftCell is not null)
            {
                _topLeftCell.Background = new SolidColorBrush(Color.FromRgb((byte)(even ? 255 : 254), 0, 0));
            }
            else if (_textBackground is not null)
            {
                _textBackground.Background = new SolidColorBrush(Color.FromRgb((byte)(even ? 255 : 254), 255, 255));
            }
        });

    /// <summary>
    /// Makes the window present a frame on every display refresh until it is disposed, as a running game does (measurements only).
    /// The bottom-right cell alternates between white 255 and 254, which no test reads closely.
    /// </summary>
    public void StartContinuousRedraw() =>
        _dispatcher!.Invoke(() =>
        {
            if (!_redrawing && _bottomRightCell is not null)
            {
                _redrawing = true;
                CompositionTarget.Rendering += OnRendering;
            }
        });

    /// <summary>Stops the redraw started by <see cref="StartContinuousRedraw"/>.</summary>
    public void StopContinuousRedraw() =>
        _dispatcher!.Invoke(() =>
        {
            if (_redrawing)
            {
                _redrawing = false;
                CompositionTarget.Rendering -= OnRendering;
            }
        });

    /// <summary>
    /// Switches the top-left cell between red (marker off) and yellow (marker on), a change far larger than any colour tolerance,
    /// so a captured frame shows unambiguously which state it was drawn in. Returns once the change is applied on the window thread.
    /// </summary>
    public void SetMarker(bool on) =>
        _dispatcher!.Invoke(() =>
        {
            if (_topLeftCell is not null)
            {
                _topLeftCell.Background = new SolidColorBrush(on ? Color.FromRgb(255, 255, 0) : TopLeft);
            }
        });

    public void Dispose()
    {
        _dispatcher?.Invoke(() =>
        {
            if (_redrawing)
            {
                _redrawing = false;
                CompositionTarget.Rendering -= OnRendering;
            }
        });
        _dispatcher?.Invoke(() => _window?.Close());
        _dispatcher?.InvokeShutdown();
        _thread.Join(TimeSpan.FromSeconds(10));
    }

    private void OnRendering(object? sender, EventArgs e)
    {
        _redrawTicks++;
        _bottomRightCell!.Background = new SolidColorBrush(Color.FromRgb((byte)(_redrawTicks % 2 == 0 ? 255 : 254), 255, 255));
    }

    private static Grid Quadrants(Action<Border, Border> cellsCreated)
    {
        var grid = new Grid();
        grid.RowDefinitions.Add(new RowDefinition());
        grid.RowDefinitions.Add(new RowDefinition());
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        var topLeft = Add(grid, TopLeft, 0, 0);
        Add(grid, TopRight, 0, 1);
        Add(grid, BottomLeft, 1, 0);
        var bottomRight = Add(grid, BottomRight, 1, 1);
        cellsCreated(topLeft, bottomRight);
        return grid;
    }

    private static Border Add(Grid grid, Color color, int row, int column)
    {
        var cell = new Border { Background = new SolidColorBrush(color) };
        Grid.SetRow(cell, row);
        Grid.SetColumn(cell, column);
        grid.Children.Add(cell);
        return cell;
    }
}
