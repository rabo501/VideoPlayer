using System;
using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
namespace VideoPlayer
{
    public partial class MainWindow
    {
        private void HandleMessage(JsonElement message)
        {
            if(_closing)return;
            if(message.TryGetProperty("event",out var ev)&&ev.GetString()=="client-message"&&message.TryGetProperty("args",out var args)&&args.GetArrayLength()>=2&&args[0].GetString()=="vp-action")
            {_=DispatchActionAsync(args[1].GetString()??"");return;}
            if(!message.TryGetProperty("event",out ev)||ev.GetString()!="property-change"||!message.TryGetProperty("name",out var name)||!message.TryGetProperty("data",out var d))return;
            string? prop=name.GetString();
            if(prop=="pause"&&(d.ValueKind==JsonValueKind.True||d.ValueKind==JsonValueKind.False))
            {_paused=d.GetBoolean();PlayButton.Content=_paused?"Play":"Pauza";return;}
            if(prop=="eof-reached") {_eofReached=d.ValueKind==JsonValueKind.True;return;}
            if(d.ValueKind!=JsonValueKind.Number||!d.TryGetDouble(out double value)||!double.IsFinite(value))return;
            switch(prop)
            {
                case "duration":if(value>0)SetDuration(value);break;
                case "time-pos":if(!_dragging){_updatingSlider=true;PositionSlider.Value=Math.Clamp(value,0,PositionSlider.Maximum);_updatingSlider=false;PositionText.Text=FormatTime(value);}break;
                case "video-zoom":_zoom=value;break;
                case "video-pan-x":_panX=value;break;
                case "video-pan-y":_panY=value;break;
                case "panscan":_panscan=value;break;
            }
        }
        private void SetDuration(double value)
        {_updatingSlider=true;PositionSlider.Maximum=value;PositionSlider.IsEnabled=true;_updatingSlider=false;DurationText.Text=FormatTime(value);UpdateTimelineOverlay();}
        private static string FormatTime(double seconds)
        {var t=TimeSpan.FromSeconds(Math.Max(0,seconds));return $"{(int)t.TotalHours:00}:{t.Minutes:00}:{t.Seconds:00}";}
        private async Task SendAsync(params object[] command)
        {
            if(!_ready||_closing)return;
            try{await _mpvService.SendAsync(command);}catch(Exception ex){if(!_mpvService.IsReady)_ready=false;if(!_closing)StatusText.Text=ex.Message;}
        }
        private async void Open_Click(object sender,RoutedEventArgs e)
        {
            if(!_ready||_opening||_exporting||_shutdownRequested){StatusText.Text="Poczekaj na zakończenie operacji lub uruchom mpv.";return;}
            var d=new Microsoft.Win32.OpenFileDialog{Title="Wybierz film",Filter="Pliki wideo|*.mp4;*.mkv;*.avi;*.mov;*.webm;*.m4v;*.wmv;*.ts|Wszystkie pliki|*.*"};
            if(d.ShowDialog(this)==true)await OpenMediaAsync(d.FileName);
        }
        private async Task OpenMediaAsync(string path)
        {
            if(!_ready||_opening||_exporting||_shutdownRequested)return;
            if(!File.Exists(path)){StatusText.Text="Plik nie istnieje.";return;}
            SaveSession();_opening=true;
            try
            {
                ReviewSession? session=null;
                try{session=_reviews.Load(path);}catch(Exception ex){MessageBox.Show(this,"Nie odczytano sesji: "+ex.Message,"Sesja");}
                _fileLoaded=new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                await _mpvService.SendAsync("set_property","pause",true);
                _sourceFilePath=path;_fileName=Path.GetFileName(path);_clipStart=null;_clipEnd=null;_currentMediaInfo=null;_lastExportSummary=null;_clips.Clear();
                _updatingSlider=true;PositionSlider.Value=0;PositionSlider.Maximum=1;PositionSlider.IsEnabled=false;_updatingSlider=false;
                _eofReached=false;_dragging=false;
                await _mpvService.SendAsync("loadfile",path,"replace");
                using var timeout=new CancellationTokenSource(TimeSpan.FromSeconds(20));
                await _fileLoaded.Task.WaitAsync(timeout.Token);
                var duration=await _mpvService.RequestAsync(new object[]{"get_property","duration"});
                if(duration.ValueKind!=JsonValueKind.Number||!duration.TryGetDouble(out double length)||!double.IsFinite(length)||length<=0)throw new IOException("Nie można ustalić długości filmu.");
                SetDuration(length);LoadTimelineMarkers();AddRecentFile(path);
                bool restore=RestoreSessionCheckBox.IsChecked==true&&session!=null;
                _restoring=true;
                try
                {
                    // Named ranges belong to the media, independent of the optional playback restoration.
                    if(session!=null)foreach(var c in session.Clips??new())if(double.IsFinite(c.Start)&&double.IsFinite(c.End)&&c.Start>=0&&c.End>c.Start&&c.End<=length)_clips.Add(c);
                    if(restore)
                    {
                        if(session!.ClipStart is double a&&double.IsFinite(a)&&a>=0&&a<=length)_clipStart=a;
                        if(session.ClipEnd is double b&&double.IsFinite(b)&&_clipStart.HasValue&&b>_clipStart.Value&&b<=length)_clipEnd=b;
                        foreach(ComboBoxItem item in SpeedBox.Items)if(double.TryParse(item.Tag?.ToString(),NumberStyles.Float,CultureInfo.InvariantCulture,out double speed)&&Math.Abs(speed-session.Speed)<0.001){SpeedBox.SelectedItem=item;break;}
                        await SetViewAsync(Math.Clamp(Finite(session.Zoom),0,4),Math.Clamp(Finite(session.PanX),-4,4),Math.Clamp(Finite(session.PanY),-4,4),Math.Clamp(Finite(session.Panscan),0,1));
                        await _mpvService.SendAsync("seek",Math.Clamp(Finite(session.Position),0,Math.Max(0,length-0.001)),"absolute+exact");
                        _updatingSlider=true;PositionSlider.Value=Math.Clamp(Finite(session.Position),0,length);_updatingSlider=false;
                        VideoScaleBox.SelectedIndex=-1;
                    }
                    else{await SetViewAsync(0,0,0,0);VideoScaleBox.SelectedIndex=0;}
                    await _mpvService.SendAsync("set_property","speed",GetSelectedSpeed());
                    _paused=restore?session!.Paused:false;
                    await _mpvService.SendAsync("set_property","pause",_paused);
                    PlayButton.Content=_paused?"Play":"Pauza";
                }
                finally{_restoring=false;}
                RefreshRanges();UpdateClipRangeText();
                MediaInfoText.Text="Odczytywanie parametrów…";_=UpdateMediaInfoAsync(path);
                Title="VideoPlayer 1.3.2 — "+_fileName;
                StatusText.Text=restore?"Przywrócono sesję: "+_fileName:_fileName;
            }
            catch(Exception ex)
            {
                _clipStart=null;_clipEnd=null;_sourceFilePath=null;_clips.Clear();RefreshRanges();
                PositionSlider.IsEnabled=false;StatusText.Text="Nie otwarto filmu: "+ex.Message;
                MessageBox.Show(this,ex.Message,"Otwieranie filmu");
            }
            finally{_fileLoaded=null;_opening=false;}
        }
        private static double Finite(double n)=>double.IsFinite(n)?n:0;
        private async void PlayPause_Click(object sender,RoutedEventArgs e)
        {if(!_ready||_opening)return;if(_eofReached){await SeekAsync(0);await SendAsync("set_property","pause",false);}else await SendAsync("cycle","pause");}
        private async void PreviousFrame_Click(object sender,RoutedEventArgs e)
        {if(_opening)return;await SendAsync("set_property","pause",true);await SendAsync("frame-back-step");}
        private async void NextFrame_Click(object sender,RoutedEventArgs e)
        {if(_opening)return;await SendAsync("set_property","pause",true);await SendAsync("frame-step");}
        private async void Speed_Changed(object sender,SelectionChangedEventArgs e)
        {if(_ready&&!_opening&&!_restoring)await SendAsync("set_property","speed",GetSelectedSpeed());}
        private double GetSelectedSpeed()=>SpeedBox.SelectedItem is ComboBoxItem i&&double.TryParse(i.Tag?.ToString(),NumberStyles.Float,CultureInfo.InvariantCulture,out double v)?v:1;
        private void PositionSlider_DragStarted(object sender,DragStartedEventArgs e)=>_dragging=true;
        private async void PositionSlider_DragCompleted(object sender,DragCompletedEventArgs e)
        {try{await SeekAsync(PositionSlider.Value);}finally{_dragging=false;}}
        private async void PositionSlider_ValueChanged(object sender,RoutedPropertyChangedEventArgs<double> e)
        {if(_updatingSlider||!_ready||_opening||!PositionSlider.IsEnabled)return;PositionText.Text=FormatTime(e.NewValue);if(!_dragging)await SeekAsync(e.NewValue);}
        private async Task SeekAsync(double seconds)
        {if(_ready&&!_opening&&PositionSlider.IsEnabled)await SendAsync("seek",Math.Clamp(seconds,0,PositionSlider.Maximum),"absolute+exact");}
        private async void Screenshot_Click(object sender,RoutedEventArgs e)
        {
            if(!_ready||_opening||_screenshotBusy||!PositionSlider.IsEnabled||_sourceFilePath==null)return;
            _screenshotBusy=true;ScreenshotButton.IsEnabled=false;
            try
            {
                await _mpvService.SendAsync("set_property","pause",true);
                string stamp=TimeSpan.FromSeconds(PositionSlider.Value).ToString(@"d\.hh\-mm\-ss\-fff",CultureInfo.InvariantCulture);
                var d=new Microsoft.Win32.SaveFileDialog{Title="Zapisz klatkę JPG",Filter="JPEG|*.jpg",DefaultExt=".jpg",AddExtension=true,OverwritePrompt=true,FileName=Path.GetFileNameWithoutExtension(_sourceFilePath)+"_"+stamp+".jpg"};
                if(d.ShowDialog(this)!=true)return;
                await _mpvService.RequestAsync(new object[]{"screenshot-to-file",d.FileName,"video"});
                if(!File.Exists(d.FileName))throw new IOException("Nie powstał plik JPG.");
                AddRecentScreenshot(d.FileName);StatusText.Text="Zapisano: "+d.FileName;
            }
            catch(Exception ex){if(!_closing)MessageBox.Show(this,ex.Message,"Zapis klatki");}
            finally{_screenshotBusy=false;if(!_closing)ScreenshotButton.IsEnabled=true;}
        }
    }
}
