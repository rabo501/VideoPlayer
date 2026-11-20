using System;
using Xunit;

namespace VideoPlayer.Tests
{
    public sealed class DahuaSessionServiceTests
    {
        [Theory]
        [InlineData("192.168.1.108", "37777", true, 37777)]
        [InlineData("nvr.local", "8000", true, 8000)]
        [InlineData("", "37777", false, 0)]
        [InlineData("192.168.1.108", "0", false, 0)]
        [InlineData("192.168.1.108", "70000", false, 0)]
        [InlineData("bad host name!", "37777", false, 0)]
        public void IsValidEndpoint_ValidatesHostAndPort(string host, string portText, bool expectedValid, int expectedPort)
        {
            bool actualValid = DahuaSessionService.IsValidEndpoint(host, portText, out ushort actualPort, out string error);

            Assert.Equal(expectedValid, actualValid);
            Assert.Equal((ushort)expectedPort, actualPort);
            if (!expectedValid)
                Assert.False(string.IsNullOrWhiteSpace(error));
        }

        [Fact]
        public void ValidateArchiveRange_RejectsInvertedOrMissingBounds()
        {
            var start = new DateTime(2026, 5, 1, 10, 0, 0);
            var end = new DateTime(2026, 5, 1, 11, 0, 0);

            Assert.True(DahuaSessionService.ValidateArchiveRange(start, end, out string okError));
            Assert.Equal(string.Empty, okError);

            Assert.False(DahuaSessionService.ValidateArchiveRange(end, start, out string badError));
            Assert.False(string.IsNullOrWhiteSpace(badError));

            Assert.False(DahuaSessionService.ValidateArchiveRange(null, end, out _));
        }

        [Theory]
        [InlineData(1, 1, 1)]
        [InlineData(4, 2, 2)]
        [InlineData(9, 3, 3)]
        [InlineData(16, 4, 4)]
        public void GetGridDimensions_MapsSupportedLayouts(int capacity, int expectedColumns, int expectedRows)
        {
            var (cols, rows) = DahuaSessionService.GetGridDimensions(capacity);
            Assert.Equal(expectedColumns, cols);
            Assert.Equal(expectedRows, rows);
        }

        [Fact]
        public void ComputeAreaZoomPulses_ComputesPanTiltAndZoomForOffCenterRectangle()
        {
            var plan = DahuaSessionService.ComputeAreaZoomPulses(
                viewWidth: 1000,
                viewHeight: 600,
                areaLeft: 700,
                areaTop: 50,
                areaWidth: 200,
                areaHeight: 100);

            Assert.Equal(DahuaNetSdk.EM_PTZ_COMMAND.RIGHT, plan.PanCommand);
            Assert.True(plan.PanDurationMs > 0);
            Assert.Equal(DahuaNetSdk.EM_PTZ_COMMAND.UP, plan.TiltCommand);
            Assert.True(plan.TiltDurationMs > 0);
            Assert.True(plan.ZoomDurationMs > 0);
        }

        [Fact]
        public void ComputeArchiveSeekOffsetSeconds_ClampsToBounds()
        {
            var start = new DateTime(2026, 5, 1, 8, 0, 0);
            var end = new DateTime(2026, 5, 1, 9, 0, 0);

            Assert.Equal(0, DahuaSessionService.ComputeArchiveSeekOffsetSeconds(start.AddMinutes(-5), start, end));
            Assert.Equal(1800, DahuaSessionService.ComputeArchiveSeekOffsetSeconds(start.AddMinutes(30), start, end));
            Assert.Equal(3600, DahuaSessionService.ComputeArchiveSeekOffsetSeconds(end.AddMinutes(10), start, end));
        }

        [Theory]
        [InlineData("UP_LEFT", DahuaNetSdk.EM_PTZ_COMMAND.LEFTTOP)]
        [InlineData("UP_RIGHT", DahuaNetSdk.EM_PTZ_COMMAND.RIGHTTOP)]
        [InlineData("DOWN_LEFT", DahuaNetSdk.EM_PTZ_COMMAND.LEFTDOWN)]
        [InlineData("DOWN_RIGHT", DahuaNetSdk.EM_PTZ_COMMAND.RIGHTDOWN)]
        [InlineData("INVALID", null)]
        public void ParseDiagonalDirection_ParsesExpectedPtzCommand(string tag, DahuaNetSdk.EM_PTZ_COMMAND? expected)
        {
            Assert.Equal(expected, DahuaSessionService.ParseDiagonalDirection(tag));
        }
    }
}
