using System;
using System.Globalization;
using System.IO;

namespace VideoPlayer.Services
{
    internal static class ExportWorkspaceService
    {
        public static string FormatFileSize(long bytes)
        {
            if (bytes < 0) bytes = 0;
            string[] units = { "B", "KB", "MB", "GB", "TB" };
            double size = bytes;
            int unit = 0;
            while (size >= 1024 && unit < units.Length - 1)
            {
                size /= 1024;
                unit++;
            }
            return $"{size:0.##} {units[unit]}";
        }

        public static string FormatClipTime(double seconds)
        {
            return TimeSpan
                .FromSeconds(double.IsFinite(seconds) ? Math.Max(0, seconds) : 0)
                .ToString(@"d\.hh\:mm\:ss\.fff", CultureInfo.InvariantCulture);
        }

        public static string FormatClockTime(double seconds)
        {
            var t = TimeSpan.FromSeconds(double.IsFinite(seconds) ? Math.Max(0, seconds) : 0);
            return $"{(int)t.TotalHours:00}:{t.Minutes:00}:{t.Seconds:00}";
        }

        public static string SanitizeFileName(string? value, string fallback = "fragment")
        {
            string s = value ?? string.Empty;
            foreach (char c in Path.GetInvalidFileNameChars())
                s = s.Replace(c, '_');
            s = s.Trim().TrimEnd('.');
            if (s.Length > 70)
                s = s.Substring(0, 70);
            return s.Length == 0 ? fallback : s;
        }

        public static string BuildRangeFeedbackText(double? clipStart, double? clipEnd, int clipCount)
        {
            string start = clipStart.HasValue
                ? TimeSpan.FromSeconds(Math.Max(0, clipStart.Value)).ToString(@"hh\:mm\:ss\.fff", CultureInfo.InvariantCulture)
                : "nie ustawiono";
            string end = clipEnd.HasValue
                ? TimeSpan.FromSeconds(Math.Max(0, clipEnd.Value)).ToString(@"hh\:mm\:ss\.fff", CultureInfo.InvariantCulture)
                : "nie ustawiono";
            return $"Początek: {start} | Koniec: {end} | Fragmenty: {Math.Max(0, clipCount)}";
        }

        public static string BuildClipRangeSummary(double? clipStart, double? clipEnd)
        {
            string start = clipStart.HasValue ? FormatClipTime(clipStart.Value) : "—";
            string end = clipEnd.HasValue ? FormatClipTime(clipEnd.Value) : "—";
            string duration = HasValidRange(clipStart, clipEnd)
                ? FormatClipTime(clipEnd!.Value - clipStart!.Value)
                : "—";

            return $"Początek: {start}\nKoniec: {end}\nDługość: {duration}";
        }

        public static bool HasValidRange(double? clipStart, double? clipEnd) =>
            clipStart.HasValue && clipEnd.HasValue &&
            double.IsFinite(clipStart.Value) && double.IsFinite(clipEnd.Value) &&
            clipEnd.Value > clipStart.Value;

        public static string BuildMediaInfoSummary(
            MediaInfoData? info,
            string? sourcePath,
            double? clipStart,
            double? clipEnd,
            string? lastExportSummary)
        {
            if (info == null || string.IsNullOrWhiteSpace(sourcePath))
                return "Otwórz nagranie, aby odczytać kodek, rozdzielczość, FPS, bitrate i rozmiar.";

            double fullDuration = double.IsFinite(info.Duration) ? Math.Max(0, info.Duration) : 0;
            bool hasRange = HasValidRange(clipStart, clipEnd);
            double selectedDuration = hasRange ? clipEnd!.Value - clipStart!.Value : fullDuration;
            string estimate = fullDuration > 0
                ? FormatFileSize((long)(Math.Max(0, info.FileSize) * Math.Clamp(selectedDuration / fullDuration, 0, 1)))
                : "nie można oszacować";
            string rangeLabel = hasRange ? $"zaznaczenie ({FormatClockTime(selectedDuration)})" : "całe nagranie";

            return
                $"Źródło: {info.VideoCodec}, {info.Resolution}, {info.Fps} FPS, bitrate {info.Bitrate}, audio: {info.AudioCodec}; " +
                $"czas {FormatClockTime(fullDuration)}, rozmiar {FormatFileSize(info.FileSize)}.\n" +
                $"Szacowany rozmiar {rangeLabel} przy podobnym bitrate: około {estimate}. To przybliżenie; rozmiar MP4 po ponownym kodowaniu może się różnić." +
                (string.IsNullOrWhiteSpace(lastExportSummary) ? "" : "\n" + lastExportSummary);
        }
    }
}
