using System;
using System.IO;
using System.Windows;
using System.Windows.Media.Imaging;
namespace VideoPlayer
{
    public partial class MainWindow
    {
        private async void PlateOcr_Click(object sender,RoutedEventArgs e)
        {
            if(!CanOperate()||!_ready||_screenshotBusy||!PositionSlider.IsEnabled)
            {StatusText.Text="Najpierw otwórz nagranie i poczekaj na zakończenie innych operacji.";return;}
            PlateOcrButton.IsEnabled=false;_screenshotBusy=true;ScreenshotButton.IsEnabled=false;
            try
            {
                await RunOperationAsync("Rozpoznawanie tablicy — lokalnie",async ct=>
                {
                    string folder=Path.Combine(Path.GetTempPath(),"VideoPlayer_OCR_"+Guid.NewGuid().ToString("N"));
                    Directory.CreateDirectory(folder);
                    try
                    {
                        await _mpvService.RequestAsync(new object[]{"set_property","pause",true},ct);
                        var position=await _mpvService.RequestAsync(new object[]{"get_property","time-pos"},ct);
                        double seconds=position.GetDouble();
                        string framePath=Path.Combine(folder,"frame.png");
                        await _mpvService.RequestAsync(new object[]{"screenshot-to-file",framePath,"video"},ct);
                        ct.ThrowIfCancellationRequested();
                        var frame=new BitmapImage();
                        frame.BeginInit();frame.CacheOption=BitmapCacheOption.OnLoad;
                        frame.UriSource=new Uri(framePath);frame.EndInit();frame.Freeze();
                        if(_shutdownRequested||_closing)return "Anulowano rozpoznawanie tablicy.";
                        var dialog=new PlateOcrWindow(frame,seconds){Owner=this};
                        if(dialog.ShowDialog()!=true)return "Anulowano. Nagranie pozostaje zatrzymane.";
                        ct.ThrowIfCancellationRequested();
                        string description="Tablica rejestracyjna: "+dialog.PlateNumber+" (numer potwierdzony ręcznie)";
                        var marker=new TimelineMarker{Id=Guid.NewGuid(),Seconds=seconds,Description=description};
                        _timelineMarkers.Add(marker);SaveTimelineMarkers();RefreshTimelineMarkerList();UpdateTimelineOverlay();
                        TimelineMarkersListBox.SelectedItem=marker;TimelineMarkersListBox.ScrollIntoView(marker);
                        MarkerDescriptionBox.Text=description;
                        return "Dodano zdarzenie: "+dialog.PlateNumber+" — "+TimeSpan.FromSeconds(seconds).ToString(@"hh\:mm\:ss\.fff");
                    }
                    finally{try{Directory.Delete(folder,true);}catch(IOException){}catch(UnauthorizedAccessException){}}
                });
            }
            finally{_screenshotBusy=false;if(!_closing){PlateOcrButton.IsEnabled=true;ScreenshotButton.IsEnabled=true;}}
        }
    }
}
