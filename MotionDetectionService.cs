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
        private const int FrameWidth = 320, FrameHeight = 180, FramesPerSecond = 2;
        private const int FrameBytes = FrameWidth * FrameHeight;
        internal sealed class MotionEvent
        {
            public double StartSeconds { get; init; }
            public double EndSeconds { get; set; }
            public double PeakChangedRatio { get; set; }
            public double MotionX { get; set; }
            public double MotionY { get; set; }
            public double MotionWidth { get; set; }
            public double MotionHeight { get; set; }
        }
        internal readonly record struct MotionRegion(double X, double Y, double Width, double Height)
        {
            public static MotionRegion FullFrame => new(0, 0, 1, 1);
            public bool IsFullFrame => X <= 0 && Y <= 0 && Width >= 1 && Height >= 1;
        }

        public async Task<List<MotionEvent>> AnalyzeAsync(string ffmpegPath, string sourcePath, double durationSeconds,
            double pixelDifferenceThreshold, double changedAreaPercent, double minimumEventDurationSeconds,
            MotionRegion region, IProgress<double>? progress, CancellationToken cancellationToken)
        {
            if (!File.Exists(ffmpegPath)) throw new FileNotFoundException("Nie znaleziono FFmpeg.", ffmpegPath);
            if (!File.Exists(sourcePath)) throw new FileNotFoundException("Nie znaleziono nagrania.", sourcePath);
            if (pixelDifferenceThreshold < 1 || pixelDifferenceThreshold > 255) throw new ArgumentOutOfRangeException(nameof(pixelDifferenceThreshold));
            if (changedAreaPercent <= 0 || changedAreaPercent > 100) throw new ArgumentOutOfRangeException(nameof(changedAreaPercent));
            if (minimumEventDurationSeconds < 0 || minimumEventDurationSeconds > 60) throw new ArgumentOutOfRangeException(nameof(minimumEventDurationSeconds));

            // ROI is applied to the decoded frame in C#, not as an FFmpeg crop filter.
            // This guarantees that only pixels inside the user's selected rectangle are counted.
            string filter = $"fps={FramesPerSecond},scale={FrameWidth}:{FrameHeight}:flags=area,format=gray";
            var psi = new ProcessStartInfo { FileName = ffmpegPath, UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
            foreach (string arg in new[] { "-hide_banner", "-loglevel", "error", "-i", sourcePath, "-an", "-sn", "-dn", "-vf", filter, "-f", "rawvideo", "-pix_fmt", "gray", "pipe:1" }) psi.ArgumentList.Add(arg);

            int left = Math.Clamp((int)Math.Floor(Math.Clamp(region.X, 0, 1) * FrameWidth), 0, FrameWidth - 1);
            int top = Math.Clamp((int)Math.Floor(Math.Clamp(region.Y, 0, 1) * FrameHeight), 0, FrameHeight - 1);
            int right = Math.Clamp((int)Math.Ceiling(Math.Clamp(region.X + region.Width, 0, 1) * FrameWidth), left + 1, FrameWidth);
            int bottom = Math.Clamp((int)Math.Ceiling(Math.Clamp(region.Y + region.Height, 0, 1) * FrameHeight), top + 1, FrameHeight);
            int roiPixelCount = (right - left) * (bottom - top);

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
                        int changed = 0, minX = FrameWidth, minY = FrameHeight, maxX = -1, maxY = -1;
                        for (int y = top; y < bottom; y++)
                        {
                            int row = y * FrameWidth;
                            for (int x = left; x < right; x++)
                            {
                                int index = row + x;
                                if (Math.Abs(current[index] - previous[index]) < pixelDifferenceThreshold) continue;
                                changed++;
                                minX = Math.Min(minX, x); maxX = Math.Max(maxX, x);
                                minY = Math.Min(minY, y); maxY = Math.Max(maxY, y);
                            }
                        }
                        double ratio = changed * 100.0 / roiPixelCount;
                        if (changed > 0 && ratio >= changedAreaPercent)
                        {
                            double bx = (double)minX / FrameWidth, by = (double)minY / FrameHeight;
                            double bw = (double)(maxX - minX + 1) / FrameWidth, bh = (double)(maxY - minY + 1) / FrameHeight;
                            if (active == null || seconds - active.EndSeconds > 2.0)
                            {
                                active = new MotionEvent
                                {
                                    StartSeconds = Math.Max(0, seconds - 0.5),
                                    EndSeconds = Math.Min(durationSeconds > 0 ? durationSeconds : double.MaxValue, seconds + 0.5),
                                    PeakChangedRatio = ratio, MotionX = bx, MotionY = by, MotionWidth = bw, MotionHeight = bh
                                };
                                events.Add(active);
                            }
                            else
                            {
                                active.EndSeconds = Math.Min(durationSeconds > 0 ? durationSeconds : double.MaxValue, seconds + 0.5);
                                active.PeakChangedRatio = Math.Max(active.PeakChangedRatio, ratio);
                                double oldRight = active.MotionX + active.MotionWidth, oldBottom = active.MotionY + active.MotionHeight;
                                double unionX = Math.Min(active.MotionX, bx), unionY = Math.Min(active.MotionY, by);
                                active.MotionX = unionX; active.MotionY = unionY;
                                active.MotionWidth = Math.Max(oldRight, bx + bw) - unionX;
                                active.MotionHeight = Math.Max(oldBottom, by + bh) - unionY;
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