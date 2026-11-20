using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using Forms = System.Windows.Forms;
using VideoPlayer.Services;

namespace VideoPlayer
{
    public partial class MainWindow
    {
        private ReportMetadata CurrentReportMetadata() => new ReportMetadata
        {
            CaseNumber = ReportCaseNumberBox.Text.Trim(),
            CaseTitle = ReportCaseTitleBox.Text.Trim(),
            Location = ReportLocationBox.Text.Trim(),
            Notes = ReportNotesBox.Text.Trim()
        };

        private void RefreshCaseArchiveList(Guid? selectId = null)
        {
            if (_cases == null || ReportCasesBox == null) return;
            List<CaseRecord> cases = _cases.LoadAll();
            ReportCasesBox.ItemsSource = cases;
            ReportCasesBox.SelectedItem = selectId.HasValue
                ? cases.FirstOrDefault(item => item.Id == selectId.Value)
                : null;
        }

        private void SaveCase_Click(object sender, RoutedEventArgs e)
        {
            if (_cases == null) return;
            if (string.IsNullOrWhiteSpace(_sourceFilePath) || !File.Exists(_sourceFilePath))
            {
                CaseArchiveStatusText.Text = "Najpierw otwórz nagranie, które ma zostać powiązane ze sprawą.";
                StatusText.Text = CaseArchiveStatusText.Text;
                return;
            }

            try
            {
                ReportMetadata metadata = CurrentReportMetadata();
                if (string.IsNullOrWhiteSpace(metadata.CaseNumber) && ReportCasesBox.SelectedItem is CaseRecord selected)
                    metadata = ToReportMetadata(selected);
                CaseRecord saved = _cases.Save(metadata, _sourceFilePath);
                RefreshCaseArchiveList(saved.Id);
                CaseArchiveStatusText.Text = $"Zapisano sprawę {saved.CaseNumber}. Powiązane nagrania: {saved.SourceCount}.";
                StatusText.Text = "Dane sprawy zapisano w lokalnym archiwum.";
            }
            catch (Exception ex)
            {
                CaseArchiveStatusText.Text = "Nie udało się zapisać sprawy: " + ex.Message;
                StatusText.Text = CaseArchiveStatusText.Text;
                MessageBox.Show(this, ex.Message, "Archiwum spraw", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }

        private void LoadCase_Click(object sender, RoutedEventArgs e)
        {
            if (ReportCasesBox.SelectedItem is not CaseRecord selected)
            {
                CaseArchiveStatusText.Text = "Wybierz sprawę z listy.";
                return;
            }

            CaseRecord? record = _cases.Load(selected.Id);
            if (record == null)
            {
                RefreshCaseArchiveList();
                CaseArchiveStatusText.Text = "Wybrana sprawa nie istnieje już w bazie. Odświeżono listę.";
                return;
            }

            ApplyCaseMetadata(record);
            CaseArchiveStatusText.Text = $"Wczytano sprawę {record.CaseNumber}. Powiązane nagrania: {record.SourceCount}. Zapisz ponownie, aby dołączyć bieżące nagranie.";
            StatusText.Text = "Wczytano dane sprawy z lokalnego archiwum.";
        }

        private void ReportCasesBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (ReportCasesBox.SelectedItem is CaseRecord selected)
            {
                ApplyCaseMetadata(selected);
                CaseArchiveStatusText.Text = $"Wybrano sprawę {selected.CaseNumber}. Powiązane nagrania: {selected.SourceCount}. Otwarcie kolejnego pliku i zapis dołączy go do tej sprawy.";
            }
        }

        private void ClearCaseSelection_Click(object sender, RoutedEventArgs e)
        {
            ReportCasesBox.SelectedItem = null;
            ReportCaseNumberBox.Clear();
            ReportCaseTitleBox.Clear();
            ReportLocationBox.Clear();
            ReportNotesBox.Clear();
            CaseArchiveStatusText.Text = "Wybór sprawy wyczyszczono. Raport będzie dotyczył bieżącego nagrania.";
        }

        private void ApplyCaseMetadata(CaseRecord record)
        {
            ReportCaseNumberBox.Text = record.CaseNumber;
            ReportCaseTitleBox.Text = record.Title;
            ReportLocationBox.Text = record.Location;
            ReportNotesBox.Text = record.Notes;
        }

        private void DeleteCase_Click(object sender, RoutedEventArgs e)
        {
            if (ReportCasesBox.SelectedItem is not CaseRecord selected)
            {
                CaseArchiveStatusText.Text = "Wybierz sprawę, którą chcesz usunąć.";
                return;
            }

            MessageBoxResult answer = MessageBox.Show(this,
                $"Czy na pewno usunąć sprawę {selected.CaseNumber} wraz z jej powiązaniami do nagrań?\n\nPliki nagrań, znaczniki zdarzeń i pliki eksportów nie zostaną usunięte.",
                "Usuwanie sprawy", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No);
            if (answer != MessageBoxResult.Yes) return;

            try
            {
                bool deleted = _cases.Delete(selected.Id);
                ReportCaseNumberBox.Clear();
                ReportCaseTitleBox.Clear();
                ReportLocationBox.Clear();
                ReportNotesBox.Clear();
                RefreshCaseArchiveList();
                CaseArchiveStatusText.Text = deleted
                    ? $"Usunięto sprawę {selected.CaseNumber}. Nagrania i ich dane pozostały bez zmian."
                    : "Sprawa nie istnieje już w archiwum.";
                StatusText.Text = CaseArchiveStatusText.Text;
            }
            catch (Exception ex)
            {
                CaseArchiveStatusText.Text = "Nie udało się usunąć sprawy: " + ex.Message;
                MessageBox.Show(this, ex.Message, "Archiwum spraw", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private async Task<List<CaseReportSource>> BuildCaseReportSourcesAsync(CaseRecord record, CancellationToken ct)
        {
            List<string> paths = _cases.LoadSources(record.Id);
            if (paths.Count == 0)
                throw new InvalidOperationException("Wybrana sprawa nie ma przypisanych nagrań. Otwórz nagranie i użyj opcji „Zapisz / aktualizuj sprawę”.");

            var result = new List<CaseReportSource>();
            for (int i = 0; i < paths.Count; i++)
            {
                ct.ThrowIfCancellationRequested();
                string path = paths[i];
                MediaInfoData? info = null;
                if (File.Exists(path))
                {
                    try { info = await _mediaInfoService.ReadAsync(path, ct).ConfigureAwait(false); }
                    catch (OperationCanceledException) { throw; }
                    catch { /* A technical-info error for one source must not block the case report. */ }
                }
                result.Add(new CaseReportSource
                {
                    Path = path,
                    Markers = _historyService.LoadMarkers(path),
                    Exports = _historyService.LoadExports(path),
                    MediaInfo = info
                });
            }
            return result;
        }

        private static ReportMetadata ToReportMetadata(CaseRecord record) => new ReportMetadata
        {
            CaseNumber = record.CaseNumber,
            CaseTitle = record.Title,
            Location = record.Location,
            Notes = record.Notes
        };

        private ClipRange? RangeError(string text)
        {
            StatusText.Text = text;
            ViewModel.Export.SetRangeErrorText(text);
            MessageBox.Show(this, text, "Lista fragmentów", MessageBoxButton.OK, MessageBoxImage.Information);
            return null;
        }

        private ClipRange? CurrentRange()
        {
            if (_shutdownRequested) return RangeError("Program jest zamykany.");
            if (_opening) return RangeError("Trwa otwieranie filmu. Poczekaj, aż zostanie wczytany.");
            if (string.IsNullOrWhiteSpace(_sourceFilePath) || !File.Exists(_sourceFilePath)) return RangeError("Najpierw otwórz film.");
            string? error = ClipRangeValidator.Error(ViewModel.Export.ClipStart, ViewModel.Export.ClipEnd, PositionSlider.Maximum);
            if (error != null) return RangeError(error);
            string name = ViewModel.Export.ClipName.Trim();
            if (name.Length == 0)
            {
                name = $"Fragment {ViewModel.Export.Clips.Count + 1}";
                ViewModel.Export.ClipName = name;
            }
            return new ClipRange
            {
                Name = name,
                Start = ViewModel.Export.ClipStart!.Value,
                End = ViewModel.Export.ClipEnd!.Value
            };
        }

        private void AddRange_Click(object sender, RoutedEventArgs e)
        {
            var range = CurrentRange();
            if (range == null) return;
            ViewModel.Export.AddClip(range);
            ClipRangesListBox.ScrollIntoView(range);
            StatusText.Text = $"Dodano fragment: {range.Name}. Liczba fragmentów: {ViewModel.Export.Clips.Count}.";
            SaveSession();
        }

        private void UpdateRange_Click(object sender, RoutedEventArgs e)
        {
            if (ViewModel.Export.SelectedClip is not ClipRange selected) { RangeError("Najpierw wybierz fragment na liście."); return; }
            var range = CurrentRange();
            if (range == null) return;
            if (!ViewModel.Export.ReplaceClip(selected, range)) { RangeError("Wybrany fragment nie jest już na liście."); return; }
            StatusText.Text = "Zaktualizowano fragment: " + range.Name;
            SaveSession();
        }

        private async void LoadRange_Click(object sender, RoutedEventArgs e)
        {
            if (_opening || _shutdownRequested || ViewModel.Export.SelectedClip is not ClipRange c) return;
            ViewModel.Export.ClipName = c.Name;
            ViewModel.Export.SetClipRange(c.Start, c.End);
            UpdateTimelineOverlay();
            await SendAsync("set_property", "pause", true);
            await SeekAsync(c.Start);
            SaveSession();
        }

        private void DeleteRange_Click(object sender, RoutedEventArgs e)
        {
            if (ViewModel.Export.SelectedClip is not ClipRange c) return;
            ViewModel.Export.RemoveClip(c);
            SaveSession();
        }

        private async void ExportAll_Click(object sender, RoutedEventArgs e)
        {
            if (!CanOperate() || ViewModel.Export.Clips.Count == 0) { StatusText.Text = "Dodaj fragmenty do listy."; return; }
            string source = _sourceFilePath!, profile = ViewModel.Export.SelectedProfileTag;
            bool copy = ViewModel.Export.UseStreamCopy;
            if (!ConfirmCopy(copy)) return;
            List<ClipRange> clips = ViewModel.Export.Clips.Select(c => new ClipRange { Name = c.Name, Start = c.Start, End = c.End }).ToList();
            using var dialog = new Forms.FolderBrowserDialog { Description = "Wybierz folder nadrzędny eksportu", UseDescriptionForTitle = true };
            if (dialog.ShowDialog() != Forms.DialogResult.OK) return;
            string folder = Path.Combine(dialog.SelectedPath, "VideoPlayer_" + DateTime.Now.ToString("yyyyMMdd_HHmmss") + "_" + Guid.NewGuid().ToString("N").Substring(0, 6));
            await RunOperationAsync("Eksport seryjny", async ct =>
            {
                Directory.CreateDirectory(folder);
                for (int i = 0; i < clips.Count; i++)
                {
                    ct.ThrowIfCancellationRequested();
                    ClipRange c = clips[i];
                    string output = Path.Combine(folder, $"{i + 1:000}_{ExportWorkspaceService.SanitizeFileName(c.Name)}.mp4");
                    await _exports.ExportAsync(source, output, c.Start, c.End, profile, copy, OperationProgress(ct, $"Plik {i + 1}/{clips.Count}: ", i, clips.Count), ct);
                    RecordExport(source, output, "Eksport seryjny", c.Start, c.End, $"Fragment: {c.Name}; profil: {profile}; kopiowanie strumieni: {(copy ? "tak" : "nie")}; rozmiar: {ExportWorkspaceService.FormatFileSize(new FileInfo(output).Length)}");
                }
                return $"Zapisano {clips.Count} fragmentów: {folder}";
            });
        }

        private void RecordExport(string source, string output, string operation, double? start, double? end, string details)
        {
            try
            {
                _historyService.AddExport(new ExportHistoryItem
                {
                    SourcePath = source,
                    OutputPath = output,
                    Operation = operation,
                    StartSeconds = start,
                    EndSeconds = end,
                    CreatedLocal = DateTime.Now,
                    Details = details
                });
            }
            catch (Exception ex)
            {
                StatusText.Text = "Plik został utworzony, ale nie udało się zapisać historii eksportu: " + ex.Message;
            }
        }

        private List<TimelineMarker> MarkerSnapshot() => ViewModel.EventCenter.BuildSortedSnapshot();

        private async void CsvReport_Click(object sender, RoutedEventArgs e)
        {
            CaseRecord? selectedCase = ReportCasesBox.SelectedItem as CaseRecord;
            if (selectedCase == null && (string.IsNullOrWhiteSpace(_sourceFilePath) || !File.Exists(_sourceFilePath)))
            {
                StatusText.Text = "Wybierz zapisaną sprawę albo otwórz nagranie.";
                return;
            }
            if (selectedCase == null && ViewModel.EventCenter.AllMarkers.Count == 0)
            {
                StatusText.Text = "Dodaj znaczniki zdarzeń do filmu.";
                return;
            }

            string defaultName = selectedCase != null
                ? "Sprawa_" + ExportWorkspaceService.SanitizeFileName(selectedCase.CaseNumber) + "_raport.csv"
                : Path.GetFileNameWithoutExtension(_sourceFilePath) + "_zdarzenia.csv";
            string? output = ChooseOutput("Raport CSV", defaultName, "csv");
            if (output == null) return;

            await RunOperationAsync("Raport CSV", async ct =>
            {
                if (selectedCase != null)
                {
                    CaseRecord record = _cases.Load(selectedCase.Id)
                        ?? throw new InvalidOperationException("Wybrana sprawa nie istnieje już w archiwum.");
                    List<CaseReportSource> sources = await BuildCaseReportSourcesAsync(record, ct).ConfigureAwait(false);
                    await _reports.CsvCaseAsync(sources, output, ToReportMetadata(record), ct).ConfigureAwait(false);
                    int eventCount = sources.Sum(s => s.Markers.Count);
                    return $"Zapisano raport sprawy {record.CaseNumber}: {sources.Count} nagrania, {eventCount} zdarzeń.";
                }

                string source = _sourceFilePath!;
                await _reports.CsvAsync(source, MarkerSnapshot(), output, CurrentReportMetadata(),
                    ViewModel.Export.CurrentMediaInfo, _historyService.LoadExports(source), ct).ConfigureAwait(false);
                return "Zapisano raport CSV: " + output;
            }, requireSource: selectedCase == null);
        }

        private async void PdfReport_Click(object sender, RoutedEventArgs e)
        {
            CaseRecord? selectedCase = ReportCasesBox.SelectedItem as CaseRecord;
            if (selectedCase == null && (string.IsNullOrWhiteSpace(_sourceFilePath) || !File.Exists(_sourceFilePath)))
            {
                StatusText.Text = "Wybierz zapisaną sprawę albo otwórz nagranie.";
                return;
            }
            if (selectedCase == null && ViewModel.EventCenter.AllMarkers.Count == 0)
            {
                StatusText.Text = "Dodaj znaczniki zdarzeń do filmu.";
                return;
            }

            bool images = ReportThumbnailsCheckBox.IsChecked == true;
            string defaultName = selectedCase != null
                ? "Sprawa_" + ExportWorkspaceService.SanitizeFileName(selectedCase.CaseNumber) + "_raport.pdf"
                : Path.GetFileNameWithoutExtension(_sourceFilePath) + "_raport.pdf";
            string? output = ChooseOutput("Raport PDF", defaultName, "pdf");
            if (output == null) return;

            await RunOperationAsync("Raport PDF", async ct =>
            {
                // Create Progress on the WPF UI thread before any ConfigureAwait(false).
                // Its callback then posts UI updates to the Dispatcher instead of touching
                // WPF controls from a worker thread.
                IProgress<ExportProgress> progress = OperationProgress(ct);
                if (selectedCase != null)
                {
                    CaseRecord record = _cases.Load(selectedCase.Id)
                        ?? throw new InvalidOperationException("Wybrana sprawa nie istnieje już w archiwum.");
                    List<CaseReportSource> sources = await BuildCaseReportSourcesAsync(record, ct).ConfigureAwait(false);
                    await _reports.PdfCaseAsync(sources, output, images, ToReportMetadata(record),
                        progress, ct).ConfigureAwait(false);
                    int eventCount = sources.Sum(s => s.Markers.Count);
                    return $"Zapisano raport sprawy {record.CaseNumber}: {sources.Count} nagrania, {eventCount} zdarzeń.";
                }

                string source = _sourceFilePath!;
                await _reports.PdfAsync(source, MarkerSnapshot(), output, images, CurrentReportMetadata(),
                    ViewModel.Export.CurrentMediaInfo, _historyService.LoadExports(source), progress, ct).ConfigureAwait(false);
                return "Zapisano raport PDF: " + output;
            }, requireSource: selectedCase == null);
        }
    }
}
