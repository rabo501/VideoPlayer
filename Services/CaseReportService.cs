using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using VideoPlayer;

namespace VideoPlayer.Services
{
    /// <summary>
    /// Collects case metadata, linked recordings, event markers and export history,
    /// then delegates document rendering to ReportService.
    /// </summary>
    internal sealed class CaseReportService
    {
        private readonly CaseRepository _cases;
        private readonly HistoryService _history;
        private readonly MediaInfoService _mediaInfo;
        private readonly ReportService _reports;

        public CaseReportService(CaseRepository cases, HistoryService history, MediaInfoService mediaInfo, ReportService reports)
        {
            _cases = cases ?? throw new ArgumentNullException(nameof(cases));
            _history = history ?? throw new ArgumentNullException(nameof(history));
            _mediaInfo = mediaInfo ?? throw new ArgumentNullException(nameof(mediaInfo));
            _reports = reports ?? throw new ArgumentNullException(nameof(reports));
        }

        public async Task<string> GenerateAsync(Guid caseId, bool pdf, string outputPath,
            bool includeThumbnails, IProgress<ExportProgress>? progress, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            CaseRecord record = _cases.Load(caseId)
                ?? throw new InvalidOperationException("Wybrana sprawa nie istnieje już w archiwum.");
            List<CaseReportSource> sources = await LoadSourcesAsync(record, cancellationToken).ConfigureAwait(false);

            if (pdf)
            {
                IProgress<ExportProgress> reportProgress = progress ?? new Progress<ExportProgress>(_ => { });
                await _reports.PdfCaseAsync(sources, outputPath, includeThumbnails,
                    CaseArchiveViewModel.ToReportMetadata(record), reportProgress, cancellationToken).ConfigureAwait(false);
            }
            else
            {
                await _reports.CsvCaseAsync(sources, outputPath,
                    CaseArchiveViewModel.ToReportMetadata(record), cancellationToken).ConfigureAwait(false);
            }

            int eventCount = sources.Sum(source => source.Markers.Count);
            return $"Zapisano raport sprawy {record.CaseNumber}: {sources.Count} nagrania, {eventCount} zdarzeń.";
        }

        private async Task<List<CaseReportSource>> LoadSourcesAsync(CaseRecord record, CancellationToken cancellationToken)
        {
            List<string> paths = _cases.LoadSources(record.Id);
            if (paths.Count == 0)
                throw new InvalidOperationException("Wybrana sprawa nie ma przypisanych nagrań. Otwórz nagranie i użyj opcji „Zapisz / aktualizuj sprawę”.");

            var result = new List<CaseReportSource>(paths.Count);
            foreach (string path in paths)
            {
                cancellationToken.ThrowIfCancellationRequested();
                MediaInfoData? info = null;
                if (File.Exists(path))
                {
                    try
                    {
                        info = await _mediaInfo.ReadAsync(path, cancellationToken).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException) { throw; }
                    catch
                    {
                        // A damaged/unavailable source must not block the remaining case report.
                    }
                }

                result.Add(new CaseReportSource
                {
                    Path = path,
                    Markers = _history.LoadMarkers(path),
                    Exports = _history.LoadExports(path),
                    MediaInfo = info
                });
            }
            return result;
        }
    }
}
