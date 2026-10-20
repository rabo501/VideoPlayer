using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace VideoPlayer
{
    /// <summary>Wspólne narzędzia FFmpeg używane przez eksport i operacje na nagraniu.</summary>
    internal sealed class FfmpegService
    {
        public string ExecutablePath { get; }

        public FfmpegService(string? baseDirectory = null)
        {
            ExecutablePath = Path.Combine(baseDirectory ?? AppContext.BaseDirectory, "ffmpeg", "ffmpeg.exe");
        }

        public bool IsAvailable => File.Exists(ExecutablePath);

        public ProcessStartInfo CreateStartInfo(bool redirectStandardOutput = false, bool redirectStandardError = true)
        {
            return new ProcessStartInfo
            {
                FileName = ExecutablePath,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = redirectStandardOutput,
                RedirectStandardError = redirectStandardError
            };
        }

        public async Task<(double Duration, bool HasAudio)> ProbeAsync(string sourcePath)
        {
            if (!IsAvailable)
                throw new FileNotFoundException("Nie znaleziono ffmpeg.exe.", ExecutablePath);

            var psi = CreateStartInfo();
            psi.ArgumentList.Add("-hide_banner");
            psi.ArgumentList.Add("-i");
            psi.ArgumentList.Add(sourcePath);

            using var process = new Process { StartInfo = psi };
            process.Start();
            string output = await process.StandardError.ReadToEndAsync().ConfigureAwait(false);
            await process.WaitForExitAsync().ConfigureAwait(false);

            double duration = MediaInfoService.ParseDuration(output);
            if (duration <= 0)
                throw new InvalidOperationException("FFmpeg nie rozpoznał czasu trwania pliku. Sprawdź, czy nagranie jest poprawne i obsługiwane.");

            bool hasAudio = Regex.IsMatch(output, @"Stream #\S+.*?: Audio:");
            return (duration, hasAudio);
        }
    }
}
