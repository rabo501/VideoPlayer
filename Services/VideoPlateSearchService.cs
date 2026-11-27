using System;
using System.Buffers;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;

namespace VideoPlayer
{
    internal sealed record PlateVideoSearchRequest(
        string FfmpegPath,
        string SourcePath,
        string TargetPlate,
        PlatePoint[] Points,
        int ReferenceWidth,
        int ReferenceHeight,
        double DurationSeconds,
        int RequestedIntervalSeconds,
        int MaximumSamples = 1200);

    internal sealed record PlateVideoSearchProgress(
        int ProcessedSamples,
        int EstimatedSamples,
        double CurrentSeconds,
        string StatusMessage,
        IReadOnlyList<PlateVideoSearchHit> HitsSnapshot);

    internal sealed record PlateMultiFrameRequest(
        string FfmpegPath,
        string SourcePath,
        PlatePixels CenterCrop,
        PlatePoint[] Points,
        int ReferenceWidth,
        int ReferenceHeight,
        double CenterSeconds,
        double DurationSeconds);

    /// <summary>
    /// Serwis analizy wieloklatkowej i wyszukiwania tablic rejestracyjnych w całym nagraniu.
    /// Oddzielony od WPF — korzysta ze strumieniowego potoku FFmpeg → stdout (rawvideo BGRA),
    /// równoległego prostowania/preprocessingu przez <see cref="Channel{T}"/> oraz jednej instancji
    /// <see cref="PlateOcrSession"/> (TesseractEngine) współdzielonej dla całej sesji.
    /// </summary>
    internal sealed class VideoPlateSearchService
    {
        private readonly record struct RawSearchFrame(int Index, double Seconds, byte[] PooledBuffer, int FrameBytes);
        private readonly record struct PreparedSearchFrame(int Index, double Seconds, PreparedPlateOcrInput? Input);

        public static (int EffectiveIntervalSeconds, int EstimatedSamples) ComputeEffectiveSampling(
            double durationSeconds,
            int requestedIntervalSeconds,
            int maximumSamples = 1200)
        {
            if (!double.IsFinite(durationSeconds) || durationSeconds <= 0)
                throw new ArgumentOutOfRangeException(nameof(durationSeconds), "Nie można ustalić czasu trwania nagrania.");
            if (requestedIntervalSeconds <= 0)
                requestedIntervalSeconds = 5;
            if (maximumSamples <= 0)
                maximumSamples = 1200;

            int effectiveInterval = Math.Max(requestedIntervalSeconds, (int)Math.Ceiling(durationSeconds / maximumSamples));
            int estimatedSamples = Math.Max(1, (int)Math.Ceiling(durationSeconds / effectiveInterval));
            return (effectiveInterval, estimatedSamples);
        }

        public static void MergeSearchHit(
            List<PlateVideoSearchHit> hits,
            double seconds,
            float confidence,
            int intervalSeconds)
        {
            ArgumentNullException.ThrowIfNull(hits);
            if (hits.Count > 0 && seconds - hits[^1].EndSeconds <= intervalSeconds * 1.6)
            {
                PlateVideoSearchHit previous = hits[^1];
                hits[^1] = previous with
                {
                    EndSeconds = seconds,
                    Confidence = Math.Max(previous.Confidence, confidence)
                };
            }
            else
            {
                hits.Add(new PlateVideoSearchHit(seconds, seconds, confidence));
            }
        }

        public async Task<IReadOnlyList<PlateOcrConsensusCandidate>> RecognizeAcrossFramesAsync(
            PlateMultiFrameRequest request,
            CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(request);
            cancellationToken.ThrowIfCancellationRequested();

            var crops = new List<PlatePixels> { request.CenterCrop };

            if (!string.IsNullOrWhiteSpace(request.SourcePath) &&
                File.Exists(request.SourcePath) &&
                File.Exists(request.FfmpegPath) &&
                request.ReferenceWidth >= 2 &&
                request.ReferenceHeight >= 2 &&
                request.Points.Length == 4)
            {
                double[] offsets = { -1.0, -0.5, 0.5, 1.0 };
                var neighbourTimes = offsets
                    .Select(offset => request.CenterSeconds + offset)
                    .Where(seconds => seconds >= 0 && (request.DurationSeconds <= 0 || seconds < request.DurationSeconds))
                    .ToArray();

                // Pobieramy sąsiednie klatki strumieniowo z stdout FFmpeg (bez plików tymczasowych na dysku) i prostujemy równolegle.
                var extracted = await Task.WhenAll(neighbourTimes.Select(async seconds =>
                {
                    byte[]? rawFrame = await ExtractRawBgraFrameAsync(
                        request.FfmpegPath,
                        request.SourcePath,
                        seconds,
                        request.ReferenceWidth,
                        request.ReferenceHeight,
                        cancellationToken).ConfigureAwait(false);
                    if (rawFrame == null) return null;

                    try
                    {
                        return PlatePerspective.Rectify(rawFrame, request.ReferenceWidth, request.ReferenceHeight, request.Points);
                    }
                    catch (ArgumentException)
                    {
                        return null;
                    }
                })).ConfigureAwait(false);

                foreach (PlatePixels? crop in extracted)
                {
                    if (crop != null) crops.Add(crop);
                }
            }

            // Jedna współdzielona instancja TesseractEngine na wszystkie sąsiednie klatki.
            return await Task.Run(() =>
            {
                using var session = new PlateOcrSession();
                return session.RecognizeConsensus(crops, cancellationToken);
            }, cancellationToken).ConfigureAwait(false);
        }

        public async Task<IReadOnlyList<PlateVideoSearchHit>> SearchInRecordingAsync(
            PlateVideoSearchRequest request,
            IProgress<PlateVideoSearchProgress>? progress,
            CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(request);
            if (!File.Exists(request.FfmpegPath))
                throw new FileNotFoundException("Nie znaleziono ffmpeg.exe w katalogu ffmpeg obok programu.", request.FfmpegPath);
            if (!File.Exists(request.SourcePath))
                throw new FileNotFoundException("Nie znaleziono nagrania.", request.SourcePath);
            if (request.ReferenceWidth < 2 || request.ReferenceHeight < 2)
                throw new ArgumentOutOfRangeException(nameof(request), "Nieprawidłowe wymiary klatki odniesienia.");
            if (!PlatePerspective.IsValid(request.Points, request.ReferenceWidth, request.ReferenceHeight))
                throw new ArgumentException("Zaznaczony obszar tablicy nie jest poprawnym czworokątem.", nameof(request));

            (int intervalSeconds, int estimatedSamples) = ComputeEffectiveSampling(
                request.DurationSeconds,
                request.RequestedIntervalSeconds,
                request.MaximumSamples);

            string target = PlateOcrRules.Normalize(request.TargetPlate);
            int width = request.ReferenceWidth;
            int height = request.ReferenceHeight;
            int frameBytes = checked(width * height * 4);
            PlatePoint[] points = (PlatePoint[])request.Points.Clone();

            var rawChannel = Channel.CreateBounded<RawSearchFrame>(new BoundedChannelOptions(8)
            {
                SingleWriter = true,
                SingleReader = false,
                FullMode = BoundedChannelFullMode.Wait
            });

            int workerCount = Math.Clamp(Environment.ProcessorCount - 1, 1, 4);
            var preparedChannel = Channel.CreateBounded<PreparedSearchFrame>(new BoundedChannelOptions(Math.Max(8, workerCount * 3))
            {
                SingleWriter = false,
                SingleReader = true,
                FullMode = BoundedChannelFullMode.Wait
            });

            progress?.Report(new PlateVideoSearchProgress(
                0, estimatedSamples, 0,
                "Strumieniowe dekodowanie klatek FFmpeg (bez plików tymczasowych)…",
                Array.Empty<PlateVideoSearchHit>()));

            // Etap 1: Producent — strumień FFmpeg -> rawvideo BGRA pipe:1 -> Bounded Channel
            Task producerTask = Task.Run(async () =>
            {
                Exception? failure = null;
                try
                {
                    await ProduceRawFramesFromFfmpegAsync(
                        request.FfmpegPath,
                        request.SourcePath,
                        width,
                        height,
                        intervalSeconds,
                        request.DurationSeconds,
                        frameBytes,
                        rawChannel.Writer,
                        cancellationToken).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    failure = ex;
                }
                finally
                {
                    rawChannel.Writer.TryComplete(failure);
                }
            }, cancellationToken);

            // Etap 2: Równolegli pracownicy — prostowanie perspektywy + skalowanie/szarość/próg Otsu
            Task[] workers = Enumerable.Range(0, workerCount).Select(_ => Task.Run(async () =>
            {
                await foreach (RawSearchFrame frame in rawChannel.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
                {
                    PreparedPlateOcrInput? prepared = null;
                    try
                    {
                        PlatePixels rectified = PlatePerspective.Rectify(frame.PooledBuffer, width, height, points);
                        if (rectified.Width >= 16 && rectified.Height >= 8)
                            prepared = PlateOcrService.PrepareInput(rectified, cancellationToken);
                    }
                    catch (ArgumentException)
                    {
                        prepared = null;
                    }
                    finally
                    {
                        ArrayPool<byte>.Shared.Return(frame.PooledBuffer);
                    }

                    await preparedChannel.Writer.WriteAsync(
                        new PreparedSearchFrame(frame.Index, frame.Seconds, prepared),
                        cancellationToken).ConfigureAwait(false);
                }
            }, cancellationToken)).ToArray();

            Task workersCompletion = Task.WhenAll(workers).ContinueWith(
                t =>
                {
                    if (t.IsFaulted && t.Exception != null)
                        preparedChannel.Writer.TryComplete(t.Exception.InnerException ?? t.Exception);
                    else if (t.IsCanceled)
                        preparedChannel.Writer.TryComplete(new OperationCanceledException(cancellationToken));
                    else
                        preparedChannel.Writer.TryComplete();
                },
                CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);

            // Etap 3: Konsument OCR — jedna instancja TesseractEngine na całą sesję, zachowując kolejność chronologiczną
            var hits = new List<PlateVideoSearchHit>();
            var pendingByIndex = new SortedDictionary<int, PreparedSearchFrame>();
            int nextExpectedIndex = 0;
            int processedSamples = 0;

            Task consumerTask = Task.Run(async () =>
            {
                using var session = new PlateOcrSession();

                await foreach (PreparedSearchFrame item in preparedChannel.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
                {
                    pendingByIndex[item.Index] = item;

                    while (pendingByIndex.Remove(nextExpectedIndex, out PreparedSearchFrame ordered))
                    {
                        nextExpectedIndex++;
                        processedSamples++;
                        cancellationToken.ThrowIfCancellationRequested();

                        bool hitAdded = false;
                        if (ordered.Input != null)
                        {
                            var candidates = session.RecognizePrepared(ordered.Input, cancellationToken);
                            var match = candidates
                                .Where(candidate => string.Equals(candidate.Text, target, StringComparison.Ordinal))
                                .OrderByDescending(candidate => candidate.Confidence)
                                .FirstOrDefault();

                            if (match != null)
                            {
                                MergeSearchHit(hits, ordered.Seconds, match.Confidence, intervalSeconds);
                                hitAdded = true;
                            }
                        }

                        if (hitAdded || processedSamples == 1 || processedSamples % 5 == 0)
                        {
                            string formattedTime = TimeSpan.FromSeconds(ordered.Seconds).ToString(@"hh\:mm\:ss", CultureInfo.InvariantCulture);
                            progress?.Report(new PlateVideoSearchProgress(
                                processedSamples,
                                Math.Max(processedSamples, estimatedSamples),
                                ordered.Seconds,
                                $"Analiza OCR: {processedSamples}/{Math.Max(processedSamples, estimatedSamples)} próbek • {formattedTime} • trafienia: {hits.Count}",
                                hitAdded ? hits.ToArray() : Array.Empty<PlateVideoSearchHit>()));
                        }
                    }
                }

                if (processedSamples == 0)
                    throw new InvalidOperationException("FFmpeg nie wyodrębnił żadnej klatki z nagrania.");
            }, cancellationToken);

            try
            {
                await Task.WhenAll(producerTask, workersCompletion, consumerTask).ConfigureAwait(false);
            }
            catch
            {
                // Opróżnij ewentualne nieodebrane bufory z rawChannel z powrotem do ArrayPool
                while (rawChannel.Reader.TryRead(out RawSearchFrame leftover))
                    ArrayPool<byte>.Shared.Return(leftover.PooledBuffer);
                throw;
            }

            return hits;
        }

        private static async Task ProduceRawFramesFromFfmpegAsync(
            string ffmpegPath,
            string sourcePath,
            int width,
            int height,
            int intervalSeconds,
            double durationSeconds,
            int frameBytes,
            ChannelWriter<RawSearchFrame> writer,
            CancellationToken cancellationToken)
        {
            var psi = new ProcessStartInfo
            {
                FileName = ffmpegPath,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };

            string filter = $"fps=1/{intervalSeconds}:start_time=0,scale={width}:{height}:flags=bilinear";
            foreach (string arg in new[]
            {
                "-hide_banner", "-loglevel", "error", "-i", sourcePath,
                "-map", "0:v:0", "-an", "-sn", "-dn",
                "-vf", filter,
                "-f", "rawvideo", "-pix_fmt", "bgra", "pipe:1"
            })
                psi.ArgumentList.Add(arg);

            using var process = new Process { StartInfo = psi, EnableRaisingEvents = true };
            if (!process.Start())
                throw new InvalidOperationException("Nie udało się uruchomić FFmpeg.");

            using var registration = cancellationToken.Register(() =>
            {
                try { if (!process.HasExited) process.Kill(true); } catch { }
            });

            Task<string> errorTask = process.StandardError.ReadToEndAsync();
            Stream stdout = process.StandardOutput.BaseStream;
            int index = 0;

            try
            {
                while (true)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    byte[] buffer = ArrayPool<byte>.Shared.Rent(frameBytes);
                    int read = await ReadExactFrameAsync(stdout, buffer, frameBytes, cancellationToken).ConfigureAwait(false);
                    if (read == 0)
                    {
                        ArrayPool<byte>.Shared.Return(buffer);
                        break;
                    }
                    if (read < frameBytes)
                    {
                        ArrayPool<byte>.Shared.Return(buffer);
                        throw new InvalidDataException("FFmpeg zwrócił niepełną klatkę obrazu w strumieniu.");
                    }

                    double seconds = Math.Min(index * (double)intervalSeconds, durationSeconds);
                    await writer.WriteAsync(new RawSearchFrame(index, seconds, buffer, frameBytes), cancellationToken).ConfigureAwait(false);
                    index++;
                }

                await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
                string error = await errorTask.ConfigureAwait(false);
                cancellationToken.ThrowIfCancellationRequested();
                if (process.ExitCode != 0)
                    throw new InvalidOperationException("FFmpeg nie przygotował strumienia klatek nagrania: " + error.Trim());
            }
            catch
            {
                try { if (!process.HasExited) process.Kill(true); } catch { }
                throw;
            }
        }

        private static async Task<byte[]?> ExtractRawBgraFrameAsync(
            string ffmpegPath,
            string sourcePath,
            double seconds,
            int width,
            int height,
            CancellationToken cancellationToken)
        {
            var psi = new ProcessStartInfo
            {
                FileName = ffmpegPath,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };

            string filter = $"scale={width}:{height}:flags=bilinear";
            foreach (string arg in new[]
            {
                "-hide_banner", "-loglevel", "error",
                "-ss", seconds.ToString("0.###", CultureInfo.InvariantCulture),
                "-i", sourcePath,
                "-frames:v", "1", "-an", "-sn", "-dn",
                "-vf", filter,
                "-f", "rawvideo", "-pix_fmt", "bgra", "pipe:1"
            })
                psi.ArgumentList.Add(arg);

            using var process = new Process { StartInfo = psi };
            if (!process.Start())
                throw new InvalidOperationException("Nie udało się uruchomić FFmpeg do pobrania klatki OCR.");

            using var registration = cancellationToken.Register(() =>
            {
                try { if (!process.HasExited) process.Kill(true); } catch { }
            });

            int frameBytes = checked(width * height * 4);
            byte[] buffer = new byte[frameBytes];
            Task<string> errorTask = process.StandardError.ReadToEndAsync();
            int read = await ReadExactFrameAsync(process.StandardOutput.BaseStream, buffer, frameBytes, cancellationToken).ConfigureAwait(false);

            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
            string error = await errorTask.ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();

            if (process.ExitCode != 0)
                throw new InvalidOperationException("FFmpeg nie pobrał klatki do analizy wieloklatkowej: " + error.Trim());

            return read == frameBytes ? buffer : null;
        }

        private static async Task<int> ReadExactFrameAsync(Stream stream, byte[] buffer, int frameBytes, CancellationToken token)
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
