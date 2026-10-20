using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace VideoPlayer
{
    internal sealed class MediaInfoService
    {
        private readonly string _ffmpegPath;
        public MediaInfoService(string ffmpegPath) => _ffmpegPath = ffmpegPath;

        public async Task<MediaInfoData> ReadAsync(string sourcePath)
        {
            if (!File.Exists(_ffmpegPath))
                throw new FileNotFoundException("Brak ffmpeg.exe — nie można odczytać parametrów nagrania.", _ffmpegPath);

            var psi = new ProcessStartInfo
            {
                FileName = _ffmpegPath,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardError = true
            };
            psi.ArgumentList.Add("-hide_banner");
            psi.ArgumentList.Add("-i");
            psi.ArgumentList.Add(sourcePath);

            using var process = new Process { StartInfo = psi };
            process.Start();
            string output = await process.StandardError.ReadToEndAsync().ConfigureAwait(false);
            await process.WaitForExitAsync().ConfigureAwait(false);

            var info = new MediaInfoData
            {
                FileSize = new FileInfo(sourcePath).Length,
                Duration = ParseDuration(output)
            };
            var videoLine = Regex.Match(output, @"Stream #\S+.*?: Video:\s*([^, ]+)(.*)");
            if (videoLine.Success)
            {
                info.VideoCodec = videoLine.Groups[1].Value;
                string details = videoLine.Groups[2].Value;
                var resolution = Regex.Match(details, @"(\d{2,5})x(\d{2,5})");
                if (resolution.Success) info.Resolution = resolution.Groups[1].Value + "×" + resolution.Groups[2].Value;
                var fps = Regex.Match(details, @"([\d.]+)\s*fps");
                if (fps.Success) info.Fps = fps.Groups[1].Value;
            }
            var audio = Regex.Match(output, @"Stream #\S+.*?: Audio:\s*([^, ]+)");
            if (audio.Success) info.AudioCodec = audio.Groups[1].Value;
            var bitrate = Regex.Match(output, @"bitrate:\s*(\d+)\s*kb/s");
            if (bitrate.Success) info.Bitrate = bitrate.Groups[1].Value + " kb/s";
            return info;
        }

        public static double ParseDuration(string output)
        {
            var match = Regex.Match(output, @"Duration:\s*(\d+):(\d+):(\d+(?:\.\d+)?)");
            if (!match.Success) return 0;
            return double.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture) * 3600 +
                   double.Parse(match.Groups[2].Value, CultureInfo.InvariantCulture) * 60 +
                   double.Parse(match.Groups[3].Value, CultureInfo.InvariantCulture);
        }
    }

    internal sealed class MediaInfoData
    {
        public double Duration { get; set; }
        public string VideoCodec { get; set; } = "nieznany";
        public string Resolution { get; set; } = "nieznana";
        public string Fps { get; set; } = "nieznane";
        public string Bitrate { get; set; } = "nieznany";
        public string AudioCodec { get; set; } = "brak / nieznany";
        public long FileSize { get; set; }
    }
}
