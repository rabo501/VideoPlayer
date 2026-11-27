using System;
using System.Buffers;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Numerics;
using System.Threading;
using System.Threading.Tasks;

namespace VideoPlayer
{
    internal readonly record struct MotionFrameDiffResult(int ChangedPixels, int MinX, int MinY, int MaxX, int MaxY);

    internal sealed class MotionDetectionService
    {
        public const int FrameWidth = 320;
        public const int FrameHeight = 180;
        private const int FramesPerSecond = 2;
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
                FileName = ffmpegPath,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
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
            byte[] previous = ArrayPool<byte>.Shared.Rent(FrameBytes);
            byte[] current = ArrayPool<byte>.Shared.Rent(FrameBytes);
            bool hasPrevious = false;
            MotionEvent? active = null;
            double lastDetectionSeconds = double.NegativeInfinity;
            double pendingDetectionSeconds = double.NaN;
            long frameIndex = 0;
            int read;

            try
            {
                while ((read = await ReadFrameAsync(process.StandardOutput.BaseStream, current, FrameBytes, cancellationToken).ConfigureAwait(false)) == FrameBytes)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    double seconds = (double)frameIndex++ / FramesPerSecond;

                    if (hasPrevious)
                    {
                        MotionFrameDiffResult diff = AnalyzeFrameDifference(
                            previous.AsSpan(0, FrameBytes),
                            current.AsSpan(0, FrameBytes),
                            threshold,
                            left, top, right, bottom,
                            FrameWidth, FrameHeight);

                        double ratio = diff.ChangedPixels * 100.0 / roiPixelCount;
                        bool detected = diff.ChangedPixels > 0 && ratio >= changedAreaPercent;
                        if (detected)
                        {
                            if (active != null && seconds - lastDetectionSeconds <= EventMergeGapSeconds)
                            {
                                ExtendEvent(active, seconds, ratio, diff.MinX, diff.MinY, diff.MaxX, diff.MaxY, durationSeconds);
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
                                        MotionX = (double)diff.MinX / FrameWidth,
                                        MotionY = (double)diff.MinY / FrameHeight,
                                        MotionWidth = (double)(diff.MaxX - diff.MinX + 1) / FrameWidth,
                                        MotionHeight = (double)(diff.MaxY - diff.MinY + 1) / FrameHeight
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

                    // Zamiana buforów O(1) zamiast Buffer.BlockCopy na każdą klatkę.
                    (previous, current) = (current, previous);
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
            finally
            {
                ArrayPool<byte>.Shared.Return(previous);
                ArrayPool<byte>.Shared.Return(current);
            }
        }

        internal static MotionFrameDiffResult AnalyzeFrameDifference(
            ReadOnlySpan<byte> previous,
            ReadOnlySpan<byte> current,
            int threshold,
            int left,
            int top,
            int right,
            int bottom,
            int frameWidth,
            int frameHeight)
        {
            if (previous.Length < frameWidth * frameHeight || current.Length < frameWidth * frameHeight)
                throw new ArgumentException("Bufory klatek są mniejsze niż zadeklarowane wymiary obrazu.");

            int changed = 0, minX = frameWidth, minY = frameHeight, maxX = -1, maxY = -1;
            int roiWidth = right - left;
            int vecLen = Vector<byte>.Count;
            byte byteThreshold = (byte)Math.Clamp(threshold, 1, 255);
            var thresholdVec = new Vector<byte>(byteThreshold);

            for (int y = top; y < bottom; y++)
            {
                int row = y * frameWidth;

                // Szybkie skanowanie wektorowe wiersza — pozwala pominąć wiersze bez żadnej zmiany pikseli.
                if (Vector.IsHardwareAccelerated && roiWidth >= vecLen)
                {
                    bool rowHasCandidate = false;
                    int xVec = left;
                    int lastVecEnd = right - vecLen;
                    for (; xVec <= lastVecEnd; xVec += vecLen)
                    {
                        var vPrev = new Vector<byte>(previous.Slice(row + xVec, vecLen));
                        var vCurr = new Vector<byte>(current.Slice(row + xVec, vecLen));
                        var absDiff = Vector.Subtract(Vector.Max(vPrev, vCurr), Vector.Min(vPrev, vCurr));
                        if (Vector.GreaterThanOrEqualAny(absDiff, thresholdVec))
                        {
                            rowHasCandidate = true;
                            break;
                        }
                    }

                    if (!rowHasCandidate)
                    {
                        for (; xVec < right; xVec++)
                        {
                            if (Math.Abs(current[row + xVec] - previous[row + xVec]) >= threshold)
                            {
                                rowHasCandidate = true;
                                break;
                            }
                        }
                    }

                    if (!rowHasCandidate) continue;
                }

                for (int x = left; x < right; x++)
                {
                    int index = row + x;
                    if (Math.Abs(current[index] - previous[index]) < threshold) continue;

                    // Odrzucanie izolowanych pikseli (szum matrycy / artefakty kompresji):
                    // wymagamy co najmniej dwóch spośród 8 sąsiednich pikseli przekraczających próg.
                    int neighbours = 0;
                    int nyStart = Math.Max(top, y - 1);
                    int nyEnd = Math.Min(bottom - 1, y + 1);
                    int nxStart = Math.Max(left, x - 1);
                    int nxEnd = Math.Min(right - 1, x + 1);

                    for (int ny = nyStart; ny <= nyEnd && neighbours < 2; ny++)
                    {
                        int neighbourRow = ny * frameWidth;
                        for (int nx = nxStart; nx <= nxEnd; nx++)
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
                    if (x < minX) minX = x;
                    if (x > maxX) maxX = x;
                    if (y < minY) minY = y;
                    if (y > maxY) maxY = y;
                }
            }

            return new MotionFrameDiffResult(changed, minX, minY, maxX, maxY);
        }

        internal static async Task<byte[]> ExtractPreviewBmpAsync(
            string ffmpegPath,
            string sourcePath,
            double seconds,
            CancellationToken cancellationToken = default)
        {
            if (!File.Exists(ffmpegPath)) throw new FileNotFoundException("Nie znaleziono FFmpeg.", ffmpegPath);
            if (!File.Exists(sourcePath)) throw new FileNotFoundException("Nie znaleziono nagrania.", sourcePath);

            var psi = new ProcessStartInfo
            {
                FileName = ffmpegPath,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            foreach (string arg in new[]
            {
                "-hide_banner", "-loglevel", "error",
                "-ss", Math.Max(0, seconds).ToString("0.###", CultureInfo.InvariantCulture),
                "-i", sourcePath,
                "-frames:v", "1", "-an", "-sn", "-dn",
                "-f", "image2pipe", "-vcodec", "bmp", "pipe:1"
            })
                psi.ArgumentList.Add(arg);

            using var process = Process.Start(psi) ?? throw new InvalidOperationException("Nie udało się uruchomić FFmpeg.");
            using var reg = cancellationToken.Register(() => { try { if (!process.HasExited) process.Kill(true); } catch { } });

            using var outputStream = new MemoryStream();
            Task copyTask = process.StandardOutput.BaseStream.CopyToAsync(outputStream, cancellationToken);
            Task<string> errorTask = process.StandardError.ReadToEndAsync();

            await Task.WhenAll(copyTask, process.WaitForExitAsync(cancellationToken)).ConfigureAwait(false);
            string error = await errorTask.ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();

            if (process.ExitCode != 0 || outputStream.Length == 0)
                throw new IOException("Nie udało się pobrać klatki zdarzenia. " + error.Trim());

            return outputStream.ToArray();
        }

        internal static void ExtendEvent(MotionEvent motionEvent, double seconds, double ratio,
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

        private static async Task<int> ReadFrameAsync(Stream stream, byte[] buffer, int frameBytes, CancellationToken token)
        {
            int offset = 0;
            while (offset < frameBytes)
            {
                int count = await stream.ReadAsync(buffer.AsMemory(offset, frameBytes - offset), token).ConfigureAwait(false);
                if (count == 0) break;
                offset += count;
            }
            return offset;
        }
    }
}