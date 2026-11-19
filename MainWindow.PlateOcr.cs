using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using Path = System.IO.Path;

namespace VideoPlayer
{
    public partial class MainWindow
    {
        private bool _ocrActive;
        private bool _ocrFromDahua;
        private double _ocrFrameSeconds;
        private BitmapSource? _ocrFrame;
        private byte[]? _ocrFramePixels;
        private readonly Ellipse[] _ocrHandles = new Ellipse[4];
        private readonly TextBlock[] _ocrLabels = new TextBlock[4];
        private PlatePoint[]? _ocrPoints;
        private PlatePoint _ocrStart;
        private PlateViewState _ocrView = new(1, 0, 0);
        private bool _ocrPanning;
        private Point _ocrPanAnchor;
        private double _ocrPanStartX, _ocrPanStartY;
        private int _ocrDragCorner = -1;
        private bool _ocrGesture, _ocrCreating, _ocrBusy;
        private byte[]? _ocrRectifiedPng;
        private CancellationTokenSource? _ocrWorkCts;

        private void InitializeOcrOverlayHandles()
        {
            for (int i = 0; i < 4; i++)
            {
                _ocrHandles[i] = new Ellipse
                {
                    Width = 18,
                    Height = 18,
                    Fill = Brushes.White,
                    Stroke = Brushes.DeepSkyBlue,
                    StrokeThickness = 3,
                    Visibility = Visibility.Collapsed
                };
                _ocrLabels[i] = new TextBlock
                {
                    Text = (i + 1).ToString(),
                    Foreground = Brushes.White,
                    Background = Brushes.Black,
                    FontWeight = FontWeights.Bold,
                    Padding = new Thickness(2),
                    Visibility = Visibility.Collapsed
                };
                OcrSelectionCanvas.Children.Add(_ocrHandles[i]);
                OcrSelectionCanvas.Children.Add(_ocrLabels[i]);
            }
        }

        private async void PlateOcr_Click(object sender, RoutedEventArgs e)
        {
            if (!CanOperate() || !_ready || _screenshotBusy || !PositionSlider.IsEnabled)
            {
                StatusText.Text = "Najpierw otwórz nagranie i poczekaj na zakończenie innych operacji.";
                return;
            }
            if (_ocrBusy)
            {
                StatusText.Text = "Trwa bieżące rozpoznawanie OCR.";
                return;
            }

            PlateOcrButton.IsEnabled = false;
            _screenshotBusy = true;
            ScreenshotButton.IsEnabled = false;
            string folder = Path.Combine(Path.GetTempPath(), "VideoPlayer_OCR_" + Guid.NewGuid().ToString("N"));
            try
            {
                Directory.CreateDirectory(folder);
                await _mpvService.RequestAsync(new object[] { "set_property", "pause", true });
                var position = await _mpvService.RequestAsync(new object[] { "get_property", "time-pos" });
                double seconds = position.GetDouble();
                string framePath = Path.Combine(folder, "frame.png");
                await _mpvService.RequestAsync(new object[] { "screenshot-to-file", framePath, "video" });
                if (_shutdownRequested || _closing || !File.Exists(framePath)) return;

                var frame = new BitmapImage();
                frame.BeginInit();
                frame.CacheOption = BitmapCacheOption.OnLoad;
                frame.UriSource = new Uri(framePath);
                frame.EndInit();
                frame.Freeze();

                StartInlinePlateOcr(frame, seconds, fromDahua: false);
            }
            catch (Exception ex)
            {
                if (!_closing)
                {
                    StatusText.Text = "Nie udało się pobrać klatki do OCR: " + ex.Message;
                    MessageBox.Show(this, ex.Message, "Rozpoznawanie tablicy", MessageBoxButton.OK, MessageBoxImage.Warning);
                }
            }
            finally
            {
                try { Directory.Delete(folder, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
                _screenshotBusy = false;
                if (!_closing)
                {
                    PlateOcrButton.IsEnabled = true;
                    ScreenshotButton.IsEnabled = true;
                }
            }
        }

        private void StartInlinePlateOcr(BitmapSource frame, double seconds, bool fromDahua)
        {
            if (_ocrBusy)
            {
                _ocrWorkCts?.Cancel();
            }

            _ocrFromDahua = fromDahua;
            _ocrFrameSeconds = seconds;
            _ocrFrame = frame;
            OcrFrameImage.Source = frame;

            var converted = new FormatConvertedBitmap(frame, PixelFormats.Bgra32, null, 0);
            _ocrFramePixels = new byte[checked(frame.PixelWidth * frame.PixelHeight * 4)];
            converted.CopyPixels(_ocrFramePixels, frame.PixelWidth * 4, 0);

            _ocrPoints = null;
            _ocrView = new PlateViewState(1, 0, 0);
            _ocrVehicleMakeBoxClear();
            InvalidateOcrResult();
            OcrSelectionInfo.Text = "Zaznacz tablicę przeciągnięciem na klatce po lewej i dopasuj cztery narożniki.";
            OcrStatusText.Text = "Silnik OCR działa lokalnie. Wynik wymaga sprawdzenia — szczególnie O/0, I/1 i B/8.";
            OcrFrameInfo.Text = fromDahua
                ? "Klatka ze strumienia Dahua — zaznacz tablicę i dopasuj 4 narożniki"
                : "Klatka " + TimeSpan.FromSeconds(seconds).ToString(@"hh\:mm\:ss\.fff") + " — zaznacz tablicę i dopasuj 4 narożniki";

            _ocrActive = true;
            SetWorkspaceMode(WorkspaceMode.PlateOcr, 1);
            Dispatcher.BeginInvoke(new Action(DrawOcrSelection));
            StatusText.Text = "Tryb OCR: zaznacz tablicę na podglądzie po lewej i potwierdź wynik po prawej.";
        }

        private void _ocrVehicleMakeBoxClear()
        {
            if (OcrVehicleMakeBox != null) OcrVehicleMakeBox.Clear();
        }

        private void ResetOcrWorkspaceState(bool silent)
        {
            _ocrWorkCts?.Cancel();
            _ocrActive = false;
            _ocrGesture = false;
            _ocrPanning = false;
            _ocrCreating = false;
            _ocrDragCorner = -1;
            _ocrPoints = null;
            _ocrRectifiedPng = null;
            _ocrFrame = null;
            _ocrFramePixels = null;
            if (OcrFrameImage != null) OcrFrameImage.Source = null;
            if (OcrRectifiedPreview != null) OcrRectifiedPreview.Source = null;
            if (OcrPictureArea != null) OcrPictureArea.ReleaseMouseCapture();
            if (OcrSelectionPolygon != null) OcrSelectionPolygon.Visibility = Visibility.Collapsed;
            for (int i = 0; i < 4; i++)
            {
                if (_ocrHandles[i] != null) _ocrHandles[i].Visibility = Visibility.Collapsed;
                if (_ocrLabels[i] != null) _ocrLabels[i].Visibility = Visibility.Collapsed;
            }
            if (!silent)
            {
                StatusText.Text = "Zakończono tryb rozpoznawania tablicy.";
            }
        }

        private PlateView GetOcrPlacement() => _ocrFrame == null
            ? new PlateView(0, 0, 0)
            : PlateViewport.Place(_ocrFrame.PixelWidth, _ocrFrame.PixelHeight, OcrPictureArea.ActualWidth, OcrPictureArea.ActualHeight, _ocrView);

        private void ChangeOcrZoom(double target, Point anchor)
        {
            if (_ocrBusy || _ocrGesture || _ocrPanning || _ocrFrame == null) return;
            _ocrView = PlateViewport.ZoomAt(_ocrFrame.PixelWidth, _ocrFrame.PixelHeight, OcrPictureArea.ActualWidth, OcrPictureArea.ActualHeight, _ocrView, target, anchor.X, anchor.Y);
            DrawOcrSelection();
        }

        private void OcrPicture_Wheel(object sender, MouseWheelEventArgs e)
        {
            e.Handled = true;
            ChangeOcrZoom(_ocrView.Zoom * Math.Pow(1.2, e.Delta / 120.0), e.GetPosition(OcrPictureArea));
        }

        private Point GetOcrViewCenter() => new Point(OcrPictureArea.ActualWidth / 2, OcrPictureArea.ActualHeight / 2);
        private void OcrZoomIn_Click(object sender, RoutedEventArgs e) => ChangeOcrZoom(_ocrView.Zoom * 1.25, GetOcrViewCenter());
        private void OcrZoomOut_Click(object sender, RoutedEventArgs e) => ChangeOcrZoom(_ocrView.Zoom / 1.25, GetOcrViewCenter());

        private void OcrFitView_Click(object sender, RoutedEventArgs e)
        {
            if (_ocrBusy || _ocrGesture || _ocrPanning) return;
            _ocrView = new PlateViewState(1, 0, 0);
            DrawOcrSelection();
        }

        private void OcrPicture_PanDown(object sender, MouseButtonEventArgs e)
        {
            e.Handled = true;
            if (_ocrBusy || _ocrGesture) return;
            _ocrPanning = true;
            _ocrPanAnchor = e.GetPosition(OcrPictureArea);
            _ocrPanStartX = _ocrView.PanX;
            _ocrPanStartY = _ocrView.PanY;
            OcrPictureArea.Cursor = Cursors.Hand;
            OcrPictureArea.CaptureMouse();
        }

        private void OcrPicture_PanUp(object sender, MouseButtonEventArgs e)
        {
            e.Handled = true;
            if (!_ocrPanning) return;
            OcrPicture_Move(sender, e);
            _ocrPanning = false;
            OcrPictureArea.ReleaseMouseCapture();
            OcrPictureArea.Cursor = Cursors.Cross;
        }

        private Point OcrToScreen(PlatePoint point)
        {
            var p = GetOcrPlacement();
            return new Point(p.Left + point.X * p.Scale, p.Top + point.Y * p.Scale);
        }

        private PlatePoint OcrToPixel(Point point)
        {
            if (_ocrFrame == null) return new PlatePoint(0, 0);
            var p = GetOcrPlacement();
            return new PlatePoint(
                Math.Clamp((point.X - p.Left) / p.Scale, 0, _ocrFrame.PixelWidth - 1),
                Math.Clamp((point.Y - p.Top) / p.Scale, 0, _ocrFrame.PixelHeight - 1));
        }

        private void InvalidateOcrResult()
        {
            _ocrRectifiedPng = null;
            if (OcrRectifiedPreview != null) OcrRectifiedPreview.Source = null;
            if (OcrRecognizeButton != null) OcrRecognizeButton.IsEnabled = false;
            if (OcrPlateBox != null) OcrPlateBox.Clear();
            if (OcrCandidatesBox != null) OcrCandidatesBox.ItemsSource = null;
            if (OcrConfirmedBox != null) OcrConfirmedBox.IsChecked = false;
            UpdateOcrSave();
        }

        private void DrawOcrSelection()
        {
            if (_ocrFrame == null || OcrPictureArea == null || _ocrHandles[0] == null) return;
            var pan = PlateViewport.ConstrainPan(_ocrFrame.PixelWidth, _ocrFrame.PixelHeight, OcrPictureArea.ActualWidth, OcrPictureArea.ActualHeight, _ocrView.Zoom, _ocrView.PanX, _ocrView.PanY);
            _ocrView = new PlateViewState(_ocrView.Zoom, pan.X, pan.Y);
            var placement = GetOcrPlacement();
            if (placement.Scale <= 0) return;

            OcrFrameImage.Width = _ocrFrame.PixelWidth * placement.Scale;
            OcrFrameImage.Height = _ocrFrame.PixelHeight * placement.Scale;
            Canvas.SetLeft(OcrFrameImage, placement.Left);
            Canvas.SetTop(OcrFrameImage, placement.Top);
            OcrZoomLabel.Text = _ocrView.Zoom.ToString("0.##", System.Globalization.CultureInfo.CurrentCulture) + "×";

            bool has = _ocrPoints != null;
            OcrSelectionPolygon.Visibility = has ? Visibility.Visible : Visibility.Collapsed;
            for (int i = 0; i < 4; i++)
            {
                _ocrHandles[i].Visibility = has ? Visibility.Visible : Visibility.Collapsed;
                _ocrLabels[i].Visibility = has ? Visibility.Visible : Visibility.Collapsed;
            }
            if (_ocrPoints == null) return;

            var screen = new PointCollection();
            for (int i = 0; i < 4; i++)
            {
                var q = OcrToScreen(_ocrPoints[i]);
                screen.Add(q);
                Canvas.SetLeft(_ocrHandles[i], q.X - 9);
                Canvas.SetTop(_ocrHandles[i], q.Y - 9);
                Canvas.SetLeft(_ocrLabels[i], q.X + 10);
                Canvas.SetTop(_ocrLabels[i], q.Y - 22);
            }
            OcrSelectionPolygon.Points = screen;
            OcrSelectionPolygon.Stroke = PlatePerspective.IsValid(_ocrPoints, _ocrFrame.PixelWidth, _ocrFrame.PixelHeight)
                ? Brushes.DeepSkyBlue
                : Brushes.OrangeRed;
        }

        private void OcrPicture_Down(object sender, MouseButtonEventArgs e)
        {
            if (_ocrBusy || _ocrPanning || _ocrFrame == null) return;
            var screen = e.GetPosition(OcrPictureArea);
            var placement = GetOcrPlacement();
            if (placement.Scale <= 0) return;

            int hit = -1;
            double closest = 18;
            if (_ocrPoints != null)
            {
                for (int i = 0; i < 4; i++)
                {
                    var q = OcrToScreen(_ocrPoints[i]);
                    double d = (q - screen).Length;
                    if (d < closest) { hit = i; closest = d; }
                }
            }

            if (hit < 0 && (screen.X < placement.Left || screen.Y < placement.Top ||
                screen.X > placement.Left + _ocrFrame.PixelWidth * placement.Scale ||
                screen.Y > placement.Top + _ocrFrame.PixelHeight * placement.Scale))
                return;

            InvalidateOcrResult();
            _ocrGesture = true;
            _ocrCreating = hit < 0;
            _ocrDragCorner = hit;
            _ocrStart = OcrToPixel(screen);
            if (_ocrCreating) _ocrPoints = new[] { _ocrStart, _ocrStart, _ocrStart, _ocrStart };
            OcrPictureArea.CaptureMouse();
            DrawOcrSelection();
            e.Handled = true;
        }

        private void OcrPicture_Move(object sender, MouseEventArgs e)
        {
            if (_ocrFrame == null) return;
            if (_ocrPanning)
            {
                var delta = e.GetPosition(OcrPictureArea) - _ocrPanAnchor;
                var pan = PlateViewport.ConstrainPan(_ocrFrame.PixelWidth, _ocrFrame.PixelHeight, OcrPictureArea.ActualWidth, OcrPictureArea.ActualHeight, _ocrView.Zoom, _ocrPanStartX + delta.X, _ocrPanStartY + delta.Y);
                _ocrView = new PlateViewState(_ocrView.Zoom, pan.X, pan.Y);
                DrawOcrSelection();
                return;
            }
            if (!_ocrGesture || _ocrPoints == null) return;
            var p = OcrToPixel(e.GetPosition(OcrPictureArea));
            if (_ocrCreating)
            {
                double l = Math.Min(_ocrStart.X, p.X), r = Math.Max(_ocrStart.X, p.X);
                double t = Math.Min(_ocrStart.Y, p.Y), b = Math.Max(_ocrStart.Y, p.Y);
                _ocrPoints = new[] { new PlatePoint(l, t), new PlatePoint(r, t), new PlatePoint(r, b), new PlatePoint(l, b) };
            }
            else if (_ocrDragCorner >= 0) _ocrPoints[_ocrDragCorner] = p;
            DrawOcrSelection();
        }

        private void OcrPicture_Up(object sender, MouseButtonEventArgs e)
        {
            if (!_ocrGesture) return;
            OcrPicture_Move(sender, e);
            FinishOcrGesture();
            e.Handled = true;
        }

        private void OcrPicture_LostCapture(object sender, MouseEventArgs e)
        {
            if (_ocrGesture) FinishOcrGesture();
            if (_ocrPanning) { _ocrPanning = false; OcrPictureArea.Cursor = Cursors.Cross; }
        }

        private void FinishOcrGesture()
        {
            _ocrGesture = false;
            _ocrCreating = false;
            _ocrDragCorner = -1;
            OcrPictureArea.ReleaseMouseCapture();
            DrawOcrSelection();
            UpdateOcrPreview();
        }

        private void OcrPicture_SizeChanged(object sender, SizeChangedEventArgs e)
        {
            if (_ocrFrame != null && _ocrHandles[0] != null) DrawOcrSelection();
        }

        private void OcrResetSelection_Click(object sender, RoutedEventArgs e)
        {
            if (_ocrBusy) return;
            _ocrGesture = false;
            OcrPictureArea.ReleaseMouseCapture();
            _ocrPoints = null;
            InvalidateOcrResult();
            DrawOcrSelection();
            OcrSelectionInfo.Text = "Zaznacz nowy obszar, a potem dopasuj narożniki.";
        }

        private void UpdateOcrPreview()
        {
            InvalidateOcrResult();
            if (_ocrFrame == null || _ocrFramePixels == null) return;
            if (_ocrPoints == null || !PlatePerspective.IsValid(_ocrPoints, _ocrFrame.PixelWidth, _ocrFrame.PixelHeight))
            {
                OcrSelectionInfo.Text = "Popraw narożniki — boki nie mogą się krzyżować, a obszar nie może być zbyt mały.";
                return;
            }

            try
            {
                var result = PlatePerspective.Rectify(_ocrFramePixels, _ocrFrame.PixelWidth, _ocrFrame.PixelHeight, _ocrPoints);
                var image = BitmapSource.Create(result.Width, result.Height, 96, 96, PixelFormats.Bgra32, null, result.Pixels, result.Width * 4);
                image.Freeze();
                OcrRectifiedPreview.Source = image;
                if (result.Width < 16 || result.Height < 8)
                {
                    OcrSelectionInfo.Text = "Wycinek jest zbyt mały. Zaznacz większy obszar.";
                    return;
                }

                var encoder = new PngBitmapEncoder();
                encoder.Frames.Add(BitmapFrame.Create(image));
                using var png = new MemoryStream();
                encoder.Save(png);
                _ocrRectifiedPng = png.ToArray();
                OcrRecognizeButton.IsEnabled = true;
                OcrSelectionInfo.Text = $"Po prostowaniu: {result.Width} × {result.Height} px. Możesz przesunąć każdy narożnik.";
                OcrStatusText.Text = "Sprawdź wyprostowany podgląd, potem kliknij Rozpoznaj. Numer nadal wymaga ręcznego potwierdzenia.";
            }
            catch (Exception ex)
            {
                OcrSelectionInfo.Text = "Nie można wyprostować zaznaczenia: " + ex.Message;
            }
        }

        private async void OcrRecognize_Click(object sender, RoutedEventArgs e)
        {
            if (_ocrBusy || _ocrRectifiedPng == null) return;
            byte[] bytes = _ocrRectifiedPng;
            _ocrBusy = true;
            OcrResetSelectionButton.IsEnabled = false;
            _ocrWorkCts = new CancellationTokenSource();
            OcrRecognizeButton.IsEnabled = false;
            OcrPlateBox.IsEnabled = false;
            OcrCandidatesBox.IsEnabled = false;
            OcrConfirmedBox.IsChecked = false;
            OcrConfirmedBox.IsEnabled = false;
            UpdateOcrSave();
            OcrStatusText.Text = "Rozpoznawanie lokalne — poczekaj…";

            try
            {
                var results = await Task.Run(() => PlateOcrService.Recognize(bytes, _ocrWorkCts.Token));
                if (!_ocrActive || _closing) return;
                OcrCandidatesBox.ItemsSource = results.Select(r => r.Text).ToArray();
                if (results.Count > 0)
                {
                    OcrPlateBox.Text = results[0].Text;
                    OcrCandidatesBox.SelectedIndex = 0;
                    OcrStatusText.Text = "To propozycja OCR, nie pewny odczyt. Popraw numer i potwierdź zgodność z obrazem.";
                }
                else
                {
                    OcrStatusText.Text = "Nie znaleziono czytelnego numeru. Zaznacz dokładniej albo wpisz go ręcznie, jeśli jest czytelny.";
                }
            }
            catch (OperationCanceledException)
            {
                OcrStatusText.Text = "Anulowano.";
            }
            catch (Exception ex)
            {
                OcrStatusText.Text = "Nie udało się uruchomić OCR. Możesz wpisać numer ręcznie.";
                if (!_closing)
                {
                    string details = ex.Message;
                    for (Exception? inner = ex.InnerException; inner != null; inner = inner.InnerException)
                        details += "\\n\\nSzczegóły: " + inner.GetType().Name + ": " + inner.Message;
                    MessageBox.Show(this,
                        details + "\\n\\nWymagane pliki: x64\\\\tesseract50.dll, x64\\\\leptonica-1.82.0.dll oraz tessdata\\\\eng.traineddata. Zainstaluj również Microsoft Visual C++ Redistributable 2015–2022 (x64).",
                        "OCR lokalny", MessageBoxButton.OK, MessageBoxImage.Warning);
                }
            }
            finally
            {
                _ocrBusy = false;
                _ocrWorkCts?.Dispose();
                _ocrWorkCts = null;
                OcrPlateBox.IsEnabled = true;
                OcrCandidatesBox.IsEnabled = true;
                OcrConfirmedBox.IsEnabled = true;
                OcrRecognizeButton.IsEnabled = _ocrRectifiedPng != null;
                OcrResetSelectionButton.IsEnabled = true;
                UpdateOcrSave();
            }
        }

        private void OcrCandidate_Changed(object sender, SelectionChangedEventArgs e)
        {
            if (OcrCandidatesBox.SelectedItem is string text) OcrPlateBox.Text = text;
        }

        private void OcrPlate_Changed(object sender, TextChangedEventArgs e)
        {
            if (OcrConfirmedBox != null) OcrConfirmedBox.IsChecked = false;
            UpdateOcrSave();
        }

        private void OcrConfirmation_Changed(object sender, RoutedEventArgs e) => UpdateOcrSave();

        private void UpdateOcrSave()
        {
            if (OcrSaveEventButton == null || OcrPlateBox == null || OcrConfirmedBox == null) return;
            string text = OcrPlateBox.Text.Trim().Replace(" ", "").Replace("-", "");
            OcrSaveEventButton.IsEnabled = !_ocrBusy && OcrConfirmedBox.IsChecked == true && PlateOcrRules.IsValidManual(text);
        }

        private void OcrSave_Click(object sender, RoutedEventArgs e)
        {
            UpdateOcrSave();
            if (!OcrSaveEventButton.IsEnabled) return;

            string plateNumber = PlateOcrService.Normalize(OcrPlateBox.Text);
            string vehicleMake = OcrVehicleMakeBox.Text.Trim();
            bool fromDahua = _ocrFromDahua;
            double seconds = _ocrFrameSeconds;

            ResetOcrWorkspaceState(silent: true);

            if (fromDahua)
            {
                SetWorkspaceMode(WorkspaceMode.Dahua, 3);
                DahuaStatusLabel.Text = $"OCR (Dahua): {plateNumber}" + (string.IsNullOrWhiteSpace(vehicleMake) ? "" : $" | Marka: {vehicleMake}");
                StatusText.Text = DahuaStatusLabel.Text;
                MessageBox.Show(this,
                    $"Rozpoznano numer tablicy: {plateNumber}\nMarka: {(string.IsNullOrWhiteSpace(vehicleMake) ? "nie podano" : vehicleMake)}",
                    "Rozpoznanie tablicy ze strumienia Dahua",
                    MessageBoxButton.OK, MessageBoxImage.Information);
            }
            else
            {
                SetWorkspaceMode(WorkspaceMode.Player, 1);
                string description = "Tablica rejestracyjna: " + plateNumber + " (numer potwierdzony ręcznie)";
                var marker = new TimelineMarker
                {
                    Id = Guid.NewGuid(),
                    Seconds = seconds,
                    Description = description,
                    VehicleMake = vehicleMake
                };
                _timelineMarkers.Add(marker);
                SaveTimelineMarkers();
                RefreshTimelineMarkerList();
                UpdateTimelineOverlay();
                TimelineMarkersListBox.SelectedItem = marker;
                TimelineMarkersListBox.ScrollIntoView(marker);
                MarkerDescriptionBox.Text = description;
                StatusText.Text = "Dodano zdarzenie: " + plateNumber + " — " + TimeSpan.FromSeconds(seconds).ToString(@"hh\:mm\:ss\.fff");
            }
        }

        private void OcrCancel_Click(object sender, RoutedEventArgs e)
        {
            if (_ocrBusy)
            {
                _ocrWorkCts?.Cancel();
            }
            bool returnToDahua = _ocrFromDahua;
            ResetOcrWorkspaceState(silent: true);
            SetWorkspaceMode(returnToDahua ? WorkspaceMode.Dahua : WorkspaceMode.Player, returnToDahua ? 3 : 1);
            StatusText.Text = returnToDahua ? "Anulowano OCR. Powrót do kamer Dahua IP." : "Anulowano rozpoznawanie tablicy. Nagranie pozostaje zatrzymane.";
        }
    }
}
