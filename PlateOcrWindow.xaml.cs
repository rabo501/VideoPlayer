using System;
using System.ComponentModel;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
namespace VideoPlayer
{
    public partial class PlateOcrWindow:Window
    {
        private readonly BitmapSource _frame;
        private readonly byte[] _framePixels;
        private readonly Ellipse[] _handles=new Ellipse[4];
        private readonly TextBlock[] _labels=new TextBlock[4];
        private PlatePoint[]? _points;
        private PlatePoint _start;
        private PlateViewState _view=new(1,0,0);
        private bool _panning;
        private Point _panAnchor;
        private double _panStartX,_panStartY;
        private int _dragCorner=-1;
        private bool _gesture,_creating,_busy,_closeAfterWork;
        private byte[]? _rectifiedPng;
        private CancellationTokenSource? _workCts;
        public string PlateNumber{get;private set;}="";
        internal PlateOcrWindow(BitmapSource frame,double seconds)
        {
            InitializeComponent();_frame=frame;FrameImage.Source=frame;
            var converted=new FormatConvertedBitmap(frame,PixelFormats.Bgra32,null,0);
            _framePixels=new byte[checked(frame.PixelWidth*frame.PixelHeight*4)];
            converted.CopyPixels(_framePixels,frame.PixelWidth*4,0);
            for(int i=0;i<4;i++)
            {
                _handles[i]=new Ellipse{Width=18,Height=18,Fill=Brushes.White,Stroke=Brushes.DeepSkyBlue,StrokeThickness=3,Visibility=Visibility.Collapsed};
                _labels[i]=new TextBlock{Text=(i+1).ToString(),Foreground=Brushes.White,Background=Brushes.Black,FontWeight=FontWeights.Bold,Padding=new Thickness(2),Visibility=Visibility.Collapsed};
                SelectionCanvas.Children.Add(_handles[i]);SelectionCanvas.Children.Add(_labels[i]);
            }
            Loaded+=(_,__)=>DrawSelection();
            FrameInfo.Text="Klatka "+TimeSpan.FromSeconds(seconds).ToString(@"hh\:mm\:ss\.fff")+" — dopasuj cztery narożniki";
        }
        private PlateView Placement()=>PlateViewport.Place(_frame.PixelWidth,_frame.PixelHeight,PictureArea.ActualWidth,PictureArea.ActualHeight,_view);
        private void ChangeZoom(double target,Point anchor)
        {
            if(_busy||_gesture||_panning)return;
            _view=PlateViewport.ZoomAt(_frame.PixelWidth,_frame.PixelHeight,PictureArea.ActualWidth,PictureArea.ActualHeight,_view,target,anchor.X,anchor.Y);
            DrawSelection();
        }
        private void Picture_Wheel(object sender,MouseWheelEventArgs e)
        {
            e.Handled=true;
            ChangeZoom(_view.Zoom*Math.Pow(1.2,e.Delta/120.0),e.GetPosition(PictureArea));
        }
        private Point ViewCenter()=>new Point(PictureArea.ActualWidth/2,PictureArea.ActualHeight/2);
        private void ZoomIn_Click(object sender,RoutedEventArgs e)=>ChangeZoom(_view.Zoom*1.25,ViewCenter());
        private void ZoomOut_Click(object sender,RoutedEventArgs e)=>ChangeZoom(_view.Zoom/1.25,ViewCenter());
        private void FitView_Click(object sender,RoutedEventArgs e)
        {
            if(_busy||_gesture||_panning)return;_view=new(1,0,0);DrawSelection();
        }
        private void Picture_PanDown(object sender,MouseButtonEventArgs e)
        {
            e.Handled=true;if(_busy||_gesture)return;
            _panning=true;_panAnchor=e.GetPosition(PictureArea);_panStartX=_view.PanX;_panStartY=_view.PanY;
            PictureArea.Cursor=Cursors.Hand;PictureArea.CaptureMouse();
        }
        private void Picture_PanUp(object sender,MouseButtonEventArgs e)
        {
            e.Handled=true;if(!_panning)return;
            Picture_Move(sender,e);_panning=false;PictureArea.ReleaseMouseCapture();PictureArea.Cursor=Cursors.Cross;
        }
        private Point Screen(PlatePoint point)
        {var p=Placement();return new Point(p.Left+point.X*p.Scale,p.Top+point.Y*p.Scale);}
        private PlatePoint Pixel(Point point)
        {var p=Placement();return new PlatePoint(Math.Clamp((point.X-p.Left)/p.Scale,0,_frame.PixelWidth-1),Math.Clamp((point.Y-p.Top)/p.Scale,0,_frame.PixelHeight-1));}
        private void InvalidateResult()
        {
            _rectifiedPng=null;RectifiedPreview.Source=null;RecognizeButton.IsEnabled=false;
            PlateBox.Clear();CandidatesBox.ItemsSource=null;ConfirmedBox.IsChecked=false;
        }
        private void DrawSelection()
        {
            if(_frame==null)return;
            var pan=PlateViewport.ConstrainPan(_frame.PixelWidth,_frame.PixelHeight,PictureArea.ActualWidth,PictureArea.ActualHeight,_view.Zoom,_view.PanX,_view.PanY);
            _view=new(_view.Zoom,pan.X,pan.Y);var placement=Placement();
            if(placement.Scale<=0)return;
            FrameImage.Width=_frame.PixelWidth*placement.Scale;FrameImage.Height=_frame.PixelHeight*placement.Scale;
            Canvas.SetLeft(FrameImage,placement.Left);Canvas.SetTop(FrameImage,placement.Top);
            ZoomLabel.Text=_view.Zoom.ToString("0.##",System.Globalization.CultureInfo.CurrentCulture)+"×";
            bool has=_points!=null;
            SelectionPolygon.Visibility=has?Visibility.Visible:Visibility.Collapsed;
            for(int i=0;i<4;i++){_handles[i].Visibility=has?Visibility.Visible:Visibility.Collapsed;_labels[i].Visibility=has?Visibility.Visible:Visibility.Collapsed;}
            if(_points==null)return;
            var screen=new PointCollection();
            for(int i=0;i<4;i++)
            {
                var q=Screen(_points[i]);screen.Add(q);
                Canvas.SetLeft(_handles[i],q.X-9);Canvas.SetTop(_handles[i],q.Y-9);
                Canvas.SetLeft(_labels[i],q.X+10);Canvas.SetTop(_labels[i],q.Y-22);
            }
            SelectionPolygon.Points=screen;
            SelectionPolygon.Stroke=PlatePerspective.IsValid(_points,_frame.PixelWidth,_frame.PixelHeight)?Brushes.DeepSkyBlue:Brushes.OrangeRed;
        }
        private void Picture_Down(object sender,MouseButtonEventArgs e)
        {
            if(_busy||_panning)return;var screen=e.GetPosition(PictureArea);var placement=Placement();if(placement.Scale<=0)return;
            int hit=-1;double closest=18;
            if(_points!=null)for(int i=0;i<4;i++){var q=Screen(_points[i]);double d=(q-screen).Length;if(d<closest){hit=i;closest=d;}}
            if(hit<0&&(screen.X<placement.Left||screen.Y<placement.Top||screen.X>placement.Left+_frame.PixelWidth*placement.Scale||screen.Y>placement.Top+_frame.PixelHeight*placement.Scale))return;
            InvalidateResult();_gesture=true;_creating=hit<0;_dragCorner=hit;_start=Pixel(screen);
            if(_creating)_points=new[]{_start,_start,_start,_start};
            PictureArea.CaptureMouse();DrawSelection();e.Handled=true;
        }
        private void Picture_Move(object sender,MouseEventArgs e)
        {
            if(_panning)
            {
                var delta=e.GetPosition(PictureArea)-_panAnchor;
                var pan=PlateViewport.ConstrainPan(_frame.PixelWidth,_frame.PixelHeight,PictureArea.ActualWidth,PictureArea.ActualHeight,_view.Zoom,_panStartX+delta.X,_panStartY+delta.Y);
                _view=new(_view.Zoom,pan.X,pan.Y);DrawSelection();return;
            }
            if(!_gesture||_points==null)return;var p=Pixel(e.GetPosition(PictureArea));
            if(_creating)
            {
                double l=Math.Min(_start.X,p.X),r=Math.Max(_start.X,p.X),t=Math.Min(_start.Y,p.Y),b=Math.Max(_start.Y,p.Y);
                _points=new[]{new PlatePoint(l,t),new PlatePoint(r,t),new PlatePoint(r,b),new PlatePoint(l,b)};
            }
            else if(_dragCorner>=0)_points[_dragCorner]=p;
            DrawSelection();
        }
        private void Picture_Up(object sender,MouseButtonEventArgs e)
        {
            if(!_gesture)return;Picture_Move(sender,e);FinishGesture();e.Handled=true;
        }
        private void Picture_LostCapture(object sender,MouseEventArgs e)
        {if(_gesture)FinishGesture();if(_panning){_panning=false;PictureArea.Cursor=Cursors.Cross;}}
        private void FinishGesture()
        {
            _gesture=false;_creating=false;_dragCorner=-1;PictureArea.ReleaseMouseCapture();
            DrawSelection();UpdatePreview();
        }
        private void Picture_SizeChanged(object sender,SizeChangedEventArgs e)
        {if(_frame!=null&&_handles[0]!=null)DrawSelection();}
        private void ResetSelection_Click(object sender,RoutedEventArgs e)
        {
            if(_busy)return;_gesture=false;PictureArea.ReleaseMouseCapture();_points=null;InvalidateResult();DrawSelection();
            SelectionInfo.Text="Zaznacz nowy obszar, a potem dopasuj narożniki.";
        }
        private void UpdatePreview()
        {
            InvalidateResult();
            if(_points==null||!PlatePerspective.IsValid(_points,_frame.PixelWidth,_frame.PixelHeight))
            {SelectionInfo.Text="Popraw narożniki — boki nie mogą się krzyżować, a obszar nie może być zbyt mały.";return;}
            try
            {
                var result=PlatePerspective.Rectify(_framePixels,_frame.PixelWidth,_frame.PixelHeight,_points);
                var image=BitmapSource.Create(result.Width,result.Height,96,96,PixelFormats.Bgra32,null,result.Pixels,result.Width*4);image.Freeze();
                RectifiedPreview.Source=image;
                if(result.Width<16||result.Height<8){SelectionInfo.Text="Wycinek jest zbyt mały. Zaznacz większy obszar.";return;}
                var encoder=new PngBitmapEncoder();encoder.Frames.Add(BitmapFrame.Create(image));
                using var png=new MemoryStream();encoder.Save(png);_rectifiedPng=png.ToArray();
                RecognizeButton.IsEnabled=true;SelectionInfo.Text=$"Po prostowaniu: {result.Width} × {result.Height} px. Możesz przesunąć każdy narożnik.";
                OcrStatus.Text="Sprawdź wyprostowany podgląd, potem kliknij Rozpoznaj. Numer nadal wymaga ręcznego potwierdzenia.";
            }
            catch(Exception ex){SelectionInfo.Text="Nie można wyprostować zaznaczenia: "+ex.Message;}
        }
        private async void Recognize_Click(object sender,RoutedEventArgs e)
        {
            if(_busy||_rectifiedPng==null)return;
            byte[] bytes=_rectifiedPng;
            _busy=true;ResetSelectionButton.IsEnabled=false;_workCts=new CancellationTokenSource();RecognizeButton.IsEnabled=false;PlateBox.IsEnabled=false;CandidatesBox.IsEnabled=false;ConfirmedBox.IsChecked=false;ConfirmedBox.IsEnabled=false;
            UpdateSave();OcrStatus.Text="Rozpoznawanie lokalne — poczekaj…";
            try
            {
                var results=await Task.Run(()=>PlateOcrService.Recognize(bytes,_workCts.Token));
                if(_closeAfterWork)return;
                CandidatesBox.ItemsSource=System.Linq.Enumerable.ToArray(System.Linq.Enumerable.Select(results,r=>r.Text));
                if(results.Count>0)
                {PlateBox.Text=results[0].Text;CandidatesBox.SelectedIndex=0;OcrStatus.Text="To propozycja OCR, nie pewny odczyt. Popraw numer i potwierdź zgodność z obrazem.";}
                else OcrStatus.Text="Nie znaleziono czytelnego numeru. Zaznacz dokładniej albo wpisz go ręcznie, jeśli jest czytelny.";
            }
            catch(OperationCanceledException){OcrStatus.Text="Anulowano.";}
            catch(Exception ex)
            {
                OcrStatus.Text="Nie udało się uruchomić OCR. Możesz wpisać numer ręcznie.";
                if(!_closeAfterWork)MessageBox.Show(this,ex.Message+"\n\nSprawdź katalogi x64 i tessdata. Silnik wymaga Microsoft Visual C++ Redistributable 2015–2022 (x64).","OCR lokalny",MessageBoxButton.OK,MessageBoxImage.Warning);
            }
            finally
            {
                _busy=false;_workCts.Dispose();_workCts=null;PlateBox.IsEnabled=true;CandidatesBox.IsEnabled=true;ConfirmedBox.IsEnabled=true;
                RecognizeButton.IsEnabled=_rectifiedPng!=null;ResetSelectionButton.IsEnabled=true;UpdateSave();
                if(_closeAfterWork)Close();
            }
        }
        private void Candidate_Changed(object sender,SelectionChangedEventArgs e)
        {if(CandidatesBox.SelectedItem is string text)PlateBox.Text=text;}
        private void Plate_Changed(object sender,TextChangedEventArgs e)
        {if(ConfirmedBox!=null)ConfirmedBox.IsChecked=false;UpdateSave();}
        private void Confirmation_Changed(object sender,RoutedEventArgs e)=>UpdateSave();
        private void UpdateSave()
        {
            if(SaveButton==null||PlateBox==null||ConfirmedBox==null)return;
            string text=PlateBox.Text.Trim().Replace(" ","").Replace("-","");
            SaveButton.IsEnabled=!_busy&&ConfirmedBox.IsChecked==true&&PlateOcrRules.IsValidManual(text);
        }
        private void Save_Click(object sender,RoutedEventArgs e)
        {
            UpdateSave();if(!SaveButton.IsEnabled)return;
            PlateNumber=PlateOcrService.Normalize(PlateBox.Text);DialogResult=true;
        }
        private void Cancel_Click(object sender,RoutedEventArgs e)=>Close();
        private void Window_Closing(object? sender,CancelEventArgs e)
        {if(_busy){e.Cancel=true;_closeAfterWork=true;_workCts?.Cancel();OcrStatus.Text="Kończenie bieżącego odczytu…";}}
    }
}
