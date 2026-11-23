using System;
using System.Collections.Generic;
using VideoPlayer.ViewModels;
using Xunit;

namespace VideoPlayer.Tests
{
    public sealed class MotionDetectionServiceTests
    {
        [Fact]
        public void AnalyzeFrameDifference_IgnoresIsolatedSinglePixelNoise()
        {
            const int w = 64;
            const int h = 32;
            byte[] prev = new byte[w * h];
            byte[] curr = new byte[w * h];

            // Pojedynczy odizolowany piksel z dużą zmianą powinien zostać odfiltrowany (szum/artefakt).
            curr[10 * w + 20] = 200;

            MotionFrameDiffResult diff = MotionDetectionService.AnalyzeFrameDifference(
                prev, curr, threshold: 25, left: 0, top: 0, right: w, bottom: h, frameWidth: w, frameHeight: h);

            Assert.Equal(0, diff.ChangedPixels);
        }

        [Fact]
        public void AnalyzeFrameDifference_DetectsConnectedClusterAndBoundingBox()
        {
            const int w = 64;
            const int h = 32;
            byte[] prev = new byte[w * h];
            byte[] curr = new byte[w * h];

            // Zwarte skupisko 2x2 pikseli (każdy ma 3 sąsiadów spełniających próg).
            curr[12 * w + 30] = 100;
            curr[12 * w + 31] = 100;
            curr[13 * w + 30] = 100;
            curr[13 * w + 31] = 100;

            MotionFrameDiffResult diff = MotionDetectionService.AnalyzeFrameDifference(
                prev, curr, threshold: 25, left: 0, top: 0, right: w, bottom: h, frameWidth: w, frameHeight: h);

            Assert.Equal(4, diff.ChangedPixels);
            Assert.Equal(30, diff.MinX);
            Assert.Equal(31, diff.MaxX);
            Assert.Equal(12, diff.MinY);
            Assert.Equal(13, diff.MaxY);
        }

        [Fact]
        public void AnalyzeFrameDifference_RespectsRegionOfInterestBounds()
        {
            const int w = 64;
            const int h = 32;
            byte[] prev = new byte[w * h];
            byte[] curr = new byte[w * h];

            // Ruch poza wybranym ROI (x = 5..6, ROI zaczyna się od x = 20) nie może wpływać na wynik.
            curr[10 * w + 5] = 120;
            curr[10 * w + 6] = 120;
            curr[11 * w + 5] = 120;
            curr[11 * w + 6] = 120;

            MotionFrameDiffResult diff = MotionDetectionService.AnalyzeFrameDifference(
                prev, curr, threshold: 25, left: 20, top: 0, right: w, bottom: h, frameWidth: w, frameHeight: h);

            Assert.Equal(0, diff.ChangedPixels);
        }

        [Fact]
        public void ExtendEvent_UnionsBoundingBoxesAndPeakRatio()
        {
            var ev = new MotionDetectionService.MotionEvent
            {
                StartSeconds = 10.0,
                EndSeconds = 11.0,
                PeakChangedRatio = 3.5,
                MotionX = 0.2,
                MotionY = 0.2,
                MotionWidth = 0.1,
                MotionHeight = 0.1
            };

            MotionDetectionService.ExtendEvent(
                ev,
                seconds: 12.0,
                ratio: 7.2,
                minX: 16,
                minY: 18,
                maxX: 159,
                maxY: 89,
                durationSeconds: 30.0);

            Assert.Equal(12.5, ev.EndSeconds);
            Assert.Equal(7.2, ev.PeakChangedRatio);
            Assert.InRange(ev.MotionX, 0.049, 0.051); // 16 / 320 = 0.05
            Assert.InRange(ev.MotionY, 0.099, 0.101); // 18 / 180 = 0.10
            Assert.InRange(ev.MotionX + ev.MotionWidth, 0.499, 0.501); // 160 / 320 = 0.50
            Assert.InRange(ev.MotionY + ev.MotionHeight, 0.499, 0.501); // 90 / 180 = 0.50
        }

        [Fact]
        public void MotionDetectionViewModel_CreatesTimelineMarkersFromAnalysisResults()
        {
            var events = new[]
            {
                new MotionDetectionService.MotionEvent
                {
                    StartSeconds = 8.5, EndSeconds = 11.25, PeakChangedRatio = 14.2,
                    MotionX = 0.1, MotionY = 0.2, MotionWidth = 0.3, MotionHeight = 0.4
                }
            };
            List<TimelineMarker> markers = MotionDetectionViewModel.CreateMarkers(events, 10);
            Assert.Single(markers);
            Assert.Equal(8.5, markers[0].Seconds);
            Assert.Equal(10d, markers[0].EndSeconds!.Value);
            Assert.Equal(0.1, markers[0].MotionX);
            Assert.Contains("14.2%", markers[0].Description);
        }
    }
}
