using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace VideoPlayer
{
    internal sealed class MotionDetectionService
    {
        private const int FrameWidth = 320;
        private const int FrameHeight = 180;
        private const int FramesPerSecond = 2;
        private const int FrameBytes = FrameWidth * FrameHeight;

        internal sealed class MotionEvent
        {
            public double StartSeconds { get; init; }
            public double EndSeconds { get; set; }
            public double PeakChangedRatio { get; set; }
        }

        internal readonly record struct MotionRegion(double X, double Y, double Width, double Height)
        {
            public static MotionRegion FullFrame => new(0, 0, 1, 1);
            public bool IsFullFrame => X <= 0 && Y <= 0 && Width >= 1 && Height >= 1;
        }

        public async Task<List<MotionEvent>> AnalyzeAsync(
            string ffmpegPath, string sourcePath, double durationSeconds,
            double pixelDifferenceThreshold, double changedAreaPercent,
            double minimumEventDurationSeconds, MotionRegion region,
            IProgress<double>? progress, CancellationToken cancellationToken)
        {
            if (!File.Exists(ffmpegPath)) throw new FileNotFoundException("Nie znaleziono FFmpeg.", ffmpegPath);
            if (!File.Exists(sourcePath)) throw new FileNotFoundException("Nie znaleziono nagrania.", sourcePath);
            if (pixelDifferenceThreshold < 1 || pixelDifferenceThreshold > 100) throw new ArgumentOutOfRangeException(nameof(pixelDifferenceThreshold));
            if (changedAreaPercent <= 0 || changedAreaPercent > 100) throw new ArgumentOutOfRangeException(nameof(changedAreaPercent));
            if (minimumEventDurationSeconds < 0 || minimumEventDurationSeconds > 60) throw new ArgumentOutOfRangeException(nameof(minimumEventDurationSeconds));

            string filter = "";
            if (!region.IsFullFrame)
            {
                double x = Math.Clamp(region.X, 0, 0.99), y = Math.Clamp(region.Y, 0, 0.99);
                double w = Math.Clamp(region.Width, 0.01, 1 - x), h = Math.Clamp(region.Height, 0.01, 1 - y);
                string F(double v) => v.ToString("0.######", System.Globalization.CultureInfo.InvariantCulture);
                filter = $"crop=iw*{F(w)}:ih*{F(h)}:iw*{F(x)}:ih*{F(y)},";
            }
            filter += $"fps={FramesPerSecond},scale={FrameWidth}:{FrameHeight}:force_original_aspect_ratio=decrease:flags=area,pad={FrameWidth}:{FrameHeight}:(ow-iw)/2:(oh-ih)/2,format=gray";

            var psi = new ProcessStartInfo { FileName = ffmpegPath, UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
            foreach (string arg in new[] { "-hide_banner", "-loglevel", "error", "-i", sourcePath, "-an", "-sn", "-dn", "-vf", filter, "-f", "rawvideo", "-pix_fmt", "gray", "pipe:1" })
                psi.ArgumentList.Add(arg);

            using var process = new Process { StartInfo = psi, EnableRaisingEvents = true };
            if (!process.Start()) throw new InvalidOperationException("Nie udało się uruchomić FFmpeg.");
            using var registration = cancellationToken.Register(() => { try { if (!process.HasExited) process.Kill(true); } catch { } });
            Task<string> errorTask = process.StandardError.ReadToEndAsync();
            var events = new List<MotionEvent>();
            byte[] previous = new byte[FrameBytes], current = new byte[FrameBytes];
            bool hasPrevious = false;
            MotionEvent? active = null;
            long frameIndex = 0;
            int read;
            try
            {
                while ((read = await ReadFrameAsync(process.StandardOutput.BaseStream, current, cancellationToken).ConfigureAwait(false)) == FrameBytes)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    double seconds = (double)frameIndex++ / FramesPerSecond;
                    if (hasPrevious)
                    {
                        int changed = 0;
                        for (int i = 0; i < FrameBytes; i++)
                            if (Math.Abs(current[i] - previous[i]) >= pixelDifferenceThreshold) changed++;
                        double ratio = changed * 100.0 / FrameBytes;
                        if (ratio >= changedAreaPercent)
                        {
                            if (active == null || seconds - active.EndSeconds > 2.0)
                            {
                                active = new MotionEvent { StartSeconds = Math.Max(0, seconds - 0.5), EndSeconds = seconds, PeakChangedRatio = ratio };
                                events.Add(active);
                            }
                            else
                            {
                                active.EndSeconds = seconds;
                                active.PeakChangedRatio = Math.Max(active.PeakChangedRatio, ratio);
                            }
                        }
                    }
                    Buffer.BlockCopy(current, 0, previous, 0, FrameBytes);
                    hasPrevious = true;
                    if (durationSeconds > 0) progress?.Report(Math.Min(99, seconds / durationSeconds * 100));
                }
                await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
                string error = await errorTask.ConfigureAwait(false);
                cancellationToken.ThrowIfCancellationRequested();
                if (process.ExitCode != 0) throw new InvalidOperationException("FFmpeg nie przeanalizował nagrania: " + error.Trim());
                if (read > 0) throw new InvalidDataException("FFmpeg zwrócił niepełną klatkę obrazu.");
                events.RemoveAll(x => x.EndSeconds - x.StartSeconds < minimumEventDurationSeconds);
                progress?.Report(100);
                return events;
            }
            catch { try { if (!process.HasExited) process.Kill(true); } catch { } throw; }
        }

        private static async Task<int> ReadFrameAsync(Stream stream, byte[] buffer, CancellationToken token)
        {
            int offset = 0;
            while (offset < buffer.Length)
            {
                int count = await stream.ReadAsync(buffer.AsMemory(offset, buffer.Length - offset), token).ConfigureAwait(false);
                if (count == 0) break;
                offset += count;
            }
            return offset;
        }
    }
}