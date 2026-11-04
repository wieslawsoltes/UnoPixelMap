using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using Windows.Foundation;

namespace PixelMap.App;

/// <summary>A captured, independent touch stick with a radial dead zone.</summary>
public sealed class GameStick : UserControl
{
    private readonly TranslateTransform _offset = new();
    private uint? _pointer;
    public Point Value { get; private set; }
    public event EventHandler? ValueChanged;

    public GameStick()
    {
        Width = Height = 112;
        IsTabStop = false;
        var root = new Grid { Background = new SolidColorBrush(Colors.Transparent) };
        root.Children.Add(new Ellipse
        {
            Width = 106, Height = 106,
            Fill = new SolidColorBrush(ColorHelper.FromArgb(220, 20, 47, 50)),
            Stroke = new SolidColorBrush(ColorHelper.FromArgb(230, 137, 185, 161)), StrokeThickness = 1
        });
        root.Children.Add(new Ellipse
        {
            Width = 62, Height = 62,
            Stroke = new SolidColorBrush(ColorHelper.FromArgb(100, 137, 185, 161)), StrokeThickness = 1
        });
        root.Children.Add(new Ellipse
        {
            Width = 42, Height = 42, RenderTransform = _offset,
            Fill = new SolidColorBrush(ColorHelper.FromArgb(245, 171, 218, 181)),
            Stroke = new SolidColorBrush(ColorHelper.FromArgb(255, 236, 242, 212)), StrokeThickness = 2
        });
        Content = root;
        PointerPressed += Pressed;
        PointerMoved += Moved;
        PointerReleased += Released;
        PointerCanceled += Released;
        PointerCaptureLost += Released;
        Unloaded += (_, _) => Reset();
        AutomationProperties.SetName(this, "Touch movement stick");
    }

    private void Pressed(object sender, PointerRoutedEventArgs e)
    {
        if (_pointer.HasValue || !CapturePointer(e.Pointer)) return;
        _pointer = e.Pointer.PointerId;
        Update(e.GetCurrentPoint(this).Position);
        e.Handled = true;
    }

    private void Moved(object sender, PointerRoutedEventArgs e)
    {
        if (_pointer != e.Pointer.PointerId) return;
        Update(e.GetCurrentPoint(this).Position);
        e.Handled = true;
    }

    private void Released(object sender, PointerRoutedEventArgs e)
    {
        if (_pointer != e.Pointer.PointerId) return;
        _pointer = null;
        ReleasePointerCapture(e.Pointer);
        SetValue(default);
        e.Handled = true;
    }

    private void Update(Point point)
    {
        double x = (point.X - ActualWidth * .5) / 35;
        double y = (point.Y - ActualHeight * .5) / 35;
        double length = Math.Sqrt(x * x + y * y);
        if (length <= .12) { SetValue(default); return; }
        double magnitude = (Math.Min(1, length) - .12) / .88;
        SetValue(new Point(x / length * magnitude, y / length * magnitude));
    }

    private void SetValue(Point value)
    {
        if (Value.Equals(value)) return;
        Value = value;
        _offset.X = value.X * 35;
        _offset.Y = value.Y * 35;
        ValueChanged?.Invoke(this, EventArgs.Empty);
    }

    public void Reset()
    {
        _pointer = null;
        ReleasePointerCaptures();
        SetValue(default);
    }
}
