using System;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;

namespace VideoPlayer
{
    internal sealed class MotionEventPreviewWindow : Window
    {
        private readonly Image _image = new() { Stretch = Stretch.Uniform };
        private readonly Canvas _overlay = new() { Background = Brushes.Transparent };
        private readonly Rectangle _region = new()
        {
            Stroke = Brushes.Lime,
            StrokeThickness = 3,
            Fill = new SolidColorBrush(Color.FromArgb(28, 50, 220, 100)),
            IsHitTestVisible = false
        };
        private readonly double _x, _y, _width, _height;

        public MotionEventPreviewWindow(string imagePath, double x, double y, double width, double height)
            : this(LoadBitmapFromPath(imagePath), x, y, width, height)
        {
        }

        public MotionEventPreviewWindow(byte[] encodedImageBytes, double x, double y, double width, double height)
            : this(LoadBitmapFromBytes(encodedImageBytes), x, y, width, height)
        {
        }

        private MotionEventPreviewWindow(BitmapSource bitmap, double x, double y, double width, double height)
        {
            _x = Math.Clamp(x, 0, 1); _y = Math.Clamp(y, 0, 1);
            _width = Math.Clamp(width, 0, 1 - _x); _height = Math.Clamp(height, 0, 1 - _y);
            Title = "Podgląd wykrytego ruchu"; Width = 1000; Height = 700; MinWidth = 640; MinHeight = 420;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
            Background = new SolidColorBrush(Color.FromRgb(17, 19, 24));
            var root = new DockPanel { Margin = new Thickness(12) };
            var note = new TextBlock { Text = "Zielony prostokąt oznacza obszar, w którym wykryto zmianę obrazu.", Foreground = Brushes.LightGray, Margin = new Thickness(0, 0, 0, 8) };
            DockPanel.SetDock(note, Dock.Top); root.Children.Add(note);
            var frame = new Border
            {
                Background = Brushes.Black,
                BorderBrush = new SolidColorBrush(Color.FromRgb(65, 75, 92)),
                BorderThickness = new Thickness(1),
                Child = new Grid { Children = { _image, _overlay } }
            };
            _overlay.Children.Add(_region); root.Children.Add(frame); Content = root;
            _image.Source = bitmap;
            _overlay.SizeChanged += (_, __) => Redraw();
            Loaded += (_, __) => Redraw();
        }

        private static BitmapSource LoadBitmapFromPath(string imagePath)
        {
            var bitmap = new BitmapImage();
            bitmap.BeginInit();
            bitmap.CacheOption = BitmapCacheOption.OnLoad;
            bitmap.UriSource = new Uri(imagePath);
            bitmap.EndInit();
            bitmap.Freeze();
            return bitmap;
        }

        private static BitmapSource LoadBitmapFromBytes(byte[] encodedImageBytes)
        {
            using var stream = new MemoryStream(encodedImageBytes);
            var bitmap = new BitmapImage();
            bitmap.BeginInit();
            bitmap.CacheOption = BitmapCacheOption.OnLoad;
            bitmap.StreamSource = stream;
            bitmap.EndInit();
            bitmap.Freeze();
            return bitmap;
        }

        private void Redraw()
        {
            if (_image.Source is not BitmapSource bitmap || _overlay.ActualWidth <= 0 || _overlay.ActualHeight <= 0) return;
            double scale = Math.Min(_overlay.ActualWidth / bitmap.PixelWidth, _overlay.ActualHeight / bitmap.PixelHeight);
            double iw = bitmap.PixelWidth * scale, ih = bitmap.PixelHeight * scale;
            double left = (_overlay.ActualWidth - iw) / 2, top = (_overlay.ActualHeight - ih) / 2;
            Canvas.SetLeft(_region, left + _x * iw); Canvas.SetTop(_region, top + _y * ih);
            _region.Width = _width * iw; _region.Height = _height * ih;
        }
    }
}