using System;
using System.ComponentModel;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media.Imaging;
namespace VideoPlayer
{
    public partial class PlateOcrWindow:Window
    {
        private readonly BitmapSource _frame;
        private Point? _anchor;
        private Int32Rect _selection;
        private bool _busy,_closeAfterWork;
        private CancellationTokenSource? _workCts;
        public string PlateNumber{get;private set;}="";
        internal PlateOcrWindow(BitmapSource frame,double seconds)
        {
            InitializeComponent();_frame=frame;FrameImage.Source=frame;
            FrameInfo.Text="Klatka "+TimeSpan.FromSeconds(seconds).ToString(@"hh\:mm\:ss\.fff")+" — zaznacz tablicę";
        }
        private (double Scale,double Left,double Top) Placement()
        {double s=Math.Min(PictureArea.ActualWidth/_frame.PixelWidth,PictureArea.ActualHeight/_frame.PixelHeight);return(s,(PictureArea.ActualWidth-_frame.PixelWidth*s)/2,(PictureArea.ActualHeight-_frame.PixelHeight*s)/2);}
        private Point Clamp(Point point)
        {var p=Placement();return new Point(Math.Clamp(point.X,p.Left,p.Left+_frame.PixelWidth*p.Scale),Math.Clamp(point.Y,p.Top,p.Top+_frame.PixelHeight*p.Scale));}
        private void Picture_Down(object sender,MouseButtonEventArgs e)
        {
            if(_busy)return;var point=e.GetPosition(PictureArea);var bounded=Clamp(point);
            if(point!=bounded)return;
            _anchor=bounded;_selection=Int32Rect.Empty;RecognizeButton.IsEnabled=false;
            PlateBox.Clear();CandidatesBox.ItemsSource=null;ConfirmedBox.IsChecked=false;
            SelectionBox.Visibility=Visibility.Visible;SelectionBox.Width=SelectionBox.Height=0;
            Canvas.SetLeft(SelectionBox,bounded.X);Canvas.SetTop(SelectionBox,bounded.Y);
            PictureArea.CaptureMouse();e.Handled=true;
        }
        private void Picture_Move(object sender,MouseEventArgs e)
        {
            if(_anchor is not Point start)return;var end=Clamp(e.GetPosition(PictureArea));
            Canvas.SetLeft(SelectionBox,Math.Min(start.X,end.X));Canvas.SetTop(SelectionBox,Math.Min(start.Y,end.Y));
            SelectionBox.Width=Math.Abs(end.X-start.X);SelectionBox.Height=Math.Abs(end.Y-start.Y);
        }
        private void Picture_Up(object sender,MouseButtonEventArgs e)
        {
            if(_anchor is not Point start)return;var end=Clamp(e.GetPosition(PictureArea));
            _anchor=null;PictureArea.ReleaseMouseCapture();var p=Placement();if(p.Scale<=0)return;
            var r=PlateOcrRules.Crop(_frame.PixelWidth,_frame.PixelHeight,PictureArea.ActualWidth,PictureArea.ActualHeight,start.X,start.Y,end.X,end.Y);
            _selection=new Int32Rect(r.X,r.Y,r.Width,r.Height);
            RecognizeButton.IsEnabled=_selection.Width>=16&&_selection.Height>=8;
            SelectionInfo.Text=RecognizeButton.IsEnabled?$"Zaznaczono {_selection.Width} × {_selection.Height} pikseli.":"Zaznaczenie jest zbyt małe. Zaznacz tablicę ponownie.";
        }
        private void Picture_SizeChanged(object sender,SizeChangedEventArgs e)
        {
            if(_frame==null||_selection.IsEmpty)return;
            var p=Placement();Canvas.SetLeft(SelectionBox,p.Left+_selection.X*p.Scale);Canvas.SetTop(SelectionBox,p.Top+_selection.Y*p.Scale);
            SelectionBox.Width=_selection.Width*p.Scale;SelectionBox.Height=_selection.Height*p.Scale;
        }
        private async void Recognize_Click(object sender,RoutedEventArgs e)
        {
            if(_busy||_selection.IsEmpty)return;
            var crop=new CroppedBitmap(_frame,_selection);var encoder=new PngBitmapEncoder();encoder.Frames.Add(BitmapFrame.Create(crop));
            using var png=new MemoryStream();encoder.Save(png);byte[] bytes=png.ToArray();
            _busy=true;_workCts=new CancellationTokenSource();RecognizeButton.IsEnabled=false;PlateBox.IsEnabled=false;CandidatesBox.IsEnabled=false;ConfirmedBox.IsChecked=false;ConfirmedBox.IsEnabled=false;
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
                RecognizeButton.IsEnabled=_selection.Width>=16&&_selection.Height>=8;UpdateSave();
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
