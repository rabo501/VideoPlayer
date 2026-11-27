using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using Tesseract;

namespace VideoPlayer
{
    internal sealed record PlateOcrCandidate(string Text, float Confidence);

    internal sealed record PlateOcrConsensusCandidate(string Text, int VoteCount, int FrameCount, float AverageConfidence);

    /// <summary>
    /// Gotowy do rozpoznania wycinek tablicy po skalowaniu, konwersji do skali szarości,
    /// dodaniu białego marginesu i wyznaczeniu progu Otsu — gotowy do przekazania do silnika Tesseract bez kompresji PNG.
    /// </summary>
    internal sealed record PreparedPlateOcrInput(int Width, int Height, byte[] GrayscalePixels, int OtsuThreshold);

    /// <summary>
    /// Jednorazowa sesja silnika Tesseract OCR (utrzymuje jedną instancję <see cref="TesseractEngine"/>
    /// przez cały czas wyszukiwania lub analizy wieloklatkowej, eliminując wielokrotne ładowanie modelu LSTM).
    /// Instancja nie jest wielowątkowa; w potokach równoległych powinna być używana przez dedykowanego konsumenta.
    /// </summary>
    internal sealed class PlateOcrSession : IDisposable
    {
        private readonly TesseractEngine _engine;
        private bool _disposed;

        public PlateOcrSession(string? baseDirectory = null)
        {
            string root = string.IsNullOrWhiteSpace(baseDirectory) ? AppContext.BaseDirectory : baseDirectory;
            PlateOcrService.EnsureRuntimeAssetsAvailable(root);

            string tessdataPath = Path.Combine(root, "tessdata");
            _engine = new TesseractEngine(tessdataPath, "eng", EngineMode.LstmOnly);
            _engine.SetVariable("tessedit_char_whitelist", "ABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789");
            _engine.SetVariable("user_defined_dpi", "300");
        }

        public IReadOnlyList<PlateOcrCandidate> Recognize(PlatePixels crop, CancellationToken cancellationToken)
        {
            PreparedPlateOcrInput prepared = PlateOcrService.PrepareInput(crop, cancellationToken);
            return RecognizePrepared(prepared, cancellationToken);
        }

        public IReadOnlyList<PlateOcrCandidate> RecognizePrepared(PreparedPlateOcrInput prepared, CancellationToken cancellationToken)
        {
            if (_disposed) throw new ObjectDisposedException(nameof(PlateOcrSession));
            ArgumentNullException.ThrowIfNull(prepared);
            cancellationToken.ThrowIfCancellationRequested();

            byte[] gray = prepared.GrayscalePixels;
            int width = prepared.Width;
            int height = prepared.Height;
            int threshold = prepared.OtsuThreshold;

            int bmpStride = (width + 3) & ~3;
            const int bmpHeaderSize = 1078;
            byte[] bmpBuffer = PlateOcrService.AllocateGrayscaleBmpBuffer(width, height, bmpStride);

            var candidates = new List<PlateOcrCandidate>(6);
            byte[]? modeBuffer = null;

            for (int mode = 0; mode < 3; mode++)
            {
                cancellationToken.ThrowIfCancellationRequested();

                byte[] sourceGray;
                if (mode == 0)
                {
                    sourceGray = gray;
                }
                else
                {
                    modeBuffer ??= new byte[gray.Length];
                    bool invert = mode == 2;
                    for (int i = 0; i < gray.Length; i++)
                    {
                        byte v = gray[i] > threshold ? (byte)255 : (byte)0;
                        modeBuffer[i] = invert ? (byte)(255 - v) : v;
                    }
                    sourceGray = modeBuffer;
                }

                PlateOcrService.WriteGrayscalePayloadToBmp(bmpBuffer, bmpHeaderSize, width, height, bmpStride, sourceGray);
                using var pix = Pix.LoadFromMemory(bmpBuffer);

                foreach (PageSegMode segmentation in PlateOcrService.SegmentationModes)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    using var page = _engine.Process(pix, segmentation);
                    string text = PlateOcrRules.Normalize(page.GetText());

                    // Odrzucamy krótkie fragmenty obrazu (np. 2–3 znaki), aby ograniczyć fałszywe trafienia.
                    if (text.Length >= 4 && text.Length <= 12)
                        candidates.Add(new PlateOcrCandidate(text, page.GetMeanConfidence()));
                }
            }

            cancellationToken.ThrowIfCancellationRequested();
            return candidates
                .GroupBy(x => x.Text, StringComparer.Ordinal)
                .Select(g => g.OrderByDescending(x => x.Confidence).First())
                .OrderByDescending(x => x.Confidence)
                .Take(5)
                .ToArray();
        }

        public IReadOnlyList<PlateOcrConsensusCandidate> RecognizeConsensus(IReadOnlyList<PlatePixels> crops, CancellationToken cancellationToken)
        {
            if (_disposed) throw new ObjectDisposedException(nameof(PlateOcrSession));
            if (crops == null || crops.Count == 0) return Array.Empty<PlateOcrConsensusCandidate>();

            var readings = new List<(string Text, float Confidence, int Frame)>();
            for (int i = 0; i < crops.Count; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                foreach (var candidate in Recognize(crops[i], cancellationToken))
                    readings.Add((candidate.Text, candidate.Confidence, i));
            }

            return PlateOcrService.AggregateConsensus(readings, crops.Count);
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _engine.Dispose();
        }
    }

    internal static class PlateOcrService
    {
        internal static readonly PageSegMode[] SegmentationModes =
        {
            PageSegMode.SingleLine,
            PageSegMode.RawLine
        };

        private const int BorderPadding = 20;
        private const int GrayscaleBmpHeaderSize = 1078;

        internal static string Normalize(string text) => PlateOcrRules.Normalize(text);

        internal static void EnsureRuntimeAssetsAvailable(string baseDirectory)
        {
            string tessdataPath = Path.Combine(baseDirectory, "tessdata");
            if (!File.Exists(Path.Combine(tessdataPath, "eng.traineddata")))
            {
                throw new FileNotFoundException(
                    "Brak pliku tessdata/eng.traineddata. Sprawdź, czy katalog tessdata znajduje się obok pliku VideoPlayer.exe.");
            }

            // Wrapper charlesw/Tesseract 5.2.0 ładuje natywne biblioteki z podkatalogu x64.
            string nativeDirectory = Path.Combine(baseDirectory, "x64");
            string[] nativeLibraries = { "tesseract50.dll", "leptonica-1.82.0.dll" };
            foreach (string library in nativeLibraries)
            {
                string libraryPath = Path.Combine(nativeDirectory, library);
                if (!File.Exists(libraryPath))
                {
                    throw new FileNotFoundException(
                        $"Brak natywnej biblioteki OCR: x64\\{library}. Sprawdź obecność pakietu Tesseract 5.2.0 lub skopiuj 64-bitowe biblioteki do katalogu x64 obok VideoPlayer.exe.",
                        libraryPath);
                }
            }
        }

        /// <summary>
        /// Przygotowuje wyprostowany wycinek <see cref="PlatePixels"/> (BGRA32) bez użycia GDI+ i kodowania PNG:
        /// skaluje dwuliniowo do wysokości docelowej OCR, dodaje 20 px białego marginesu i liczy próg Otsu.
        /// Ta metoda jest bezpieczna wielowątkowo i może być uruchamiana równolegle przed konsumentem OCR.
        /// </summary>
        internal static PreparedPlateOcrInput PrepareInput(PlatePixels crop, CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(crop);
            if (crop.Width <= 0 || crop.Height <= 0)
                throw new ArgumentOutOfRangeException(nameof(crop), "Nieprawidłowe wymiary wycinka tablicy.");
            if (crop.Pixels.LongLength != (long)crop.Width * crop.Height * 4)
                throw new ArgumentException("Nieprawidłowy rozmiar bufora BGRA.", nameof(crop));

            int srcWidth = crop.Width;
            int srcHeight = crop.Height;
            byte[] src = crop.Pixels;

            double scale = Math.Min(Math.Clamp(110.0 / srcHeight, 1.0, 4.0), 1800.0 / srcWidth);
            int scaledWidth = Math.Max(1, (int)Math.Round(srcWidth * scale));
            int scaledHeight = Math.Max(1, (int)Math.Round(srcHeight * scale));

            int dstWidth = scaledWidth + BorderPadding * 2;
            int dstHeight = scaledHeight + BorderPadding * 2;
            byte[] gray = new byte[checked(dstWidth * dstHeight)];
            Array.Fill(gray, (byte)255);

            // Najpierw konwertujemy źródłowe BGRA32 -> 8-bitowa luminancja w jednym przebiegu.
            byte[] srcLuma = new byte[checked(srcWidth * srcHeight)];
            for (int p = 0; p < srcLuma.Length; p++)
            {
                int off = p * 4;
                srcLuma[p] = (byte)((src[off] * 114 + src[off + 1] * 587 + src[off + 2] * 299) / 1000);
            }

            int[] histogram = new int[256];
            // Ramka (20 px) jest biała (255).
            int borderPixels = dstWidth * dstHeight - scaledWidth * scaledHeight;
            histogram[255] = borderPixels;

            double xRatio = scaledWidth > 1 ? (srcWidth - 1) / (double)(scaledWidth - 1) : 0;
            double yRatio = scaledHeight > 1 ? (srcHeight - 1) / (double)(scaledHeight - 1) : 0;

            for (int y = 0; y < scaledHeight; y++)
            {
                if ((y & 31) == 0) cancellationToken.ThrowIfCancellationRequested();

                double sy = y * yRatio;
                int y0 = (int)sy;
                int y1 = Math.Min(y0 + 1, srcHeight - 1);
                double fy = sy - y0;
                double invFy = 1.0 - fy;

                int row0 = y0 * srcWidth;
                int row1 = y1 * srcWidth;
                int dstRow = (y + BorderPadding) * dstWidth + BorderPadding;

                for (int x = 0; x < scaledWidth; x++)
                {
                    double sx = x * xRatio;
                    int x0 = (int)sx;
                    int x1 = Math.Min(x0 + 1, srcWidth - 1);
                    double fx = sx - x0;
                    double invFx = 1.0 - fx;

                    double topVal = srcLuma[row0 + x0] * invFx + srcLuma[row0 + x1] * fx;
                    double botVal = srcLuma[row1 + x0] * invFx + srcLuma[row1 + x1] * fx;
                    byte v = (byte)Math.Clamp((int)Math.Round(topVal * invFy + botVal * fy), 0, 255);

                    gray[dstRow + x] = v;
                    histogram[v]++;
                }
            }

            int threshold = ComputeOtsuThreshold(histogram, dstWidth * dstHeight);
            return new PreparedPlateOcrInput(dstWidth, dstHeight, gray, threshold);
        }

        internal static int ComputeOtsuThreshold(ReadOnlySpan<int> histogram, int totalPixels)
        {
            if (totalPixels <= 0) return 127;

            int weight = 0;
            int threshold = 127;
            double sum = 0;
            double leftSum = 0;
            double best = -1;

            for (int i = 0; i < 256; i++)
                sum += i * (double)histogram[i];

            for (int i = 0; i < 256; i++)
            {
                weight += histogram[i];
                if (weight == 0) continue;
                int right = totalPixels - weight;
                if (right == 0) break;
                leftSum += i * (double)histogram[i];
                double delta = leftSum / weight - (sum - leftSum) / right;
                double variance = (double)weight * right * delta * delta;
                if (variance > best)
                {
                    best = variance;
                    threshold = i;
                }
            }

            return threshold;
        }

        internal static byte[] AllocateGrayscaleBmpBuffer(int width, int height, int bmpStride)
        {
            int pixelBytes = checked(bmpStride * height);
            int fileSize = checked(GrayscaleBmpHeaderSize + pixelBytes);
            byte[] bmp = new byte[fileSize];

            // BITMAPFILEHEADER (14 B)
            bmp[0] = (byte)'B';
            bmp[1] = (byte)'M';
            BinaryPrimitives.WriteInt32LittleEndian(bmp.AsSpan(2, 4), fileSize);
            BinaryPrimitives.WriteInt32LittleEndian(bmp.AsSpan(10, 4), GrayscaleBmpHeaderSize);

            // BITMAPINFOHEADER (40 B)
            BinaryPrimitives.WriteInt32LittleEndian(bmp.AsSpan(14, 4), 40);
            BinaryPrimitives.WriteInt32LittleEndian(bmp.AsSpan(18, 4), width);
            BinaryPrimitives.WriteInt32LittleEndian(bmp.AsSpan(22, 4), height); // bottom-up
            BinaryPrimitives.WriteUInt16LittleEndian(bmp.AsSpan(26, 2), 1);     // biPlanes
            BinaryPrimitives.WriteUInt16LittleEndian(bmp.AsSpan(28, 2), 8);     // biBitCount = 8bpp
            BinaryPrimitives.WriteInt32LittleEndian(bmp.AsSpan(30, 4), 0);      // BI_RGB
            BinaryPrimitives.WriteInt32LittleEndian(bmp.AsSpan(34, 4), pixelBytes);
            BinaryPrimitives.WriteInt32LittleEndian(bmp.AsSpan(38, 4), 11811);  // ~300 DPI X
            BinaryPrimitives.WriteInt32LittleEndian(bmp.AsSpan(42, 4), 11811);  // ~300 DPI Y
            BinaryPrimitives.WriteInt32LittleEndian(bmp.AsSpan(46, 4), 256);    // biClrUsed
            BinaryPrimitives.WriteInt32LittleEndian(bmp.AsSpan(50, 4), 256);    // biClrImportant

            // Paleta 256 odcieni szarości (BGRA)
            int paletteOffset = 54;
            for (int i = 0; i < 256; i++)
            {
                int p = paletteOffset + i * 4;
                bmp[p] = (byte)i;
                bmp[p + 1] = (byte)i;
                bmp[p + 2] = (byte)i;
                bmp[p + 3] = 0;
            }

            return bmp;
        }

        internal static void WriteGrayscalePayloadToBmp(
            byte[] bmpBuffer, int headerSize, int width, int height, int bmpStride, byte[] grayPixels)
        {
            for (int y = 0; y < height; y++)
            {
                int srcRow = (height - 1 - y) * width;
                int dstRow = headerSize + y * bmpStride;
                Buffer.BlockCopy(grayPixels, srcRow, bmpBuffer, dstRow, width);
            }
        }

        internal static IReadOnlyList<PlateOcrConsensusCandidate> AggregateConsensus(
            IReadOnlyList<(string Text, float Confidence, int Frame)> readings,
            int frameCount)
        {
            if (readings.Count == 0 || frameCount <= 0)
                return Array.Empty<PlateOcrConsensusCandidate>();

            return readings
                .GroupBy(x => x.Text, StringComparer.Ordinal)
                .Select(g => new PlateOcrConsensusCandidate(
                    g.Key,
                    g.Select(x => x.Frame).Distinct().Count(),
                    frameCount,
                    (float)g.Average(x => x.Confidence)))
                // Najpierw jakość odczytu, dopiero potem powtarzalność między klatkami.
                .OrderByDescending(x => x.AverageConfidence)
                .ThenByDescending(x => x.VoteCount)
                .ThenByDescending(x => x.Text.Length)
                .ThenBy(x => x.Text, StringComparer.Ordinal)
                .Take(5)
                .ToArray();
        }

        internal static IReadOnlyList<PlateOcrConsensusCandidate> RecognizeConsensus(IReadOnlyList<PlatePixels> crops, CancellationToken ct)
        {
            if (crops == null || crops.Count == 0) return Array.Empty<PlateOcrConsensusCandidate>();
            using var session = new PlateOcrSession();
            return session.RecognizeConsensus(crops, ct);
        }

        internal static IReadOnlyList<PlateOcrConsensusCandidate> RecognizeConsensus(IReadOnlyList<byte[]> crops, CancellationToken ct)
        {
            if (crops == null || crops.Count == 0) return Array.Empty<PlateOcrConsensusCandidate>();
            var decoded = new List<PlatePixels>(crops.Count);
            foreach (byte[] bytes in crops)
            {
                ct.ThrowIfCancellationRequested();
                decoded.Add(DecodeEncodedImageToPlatePixels(bytes));
            }
            using var session = new PlateOcrSession();
            return session.RecognizeConsensus(decoded, ct);
        }

        internal static IReadOnlyList<PlateOcrCandidate> Recognize(byte[] crop, CancellationToken ct)
        {
            using var session = new PlateOcrSession();
            return session.Recognize(DecodeEncodedImageToPlatePixels(crop), ct);
        }

        internal static PlatePixels DecodeEncodedImageToPlatePixels(byte[] encodedBytes)
        {
            using var input = new MemoryStream(encodedBytes);
            using var source = new Bitmap(input);
            using var argb = source.PixelFormat == PixelFormat.Format32bppArgb
                ? null
                : new Bitmap(source.Width, source.Height, PixelFormat.Format32bppArgb);

            Bitmap target = argb ?? source;
            if (argb != null)
            {
                using var g = Graphics.FromImage(argb);
                g.DrawImage(source, 0, 0, source.Width, source.Height);
            }

            var rect = new Rectangle(0, 0, target.Width, target.Height);
            BitmapData bits = target.LockBits(rect, ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
            try
            {
                int rowBytes = target.Width * 4;
                byte[] pixels = new byte[checked(rowBytes * target.Height)];
                if (bits.Stride == rowBytes)
                {
                    Marshal.Copy(bits.Scan0, pixels, 0, pixels.Length);
                }
                else
                {
                    for (int y = 0; y < target.Height; y++)
                    {
                        Marshal.Copy(IntPtr.Add(bits.Scan0, y * bits.Stride), pixels, y * rowBytes, rowBytes);
                    }
                }
                return new PlatePixels(target.Width, target.Height, pixels);
            }
            finally
            {
                target.UnlockBits(bits);
            }
        }
    }
}