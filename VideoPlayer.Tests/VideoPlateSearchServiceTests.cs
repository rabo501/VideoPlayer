using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Threading;
using Xunit;

namespace VideoPlayer.Tests
{
    public sealed class VideoPlateSearchServiceTests
    {
        [Fact]
        public void ComputeEffectiveSampling_RespectsMaximumSamplesCap()
        {
            var (interval, samples) = VideoPlateSearchService.ComputeEffectiveSampling(
                durationSeconds: 7200,
                requestedIntervalSeconds: 1,
                maximumSamples: 1200);

            Assert.Equal(6, interval);
            Assert.Equal(1200, samples);
        }

        [Fact]
        public void ComputeEffectiveSampling_PreservesValidIntervalBelowCap()
        {
            var (interval, samples) = VideoPlateSearchService.ComputeEffectiveSampling(
                durationSeconds: 65,
                requestedIntervalSeconds: 5,
                maximumSamples: 1200);

            Assert.Equal(5, interval);
            Assert.Equal(13, samples);
        }

        [Fact]
        public void ComputeEffectiveSampling_ThrowsOnInvalidDuration()
        {
            Assert.Throws<ArgumentOutOfRangeException>(() =>
                VideoPlateSearchService.ComputeEffectiveSampling(0, 5));
        }

        [Fact]
        public void MergeSearchHit_MergesAdjacentTimestampsAndSplitsGaps()
        {
            var hits = new List<PlateVideoSearchHit>();

            VideoPlateSearchService.MergeSearchHit(hits, 10.0, 0.72f, intervalSeconds: 5);
            VideoPlateSearchService.MergeSearchHit(hits, 15.0, 0.89f, intervalSeconds: 5);
            VideoPlateSearchService.MergeSearchHit(hits, 30.0, 0.81f, intervalSeconds: 5);

            Assert.Equal(2, hits.Count);
            Assert.Equal(10.0, hits[0].StartSeconds);
            Assert.Equal(15.0, hits[0].EndSeconds);
            Assert.Equal(0.89f, hits[0].Confidence);
            Assert.Equal(30.0, hits[1].StartSeconds);
            Assert.Equal(30.0, hits[1].EndSeconds);
        }

        [Fact]
        public void PrepareInput_ProducesScaledPaddedGrayscaleAndValidOtsuThreshold()
        {
            const int width = 40;
            const int height = 20;
            byte[] bgra = new byte[width * height * 4];
            for (int y = 0; y < height; y++)
            {
                for (int x = 0; x < width; x++)
                {
                    int off = (y * width + x) * 4;
                    byte val = x >= 10 && x < 30 && y >= 4 && y < 16 ? (byte)20 : (byte)230;
                    bgra[off] = val;
                    bgra[off + 1] = val;
                    bgra[off + 2] = val;
                    bgra[off + 3] = 255;
                }
            }

            PreparedPlateOcrInput prepared = PlateOcrService.PrepareInput(
                new PlatePixels(width, height, bgra),
                CancellationToken.None);

            // Wysokość 20 px jest skalowana 4x -> 80 px + po 20 px białego marginesu z każdej strony.
            Assert.Equal(160 + 40, prepared.Width);
            Assert.Equal(80 + 40, prepared.Height);
            Assert.Equal(prepared.Width * prepared.Height, prepared.GrayscalePixels.Length);
            Assert.InRange(prepared.OtsuThreshold, 20, 230);
            Assert.Equal(255, prepared.GrayscalePixels[0]);
        }

        [Fact]
        public void AllocateAndWriteGrayscaleBmpBuffer_WritesValidBottomUp8BppBmp()
        {
            const int width = 5;
            const int height = 2;
            int stride = (width + 3) & ~3; // 8 bajtów na wiersz
            const int headerSize = 1078;

            byte[] bmp = PlateOcrService.AllocateGrayscaleBmpBuffer(width, height, stride);
            byte[] gray =
            {
                10, 11, 12, 13, 14,
                20, 21, 22, 23, 24
            };
            PlateOcrService.WriteGrayscalePayloadToBmp(bmp, headerSize, width, height, stride, gray);

            Assert.Equal((byte)'B', bmp[0]);
            Assert.Equal((byte)'M', bmp[1]);
            Assert.Equal(8, BinaryPrimitives.ReadUInt16LittleEndian(bmp.AsSpan(28, 2)));
            Assert.Equal(headerSize + stride * height, bmp.Length);
            // W formacie BMP bottom-up dolny wiersz obrazu (20..24) znajduje się tuż po nagłówku.
            Assert.Equal(20, bmp[headerSize]);
            Assert.Equal(10, bmp[headerSize + stride]);
        }

        [Fact]
        public void AggregateConsensus_PrefersHigherConfidenceThenVoteCount()
        {
            var readings = new List<(string Text, float Confidence, int Frame)>
            {
                ("WA12345", 0.91f, 0),
                ("WA12345", 0.89f, 1),
                ("WA1234S", 0.75f, 2),
                ("WA1234S", 0.76f, 3),
                ("WA1234S", 0.74f, 4)
            };

            var consensus = PlateOcrService.AggregateConsensus(readings, frameCount: 5);

            Assert.Equal(2, consensus.Count);
            Assert.Equal("WA12345", consensus[0].Text);
            Assert.Equal(2, consensus[0].VoteCount);
            Assert.Equal(5, consensus[0].FrameCount);
        }

        [Fact]
        public void Rectify_ExtractsValidRegionFromRawBgraFrame()
        {
            const int width = 100;
            const int height = 60;
            byte[] bgra = new byte[width * height * 4];
            var points = new[]
            {
                new PlatePoint(10, 10),
                new PlatePoint(50, 10),
                new PlatePoint(50, 30),
                new PlatePoint(10, 30)
            };

            PlatePixels rectified = PlatePerspective.Rectify(bgra, width, height, points);

            Assert.True(rectified.Width >= 40);
            Assert.True(rectified.Height >= 20);
            Assert.Equal(rectified.Width * rectified.Height * 4, rectified.Pixels.Length);
        }
    }
}
