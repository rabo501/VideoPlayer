using System;
using System.Collections.Generic;
using VideoPlayer.ViewModels;
using Xunit;

namespace VideoPlayer.Tests
{
    public sealed class EventCenterTests
    {
        [Fact]
        public void CorrelateAndFilter_LinksPlatesWithinMotionToleranceWindow()
        {
            var markers = new List<TimelineMarker>
            {
                new() { Seconds = 20.0, EndSeconds = 25.0, Description = "Ruch (12.0% zaznaczonego obszaru)" },
                new() { Seconds = 18.0, Description = "Tablica rejestracyjna: WA12345", VehicleMake = "Toyota" },
                new() { Seconds = 60.0, Description = "Tablica rejestracyjna: KR99999", VehicleMake = "BMW" },
                new() { Seconds = 5.0, Description = "Otwarcie bramy" }
            };

            EventCenterSummary all = EventCenterService.CorrelateAndFilter(markers, filterText: null, categoryTag: "all");
            Assert.Equal(4, all.TotalCount);
            Assert.Equal(1, all.MotionCount);
            Assert.Equal(2, all.PlateCount);
            Assert.Equal(1, all.ManualCount);
            Assert.Equal(1, all.RelatedPlateCount);
            Assert.Equal(1, markers[1].RelatedMotionCount);
            Assert.Equal(0, markers[2].RelatedMotionCount);

            EventCenterSummary relatedOnly = EventCenterService.CorrelateAndFilter(markers, filterText: null, categoryTag: "related");
            Assert.Single(relatedOnly.VisibleMarkers);
            Assert.Equal("Toyota", relatedOnly.VisibleMarkers[0].VehicleMake);
        }

        [Fact]
        public void EventCenterViewModel_FiltersAndClearsReactively()
        {
            var vm = new EventCenterViewModel();
            var plate = new TimelineMarker { Seconds = 12, Description = "Tablica rejestracyjna: GD77777", VehicleMake = "Audi" };
            var manual = new TimelineMarker { Seconds = 4, Description = "Wejście osoby" };

            vm.SetMarkers(new[] { plate, manual });
            Assert.Equal(2, vm.VisibleMarkers.Count);

            vm.FilterText = "Audi";
            Assert.Single(vm.VisibleMarkers);
            Assert.Same(plate, vm.VisibleMarkers[0]);

            vm.ClearFilterCommand.Execute(null);
            Assert.Equal(string.Empty, vm.FilterText);
            Assert.Equal("all", vm.SelectedCategoryTag);
            Assert.Equal(2, vm.VisibleMarkers.Count);
        }

        [Fact]
        public void EventCenterViewModel_SelectingMarkerPopulatesDescription()
        {
            var vm = new EventCenterViewModel();
            var marker = new TimelineMarker { Seconds = 15, Description = "Dostawa paczki" };
            vm.AddMarker(marker);

            Assert.Same(marker, vm.SelectedMarker);
            Assert.Equal("Dostawa paczki", vm.MarkerDescription);
        }
    }
}
