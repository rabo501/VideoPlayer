using System;

namespace VideoPlayer
{
    internal sealed record PlateVideoSearchHit(double StartSeconds, double EndSeconds, float Confidence)
    {
        public string DisplayText
        {
            get
            {
                string start = FormatTime(StartSeconds);
                string end = FormatTime(EndSeconds);
                string range = Math.Abs(EndSeconds - StartSeconds) < 0.5 ? start : $"{start} – {end}";
                return $"{range}  |  pewność OCR: {Confidence:P0}";
            }
        }

        private static string FormatTime(double seconds)
        {
            TimeSpan time = TimeSpan.FromSeconds(Math.Max(0, seconds));
            return time.TotalDays >= 1
                ? $"{(int)time.TotalDays}.{time:hh\\:mm\\:ss}"
                : time.ToString(@"hh\:mm\:ss");
        }
    }
}
