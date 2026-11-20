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
        private const double EventMergeGapSeconds = 1.5;

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
            if (!double.IsFinite(durationSeconds) || durationSeconds < 0) throw new ArgumentOutOfRangeException(nameof(durationSeconds));
            if (!double.IsFinite(pixelDifferenceThreshold) || pixelDifferenceThreshold < 1 || pixelDifferenceThreshold > 255) throw new ArgumentOutOfRangeException(nameof(pixelDifferenceThreshold));
            if (!double.IsFinite(changedAreaPercent) || changedAreaPercent <= 0 || changedAreaPercent > 100) throw new ArgumentOutOfRangeException(nameof(changedAreaPercent));
            if (!double.IsFinite(minimumEventDurationSeconds) || minimumEventDurationSeconds < 0 || minimumEventDurationSeconds > 60) throw new ArgumentOutOfRangeException(nameof(minimumEventDurationSeconds));

            // Decode at a fixed resolution. The ROI is applied below in C#, so pixels
            // outside the selected rectangle never contribute to the detection ratio.
            string filter = $"fps={FramesPerSecond},scale={FrameWidth}:{FrameHeight}:flags=area,format=gray";
            var psi = new ProcessStartInfo
            {
                FileName = ffmpegPath, UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardOutput = true, RedirectStandardError = true
            };
            foreach (string arg in new[] { "-hide_banner", "-loglevel", "error", "-i", sourcePath, "-an", "-sn", "-dn", "-vf", filter, "-f", "rawvideo", "-pix_fmt", "gray", "pipe:1" })
                psi.ArgumentList.Add(arg);

            int left = Math.Clamp((int)Math.Floor(Math.Clamp(region.X, 0, 1) * FrameWidth), 0, FrameWidth - 1);
            int top = Math.Clamp((int)Math.Floor(Math.Clamp(region.Y, 0, 1) * FrameHeight), 0, FrameHeight - 1);
            int right = Math.Clamp((int)Math.Ceiling(Math.Clamp(region.X + region.Width, 0, 1) * FrameWidth), left + 1, FrameWidth);
            int bottom = Math.Clamp((int)Math.Ceiling(Math.Clamp(region.Y + region.Height, 0, 1) * FrameHeight), top + 1, FrameHeight);
            int roiPixelCount = (right - left) * (bottom - top);
            int threshold = (int)Math.Round(pixelDifferenceThreshold);

            using var process = new Process { StartInfo = psi, EnableRaisingEvents = true };
            if (!process.Start()) throw new InvalidOperationException("Nie udało się uruchomić FFmpeg.");
            using var registration = cancellationToken.Register(() => { try { if (!process.HasExited) process.Kill(true); } catch { } });

            Task<string> errorTask = process.StandardError.ReadToEndAsync();
            var events = new List<MotionEvent>();
            byte[] previous = new byte[FrameBytes], current = new byte[FrameBytes];
            bool hasPrevious = false;
            MotionEvent? active = null;
            double lastDetectionSeconds = double.NegativeInfinity;
            double pendingDetectionSeconds = double.NaN;
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

                        // Reject isolated pixel changes (sensor noise/compression artefacts):
                        // a changed pixel is counted only when at least two of its 8 neighbours
                        // also exceed the configured brightness-difference threshold.
                        for (int y = top; y < bottom; y++)
                        {
                            int row = y * FrameWidth;
                            for (int x = left; x < right; x++)
                            {
                                int index = row + x;
                                if (Math.Abs(current[index] - previous[index]) < threshold) continue;

                                int neighbours = 0;
                                for (int ny = Math.Max(top, y - 1); ny <= Math.Min(bottom - 1, y + 1) && neighbours < 2; ny++)
                                {
                                    int neighbourRow = ny * FrameWidth;
                                    for (int nx = Math.Max(left, x - 1); nx <= Math.Min(right - 1, x + 1); nx++)
                                    {
                                        if (nx == x && ny == y) continue;
                                        if (Math.Abs(current[neighbourRow + nx] - previous[neighbourRow + nx]) >= threshold)
                                        {
                                            neighbours++;
                                            if (neighbours >= 2) break;
                                        }
                                    }
                                }
                                if (neighbours < 2) continue;

                                changed++;
                                minX = Math.Min(minX, x); maxX = Math.Max(maxX, x);
                                minY = Math.Min(minY, y); maxY = Math.Max(maxY, y);
                            }
                        }

                        double ratio = changed * 100.0 / roiPixelCount;
                        bool detected = changed > 0 && ratio >= changedAreaPercent;
                        if (detected)
                        {
                            if (active != null && seconds - lastDetectionSeconds <= EventMergeGapSeconds)
                            {
                                ExtendEvent(active, seconds, ratio, minX, minY, maxX, maxY, durationSeconds);
                                lastDetectionSeconds = seconds;
                                pendingDetectionSeconds = double.NaN;
                            }
                            else
                            {
                                // Require two consecutive sampled frames before creating an event.
                                // This filters one-frame flashes while preserving the first frame's time.
                                if (double.IsNaN(pendingDetectionSeconds) || seconds - pendingDetectionSeconds > 1.0)
                                {
                                    pendingDetectionSeconds = seconds;
                                }
                                else
                                {
                                    active = new MotionEvent
                                    {
                                        StartSeconds = Math.Max(0, pendingDetectionSeconds - 0.5),
                                        EndSeconds = ClampEnd(seconds + 0.5, durationSeconds),
                                        PeakChangedRatio = ratio,
                                        MotionX = (double)minX / FrameWidth,
                                        MotionY = (double)minY / FrameHeight,
                                        MotionWidth = (double)(maxX - minX + 1) / FrameWidth,
                                        MotionHeight = (double)(maxY - minY + 1) / FrameHeight
                                    };
                                    events.Add(active);
                                    lastDetectionSeconds = seconds;
                                    pendingDetectionSeconds = double.NaN;
                                }
                            }
                        }
                        else
                        {
                            pendingDetectionSeconds = double.NaN;
                            if (active != null && seconds - lastDetectionSeconds > EventMergeGapSeconds)
                                active = null;
                        }
                    }

                    Buffer.BlockCopy(current, 0, previous, 0, FrameBytes);
                    hasPrevious = true;
                    if (durationSeconds > 0)
                        progress?.Report(Math.Clamp(seconds / durationSeconds * 100, 0, 99));
                }

                await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
                string error = await errorTask.ConfigureAwait(false);
                cancellationToken.ThrowIfCancellationRequested();
                if (process.ExitCode != 0)
                    throw new InvalidOperationException("FFmpeg nie przeanalizował nagrania: " + error.Trim());
                if (read > 0)
                    throw new InvalidDataException("FFmpeg zwrócił niepełną klatkę obrazu.");

                events.RemoveAll(x => x.EndSeconds - x.StartSeconds < minimumEventDurationSeconds);
                progress?.Report(100);
                return events;
            }
            catch
            {
                try { if (!process.HasExited) process.Kill(true); } catch { }
                throw;
            }
        }

        private static void ExtendEvent(MotionEvent motionEvent, double seconds, double ratio,
            int minX, int minY, int maxX, int maxY, double durationSeconds)
        {
            motionEvent.EndSeconds = ClampEnd(seconds + 0.5, durationSeconds);
            motionEvent.PeakChangedRatio = Math.Max(motionEvent.PeakChangedRatio, ratio);

            double bx = (double)minX / FrameWidth, by = (double)minY / FrameHeight;
            double bw = (double)(maxX - minX + 1) / FrameWidth, bh = (double)(maxY - minY + 1) / FrameHeight;
            double oldRight = motionEvent.MotionX + motionEvent.MotionWidth;
            double oldBottom = motionEvent.MotionY + motionEvent.MotionHeight;
            double unionX = Math.Min(motionEvent.MotionX, bx), unionY = Math.Min(motionEvent.MotionY, by);
            motionEvent.MotionX = unionX;
            motionEvent.MotionY = unionY;
            motionEvent.MotionWidth = Math.Max(oldRight, bx + bw) - unionX;
            motionEvent.MotionHeight = Math.Max(oldBottom, by + bh) - unionY;
        }

        private static double ClampEnd(double value, double durationSeconds) =>
            durationSeconds > 0 ? Math.Min(value, durationSeconds) : value;

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