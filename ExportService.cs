using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
namespace VideoPlayer
{
    internal sealed record ExportProgress(double Percent, string Detail);
    internal sealed class ExportService
    {
        private readonly FfmpegService _ffmpeg;
        public ExportService(FfmpegService ffmpeg)=>_ffmpeg=ffmpeg;
        public async Task RunAsync(IEnumerable<string> arguments,double duration,IProgress<ExportProgress>? progress,CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            if (!_ffmpeg.IsAvailable) throw new FileNotFoundException("Brak ffmpeg.exe.",_ffmpeg.ExecutablePath);
            var psi=_ffmpeg.CreateStartInfo(true,true);
            foreach(var a in new[]{"-hide_banner","-nostdin","-y","-progress","pipe:1","-nostats","-stats_period","0.25"})psi.ArgumentList.Add(a);
            foreach(var a in arguments) psi.ArgumentList.Add(a);
            using var p=new Process {StartInfo=psi};
            if(!p.Start()) throw new IOException("Nie uruchomiono FFmpeg.");
            using var cancel=ct.Register(()=>{try {if(!p.HasExited)p.Kill(true);}catch{} });
            var clock=Stopwatch.StartNew();
            Task<string> errors=ReadErrorsAsync(p);
            Task read=ReadProgressAsync(p,duration,clock,progress);
            await p.WaitForExitAsync().ConfigureAwait(false);
            await read.ConfigureAwait(false);
            string error=await errors.ConfigureAwait(false);
            ct.ThrowIfCancellationRequested();
            if(p.ExitCode!=0)throw new InvalidOperationException("FFmpeg: "+error);
        }
        private static async Task<string> ReadErrorsAsync(Process p)
        {
            var tail=new StringBuilder();string? line;
            while((line=await p.StandardError.ReadLineAsync().ConfigureAwait(false))!=null)
            { tail.AppendLine(line);if(tail.Length>16000)tail.Remove(0,tail.Length-12000); }
            return tail.ToString();
        }
        private static async Task ReadProgressAsync(Process p,double duration,Stopwatch clock,IProgress<ExportProgress>? report)
        {
            string? line;
            while((line=await p.StandardOutput.ReadLineAsync().ConfigureAwait(false))!=null)
            {
                if(!line.StartsWith("out_time_us=",StringComparison.Ordinal)||!double.TryParse(line.Substring(12),NumberStyles.Float,CultureInfo.InvariantCulture,out double us))continue;
                double s=us/1000000,percent=duration>0?Math.Clamp(s/duration*100,0,99.9):0;
                double rate=clock.Elapsed.TotalSeconds>0?s/clock.Elapsed.TotalSeconds:0;
                double eta=rate>0?Math.Max(0,duration-s)/rate:double.NaN;
                string left=double.IsFinite(eta)&&eta<864000?TimeSpan.FromSeconds(eta).ToString(@"hh\:mm\:ss"):"obliczanie";
                report?.Report(new(percent,$"{percent:0.0}% | pozostało około {left}"));
            }
        }
        public async Task ExportAsync(string source,string output,double start,double end,string profile,bool copy,IProgress<ExportProgress> progress,CancellationToken ct)
        {
            if(!double.IsFinite(start)||!double.IsFinite(end)||start<0||end<=start)throw new ArgumentException("Nieprawidłowy zakres.");
            Guard(source,output);
            string temp=Temporary(output);
            try
            {
                var args=new List<string>{"-ss",F(start),"-i",source,"-t",F(end-start),"-map","0:v:0","-map","0:a:0?"};
                if(copy)args.AddRange(new[]{"-c","copy","-avoid_negative_ts","make_zero"});
                else
                {
                    var (preset,crf)=profile switch {"fast"=>("veryfast","22"),"small"=>("slow","27"),"quality"=>("slow","16"),_=>("medium","20")};
                    args.AddRange(new[]{"-c:v","libx264","-preset",preset,"-crf",crf,"-vf","pad=ceil(iw/2)*2:ceil(ih/2)*2","-pix_fmt","yuv420p","-c:a","aac","-b:a","192k"});
                }
                args.AddRange(new[]{"-sn","-dn","-map_chapters","-1","-movflags","+faststart",temp});
                await RunAsync(args,end-start,progress,ct).ConfigureAwait(false);
                Commit(temp,output,ct);
            }
            finally{Cleanup(temp);}
        }
        public async Task RemoveAsync(string source,string output,double start,double end,double duration,bool hasAudio,IProgress<ExportProgress> progress,CancellationToken ct)
        {
            Guard(source,output);
            if(!double.IsFinite(start)||!double.IsFinite(end)||start<0||end<=start||end>duration+0.15)throw new ArgumentException("Nieprawidłowy zakres.");
            end=Math.Min(duration,end);
            bool before=start>0.001, after=end<duration-0.001;
            if(!before&&!after)throw new ArgumentException("Nie można usunąć całego nagrania.");
            string Chain(string input,string trim,string pts,string outputLabel)
            {
                if(before&&after)return $"[{input}]{trim}=start=0:end={F(start)},{pts}=PTS-STARTPTS[{outputLabel}0];[{input}]{trim}=start={F(end)},{pts}=PTS-STARTPTS[{outputLabel}1];[{outputLabel}0][{outputLabel}1]concat=n=2:v={(outputLabel=="v"?1:0)}:a={(outputLabel=="a"?1:0)}[{outputLabel}]";
                return $"[{input}]{trim}=start={(before?"0":F(end))}"+(before?$":end={F(start)}":"")+$",{pts}=PTS-STARTPTS[{outputLabel}]";
            }
            string filter=Chain("0:v:0","trim","setpts","v");
            if(hasAudio)filter+=";"+Chain("0:a:0","atrim","asetpts","a");
            filter+=";[v]pad=ceil(iw/2)*2:ceil(ih/2)*2,format=yuv420p[vout]";
            string temp=Temporary(output);
            try
            {
                var args=new List<string>{"-i",source,"-filter_complex",filter,"-map","[vout]"};
                if(hasAudio)args.AddRange(new[]{"-map","[a]","-c:a","aac","-b:a","192k"});
                args.AddRange(new[]{"-c:v","libx264","-preset","fast","-crf","20","-sn","-dn","-map_chapters","-1","-movflags","+faststart",temp});
                await RunAsync(args,duration-(end-start),progress,ct).ConfigureAwait(false);Commit(temp,output,ct);
            }finally{Cleanup(temp);}
        }
        public async Task ThumbnailAsync(string source,double seconds,string output,CancellationToken ct)
        {
            await RunAsync(new[]{"-ss",F(seconds),"-i",source,"-frames:v","1","-vf","scale=640:360:force_original_aspect_ratio=decrease","-q:v","3",output},0,null,ct).ConfigureAwait(false);
            if(!File.Exists(output)||new FileInfo(output).Length==0)throw new IOException("Nie powstała miniatura.");
        }
        public async Task<(double Duration,bool HasAudio)> ProbeAsync(string source,CancellationToken ct)
        {
            // Output target is not needed; FFmpeg may exit nonzero after printing input metadata.
            var psi=_ffmpeg.CreateStartInfo(false,true);psi.ArgumentList.Add("-nostdin");psi.ArgumentList.Add("-hide_banner");psi.ArgumentList.Add("-i");psi.ArgumentList.Add(source);
            using var p=new Process{StartInfo=psi};p.Start();
            using var registration=ct.Register(()=>{try{if(!p.HasExited)p.Kill(true);}catch{}});
            string text=await p.StandardError.ReadToEndAsync().ConfigureAwait(false);await p.WaitForExitAsync().ConfigureAwait(false);ct.ThrowIfCancellationRequested();
            double duration=MediaInfoService.ParseDuration(text);if(duration<=0)throw new IOException("Nie rozpoznano długości filmu.");
            return(duration,System.Text.RegularExpressions.Regex.IsMatch(text,@"Stream #\S+.*?: Audio:"));
        }
        private static string F(double n)=>n.ToString("R",CultureInfo.InvariantCulture);
        private static void Guard(string source,string output)
        {
            if(!File.Exists(source))throw new FileNotFoundException("Brak źródła.",source);
            if(string.Equals(Path.GetFullPath(source),Path.GetFullPath(output),StringComparison.OrdinalIgnoreCase))throw new IOException("Wynik nie może zastępować źródła.");
            // Conservative: refuse an existing destination, including potential hard-link aliases.
            if(File.Exists(output))throw new IOException("Wybierz nową nazwę: plik wynikowy już istnieje.");
        }
        internal static string Temporary(string output)=>Path.Combine(Path.GetDirectoryName(Path.GetFullPath(output))!,".vp_"+Guid.NewGuid().ToString("N")+Path.GetExtension(output));
        internal static void Commit(string temp,string output,CancellationToken ct)
        {ct.ThrowIfCancellationRequested();if(!File.Exists(temp)||new FileInfo(temp).Length==0)throw new IOException("Pusty wynik.");File.Move(temp,output,false);}
        internal static void Cleanup(string path){try{if(File.Exists(path))File.Delete(path);}catch{}}
    }
}
