using System.Linq;
using System;
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
            var images=new Dictionary<Guid,string>();
            try
            {
                if(thumbnails)
                {
                    for(int i=0;i<markers.Count;i++)
                    {
                        ct.ThrowIfCancellationRequested();var marker=markers[i];string jpg=Path.Combine(scratch,i+".jpg");
                        await _ffmpeg.ThumbnailAsync(source,marker.Seconds,jpg,ct).ConfigureAwait(false);images[marker.Id]=jpg;
                        progress.Report(new((i+1.0)/markers.Count*85,$"Miniatura {i+1}/{markers.Count}"));
                    }
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
            var images = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            try
            {
                int total = sources.Sum(s => s.Markers.Count);
                int completed = 0;
                if (thumbnails && total > 0)
                {
                    foreach (CaseReportSource source in sources)
                    {
                        foreach (TimelineMarker marker in source.Markers)
                        {
                            ct.ThrowIfCancellationRequested();
                            if (File.Exists(source.Path))
                            {
                                string jpg = Path.Combine(scratch, completed.ToString(CultureInfo.InvariantCulture) + ".jpg");
                                try
                                {
                                    await _ffmpeg.ThumbnailAsync(source.Path, marker.Seconds, jpg, ct).ConfigureAwait(false);
                                    images[ImageKey(source.Path, marker.Id)] = jpg;
                                }
                                catch (OperationCanceledException) { throw; }
                                catch { /* A missing or damaged frame must not prevent the case report. */ }
                            }
                            completed++;
                            progress.Report(new(total == 0 ? 85 : completed * 85.0 / total,
                                $"Miniatury {completed}/{total}"));
                        }
                    }
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
        private static void WritePdf(string source,List<TimelineMarker> markers,string temp,Dictionary<Guid,string> images,string generatedBy,ReportMetadata metadata,MediaInfoData? mediaInfo,List<ExportHistoryItem> exports,CancellationToken ct)
        {
            using var doc=new PdfDocument();doc.Info.Title="VideoPlayer — raport zdarzeń";doc.Info.Author=generatedBy;
            var options=new XPdfFontOptions(PdfFontEncoding.Unicode);
            var font=new XFont("Arial",10,XFontStyleEx.Regular,options);
            var footerFont=new XFont("Arial",8,XFontStyleEx.Regular,options);
            var bold=new XFont("Arial",11,XFontStyleEx.Bold,options);
            var title=new XFont("Arial",17,XFontStyleEx.Bold,options);
            XGraphics? gfx=null;double y=0;int pageNumber=0;double width=0,bottom=0;
            void Page()
            {
                gfx?.Dispose();var page=doc.AddPage();page.Size=PageSize.A4;
                width=page.Width.Point-84;gfx=XGraphics.FromPdfPage(page);y=42;pageNumber++;
                // Wrap long full names instead of clipping or overlapping the page number.
                var footerLines=new List<string>();
                string footerLine="";
                foreach(char ch in "Wygenerował: "+generatedBy)
                {
                    if(char.IsControl(ch))continue;
                    string next=footerLine+ch;
                    if(footerLine.Length>0&&gfx.MeasureString(next,footerFont).Width>width)
                    {footerLines.Add(footerLine);footerLine=ch.ToString();}
                    else footerLine=next;
                }
                if(footerLine.Length>0)footerLines.Add(footerLine);
                double footerTop=page.Height.Point-40-Math.Max(0,footerLines.Count-1)*12;
                bottom=footerTop-14;
                gfx.DrawString("VideoPlayer — raport zdarzeń",title,XBrushes.Black,new XPoint(42,y+17));y+=34;
                for(int i=0;i<footerLines.Count;i++)
                    gfx.DrawString(footerLines[i],footerFont,XBrushes.Gray,new XPoint(42,footerTop+i*12));
                gfx.DrawString("Strona "+pageNumber,footerFont,XBrushes.Gray,new XPoint(42,page.Height.Point-25));
            }
            void Line(string text,XFont f)
            {ct.ThrowIfCancellationRequested();if(y+16>bottom)Page();gfx!.DrawString(text,f,XBrushes.Black,new XPoint(42,y+11));y+=16;}
            void Text(string text,XFont f)
            {
                foreach(string paragraph in (text??"").Replace("\r","").Split('\n'))
                {
                    string line="";
                    foreach(char ch in paragraph)
                    {
                        if(char.IsControl(ch)&&ch!='\t')continue;
                        string next=line+(ch=='\t'?' ':ch);
                        if(gfx!.MeasureString(next,f).Width>width&&line.Length>0){Line(line,f);line=ch.ToString();}else line=next;
                    }
                    Line(line,f);
                }
            }
            try
            {
                Page();
                Text("RAPORT ANALIZY NAGRANIA",bold);
                if(!string.IsNullOrWhiteSpace(metadata.CaseNumber))Text("Numer sprawy: "+metadata.CaseNumber,font);
                if(!string.IsNullOrWhiteSpace(metadata.CaseTitle))Text("Tytuł: "+metadata.CaseTitle,font);
                if(!string.IsNullOrWhiteSpace(metadata.Location))Text("Miejsce: "+metadata.Location,font);
                if(!string.IsNullOrWhiteSpace(metadata.Notes))Text("Uwagi: "+metadata.Notes,font);
                Text("Źródło: "+source,font);
                Text("Nazwa pliku: "+Path.GetFileName(source),font);
                Text("Utworzono: "+DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"),font);
                if(mediaInfo!=null)
                {
                    Text("Parametry nagrania: "+mediaInfo.VideoCodec+", "+mediaInfo.Resolution+", "+mediaInfo.Fps+" FPS, bitrate "+mediaInfo.Bitrate+", audio: "+mediaInfo.AudioCodec,font);
                    Text("Czas trwania: "+Timestamp(mediaInfo.Duration)+" | Rozmiar: "+mediaInfo.FileSize.ToString("N0",CultureInfo.GetCultureInfo("pl-PL"))+" B",font);
                }
                var sourceInfo=new FileInfo(source);
                Text("Rozmiar pliku źródłowego: "+sourceInfo.Length.ToString("N0",CultureInfo.GetCultureInfo("pl-PL"))+" B",font);
                int plateCount=markers.Count(m=>string.Equals(m.EventType,"Tablica",StringComparison.OrdinalIgnoreCase));
                int motionCount=markers.Count(m=>string.Equals(m.EventType,"Ruch",StringComparison.OrdinalIgnoreCase));
                int manualCount=markers.Count(m=>string.Equals(m.EventType,"Ręczne",StringComparison.OrdinalIgnoreCase));
                Text("Liczba zdarzeń: "+markers.Count+" | Tablice: "+plateCount+" | Ruch: "+motionCount+" | Ręczne: "+manualCount+".",font);
                Text("Czasy zdarzeń są względne względem początku pliku, nie są czasem zegara kamery.",font);
                y+=10;
                if(!string.IsNullOrWhiteSpace(metadata.Notes))y+=4;
                if(exports.Count>0)
                {
                    Text("ZAREJESTROWANE EKSPORTY",bold);
                    foreach(var export in exports.OrderBy(e=>e.CreatedLocal))
                    {
                        string range=export.StartSeconds.HasValue&&export.EndSeconds.HasValue
                            ? Timestamp(export.StartSeconds.Value)+" – "+Timestamp(export.EndSeconds.Value) : "cały materiał / zakres nieokreślony";
                        Text(export.CreatedLocal.ToString("yyyy-MM-dd HH:mm:ss")+" | "+export.Operation+" | "+range,font);
                        Text("Plik wynikowy: "+export.OutputPath,font);
                        if(!string.IsNullOrWhiteSpace(export.Details))Text("Szczegóły: "+export.Details,font);
                    }
                    y+=8;
                }
                Text("ZESTAWIENIE ZDARZEŃ",bold);
                foreach(var m in markers)
                {
                    ct.ThrowIfCancellationRequested();if(y+60>bottom)Page();
                    string eventRange=m.EndSeconds.HasValue&&m.EndSeconds.Value>m.Seconds
                        ? Timestamp(m.Seconds)+" – "+Timestamp(m.EndSeconds.Value)
                        : Timestamp(m.Seconds);
                    Text("["+m.EventType+"] Czas od początku nagrania: "+eventRange,bold);
                    if(!string.IsNullOrWhiteSpace(m.VehicleMake))
                        Text("Marka pojazdu: "+m.VehicleMake,font);
                    if(m.RelatedMotionCount>0)
                        Text("Powiązane zdarzenia ruchu: "+m.RelatedMotionCount,font);
                    Text("Opis: "+m.Description,font);
                    if(images.TryGetValue(m.Id,out string? image))
                    {
                        using var pic=XImage.FromFile(image);double h=Math.Min(160,260*pic.PixelHeight/(double)pic.PixelWidth);
                        if(y+h+12>bottom)Page();gfx!.DrawImage(pic,42,y,260,h);y+=h+10;
                    }
                    y+=12;
                }
                ct.ThrowIfCancellationRequested();gfx?.Dispose();gfx=null;doc.Save(temp);
            }
            finally{gfx?.Dispose();}
        }

        private static void WriteCasePdf(List<CaseReportSource> sources, string temp,
            Dictionary<string, string> images, string generatedBy, ReportMetadata metadata, CancellationToken ct)
        {
            using var doc = new PdfDocument();
            doc.Info.Title = "VideoPlayer — raport sprawy";
            doc.Info.Author = generatedBy;
            var options = new XPdfFontOptions(PdfFontEncoding.Unicode);
            var font = new XFont("Arial", 9, XFontStyleEx.Regular, options);
            var small = new XFont("Arial", 8, XFontStyleEx.Regular, options);
            var bold = new XFont("Arial", 10, XFontStyleEx.Bold, options);
            var title = new XFont("Arial", 16, XFontStyleEx.Bold, options);
            XGraphics? gfx = null;
            double y = 0, width = 0, bottom = 0;
            int pageNumber = 0;
            void Page()
            {
                gfx?.Dispose();
                var page = doc.AddPage();
                page.Size = PageSize.A4;
                width = page.Width.Point - 84;
                gfx = XGraphics.FromPdfPage(page);
                y = 42;
                bottom = page.Height.Point - 58;
                pageNumber++;
                gfx.DrawString("VideoPlayer — raport sprawy", title, XBrushes.Black, new XPoint(42, y + 17));
                gfx.DrawString("Wygenerował: " + generatedBy, small, XBrushes.Gray, new XPoint(42, page.Height.Point - 38));
                gfx.DrawString("Strona " + pageNumber, small, XBrushes.Gray, new XPoint(42, page.Height.Point - 22));
                y += 34;
            }
            void Line(string value, XFont f)
            {
                ct.ThrowIfCancellationRequested();
                if (y + 16 > bottom) Page();
                gfx!.DrawString(value, f, XBrushes.Black, new XPoint(42, y + 11));
                y += 15;
            }
            void Text(string value, XFont f)
            {
                foreach (string paragraph in (value ?? "").Replace("\r", "").Split('\n'))
                {
                    string line = "";
                    foreach (char ch in paragraph)
                    {
                        if (char.IsControl(ch) && ch != '\t') continue;
                        string next = line + (ch == '\t' ? ' ' : ch);
                        if (gfx!.MeasureString(next, f).Width > width && line.Length > 0)
                        { Line(line, f); line = ch.ToString(); }
                        else line = next;
                    }
                    Line(line, f);
                }
            }
            try
            {
                Page();
                Text("RAPORT ZBIORCZY SPRAWY", bold);
                if (!string.IsNullOrWhiteSpace(metadata.CaseNumber)) Text("Numer sprawy: " + metadata.CaseNumber, font);
                if (!string.IsNullOrWhiteSpace(metadata.CaseTitle)) Text("Tytuł: " + metadata.CaseTitle, font);
                if (!string.IsNullOrWhiteSpace(metadata.Location)) Text("Miejsce: " + metadata.Location, font);
                if (!string.IsNullOrWhiteSpace(metadata.Notes)) Text("Uwagi: " + metadata.Notes, font);
                Text("Utworzono: " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"), font);
                int markerCount = sources.Sum(s => s.Markers.Count);
                int exportCount = sources.Sum(s => s.Exports.Count);
                Text($"Liczba nagrań: {sources.Count} | Zdarzenia: {markerCount} | Eksporty: {exportCount}", font);
                Text("Czasy zdarzeń są względne względem początku danego pliku, a nie czasem zegara kamery.", small);
                y += 8;

                Text("WYKAZ NAGRAŃ", bold);
                foreach (CaseReportSource source in sources)
                {
                    ct.ThrowIfCancellationRequested();
                    Text(Path.GetFileName(source.Path) + (File.Exists(source.Path) ? "" : " [PLIK NIEDOSTĘPNY]"), font);
                    Text("Ścieżka: " + source.Path, small);
                    if (source.MediaInfo != null)
                    {
                        MediaInfoData info = source.MediaInfo;
                        Text($"Parametry: {info.VideoCodec}, {info.Resolution}, {info.Fps} FPS, audio: {info.AudioCodec}", small);
                        Text($"Czas trwania: {Timestamp(info.Duration)} | Rozmiar: {info.FileSize.ToString("N0", CultureInfo.GetCultureInfo("pl-PL"))} B", small);
                    }
                    else if (File.Exists(source.Path))
                        Text("Nie udało się odczytać parametrów technicznych nagrania.", small);
                    Text($"Zdarzenia: {source.Markers.Count} | Eksporty: {source.Exports.Count}", small);
                    y += 5;
                }

                Text("ZESTAWIENIE ZDARZEŃ", bold);
                foreach (CaseReportSource source in sources)
                {
                    foreach (TimelineMarker marker in source.Markers.OrderBy(m => m.Seconds))
                    {
                        ct.ThrowIfCancellationRequested();
                        Text(Path.GetFileName(source.Path) + " — " + source.Path, small);
                        string range = marker.EndSeconds.HasValue && marker.EndSeconds.Value > marker.Seconds
                            ? Timestamp(marker.Seconds) + " – " + Timestamp(marker.EndSeconds.Value)
                            : Timestamp(marker.Seconds);
                        Text("[" + marker.EventType + "] " + range, bold);
                        if (!string.IsNullOrWhiteSpace(marker.VehicleMake)) Text("Marka pojazdu: " + marker.VehicleMake, small);
                        Text("Opis: " + marker.Description, font);
                        if (images.TryGetValue(ImageKey(source.Path, marker.Id), out string? image))
                        {
                            using var pic = XImage.FromFile(image);
                            double h = Math.Min(150, 240 * pic.PixelHeight / (double)pic.PixelWidth);
                            if (y + h + 10 > bottom) Page();
                            gfx!.DrawImage(pic, 42, y, 240, h);
                            y += h + 8;
                        }
                        y += 7;
                    }
                }

                Text("HISTORIA EKSPORTÓW", bold);
                foreach (CaseReportSource source in sources)
                {
                    foreach (ExportHistoryItem export in source.Exports.OrderBy(e => e.CreatedLocal))
                    {
                        ct.ThrowIfCancellationRequested();
                        Text(Path.GetFileName(source.Path) + " | " +
                            export.CreatedLocal.ToString("yyyy-MM-dd HH:mm:ss") + " | " + export.Operation, font);
                        string range = export.StartSeconds.HasValue && export.EndSeconds.HasValue
                            ? Timestamp(export.StartSeconds.Value) + " – " + Timestamp(export.EndSeconds.Value)
                            : "cały materiał / zakres nieokreślony";
                        Text("Zakres: " + range + " | Plik wynikowy: " + export.OutputPath, small);
                        if (!string.IsNullOrWhiteSpace(export.Details)) Text("Szczegóły: " + export.Details, small);
                    }
                }
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
