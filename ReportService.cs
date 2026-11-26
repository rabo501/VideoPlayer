using System.Linq;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Security.Principal;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using PdfSharp;
using PdfSharp.Drawing;
using PdfSharp.Pdf;

namespace VideoPlayer
{
    internal sealed class ReportService
    {
        private readonly ExportService _ffmpeg;
        public ReportService(ExportService ffmpeg)=>_ffmpeg=ffmpeg;
        private static string Timestamp(double seconds)
        {var t=TimeSpan.FromSeconds(Math.Max(0,seconds));return $"{(int)t.TotalHours:00}:{t.Minutes:00}:{t.Seconds:00}.{t.Milliseconds:000}";}
        private static string CsvCell(string text)
        {
            text??="";string trimmed=text.TrimStart();
            if(trimmed.Length>0&&"=+-@".Contains(trimmed[0]))text="'"+text;
            return "\""+text.Replace("\"","\"\"")+"\"";
        }
        public async Task CsvAsync(string source,List<TimelineMarker> markers,string output,ReportMetadata metadata,MediaInfoData? mediaInfo,List<ExportHistoryItem> exports,CancellationToken ct)
        {
            string temp=ExportService.Temporary(output);
            try
            {
                await using(var stream=new FileStream(temp,FileMode.CreateNew,FileAccess.Write,FileShare.None,4096,true))
                await using(var writer=new StreamWriter(stream,new UTF8Encoding(true)))
                {
                    await writer.WriteLineAsync("Rodzaj;Numer sprawy;Tytuł sprawy;Miejsce;Uwagi;Plik źródłowy;Czas;Koniec;Sekundy;Sekundy końca;Typ zdarzenia;Opis;Marka pojazdu;Powiązane zdarzenia ruchu;Data eksportu;Operacja;Plik wynikowy;Szczegóły;Kodek wideo;Rozdzielczość;FPS;Kodek audio;Czas trwania nagrania;Rozmiar źródła (B)".AsMemory(),ct);
                    string caseNumber=CsvCell(metadata.CaseNumber);
                    string caseTitle=CsvCell(metadata.CaseTitle);
                    string location=CsvCell(metadata.Location);
                    string notes=CsvCell(metadata.Notes);
                    string sourceCell=CsvCell(source);
                    string videoCodec=CsvCell(mediaInfo?.VideoCodec ?? "");
                    string resolution=CsvCell(mediaInfo?.Resolution ?? "");
                    string fps=CsvCell(mediaInfo?.Fps ?? "");
                    string audioCodec=CsvCell(mediaInfo?.AudioCodec ?? "");
                    string duration=CsvCell(mediaInfo == null ? "" : Timestamp(mediaInfo.Duration));
                    string fileSize=CsvCell(mediaInfo == null ? "" : mediaInfo.FileSize.ToString(CultureInfo.InvariantCulture));
                    string[] mediaFields={videoCodec,resolution,fps,audioCodec,duration,fileSize};

                    foreach(var marker in markers.OrderBy(m=>m.Seconds))
                    {
                        ct.ThrowIfCancellationRequested();
                        string endText=marker.EndSeconds.HasValue?Timestamp(marker.EndSeconds.Value):"";
                        string endSeconds=marker.EndSeconds?.ToString("0.###",CultureInfo.InvariantCulture)??"";
                        string line=string.Join(";",new[]{
                            CsvCell("Zdarzenie"),caseNumber,caseTitle,location,notes,sourceCell,
                            CsvCell(Timestamp(marker.Seconds)),CsvCell(endText),
                            CsvCell(marker.Seconds.ToString("0.###",CultureInfo.InvariantCulture)),
                            CsvCell(endSeconds),CsvCell(marker.EventType),CsvCell(marker.Description),
                            CsvCell(marker.VehicleMake),CsvCell(marker.RelatedMotionCount.ToString(CultureInfo.InvariantCulture)),
                            CsvCell(""),CsvCell(""),CsvCell(""),CsvCell("")
                        }.Concat(mediaFields));
                        await writer.WriteLineAsync(line.AsMemory(),ct);
                    }

                    foreach(var export in exports.OrderBy(e=>e.CreatedLocal))
                    {
                        ct.ThrowIfCancellationRequested();
                        string rangeStart=export.StartSeconds.HasValue?Timestamp(export.StartSeconds.Value):"";
                        string rangeEnd=export.EndSeconds.HasValue?Timestamp(export.EndSeconds.Value):"";
                        string line=string.Join(";",new[]{
                            CsvCell("Eksport"),caseNumber,caseTitle,location,notes,sourceCell,
                            CsvCell(rangeStart),CsvCell(rangeEnd),
                            CsvCell(export.StartSeconds?.ToString("0.###",CultureInfo.InvariantCulture)??""),
                            CsvCell(export.EndSeconds?.ToString("0.###",CultureInfo.InvariantCulture)??""),
                            CsvCell(""),CsvCell(""),CsvCell(""),CsvCell(""),
                            CsvCell(export.CreatedLocal.ToString("yyyy-MM-dd HH:mm:ss",CultureInfo.InvariantCulture)),
                            CsvCell(export.Operation),CsvCell(export.OutputPath),CsvCell(export.Details)
                        }.Concat(mediaFields));
                        await writer.WriteLineAsync(line.AsMemory(),ct);
                    }
                }
                ExportService.Commit(temp,output,ct);
            }finally{ExportService.Cleanup(temp);}
        }
        public async Task PdfAsync(string source,List<TimelineMarker> markers,string output,bool thumbnails,ReportMetadata metadata,MediaInfoData? mediaInfo,List<ExportHistoryItem> exports,IProgress<ExportProgress> progress,CancellationToken ct)
        {
            // Resolve the full display name before switching to the dedicated STA thread.
            string generatedBy=GetReportUser();
            string scratch=Path.Combine(Path.GetTempPath(),"VideoPlayerReport_"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(scratch);
            string temp=ExportService.Temporary(output);
            var images=new ConcurrentDictionary<Guid,string>();
            try
            {
                if(thumbnails&&markers.Count>0)
                {
                    int completed=0;
                    int maxDegree=Math.Clamp(Environment.ProcessorCount/2,2,4);
                    var jobs=markers.Select((marker,index)=>(Index:index,Marker:marker));
                    await Parallel.ForEachAsync(jobs,new ParallelOptions{MaxDegreeOfParallelism=maxDegree,CancellationToken=ct},async (job,token)=>
                    {
                        string jpg=Path.Combine(scratch,job.Index.ToString(CultureInfo.InvariantCulture)+".jpg");
                        await _ffmpeg.ThumbnailAsync(source,job.Marker.Seconds,jpg,token).ConfigureAwait(false);
                        images[job.Marker.Id]=jpg;
                        int done=Interlocked.Increment(ref completed);
                        progress.Report(new(done*85.0/markers.Count,$"Miniatura {done}/{markers.Count}"));
                    }).ConfigureAwait(false);
                }
                // WPF-backed PDFsharp is used on a dedicated STA thread, not the UI thread.
                await RunStaAsync(()=>WritePdf(source,markers,temp,images,generatedBy,metadata,mediaInfo,exports,ct)).ConfigureAwait(false);
                ExportService.Commit(temp,output,ct);progress.Report(new(100,"Raport gotowy"));
            }
            finally{ExportService.Cleanup(temp);try{Directory.Delete(scratch,true);}catch{}}
        }

        public async Task CsvCaseAsync(List<CaseReportSource> sources, string output, ReportMetadata metadata, CancellationToken ct)
        {
            string temp = ExportService.Temporary(output);
            try
            {
                await using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, true))
                await using (var writer = new StreamWriter(stream, new UTF8Encoding(true)))
                {
                    await writer.WriteLineAsync("Rodzaj;Numer sprawy;Tytuł sprawy;Miejsce;Uwagi;Plik źródłowy;Czas;Koniec;Sekundy;Sekundy końca;Typ zdarzenia;Opis;Marka pojazdu;Powiązane zdarzenia ruchu;Data eksportu;Operacja;Plik wynikowy;Szczegóły;Kodek wideo;Rozdzielczość;FPS;Kodek audio;Czas trwania nagrania;Rozmiar źródła (B)".AsMemory(), ct);
                    string caseNumber = CsvCell(metadata.CaseNumber);
                    string caseTitle = CsvCell(metadata.CaseTitle);
                    string location = CsvCell(metadata.Location);
                    string notes = CsvCell(metadata.Notes);
                    foreach (CaseReportSource source in sources)
                    {
                        ct.ThrowIfCancellationRequested();
                        MediaInfoData? info = source.MediaInfo;
                        string[] mediaFields =
                        {
                            CsvCell(info?.VideoCodec ?? ""), CsvCell(info?.Resolution ?? ""),
                            CsvCell(info?.Fps ?? ""), CsvCell(info?.AudioCodec ?? ""),
                            CsvCell(info == null ? "" : Timestamp(info.Duration)),
                            CsvCell(info == null ? "" : info.FileSize.ToString(CultureInfo.InvariantCulture))
                        };
                        string sourceCell = CsvCell(source.Path);
                        string sourceStatus = File.Exists(source.Path) ? "Nagranie" : "Brak pliku źródłowego";
                        string sourceLine = string.Join(";", new[]
                        {
                            CsvCell(sourceStatus), caseNumber, caseTitle, location, notes, sourceCell,
                            CsvCell(""), CsvCell(""), CsvCell(""), CsvCell(""), CsvCell(""),
                            CsvCell(""), CsvCell(""), CsvCell(""), CsvCell(""), CsvCell(""),
                            CsvCell(""), CsvCell("")
                        }.Concat(mediaFields));
                        await writer.WriteLineAsync(sourceLine.AsMemory(), ct);

                        foreach (TimelineMarker marker in source.Markers.OrderBy(m => m.Seconds))
                        {
                            ct.ThrowIfCancellationRequested();
                            string line = string.Join(";", new[]
                            {
                                CsvCell("Zdarzenie"), caseNumber, caseTitle, location, notes, sourceCell,
                                CsvCell(Timestamp(marker.Seconds)),
                                CsvCell(marker.EndSeconds.HasValue ? Timestamp(marker.EndSeconds.Value) : ""),
                                CsvCell(marker.Seconds.ToString("0.###", CultureInfo.InvariantCulture)),
                                CsvCell(marker.EndSeconds?.ToString("0.###", CultureInfo.InvariantCulture) ?? ""),
                                CsvCell(marker.EventType), CsvCell(marker.Description), CsvCell(marker.VehicleMake),
                                CsvCell(marker.RelatedMotionCount.ToString(CultureInfo.InvariantCulture)),
                                CsvCell(""), CsvCell(""), CsvCell(""), CsvCell("")
                            }.Concat(mediaFields));
                            await writer.WriteLineAsync(line.AsMemory(), ct);
                        }

                        foreach (ExportHistoryItem export in source.Exports.OrderBy(e => e.CreatedLocal))
                        {
                            ct.ThrowIfCancellationRequested();
                            string line = string.Join(";", new[]
                            {
                                CsvCell("Eksport"), caseNumber, caseTitle, location, notes, sourceCell,
                                CsvCell(export.StartSeconds.HasValue ? Timestamp(export.StartSeconds.Value) : ""),
                                CsvCell(export.EndSeconds.HasValue ? Timestamp(export.EndSeconds.Value) : ""),
                                CsvCell(export.StartSeconds?.ToString("0.###", CultureInfo.InvariantCulture) ?? ""),
                                CsvCell(export.EndSeconds?.ToString("0.###", CultureInfo.InvariantCulture) ?? ""),
                                CsvCell(""), CsvCell(""), CsvCell(""), CsvCell(""),
                                CsvCell(export.CreatedLocal.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture)),
                                CsvCell(export.Operation), CsvCell(export.OutputPath), CsvCell(export.Details)
                            }.Concat(mediaFields));
                            await writer.WriteLineAsync(line.AsMemory(), ct);
                        }
                    }
                }
                ExportService.Commit(temp, output, ct);
            }
            finally { ExportService.Cleanup(temp); }
        }

        public async Task PdfCaseAsync(List<CaseReportSource> sources, string output, bool thumbnails,
            ReportMetadata metadata, IProgress<ExportProgress> progress, CancellationToken ct)
        {
            string generatedBy = GetReportUser();
            string scratch = Path.Combine(Path.GetTempPath(), "VideoPlayerCaseReport_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(scratch);
            string temp = ExportService.Temporary(output);
            var images = new ConcurrentDictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            try
            {
                var jobs = new List<(int Index, string SourcePath, TimelineMarker Marker)>();
                int jobIndex = 0;
                foreach (CaseReportSource source in sources)
                {
                    foreach (TimelineMarker marker in source.Markers)
                        jobs.Add((jobIndex++, source.Path, marker));
                }

                int total = jobs.Count;
                int completed = 0;
                if (thumbnails && total > 0)
                {
                    int maxDegree = Math.Clamp(Environment.ProcessorCount / 2, 2, 4);
                    await Parallel.ForEachAsync(jobs, new ParallelOptions { MaxDegreeOfParallelism = maxDegree, CancellationToken = ct }, async (job, token) =>
                    {
                        if (File.Exists(job.SourcePath))
                        {
                            string jpg = Path.Combine(scratch, job.Index.ToString(CultureInfo.InvariantCulture) + ".jpg");
                            try
                            {
                                await _ffmpeg.ThumbnailAsync(job.SourcePath, job.Marker.Seconds, jpg, token).ConfigureAwait(false);
                                images[ImageKey(job.SourcePath, job.Marker.Id)] = jpg;
                            }
                            catch (OperationCanceledException) { throw; }
                            catch { /* A missing or damaged frame must not prevent the case report. */ }
                        }
                        int done = Interlocked.Increment(ref completed);
                        progress.Report(new(done * 85.0 / total, $"Miniatury {done}/{total}"));
                    }).ConfigureAwait(false);
                }
                await RunStaAsync(() => WriteCasePdf(sources, temp, images, generatedBy, metadata, ct)).ConfigureAwait(false);
                ExportService.Commit(temp, output, ct);
                progress.Report(new(100, "Raport sprawy gotowy"));
            }
            finally
            {
                ExportService.Cleanup(temp);
                try { Directory.Delete(scratch, true); } catch { }
            }
        }

        private static string ImageKey(string source, Guid markerId) => source + "|" + markerId.ToString("D");

        private static string GetReportUser()
        {
            // Account identifiers are used internally for matching only.
            string account="";
            try
            {
                using var identity=WindowsIdentity.GetCurrent();
                account=identity.Name??"";
            }
            catch(System.Security.SecurityException) { }
            catch(UnauthorizedAccessException) { }
            string accountUser=account;
            int separator=account.LastIndexOf('\\');
            if(separator>=0)accountUser=account.Substring(separator+1);
            bool IsFullName(string? value)
            {
                if(string.IsNullOrWhiteSpace(value))return false;
                value=value.Trim();
                return !value.Contains('\\')&&!value.Contains('@')
                    &&!string.Equals(value,account,StringComparison.OrdinalIgnoreCase)
                    &&!string.Equals(value,accountUser,StringComparison.OrdinalIgnoreCase)
                    &&!string.Equals(value,Environment.UserName,StringComparison.OrdinalIgnoreCase);
            }
            string Clean(string value)=>value.Replace('\r',' ').Replace('\n',' ').Trim();

            // NameDisplay=3 resolves the friendly display name of the current user.
            uint capacity=256;
            var name=new StringBuilder((int)capacity);
            bool found=GetUserNameEx(3,name,ref capacity);
            if(!found&&Marshal.GetLastWin32Error()==234&&capacity>0&&capacity<=32768)
            {
                name=new StringBuilder((int)capacity);
                found=GetUserNameEx(3,name,ref capacity);
            }
            if(found&&IsFullName(name.ToString()))return Clean(name.ToString());

            // For local accounts use the Full name field, not the account name.
            if(account.StartsWith(Environment.MachineName+"\\",StringComparison.OrdinalIgnoreCase)
                &&!string.IsNullOrWhiteSpace(accountUser))
            {
                IntPtr buffer=IntPtr.Zero;
                try
                {
                    if(NetUserGetInfo(null,accountUser,10,out buffer)==0&&buffer!=IntPtr.Zero)
                    {
                        var info=Marshal.PtrToStructure<UserInfo10>(buffer);
                        string? fullName=Marshal.PtrToStringUni(info.FullName);
                        if(IsFullName(fullName))return Clean(fullName!);
                    }
                }
                finally{if(buffer!=IntPtr.Zero)NetApiBufferFree(buffer);}
            }
            // Never fall back to a system login, including in PDF metadata.
            return "Nie ustalono imienia i nazwiska";
        }
        [DllImport("secur32.dll",EntryPoint="GetUserNameExW",CharSet=CharSet.Unicode,ExactSpelling=true,SetLastError=true)]
        [return:MarshalAs(UnmanagedType.U1)]
        private static extern bool GetUserNameEx(int format,StringBuilder buffer,ref uint size);
        [DllImport("netapi32.dll",CharSet=CharSet.Unicode,ExactSpelling=true)]
        private static extern uint NetUserGetInfo(string? server,string user,uint level,out IntPtr buffer);
        [DllImport("netapi32.dll",ExactSpelling=true)]
        private static extern uint NetApiBufferFree(IntPtr buffer);
        [StructLayout(LayoutKind.Sequential)]
        private struct UserInfo10
        {public IntPtr Name,Comment,UserComment,FullName;}
        private static Task RunStaAsync(Action work)
        {
            var done=new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var thread=new Thread(()=>{try{work();done.TrySetResult(true);}catch(OperationCanceledException){done.TrySetCanceled();}catch(Exception e){done.TrySetException(e);}}){IsBackground=true};
            thread.SetApartmentState(ApartmentState.STA);thread.Start();return done.Task;
        }
        private static void WritePdf(string source,List<TimelineMarker> markers,string temp,IReadOnlyDictionary<Guid,string> images,string generatedBy,ReportMetadata metadata,MediaInfoData? mediaInfo,List<ExportHistoryItem> exports,CancellationToken ct)
        {
            using var doc = new PdfDocument();
            doc.Info.Title = "Raport z analizy materiału wideo";
            doc.Info.Author = generatedBy;
            var options = new XPdfFontOptions(PdfFontEncoding.Unicode);
            var body = new XFont("Arial", 9, XFontStyleEx.Regular, options);
            var small = new XFont("Arial", 8, XFontStyleEx.Regular, options);
            var bold = new XFont("Arial", 9, XFontStyleEx.Bold, options);
            var sectionFont = new XFont("Arial", 10, XFontStyleEx.Bold, options);
            var title = new XFont("Arial", 17, XFontStyleEx.Bold, options);
            var subtitle = new XFont("Arial", 8, XFontStyleEx.Regular, options);
            XGraphics? gfx = null;
            double y = 0, width = 0, bottom = 0;
            int pageNumber = 0;
            string generatedAt = DateTime.Now.ToString("dd.MM.yyyy HH:mm:ss", CultureInfo.GetCultureInfo("pl-PL"));

            void Page()
            {
                gfx?.Dispose();
                var page = doc.AddPage();
                page.Size = PageSize.A4;
                gfx = XGraphics.FromPdfPage(page);
                width = page.Width.Point - 92;
                bottom = page.Height.Point - 58;
                pageNumber++;
                gfx.DrawString("VideoPlayer", new XFont("Arial", 8, XFontStyleEx.Bold, options),
                    XBrushes.DimGray, new XPoint(46, 27));
                gfx.DrawString("DOKUMENTACJA ANALIZY NAGRAŃ", subtitle, XBrushes.Gray, new XPoint(46, 40));
                gfx.DrawLine(new XPen(XColor.FromArgb(150, 150, 150), 0.7), 46, 49, page.Width.Point - 46, 49);
                gfx.DrawLine(new XPen(XColor.FromArgb(170, 170, 170), 0.6), 46, page.Height.Point - 43, page.Width.Point - 46, page.Height.Point - 43);
                gfx.DrawString("Wygenerowano: " + generatedAt + "   |   Sporządził(a): " + generatedBy,
                    small, XBrushes.DimGray, new XPoint(46, page.Height.Point - 29));
                gfx.DrawString("Strona " + pageNumber, small, XBrushes.DimGray,
                    new XRect(page.Width.Point - 105, page.Height.Point - 38, 59, 12), XStringFormats.CenterRight);
                y = 65;
            }
            void Ensure(double height)
            {
                ct.ThrowIfCancellationRequested();
                if (y + height > bottom) Page();
            }
            void Paragraph(string value, XFont font, double indent = 0, double lineHeight = 13)
            {
                string text = (value ?? "").Replace("\r", "").Replace("\n", " ");
                string line = "";
                foreach (char ch in text)
                {
                    if (char.IsControl(ch) && ch != '\t') continue;
                    string next = line + (ch == '\t' ? ' ' : ch);
                    if (gfx!.MeasureString(next, font).Width > width - indent && line.Length > 0)
                    {
                        Ensure(lineHeight);
                        gfx!.DrawString(line, font, XBrushes.Black, new XPoint(46 + indent, y + 9));
                        y += lineHeight;
                        line = ch.ToString();
                    }
                    else line = next;
                }
                Ensure(lineHeight);
                gfx!.DrawString(line, font, XBrushes.Black, new XPoint(46 + indent, y + 9));
                y += lineHeight;
            }
            void Section(string caption)
            {
                Ensure(25);
                gfx!.DrawRectangle(new XSolidBrush(XColor.FromArgb(235, 238, 242)), 46, y, width, 19);
                gfx.DrawRectangle(new XPen(XColor.FromArgb(165, 170, 176), 0.5), 46, y, width, 19);
                gfx.DrawString(caption.ToUpperInvariant(), sectionFont, XBrushes.Black,
                    new XRect(53, y + 1, width - 14, 17), XStringFormats.CenterLeft);
                y += 25;
            }
            void Field(string label, string value)
            {
                if (string.IsNullOrWhiteSpace(value)) return;
                Paragraph(label + ": " + value, body, 4);
            }
            try
            {
                Page();
                gfx!.DrawString("RAPORT Z ANALIZY MATERIAŁU WIDEO", title, XBrushes.Black, new XPoint(46, y + 18));
                y += 31;
                Paragraph("Dokumentacja zdarzeń zarejestrowanych w materiale wideo", subtitle);
                y += 5;

                Section("I. Dane identyfikacyjne sprawy");
                Field("Numer sprawy", metadata.CaseNumber);
                Field("Przedmiot / tytuł", metadata.CaseTitle);
                Field("Miejsce", metadata.Location);
                Field("Data sporządzenia", generatedAt);
                Field("Sporządził(a)", generatedBy);
                Field("Uwagi", metadata.Notes);
                if (string.IsNullOrWhiteSpace(metadata.CaseNumber))
                    Paragraph("Numer sprawy: nie podano", small, 4);

                Section("II. Materiał źródłowy");
                Field("Nazwa pliku", Path.GetFileName(source));
                Field("Lokalizacja pliku", source);
                if (File.Exists(source))
                    Field("Wielkość pliku", new FileInfo(source).Length.ToString("N0", CultureInfo.GetCultureInfo("pl-PL")) + " B");
                if (mediaInfo != null)
                {
                    Field("Parametry obrazu", mediaInfo.VideoCodec + ", " + mediaInfo.Resolution + ", " + mediaInfo.Fps + " FPS");
                    Field("Parametry dźwięku", mediaInfo.AudioCodec);
                    Field("Czas trwania", Timestamp(mediaInfo.Duration));
                    Field("Bitrate", mediaInfo.Bitrate);
                }

                int plateCount = markers.Count(m => string.Equals(m.EventType, "Tablica", StringComparison.OrdinalIgnoreCase));
                int motionCount = markers.Count(m => string.Equals(m.EventType, "Ruch", StringComparison.OrdinalIgnoreCase));
                int manualCount = markers.Count(m => string.Equals(m.EventType, "Ręczne", StringComparison.OrdinalIgnoreCase));
                Section("III. Zestawienie");
                Paragraph("Łączna liczba zarejestrowanych zdarzeń: " + markers.Count +
                    "   |   Rozpoznanie tablic: " + plateCount + "   |   Ruch: " + motionCount +
                    "   |   Wpisy ręczne: " + manualCount, bold, 4);
                Paragraph("Uwaga: podane czasy są względne względem początku pliku wideo. Nie stanowią czasu zegarowego kamery.", small, 4);
                y += 5;

                if (exports.Count > 0)
                {
                    Section("IV. Wykaz wykonanych eksportów");
                    int exportIndex = 0;
                    foreach (var export in exports.OrderBy(e => e.CreatedLocal))
                    {
                        ct.ThrowIfCancellationRequested();
                        exportIndex++;
                        Paragraph(exportIndex + ". " + export.CreatedLocal.ToString("dd.MM.yyyy HH:mm:ss") +
                            " — " + export.Operation, bold, 4);
                        string range = export.StartSeconds.HasValue && export.EndSeconds.HasValue
                            ? Timestamp(export.StartSeconds.Value) + " – " + Timestamp(export.EndSeconds.Value)
                            : "cały materiał / zakres nieokreślony";
                        Paragraph("Zakres: " + range + " | Plik wynikowy: " + export.OutputPath, small, 12);
                        if (!string.IsNullOrWhiteSpace(export.Details)) Paragraph("Informacje dodatkowe: " + export.Details, small, 12);
                        y += 3;
                    }
                }

                Section(exports.Count > 0 ? "V. Chronologiczny wykaz zdarzeń" : "IV. Chronologiczny wykaz zdarzeń");
                int eventIndex = 0;
                foreach (var marker in markers.OrderBy(m => m.Seconds))
                {
                    ct.ThrowIfCancellationRequested();
                    eventIndex++;
                    string eventRange = marker.EndSeconds.HasValue && marker.EndSeconds.Value > marker.Seconds
                        ? Timestamp(marker.Seconds) + " – " + Timestamp(marker.EndSeconds.Value)
                        : Timestamp(marker.Seconds);
                    Ensure(54);
                    gfx!.DrawRectangle(new XPen(XColor.FromArgb(185, 190, 196), 0.6), 46, y, width, 1);
                    y += 6;
                    Paragraph("ZDARZENIE NR " + eventIndex.ToString("000", CultureInfo.InvariantCulture) +
                        "     |     " + marker.EventType.ToUpperInvariant(), bold, 3);
                    Paragraph("Czas nagrania: " + eventRange, bold, 3);
                    if (!string.IsNullOrWhiteSpace(marker.VehicleMake))
                        Paragraph("Marka pojazdu: " + marker.VehicleMake, body, 3);
                    if (marker.RelatedMotionCount > 0)
                        Paragraph("Powiązane zdarzenia ruchu: " + marker.RelatedMotionCount, body, 3);
                    Paragraph("Opis / uwagi: " + marker.Description, body, 3);
                    if (images.TryGetValue(marker.Id, out string? image))
                    {
                        using var pic = XImage.FromFile(image);
                        double imageWidth = Math.Min(width - 8, 330);
                        double imageHeight = Math.Min(185, imageWidth * pic.PixelHeight / (double)pic.PixelWidth);
                        Ensure(imageHeight + 22);
                        gfx!.DrawRectangle(new XPen(XColor.FromArgb(185, 190, 196), 0.6),
                            49, y, imageWidth + 6, imageHeight + 6);
                        gfx.DrawImage(pic, 52, y + 3, imageWidth, imageHeight);
                        y += imageHeight + 12;
                        Paragraph("Ilustracja do zdarzenia nr " + eventIndex.ToString("000", CultureInfo.InvariantCulture), small, 3);
                    }
                    y += 7;
                }

                Section("Informacja końcowa");
                Paragraph("Niniejszy raport stanowi zestawienie danych i oznaczeń zapisanych w aplikacji VideoPlayer. " +
                    "Ocena materiału oraz interpretacja zdarzeń pozostają po stronie osoby uprawnionej.", small, 4);
                Paragraph("Podpis osoby sporządzającej: ................................................................................", body, 4);
                Paragraph("Data i miejsce: ........................................................................................................", body, 4);
                ct.ThrowIfCancellationRequested();
                gfx?.Dispose();
                gfx = null;
                doc.Save(temp);
            }
            finally { gfx?.Dispose(); }
        }

        private static void WriteCasePdf(List<CaseReportSource> sources, string temp,
            IReadOnlyDictionary<string, string> images, string generatedBy, ReportMetadata metadata, CancellationToken ct)
        {
            using var doc = new PdfDocument();
            doc.Info.Title = "Raport zbiorczy sprawy";
            doc.Info.Author = generatedBy;
            var options = new XPdfFontOptions(PdfFontEncoding.Unicode);
            var body = new XFont("Arial", 9, XFontStyleEx.Regular, options);
            var small = new XFont("Arial", 8, XFontStyleEx.Regular, options);
            var bold = new XFont("Arial", 9, XFontStyleEx.Bold, options);
            var sectionFont = new XFont("Arial", 10, XFontStyleEx.Bold, options);
            var title = new XFont("Arial", 17, XFontStyleEx.Bold, options);
            var subtitle = new XFont("Arial", 8, XFontStyleEx.Regular, options);
            XGraphics? gfx = null;
            double y = 0, width = 0, bottom = 0;
            int pageNumber = 0;
            string generatedAt = DateTime.Now.ToString("dd.MM.yyyy HH:mm:ss", CultureInfo.GetCultureInfo("pl-PL"));

            void Page()
            {
                gfx?.Dispose();
                var page = doc.AddPage();
                page.Size = PageSize.A4;
                gfx = XGraphics.FromPdfPage(page);
                width = page.Width.Point - 92;
                bottom = page.Height.Point - 58;
                pageNumber++;
                gfx.DrawString("VideoPlayer", new XFont("Arial", 8, XFontStyleEx.Bold, options),
                    XBrushes.DimGray, new XPoint(46, 27));
                gfx.DrawString("DOKUMENTACJA ANALIZY NAGRAŃ", subtitle, XBrushes.Gray, new XPoint(46, 40));
                gfx.DrawLine(new XPen(XColor.FromArgb(150, 150, 150), 0.7), 46, 49, page.Width.Point - 46, 49);
                gfx.DrawLine(new XPen(XColor.FromArgb(170, 170, 170), 0.6), 46, page.Height.Point - 43, page.Width.Point - 46, page.Height.Point - 43);
                gfx.DrawString("Wygenerowano: " + generatedAt + "   |   Sporządził(a): " + generatedBy,
                    small, XBrushes.DimGray, new XPoint(46, page.Height.Point - 29));
                gfx.DrawString("Strona " + pageNumber, small, XBrushes.DimGray,
                    new XRect(page.Width.Point - 105, page.Height.Point - 38, 59, 12), XStringFormats.CenterRight);
                y = 65;
            }
            void Ensure(double height)
            {
                ct.ThrowIfCancellationRequested();
                if (y + height > bottom) Page();
            }
            void Paragraph(string value, XFont font, double indent = 0, double lineHeight = 13)
            {
                string text = (value ?? "").Replace("\r", "").Replace("\n", " ");
                string line = "";
                foreach (char ch in text)
                {
                    if (char.IsControl(ch) && ch != '\t') continue;
                    string next = line + (ch == '\t' ? ' ' : ch);
                    if (gfx!.MeasureString(next, font).Width > width - indent && line.Length > 0)
                    {
                        Ensure(lineHeight);
                        gfx!.DrawString(line, font, XBrushes.Black, new XPoint(46 + indent, y + 9));
                        y += lineHeight;
                        line = ch.ToString();
                    }
                    else line = next;
                }
                Ensure(lineHeight);
                gfx!.DrawString(line, font, XBrushes.Black, new XPoint(46 + indent, y + 9));
                y += lineHeight;
            }
            void Section(string caption)
            {
                Ensure(25);
                gfx!.DrawRectangle(new XSolidBrush(XColor.FromArgb(235, 238, 242)), 46, y, width, 19);
                gfx.DrawRectangle(new XPen(XColor.FromArgb(165, 170, 176), 0.5), 46, y, width, 19);
                gfx.DrawString(caption.ToUpperInvariant(), sectionFont, XBrushes.Black,
                    new XRect(53, y + 1, width - 14, 17), XStringFormats.CenterLeft);
                y += 25;
            }
            void Field(string label, string value)
            {
                if (!string.IsNullOrWhiteSpace(value)) Paragraph(label + ": " + value, body, 4);
            }
            try
            {
                Page();
                gfx!.DrawString("RAPORT ZBIORCZY SPRAWY", title, XBrushes.Black, new XPoint(46, y + 18));
                y += 31;
                Paragraph("Zestawienie materiałów, zdarzeń i czynności eksportu", subtitle);
                y += 5;

                Section("I. Dane identyfikacyjne sprawy");
                Field("Numer sprawy", metadata.CaseNumber);
                Field("Przedmiot / tytuł", metadata.CaseTitle);
                Field("Miejsce", metadata.Location);
                Field("Data sporządzenia", generatedAt);
                Field("Sporządził(a)", generatedBy);
                Field("Uwagi", metadata.Notes);
                if (string.IsNullOrWhiteSpace(metadata.CaseNumber)) Paragraph("Numer sprawy: nie podano", small, 4);

                int markerCount = sources.Sum(s => s.Markers.Count);
                int exportCount = sources.Sum(s => s.Exports.Count);
                Section("II. Podsumowanie materiału");
                Paragraph("Liczba nagrań: " + sources.Count + "   |   Liczba zdarzeń: " + markerCount +
                    "   |   Liczba eksportów: " + exportCount, bold, 4);
                Paragraph("Uwaga: czasy zdarzeń są względne względem początku odpowiedniego pliku wideo.", small, 4);

                Section("III. Wykaz nagrań");
                int sourceIndex = 0;
                foreach (CaseReportSource source in sources)
                {
                    ct.ThrowIfCancellationRequested();
                    sourceIndex++;
                    Ensure(42);
                    Paragraph("MATERIAŁ NR " + sourceIndex.ToString("000", CultureInfo.InvariantCulture) +
                        (File.Exists(source.Path) ? "" : " — PLIK NIEDOSTĘPNY"), bold, 3);
                    Field("Nazwa pliku", Path.GetFileName(source.Path));
                    Field("Lokalizacja", source.Path);
                    if (source.MediaInfo != null)
                    {
                        MediaInfoData info = source.MediaInfo;
                        Field("Parametry obrazu", info.VideoCodec + ", " + info.Resolution + ", " + info.Fps + " FPS");
                        Field("Parametry dźwięku", info.AudioCodec);
                        Field("Czas trwania", Timestamp(info.Duration));
                        Field("Rozmiar", info.FileSize.ToString("N0", CultureInfo.GetCultureInfo("pl-PL")) + " B");
                    }
                    else if (File.Exists(source.Path))
                        Paragraph("Nie udało się odczytać parametrów technicznych nagrania.", small, 4);
                    Paragraph("Liczba zdarzeń: " + source.Markers.Count + "   |   Liczba eksportów: " + source.Exports.Count, small, 4);
                    y += 5;
                }

                Section("IV. Wykaz zdarzeń według materiału");
                Paragraph("Zdarzenia uporządkowano według nagrania, a w jego obrębie według czasu względnego.", small, 4);
                int eventIndex = 0;
                foreach (CaseReportSource source in sources)
                {
                    foreach (TimelineMarker marker in source.Markers.OrderBy(m => m.Seconds))
                    {
                        ct.ThrowIfCancellationRequested();
                        eventIndex++;
                        string range = marker.EndSeconds.HasValue && marker.EndSeconds.Value > marker.Seconds
                            ? Timestamp(marker.Seconds) + " – " + Timestamp(marker.EndSeconds.Value)
                            : Timestamp(marker.Seconds);
                        Ensure(54);
                        gfx!.DrawRectangle(new XPen(XColor.FromArgb(185, 190, 196), 0.6), 46, y, width, 1);
                        y += 6;
                        Paragraph("ZDARZENIE NR " + eventIndex.ToString("000", CultureInfo.InvariantCulture) +
                            "     |     " + marker.EventType.ToUpperInvariant(), bold, 3);
                        Paragraph("Materiał: " + Path.GetFileName(source.Path), small, 3);
                        Paragraph("Czas nagrania: " + range, bold, 3);
                        if (!string.IsNullOrWhiteSpace(marker.VehicleMake))
                            Paragraph("Marka pojazdu: " + marker.VehicleMake, body, 3);
                        Paragraph("Opis / uwagi: " + marker.Description, body, 3);
                        if (images.TryGetValue(ImageKey(source.Path, marker.Id), out string? image))
                        {
                            using var pic = XImage.FromFile(image);
                            double imageWidth = Math.Min(width - 8, 330);
                            double imageHeight = Math.Min(180, imageWidth * pic.PixelHeight / (double)pic.PixelWidth);
                            Ensure(imageHeight + 22);
                            gfx!.DrawRectangle(new XPen(XColor.FromArgb(185, 190, 196), 0.6),
                                49, y, imageWidth + 6, imageHeight + 6);
                            gfx.DrawImage(pic, 52, y + 3, imageWidth, imageHeight);
                            y += imageHeight + 12;
                            Paragraph("Ilustracja do zdarzenia nr " + eventIndex.ToString("000", CultureInfo.InvariantCulture), small, 3);
                        }
                        y += 7;
                    }
                }

                Section("V. Historia eksportów");
                int exportIndex = 0;
                foreach (CaseReportSource source in sources)
                {
                    foreach (ExportHistoryItem export in source.Exports.OrderBy(e => e.CreatedLocal))
                    {
                        ct.ThrowIfCancellationRequested();
                        exportIndex++;
                        Paragraph(exportIndex + ". " + Path.GetFileName(source.Path) + " | " +
                            export.CreatedLocal.ToString("dd.MM.yyyy HH:mm:ss") + " | " + export.Operation, bold, 3);
                        string range = export.StartSeconds.HasValue && export.EndSeconds.HasValue
                            ? Timestamp(export.StartSeconds.Value) + " – " + Timestamp(export.EndSeconds.Value)
                            : "cały materiał / zakres nieokreślony";
                        Paragraph("Zakres: " + range + " | Plik wynikowy: " + export.OutputPath, small, 10);
                        if (!string.IsNullOrWhiteSpace(export.Details)) Paragraph("Informacje dodatkowe: " + export.Details, small, 10);
                        y += 3;
                    }
                }

                Section("Informacja końcowa");
                Paragraph("Raport stanowi zestawienie danych zapisanych w aplikacji VideoPlayer. " +
                    "Nie zastępuje samodzielnej oceny materiału ani czynności wykonywanych przez osobę uprawnioną.", small, 4);
                Paragraph("Podpis osoby sporządzającej: ................................................................................", body, 4);
                Paragraph("Data i miejsce: ........................................................................................................", body, 4);
                ct.ThrowIfCancellationRequested();
                gfx?.Dispose();
                gfx = null;
                doc.Save(temp);
            }
            finally { gfx?.Dispose(); }
        }

    }

    internal sealed class CaseReportSource
    {
        public string Path { get; set; } = "";
        public List<TimelineMarker> Markers { get; set; } = new();
        public List<ExportHistoryItem> Exports { get; set; } = new();
        public MediaInfoData? MediaInfo { get; set; }
    }
}
