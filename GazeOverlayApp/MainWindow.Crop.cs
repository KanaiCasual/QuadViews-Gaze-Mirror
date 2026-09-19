using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;

namespace GazeOverlay;

/// <summary>
/// The Crop tab: a box, locked to a chosen shape, that is dragged and scaled on a picture of the whole mirror image. The
/// mirror layer then hands OBS only that box, so the OBS source IS the box. The values live in the ring's settings file
/// (crop_* keys) and go live through the same signal as everything else there.
/// </summary>
public partial class MainWindow
{
    private readonly CropBox _crop = new();
    private MirrorPicture? _cropPicture;
    private int _cropFullWidth, _cropFullHeight;

    private readonly Rectangle[] _cropShades = [new(), new(), new(), new()];
    private readonly Rectangle _cropFrame = new() { StrokeThickness = 2, Fill = Brushes.Transparent };
    private readonly Rectangle[] _cropHandles = [new(), new(), new(), new()];
    private readonly Line[] _cropThirds = [new(), new(), new(), new()];
    private readonly Image _cropImage = new() { Stretch = Stretch.Fill };
    private readonly TextBlock _cropPlaceholder = new() { TextWrapping = TextWrapping.Wrap, TextAlignment = TextAlignment.Center };
    private Rect _cropShown;                 // Where the picture is drawn inside the canvas.
    private enum CropDrag { None, Move, Resize }
    private CropDrag _cropDrag;
    private Point _cropAnchor;               // Resize: the corner that stays put (fractions). Move: pointer offset from the centre.

    private static string LastPicturePath => System.IO.Path.Combine(AppSettings.Folder, "last-mirror-picture.png");

    private void BuildCropUi()
    {
        var accent = (Brush)FindResource("Accent");
        _cropFrame.Stroke = accent;
        CropCanvas.Children.Add(_cropImage);
        _cropPlaceholder.Foreground = (Brush)FindResource("Muted");
        CropCanvas.Children.Add(_cropPlaceholder);
        foreach (var shade in _cropShades)
        {
            shade.Fill = new SolidColorBrush(Color.FromArgb(150, 0, 0, 0));
            shade.IsHitTestVisible = false;
            CropCanvas.Children.Add(shade);
        }
        foreach (var line in _cropThirds)
        {
            line.Stroke = new SolidColorBrush(Color.FromArgb(70, 255, 255, 255));
            line.StrokeThickness = 1;
            line.IsHitTestVisible = false;
            CropCanvas.Children.Add(line);
        }
        _cropFrame.IsHitTestVisible = false;
        CropCanvas.Children.Add(_cropFrame);
        foreach (var handle in _cropHandles)
        {
            handle.Width = handle.Height = 10;
            handle.Fill = accent;
            handle.IsHitTestVisible = false;
            CropCanvas.Children.Add(handle);
        }

        foreach (var choice in Settings.All.First(d => d.Key == "crop_aspect").Choices)
        {
            CropAspect.Items.Add(new ComboBoxItem { Content = choice.Label, Tag = choice.Value });
        }

        // The crop keys are ordinary settings: loading the file, and changes made outside the app, arrive through these.
        _controlSetters["crop_enabled"] = v => { CropEnabled.IsChecked = v != "0"; DrawCrop(); };
        _controlSetters["crop_aspect"] = v =>
        {
            CropAspect.SelectedItem = CropAspect.Items.Cast<ComboBoxItem>().FirstOrDefault(i => (string)i.Tag == v) ?? CropAspect.Items[0];
            _crop.Aspect = CropBox.ParseAspect(v);
            DrawCrop();
        };
        _controlSetters["crop_center_x"] = v => { _crop.CenterX = Settings.ParseDouble(v, 0.5); DrawCrop(); };
        _controlSetters["crop_center_y"] = v => { _crop.CenterY = Settings.ParseDouble(v, 0.5); DrawCrop(); };
        _controlSetters["crop_height"] = v => { _crop.Height = Settings.ParseDouble(v, 0.5); DrawCrop(); };
        _controlSetters["crop_width"] = v => { _crop.FreeWidth = Settings.ParseDouble(v, 1); DrawCrop(); };

        CropEnabled.Click += (_, _) => { OnValueChanged("crop_enabled", CropEnabled.IsChecked == true ? "1" : "0"); DrawCrop(); };
        CropAspect.SelectionChanged += (_, _) =>
        {
            if (_loading || CropAspect.SelectedItem is not ComboBoxItem { Tag: string value }) return;
            // Going from a locked shape to "free" keeps the box as it is.
            if (value == "0") _crop.FreeWidth = _crop.Size().Width;
            _crop.Aspect = CropBox.ParseAspect(value);
            _crop.Normalize();
            OnValueChanged("crop_aspect", value);
            CommitCrop();
        };

        CropCanvas.SizeChanged += (_, _) => DrawCrop();
        CropCanvas.MouseLeftButtonDown += OnCropMouseDown;
        CropCanvas.MouseMove += OnCropMouseMove;
        CropCanvas.MouseLeftButtonUp += (_, _) => { _cropDrag = CropDrag.None; CropCanvas.ReleaseMouseCapture(); };
        CropCanvas.MouseWheel += (_, e) => { _crop.Scale(e.Delta > 0 ? 1.04 : 1 / 1.04); CommitCrop(); e.Handled = true; };

        LoadLastCropPicture();
        Tabs.SelectionChanged += async (_, e) =>
        {
            // Opening the tab asks the running game for a fresh picture once - an event, not a timer.
            if (ReferenceEquals(e.OriginalSource, Tabs) && ReferenceEquals(Tabs.SelectedItem, CropTab)) await RefreshCropPictureAsync(quietWhenNoGame: true);
        };
        // Coming back to the app with this tab open (from the game, from OBS): a fresh picture too. Still an event.
        Activated += async (_, _) =>
        {
            if (ReferenceEquals(Tabs.SelectedItem, CropTab) && CropRefresh.IsEnabled) await RefreshCropPictureAsync(quietWhenNoGame: true);
        };
    }

    private void LoadLastCropPicture()
    {
        try
        {
            if (!File.Exists(LastPicturePath) || _appSettings.MirrorWidth < 1 || _appSettings.MirrorHeight < 1) return;
            var image = new BitmapImage();
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad; // Do not keep the file open: it is replaced by the next picture.
            image.UriSource = new Uri(LastPicturePath);
            image.EndInit();
            image.Freeze();
            _cropImage.Source = image;
            _cropFullWidth = _appSettings.MirrorWidth;
            _cropFullHeight = _appSettings.MirrorHeight;
            _crop.ImageAspect = (double)_cropFullWidth / _cropFullHeight;
            CropStatus.Text = "Showing the last picture that was taken. Refresh picture gets a new one from the running game.";
        }
        catch
        {
            // No picture is fine: the box still works on an empty frame.
        }
    }

    private async Task RefreshCropPictureAsync(bool quietWhenNoGame)
    {
        CropRefresh.IsEnabled = false;
        var (picture, message) = await MirrorSnapshot.RequestAsync();
        CropRefresh.IsEnabled = true;
        if (picture == null)
        {
            if (!quietWhenNoGame || _cropImage.Source == null) CropStatus.Text = message + (_cropImage.Source == null ? " The box can still be set; it just has no picture behind it." : "");
            return;
        }

        _cropPicture = picture;
        _cropImage.Source = picture.Image;
        _cropFullWidth = picture.FullWidth;
        _cropFullHeight = picture.FullHeight;
        _crop.ImageAspect = (double)picture.FullWidth / picture.FullHeight;
        _crop.Normalize();
        CropStatus.Text = $"Picture taken at {DateTime.Now:T}.";
        DrawCrop();

        try
        {
            Directory.CreateDirectory(AppSettings.Folder);
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(picture.Image));
            using (var file = File.Create(LastPicturePath)) encoder.Save(file);
            _appSettings.MirrorWidth = picture.FullWidth;
            _appSettings.MirrorHeight = picture.FullHeight;
            _appSettings.Save();
        }
        catch
        {
            // Not being able to keep the picture for next time is not worth bothering anyone about.
        }
    }

    /// <summary>Development aid for --screenshots: a made-up picture, so the tab can be looked at without a game.</summary>
    internal void ShowCropTestPicture()
    {
        const int size = 320;
        var pixels = new byte[size * size * 4];
        for (var y = 0; y < size; y++)
        {
            for (var x = 0; x < size; x++)
            {
                var i = (y * size + x) * 4;
                var sky = y < size * 0.55;
                var lens = Math.Sqrt(Math.Pow(x - size / 2.0, 2) + Math.Pow(y - size / 2.0, 2)) < size * 0.48;
                pixels[i] = (byte)(lens ? (sky ? 200 - y / 3 : 60) : 8);
                pixels[i + 1] = (byte)(lens ? (sky ? 150 - y / 4 : 90 + (x + y) % 40) : 8);
                pixels[i + 2] = (byte)(lens ? (sky ? 90 : 70) : 8);
            }
        }
        var image = BitmapSource.Create(size, size, 96, 96, PixelFormats.Bgr32, null, pixels, size * 4);
        image.Freeze();
        _cropImage.Source = image;
        _cropFullWidth = _cropFullHeight = 8192;
        _crop.ImageAspect = 1;
        CropStatus.Text = "(made-up picture for the screenshot)";
        DrawCrop();
    }

    private async void OnCropRefresh(object sender, RoutedEventArgs e) => await RefreshCropPictureAsync(quietWhenNoGame: false);

    private void OnCropCentre(object sender, RoutedEventArgs e)
    {
        _crop.MoveTo(0.5, 0.5);
        CommitCrop();
    }

    private void OnCropFill(object sender, RoutedEventArgs e)
    {
        _crop.Height = 1;
        _crop.FreeWidth = 1;
        _crop.MoveTo(0.5, 0.5);
        CommitCrop();
    }

    /// <summary>Writes the box to the settings (saved and sent to the game by the usual debounced save) and redraws it.</summary>
    private void CommitCrop()
    {
        var c = CultureInfo.InvariantCulture;
        _crop.Normalize();
        OnValueChanged("crop_center_x", _crop.CenterX.ToString("0.####", c));
        OnValueChanged("crop_center_y", _crop.CenterY.ToString("0.####", c));
        OnValueChanged("crop_height", _crop.Height.ToString("0.####", c));
        if (_crop.Aspect <= 0) OnValueChanged("crop_width", _crop.FreeWidth.ToString("0.####", c));
        DrawCrop();
    }

    // ------------------------------------------------------------------ drawing

    private void DrawCrop()
    {
        if (CropCanvas.ActualWidth < 4 || CropCanvas.ActualHeight < 4) return;

        // The picture, as large as fits.
        var aspect = _crop.ImageAspect;
        var width = Math.Min(CropCanvas.ActualWidth, CropCanvas.ActualHeight * aspect);
        var height = width / aspect;
        _cropShown = new Rect((CropCanvas.ActualWidth - width) / 2, (CropCanvas.ActualHeight - height) / 2, width, height);
        Place(_cropImage, _cropShown);
        _cropPlaceholder.Visibility = _cropImage.Source == null ? Visibility.Visible : Visibility.Collapsed;
        _cropPlaceholder.Text = "No picture yet.\nStart your game with OBS showing the OpenXR Mirror Capture source, then press \"Refresh picture\".";
        _cropPlaceholder.Width = Math.Max(width - 40, 60);
        Canvas.SetLeft(_cropPlaceholder, _cropShown.Left + 20);
        Canvas.SetTop(_cropPlaceholder, _cropShown.Top + height / 2 - 30);

        var (left, top, boxWidth, boxHeight) = _crop.Rect();
        var box = new Rect(_cropShown.Left + left * width, _cropShown.Top + top * height, boxWidth * width, boxHeight * height);
        var active = CropEnabled.IsChecked == true;

        Place(_cropShades[0], new Rect(_cropShown.Left, _cropShown.Top, width, Math.Max(box.Top - _cropShown.Top, 0)));
        Place(_cropShades[1], new Rect(_cropShown.Left, box.Bottom, width, Math.Max(_cropShown.Bottom - box.Bottom, 0)));
        Place(_cropShades[2], new Rect(_cropShown.Left, box.Top, Math.Max(box.Left - _cropShown.Left, 0), box.Height));
        Place(_cropShades[3], new Rect(box.Right, box.Top, Math.Max(_cropShown.Right - box.Right, 0), box.Height));
        foreach (var shade in _cropShades) shade.Opacity = active ? 1 : 0.35;

        Place(_cropFrame, box);
        _cropFrame.StrokeDashArray = active ? null : [4, 3];
        var corners = new[] { box.TopLeft, box.TopRight, box.BottomLeft, box.BottomRight };
        for (var i = 0; i < 4; i++)
        {
            Canvas.SetLeft(_cropHandles[i], corners[i].X - 5);
            Canvas.SetTop(_cropHandles[i], corners[i].Y - 5);
        }
        for (var i = 0; i < 2; i++)
        {
            var x = box.Left + box.Width * (i + 1) / 3;
            (_cropThirds[i].X1, _cropThirds[i].Y1, _cropThirds[i].X2, _cropThirds[i].Y2) = (x, box.Top, x, box.Bottom);
            var y = box.Top + box.Height * (i + 1) / 3;
            (_cropThirds[i + 2].X1, _cropThirds[i + 2].Y1, _cropThirds[i + 2].X2, _cropThirds[i + 2].Y2) = (box.Left, y, box.Right, y);
        }

        // What OBS will get, in pixels - worked out the way the layer does (even sizes).
        if (_cropFullWidth > 0 && _cropFullHeight > 0)
        {
            var pixelWidth = (int)(boxWidth * _cropFullWidth) & ~1;
            var pixelHeight = (int)(boxHeight * _cropFullHeight) & ~1;
            var share = 100.0 * pixelWidth * pixelHeight / ((double)_cropFullWidth * _cropFullHeight);
            CropReadout.Text = active
                ? $"OBS gets {pixelWidth} x {pixelHeight} px - {share:0} % of the {_cropFullWidth} x {_cropFullHeight} mirror image."
                : $"Crop is off: OBS gets the whole {_cropFullWidth} x {_cropFullHeight} image. The box would be {pixelWidth} x {pixelHeight} px.";
        }
        else
        {
            CropReadout.Text = active ? "Crop is on." : "Crop is off: OBS gets the whole mirror image.";
        }
    }

    private static void Place(FrameworkElement element, Rect rect)
    {
        Canvas.SetLeft(element, rect.Left);
        Canvas.SetTop(element, rect.Top);
        element.Width = Math.Max(rect.Width, 0);
        element.Height = Math.Max(rect.Height, 0);
    }

    // ------------------------------------------------------------------ dragging

    private Point ToFraction(Point canvasPoint) => new(
        Math.Clamp((canvasPoint.X - _cropShown.Left) / Math.Max(_cropShown.Width, 1), 0, 1),
        Math.Clamp((canvasPoint.Y - _cropShown.Top) / Math.Max(_cropShown.Height, 1), 0, 1));

    private void OnCropMouseDown(object sender, MouseButtonEventArgs e)
    {
        var pointer = e.GetPosition(CropCanvas);
        var (left, top, width, height) = _crop.Rect();
        var corners = new[] { new Point(left, top), new Point(left + width, top), new Point(left, top + height), new Point(left + width, top + height) };
        var fraction = ToFraction(pointer);

        // A corner (within 14 px): resize, with the opposite corner as the anchor.
        for (var i = 0; i < 4; i++)
        {
            var corner = new Point(_cropShown.Left + corners[i].X * _cropShown.Width, _cropShown.Top + corners[i].Y * _cropShown.Height);
            if ((corner - pointer).Length > 14) continue;
            _cropDrag = CropDrag.Resize;
            _cropAnchor = corners[3 - i];
            CropCanvas.CaptureMouse();
            return;
        }

        // Inside the box: move it. Outside: bring the box there first, then move.
        var inside = fraction.X >= left && fraction.X <= left + width && fraction.Y >= top && fraction.Y <= top + height;
        if (!inside)
        {
            _crop.MoveTo(fraction.X, fraction.Y);
            CommitCrop();
        }
        _cropDrag = CropDrag.Move;
        _cropAnchor = new Point(fraction.X - _crop.CenterX, fraction.Y - _crop.CenterY);
        CropCanvas.CaptureMouse();
    }

    private void OnCropMouseMove(object sender, MouseEventArgs e)
    {
        var pointer = e.GetPosition(CropCanvas);
        if (_cropDrag == CropDrag.None)
        {
            // Show what a click here would do.
            var (left, top, width, height) = _crop.Rect();
            var corners = new[] { new Point(left, top), new Point(left + width, top), new Point(left, top + height), new Point(left + width, top + height) };
            var cursor = Cursors.SizeAll;
            for (var i = 0; i < 4; i++)
            {
                var corner = new Point(_cropShown.Left + corners[i].X * _cropShown.Width, _cropShown.Top + corners[i].Y * _cropShown.Height);
                if ((corner - pointer).Length <= 14) cursor = i is 0 or 3 ? Cursors.SizeNWSE : Cursors.SizeNESW;
            }
            CropCanvas.Cursor = cursor;
            return;
        }

        var fraction = ToFraction(pointer);
        if (_cropDrag == CropDrag.Move) _crop.MoveTo(fraction.X - _cropAnchor.X, fraction.Y - _cropAnchor.Y);
        else _crop.ResizeFromCorner(_cropAnchor.X, _cropAnchor.Y, fraction.X, fraction.Y);
        CommitCrop();
    }
}
