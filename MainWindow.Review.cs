using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows;
using Forms=System.Windows.Forms;
namespace VideoPlayer
{
    public partial class MainWindow
    {
        private void RefreshRanges()
        {
            if(!ReferenceEquals(ClipRangesListBox.ItemsSource,_clips))ClipRangesListBox.ItemsSource=_clips;
            UpdateRangeHint();
        }
        private void UpdateRangeHint()
        {
            if(RangeFeedbackText==null)return;
            string a=_clipStart.HasValue?TimeSpan.FromSeconds(_clipStart.Value).ToString(@"hh\:mm\:ss\.fff"):"nie ustawiono";
            string b=_clipEnd.HasValue?TimeSpan.FromSeconds(_clipEnd.Value).ToString(@"hh\:mm\:ss\.fff"):"nie ustawiono";
            RangeFeedbackText.Text=$"Początek: {a} | Koniec: {b} | Fragmenty: {_clips.Count}";
        }
        private ClipRange? RangeError(string text)
        {
            StatusText.Text=text;RangeFeedbackText.Text=text;
            MessageBox.Show(this,text,"Lista fragmentów",MessageBoxButton.OK,MessageBoxImage.Information);
            return null;
        }
        private ClipRange? CurrentRange()
        {
            if(_shutdownRequested)return RangeError("Program jest zamykany.");
            if(_opening)return RangeError("Trwa otwieranie filmu. Poczekaj, aż zostanie wczytany.");
            if(string.IsNullOrWhiteSpace(_sourceFilePath)||!File.Exists(_sourceFilePath))return RangeError("Najpierw otwórz film.");
            string? error=ClipRangeValidator.Error(_clipStart,_clipEnd,PositionSlider.Maximum);
            if(error!=null)return RangeError(error);
            string name=ClipNameBox.Text.Trim();
            if(name.Length==0){name=$"Fragment {_clips.Count+1}";ClipNameBox.Text=name;}
            return new ClipRange{Name=name,Start=_clipStart!.Value,End=_clipEnd!.Value};
        }
        private void AddRange_Click(object sender,RoutedEventArgs e)
        {
            var range=CurrentRange();if(range==null)return;
            _clips.Add(range);RefreshRanges();
            ClipRangesListBox.SelectedItem=range;ClipRangesListBox.ScrollIntoView(range);
            StatusText.Text=$"Dodano fragment: {range.Name}. Liczba fragmentów: {_clips.Count}.";
            SaveSession();
        }
        private void UpdateRange_Click(object sender,RoutedEventArgs e)
        {
            if(ClipRangesListBox.SelectedItem is not ClipRange selected){RangeError("Najpierw wybierz fragment na liście.");return;}
            var range=CurrentRange();if(range==null)return;
            int i=_clips.IndexOf(selected);if(i<0){RangeError("Wybrany fragment nie jest już na liście.");return;}
            _clips[i]=range;RefreshRanges();ClipRangesListBox.SelectedItem=range;
            StatusText.Text="Zaktualizowano fragment: "+range.Name;SaveSession();
        }
        private async void LoadRange_Click(object sender,RoutedEventArgs e)
        {if(_opening||_shutdownRequested||ClipRangesListBox.SelectedItem is not ClipRange c)return;_clipStart=c.Start;_clipEnd=c.End;ClipNameBox.Text=c.Name;UpdateClipRangeText();await SendAsync("set_property","pause",true);await SeekAsync(c.Start);SaveSession();}
        private void DeleteRange_Click(object sender,RoutedEventArgs e)
        {if(ClipRangesListBox.SelectedItem is not ClipRange c)return;_clips.Remove(c);RefreshRanges();SaveSession();}
        private static string SafeName(string s)
        {
            foreach(char c in Path.GetInvalidFileNameChars())s=s.Replace(c,'_');
            s=s.Trim().TrimEnd('.');if(s.Length>70)s=s.Substring(0,70);return s.Length==0?"fragment":s;
        }
        private async void ExportAll_Click(object sender,RoutedEventArgs e)
        {
            if(!CanOperate()||_clips.Count==0){StatusText.Text="Dodaj fragmenty do listy.";return;}
            string source=_sourceFilePath!,profile=SelectedProfile;bool copy=StreamCopyCheckBox.IsChecked==true;
            if(!ConfirmCopy(copy))return;
            List<ClipRange> clips=_clips.Select(c=>new ClipRange{Name=c.Name,Start=c.Start,End=c.End}).ToList();
            using var dialog=new Forms.FolderBrowserDialog{Description="Wybierz folder nadrzędny eksportu",UseDescriptionForTitle=true};
            if(dialog.ShowDialog()!=Forms.DialogResult.OK)return;
            string folder=Path.Combine(dialog.SelectedPath,"VideoPlayer_"+DateTime.Now.ToString("yyyyMMdd_HHmmss")+"_"+Guid.NewGuid().ToString("N").Substring(0,6));
            await RunOperationAsync("Eksport seryjny",async ct=>
            {
                Directory.CreateDirectory(folder);
                for(int i=0;i<clips.Count;i++)
                {
                    ct.ThrowIfCancellationRequested();ClipRange c=clips[i];
                    string output=Path.Combine(folder,$"{i+1:000}_{SafeName(c.Name)}.mp4");
                    await _exports.ExportAsync(source,output,c.Start,c.End,profile,copy,OperationProgress(ct,$"Plik {i+1}/{clips.Count}: ",i,clips.Count),ct);
                }
                return $"Zapisano {clips.Count} fragmentów: {folder}";
            });
        }
        private void RecordExport(string source,string output,string operation,double? start,double? end,string details)
        {
            try
            {
                _historyService.AddExport(new ExportHistoryItem
                {
                    SourcePath=source,OutputPath=output,Operation=operation,
                    StartSeconds=start,EndSeconds=end,CreatedLocal=DateTime.Now,Details=details
                });
            }
            catch(Exception ex)
            {
                StatusText.Text="Plik został utworzony, ale nie udało się zapisać historii eksportu: "+ex.Message;
            }
        }
        private List<TimelineMarker> MarkerSnapshot()=>_timelineMarkers.OrderBy(m=>m.Seconds).Select(m=>new TimelineMarker{Id=m.Id,Seconds=m.Seconds,Description=m.Description,VehicleMake=m.VehicleMake}).ToList();
        private async void CsvReport_Click(object sender,RoutedEventArgs e)
        {
            if(!CanOperate()||_timelineMarkers.Count==0){StatusText.Text="Dodaj znaczniki zdarzeń do filmu.";return;}
            string source=_sourceFilePath!;var markers=MarkerSnapshot();
            string? output=ChooseOutput("Raport CSV",Path.GetFileNameWithoutExtension(source)+"_zdarzenia.csv","csv");if(output==null)return;
            await RunOperationAsync("Raport CSV",async ct=>{await _reports.CsvAsync(source,markers,output,ct);return "Zapisano raport: "+output;});
        }
        private async void PdfReport_Click(object sender,RoutedEventArgs e)
        {
            if(!CanOperate()||_timelineMarkers.Count==0){StatusText.Text="Dodaj znaczniki zdarzeń do filmu.";return;}
            string source=_sourceFilePath!;var markers=MarkerSnapshot();bool images=ReportThumbnailsCheckBox.IsChecked==true;
            var detailsDialog=new ReportDetailsWindow { Owner=this };
            if(detailsDialog.ShowDialog()!=true)return;
            ReportMetadata metadata=detailsDialog.Metadata;
            MediaInfoData? mediaInfo=_currentMediaInfo;
            List<ExportHistoryItem> exports=_historyService.LoadExports(source);
            string? output=ChooseOutput("Raport PDF",Path.GetFileNameWithoutExtension(source)+"_raport.pdf","pdf");if(output==null)return;
            await RunOperationAsync("Raport PDF",async ct=>
            {
                await _reports.PdfAsync(source,markers,output,images,metadata,mediaInfo,exports,OperationProgress(ct),ct);
                return "Zapisano raport PDF: "+output;
            });
        }
    }
}
