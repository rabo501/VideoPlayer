using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;

namespace VideoPlayer
{
    internal sealed class MotionRegionWindow : Window
    {
        private readonly Image _image = new() { Stretch = Stretch.Uniform };
        private readonly Canvas _canvas = new() { Background = Brushes.Transparent };
        private readonly Border _frame = new() { BorderBrush = new SolidColorBrush(Color.FromRgb(65, 75, 92)), BorderThickness = new Thickness(1), Background = new SolidColorBrush(Color.FromRgb(12, 15, 20)) };
        private readonly Slider _sensitivity = MakeSlider(1, 100, 22);
        private readonly Slider _area = MakeSlider(0.5, 10, 1.5);
        private readonly Slider _duration = MakeSlider(0.5, 5, 1);
        private readonly TextBlock _selectionText = new() { Foreground = Brushes.LightGray, Margin = new Thickness(0, 6, 0, 0) };
        private readonly Rectangle _rectangle = new() { Stroke = Brushes.Lime, StrokeThickness = 2, Fill = new SolidColorBrush(Color.FromArgb(45, 50, 220, 100)), Visibility = Visibility.Collapsed };
        private Point _start;
        private bool _drawing;
        private Rect _imageBounds;
        private MotionDetectionService.MotionRegion _region = MotionDetectionService.MotionRegion.FullFrame;

        public MotionDetectionService.MotionRegion Region => _region;
        public double PixelThreshold => _sensitivity.Value;
        public double ChangedAreaPercent => _area.Value;
        public double MinimumDuration => _duration.Value;

        public MotionRegionWindow(string imagePath)
        {
            Title = "Ustawienia wykrywania ruchu";
            Width = 980; Height = 760; MinWidth = 760; MinHeight = 600;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
            Background = new SolidColorBrush(Color.FromRgb(17, 19, 24));
            Foreground = Brushes.White;
            var root = new DockPanel { Margin = new Thickness(14) };
            var settings = new StackPanel { Orientation = Orientation.Vertical, Margin = new Thickness(0, 12, 0, 0) };
            DockPanel.SetDock(settings, Dock.Bottom);
            settings.Children.Add(MakeSetting("Czułość — próg różnicy jasności", _sensitivity, v => $"{v:0}/255 (niższa wartość = większa czułość)"));
            settings.Children.Add(MakeSetting("Minimalny obszar zmiany", _area, v => $"{v:0.0}% analizowanego obszaru"));
            settings.Children.Add(MakeSetting("Minimalny czas zdarzenia", _duration, v => $"{v:0.0} s"));
            settings.Children.Add(_selectionText);
            var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 10, 0, 0) };
            var full = new Button { Content = "Cały obraz", Padding = new Thickness(12, 7, 12, 7), Margin = new Thickness(0, 0, 8, 0) };
            full.Click += (_, __) => { _region = MotionDetectionService.MotionRegion.FullFrame; _rectangle.Visibility = Visibility.Collapsed; UpdateSelectionText(); };
            var cancel = new Button { Content = "Anuluj", Padding = new Thickness(14, 7, 14, 7), Margin = new Thickness(0, 0, 8, 0), MinWidth = 90 };
            cancel.Click += (_, __) => { DialogResult = false; };
            var ok = new Button { Content = "Rozpocznij analizę", Padding = new Thickness(14, 7, 14, 7), MinWidth = 140, Background = new SolidColorBrush(Color.FromRgb(45, 110, 220)), Foreground = Brushes.White };
            ok.Click += (_, __) => { DialogResult = true; };
            buttons.Children.Add(full); buttons.Children.Add(cancel); buttons.Children.Add(ok);
            settings.Children.Add(buttons);
            DockPanel.SetDock(buttons, Dock.Bottom);
            var hint = new TextBlock { Text = "Przeciągnij myszą po obrazie, aby zaznaczyć obszar detekcji. Bez zaznaczenia analizowany jest cały obraz. Analiza odrzuca pojedyncze zakłócenia i potwierdza ruch na co najmniej dwóch kolejnych próbkach.", Foreground = Brushes.LightGray, Margin = new Thickness(0, 0, 0, 8) };
            DockPanel.SetDock(hint, Dock.Top);
            root.Children.Add(settings);
            root.Children.Add(hint);
            _frame.Child = new Grid { Children = { _image, _canvas } };
            _canvas.Children.Add(_rectangle);
            root.Children.Add(_frame);
            Content = root;
            var bitmap = new BitmapImage();
            bitmap.BeginInit();
            bitmap.CacheOption = BitmapCacheOption.OnLoad;
            bitmap.UriSource = new Uri(imagePath);
            bitmap.EndInit();
            bitmap.Freeze();
            _image.Source = bitmap;
            _canvas.SizeChanged += (_, __) => RedrawRectangle();
            _canvas.MouseLeftButtonDown += CanvasDown;
            _canvas.MouseMove += CanvasMove;
            _canvas.MouseLeftButtonUp += CanvasUp;
            UpdateSelectionText();
        }

        private static Slider MakeSlider(double min, double max, double value) => new() { Minimum = min, Maximum = max, Value = value, TickFrequency = 1, IsSnapToTickEnabled = false, Width = 260, VerticalAlignment = VerticalAlignment.Center };
        private static UIElement MakeSetting(string title, Slider slider, Func<double, string> format)
        {
            var panel = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 3, 0, 3) };
            panel.Children.Add(new TextBlock { Text = title, Width = 230, VerticalAlignment = VerticalAlignment.Center, Foreground = Brushes.White });
            panel.Children.Add(slider);
            var value = new TextBlock { Width = 260, Margin = new Thickness(12, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center, Foreground = Brushes.LightGray };
            void Update(object? s, RoutedPropertyChangedEventArgs<double> e) => value.Text = format(e.NewValue);
            slider.ValueChanged += Update;
            value.Text = format(slider.Value);
            panel.Children.Add(value);
            return panel;
        }
        private void GetImageBounds()
        {
            if (_image.Source is not BitmapSource bitmap || _canvas.ActualWidth <= 0 || _canvas.ActualHeight <= 0) { _imageBounds = Rect.Empty; return; }
            double scale = Math.Min(_canvas.ActualWidth / bitmap.PixelWidth, _canvas.ActualHeight / bitmap.PixelHeight);
            double w = bitmap.PixelWidth * scale, h = bitmap.PixelHeight * scale;
            _imageBounds = new Rect((_canvas.ActualWidth - w) / 2, (_canvas.ActualHeight - h) / 2, w, h);
        }
        private void CanvasDown(object sender, MouseButtonEventArgs e)
        {
            GetImageBounds(); Point p = e.GetPosition(_canvas);
            if (_imageBounds.IsEmpty || !_imageBounds.Contains(p)) return;
            _drawing = true; _start = p; _rectangle.Visibility = Visibility.Visible;
            Canvas.SetLeft(_rectangle, p.X); Canvas.SetTop(_rectangle, p.Y);
            _rectangle.Width = 0; _rectangle.Height = 0;
            _canvas.CaptureMouse(); e.Handled = true;
        }
        private void CanvasMove(object sender, MouseEventArgs e)
        {
            if (!_drawing) return;
            Point p = Clamp(e.GetPosition(_canvas));
            double x = Math.Min(_start.X, p.X), y = Math.Min(_start.Y, p.Y);
            _rectangle.Width = Math.Abs(p.X - _start.X); _rectangle.Height = Math.Abs(p.Y - _start.Y);
            Canvas.SetLeft(_rectangle, x); Canvas.SetTop(_rectangle, y);
        }
        private void CanvasUp(object sender, MouseButtonEventArgs e)
        {
            if (!_drawing) return;
            CanvasMove(sender, e); _drawing = false; _canvas.ReleaseMouseCapture();
            GetImageBounds();
            double x = Math.Max(_imageBounds.Left, Canvas.GetLeft(_rectangle));
            double y = Math.Max(_imageBounds.Top, Canvas.GetTop(_rectangle));
            double right = Math.Min(_imageBounds.Right, x + _rectangle.Width);
            double bottom = Math.Min(_imageBounds.Bottom, y + _rectangle.Height);
            if (right - x >= 8 && bottom - y >= 8)
            {
                _region = new MotionDetectionService.MotionRegion((x - _imageBounds.Left) / _imageBounds.Width, (y - _imageBounds.Top) / _imageBounds.Height,
                    (right - x) / _imageBounds.Width, (bottom - y) / _imageBounds.Height);
                UpdateSelectionText();
            }
            else { _rectangle.Visibility = Visibility.Collapsed; _region = MotionDetectionService.MotionRegion.FullFrame; UpdateSelectionText(); }
            e.Handled = true;
        }
        private Point Clamp(Point p) => new(Math.Clamp(p.X, _imageBounds.Left, _imageBounds.Right), Math.Clamp(p.Y, _imageBounds.Top, _imageBounds.Bottom));
        private void RedrawRectangle()
        {
            GetImageBounds();
            if (_region.IsFullFrame || _imageBounds.IsEmpty) return;
            Canvas.SetLeft(_rectangle, _imageBounds.Left + _region.X * _imageBounds.Width);
            Canvas.SetTop(_rectangle, _imageBounds.Top + _region.Y * _imageBounds.Height);
            _rectangle.Width = _region.Width * _imageBounds.Width;
            _rectangle.Height = _region.Height * _imageBounds.Height;
            _rectangle.Visibility = Visibility.Visible;
        }
        private void UpdateSelectionText() => _selectionText.Text = _region.IsFullFrame ? "Obszar: cały obraz" : $"Obszar: X={_region.X:P0}, Y={_region.Y:P0}, szerokość={_region.Width:P0}, wysokość={_region.Height:P0}";
    }
}