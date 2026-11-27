using System;
using System.Collections.Generic;
using System.Linq;

namespace VideoPlayer
{
    internal sealed record EventCenterSummary(
        IReadOnlyList<TimelineMarker> VisibleMarkers,
        int TotalCount,
        int MotionCount,
        int PlateCount,
        int ManualCount,
        int RelatedPlateCount,
        string SummaryText);

    /// <summary>
    /// Czysty serwis korelacji zdarzeń (tablice rejestracyjne ↔ przedziały ruchu), filtrowania i podsumowań
    /// dla bieżącego nagrania — całkowicie niezależny od kontrolek UI WPF.
    /// </summary>
    internal static class EventCenterService
    {
        public const double MotionCorrelationToleranceSeconds = 3.0;

        public static EventCenterSummary CorrelateAndFilter(
            IReadOnlyList<TimelineMarker> allMarkers,
            string? filterText,
            string? categoryTag)
        {
            ArgumentNullException.ThrowIfNull(allMarkers);

            var motionEvents = allMarkers
                .Where(m => m.EventType == "Ruch" && m.EndSeconds.HasValue)
                .ToList();

            foreach (TimelineMarker marker in allMarkers)
            {
                marker.RelatedMotionCount = marker.EventType == "Tablica"
                    ? motionEvents.Count(m =>
                        marker.Seconds >= m.Seconds - MotionCorrelationToleranceSeconds &&
                        marker.Seconds <= m.EndSeconds!.Value + MotionCorrelationToleranceSeconds)
                    : 0;
            }

            string normalizedFilter = filterText?.Trim() ?? string.Empty;
            string normalizedCategory = string.IsNullOrWhiteSpace(categoryTag) ? "all" : categoryTag.Trim();

            var visible = allMarkers
                .Where(m => normalizedCategory == "all" ||
                    (normalizedCategory == "related" ? m.RelatedMotionCount > 0 : m.EventType == normalizedCategory))
                .Where(m => normalizedFilter.Length == 0 ||
                    m.Description.Contains(normalizedFilter, StringComparison.CurrentCultureIgnoreCase) ||
                    m.VehicleMake.Contains(normalizedFilter, StringComparison.CurrentCultureIgnoreCase) ||
                    m.EventType.Contains(normalizedFilter, StringComparison.CurrentCultureIgnoreCase))
                .OrderBy(m => m.Seconds)
                .ToArray();

            int motionCount = allMarkers.Count(m => m.EventType == "Ruch");
            int plateCount = allMarkers.Count(m => m.EventType == "Tablica");
            int manualCount = allMarkers.Count(m => m.EventType == "Ręczne");
            int relatedCount = allMarkers.Count(m => m.EventType == "Tablica" && m.RelatedMotionCount > 0);

            string summaryText = allMarkers.Count == 0
                ? "Brak zdarzeń dla bieżącego nagrania."
                : $"Widoczne: {visible.Length}/{allMarkers.Count}  •  Ruch: {motionCount}  •  Tablice: {plateCount}  •  Ręczne: {manualCount}  •  Tablice powiązane z ruchem: {relatedCount}";

            return new EventCenterSummary(
                visible,
                allMarkers.Count,
                motionCount,
                plateCount,
                manualCount,
                relatedCount,
                summaryText);
        }

        public static List<TimelineMarker> BuildSortedSnapshot(IEnumerable<TimelineMarker> markers)
        {
            ArgumentNullException.ThrowIfNull(markers);
            return markers
                .OrderBy(m => m.Seconds)
                .Select(m => new TimelineMarker
                {
                    Id = m.Id,
                    Seconds = m.Seconds,
                    Description = m.Description,
                    VehicleMake = m.VehicleMake,
                    EndSeconds = m.EndSeconds,
                    MotionX = m.MotionX,
                    MotionY = m.MotionY,
                    MotionWidth = m.MotionWidth,
                    MotionHeight = m.MotionHeight,
                    RelatedMotionCount = m.RelatedMotionCount
                })
                .ToList();
        }
    }
}
