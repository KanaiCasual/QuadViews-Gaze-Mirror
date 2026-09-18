using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;

namespace GazeOverlay;

/// <summary>
/// A compact HSV colour picker: a saturation/brightness square, a hue strip and a hex box. WPF has none built in, and a
/// home-made one keeps the exe free of third-party dependencies.
/// </summary>
public sealed class ColorPicker : UserControl
{
    private const double SquareWidth = 280, SquareHeight = 150, HueHeight = 16;

    private readonly Rectangle _hueBase = new() { Width = SquareWidth, Height = SquareHeight };
    private readonly Canvas _squareThumb = new() { Width = 0, Height = 0, IsHitTestVisible = false };
    private readonly Canvas _hueThumb = new() { Width = 0, Height = 0, IsHitTestVisible = false };
    private readonly Border _chip = new() { Width = 44, Height = 28, CornerRadius = new CornerRadius(4), BorderThickness = new Thickness(1) };
    private readonly TextBox _hex = new() { Width = 96, MaxLength = 11, VerticalContentAlignment = VerticalAlignment.Center };

    // Hue and saturation are kept separately from the RGB value, so the thumbs do not jump when the colour becomes
    // grey or black (where hue/saturation are undefined).
    private double _hue, _saturation = 1, _value = 1;

    /// <summary>Raised only for changes made by the user, not by <see cref="SetColor"/>.</summary>
    public event Action<byte, byte, byte>? ColorChanged;

    public ColorPicker()
    {
        var line = (Brush)Application.Current.FindResource("Line");
        _chip.BorderBrush = line;

        // Saturation/brightness square: the pure hue, washed out to white towards the left and down to black at the bottom.
        var square = new Grid { Width = SquareWidth, Height = SquareHeight, Background = Brushes.Transparent, Cursor = Cursors.Cross };
        square.Children.Add(_hueBase);
        square.Children.Add(new Rectangle { Fill = new LinearGradientBrush(Colors.White, Color.FromArgb(0, 255, 255, 255), new Point(0, 0), new Point(1, 0)) });
        square.Children.Add(new Rectangle { Fill = new LinearGradientBrush(Color.FromArgb(0, 0, 0, 0), Colors.Black, new Point(0, 0), new Point(0, 1)) });
        var squareOverlay = new Canvas { ClipToBounds = true };
        _squareThumb.Children.Add(Ring(16, Brushes.Black, 1));
        _squareThumb.Children.Add(Ring(14, Brushes.White, 2));
        squareOverlay.Children.Add(_squareThumb);
        square.Children.Add(squareOverlay);
        Drag(square, p =>
        {
            _saturation = Math.Clamp(p.X / SquareWidth, 0, 1);
            _value = 1 - Math.Clamp(p.Y / SquareHeight, 0, 1);
        });

        // Hue strip.
        var hueBrush = new LinearGradientBrush { StartPoint = new Point(0, 0), EndPoint = new Point(1, 0) };
        for (var i = 0; i <= 6; i++)
        {
            var (r, g, b) = FromHsv(i * 60 % 360, 1, 1);
            hueBrush.GradientStops.Add(new GradientStop(Color.FromRgb(r, g, b), i / 6.0));
        }
        var hue = new Grid { Width = SquareWidth, Height = HueHeight, Margin = new Thickness(0, 8, 0, 0), Background = Brushes.Transparent, Cursor = Cursors.SizeWE };
        hue.Children.Add(new Rectangle { Fill = hueBrush, RadiusX = 3, RadiusY = 3 });
        var hueOverlay = new Canvas();
        _hueThumb.Children.Add(new Rectangle { Width = 8, Height = HueHeight + 6, Stroke = Brushes.Black, StrokeThickness = 1, RadiusX = 3, RadiusY = 3, Margin = new Thickness(-4, -3, 0, 0) });
        _hueThumb.Children.Add(new Rectangle { Width = 6, Height = HueHeight + 4, Stroke = Brushes.White, StrokeThickness = 2, RadiusX = 2, RadiusY = 2, Margin = new Thickness(-3, -2, 0, 0) });
        hueOverlay.Children.Add(_hueThumb);
        hue.Children.Add(hueOverlay);
        Drag(hue, p => _hue = Math.Clamp(p.X / SquareWidth, 0, 1) * 360);

        // Hex entry.
        _hex.LostFocus += (_, _) => CommitHex();
        _hex.KeyDown += (_, e) => { if (e.Key == Key.Enter) { CommitHex(); e.Handled = true; } };
        var entry = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 10, 0, 0) };
        entry.Children.Add(_chip);
        entry.Children.Add(new TextBlock { Text = "Hex", Margin = new Thickness(12, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center, Foreground = (Brush)Application.Current.FindResource("Muted") });
        entry.Children.Add(_hex);

        var root = new StackPanel { HorizontalAlignment = HorizontalAlignment.Left };
        root.Children.Add(new Border { BorderBrush = line, BorderThickness = new Thickness(1), Child = square, HorizontalAlignment = HorizontalAlignment.Left });
        root.Children.Add(hue);
        root.Children.Add(entry);
        Content = root;

        Refresh();
    }

    /// <summary>Shows a colour without raising <see cref="ColorChanged"/>.</summary>
    public void SetColor(byte r, byte g, byte b)
    {
        var (h, s, v) = ToHsv(r, g, b);
        if (s > 0.0001 && v > 0.0001) _hue = h;          // Keep the previous hue for greys and black.
        if (v > 0.0001) _saturation = s;                 // Keep the previous saturation for black.
        _value = v;
        Refresh();
    }

    private void Drag(FrameworkElement area, Action<Point> apply)
    {
        void Update(MouseEventArgs e)
        {
            apply(e.GetPosition(area));
            Refresh();
            var (r, g, b) = FromHsv(_hue, _saturation, _value);
            ColorChanged?.Invoke(r, g, b);
        }
        area.MouseLeftButtonDown += (_, e) => { area.CaptureMouse(); Update(e); };
        area.MouseMove += (_, e) => { if (area.IsMouseCaptured) Update(e); };
        area.MouseLeftButtonUp += (_, _) => area.ReleaseMouseCapture();
    }

    private void CommitHex()
    {
        var text = _hex.Text.Trim().TrimStart('#');
        byte r, g, b;
        var parts = text.Split(',');
        if (parts.Length == 3 && byte.TryParse(parts[0].Trim(), out r) && byte.TryParse(parts[1].Trim(), out g) && byte.TryParse(parts[2].Trim(), out b))
        {
            // "r,g,b" is accepted too, since that is how the settings file writes colours.
        }
        else if (text.Length == 6 && int.TryParse(text, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var rgb))
        {
            (r, g, b) = ((byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb);
        }
        else
        {
            Refresh(); // Not a colour: put the current one back.
            return;
        }
        SetColor(r, g, b);
        ColorChanged?.Invoke(r, g, b);
    }

    private void Refresh()
    {
        var (hr, hg, hb) = FromHsv(_hue, 1, 1);
        _hueBase.Fill = new SolidColorBrush(Color.FromRgb(hr, hg, hb));
        Canvas.SetLeft(_squareThumb, _saturation * SquareWidth);
        Canvas.SetTop(_squareThumb, (1 - _value) * SquareHeight);
        Canvas.SetLeft(_hueThumb, _hue / 360 * SquareWidth);

        var (r, g, b) = FromHsv(_hue, _saturation, _value);
        _chip.Background = new SolidColorBrush(Color.FromRgb(r, g, b));
        if (!_hex.IsKeyboardFocused) _hex.Text = $"#{r:X2}{g:X2}{b:X2}";
    }

    private static Ellipse Ring(double size, Brush stroke, double thickness) => new()
    {
        Width = size, Height = size, Stroke = stroke, StrokeThickness = thickness, Margin = new Thickness(-size / 2, -size / 2, 0, 0),
    };

    public static (byte R, byte G, byte B) FromHsv(double hue, double saturation, double value)
    {
        hue = ((hue % 360) + 360) % 360;
        var c = value * saturation;
        var x = c * (1 - Math.Abs(hue / 60 % 2 - 1));
        var m = value - c;
        var (r, g, b) = hue switch
        {
            < 60 => (c, x, 0.0),
            < 120 => (x, c, 0.0),
            < 180 => (0.0, c, x),
            < 240 => (0.0, x, c),
            < 300 => (x, 0.0, c),
            _ => (c, 0.0, x),
        };
        return ((byte)Math.Round((r + m) * 255), (byte)Math.Round((g + m) * 255), (byte)Math.Round((b + m) * 255));
    }

    public static (double Hue, double Saturation, double Value) ToHsv(byte red, byte green, byte blue)
    {
        double r = red / 255.0, g = green / 255.0, b = blue / 255.0;
        var max = Math.Max(r, Math.Max(g, b));
        var min = Math.Min(r, Math.Min(g, b));
        var delta = max - min;
        double hue = 0;
        if (delta > 0)
        {
            hue = max == r ? 60 * (((g - b) / delta % 6 + 6) % 6)
                : max == g ? 60 * ((b - r) / delta + 2)
                           : 60 * ((r - g) / delta + 4);
        }
        return (hue, max <= 0 ? 0 : delta / max, max);
    }
}
