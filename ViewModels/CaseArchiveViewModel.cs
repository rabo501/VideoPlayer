using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace VideoPlayer.ViewModels
{
    internal sealed partial class CaseArchiveViewModel : ObservableObject
    {
        private CaseRepository? _repository;
        private bool _applyingSelection;

        public CaseArchiveViewModel(CaseRepository? repository = null)
        {
            _repository = repository;
        }

        public ObservableCollection<CaseRecord> Cases { get; } = new();

        [ObservableProperty]
        private CaseRecord? _selectedCase;

        [ObservableProperty]
        private string _caseNumber = string.Empty;

        [ObservableProperty]
        private string _caseTitle = string.Empty;

        [ObservableProperty]
        private string _location = string.Empty;

        [ObservableProperty]
        private string _notes = string.Empty;

        [ObservableProperty]
        private bool _includeThumbnails = true;

        [ObservableProperty]
        private string _statusText = "Sprawa może zawierać wiele nagrań. Zapis nie kopiuje ani nie modyfikuje plików źródłowych.";

        public void AttachRepository(CaseRepository repository)
        {
            _repository = repository ?? throw new ArgumentNullException(nameof(repository));
            LoadAll();
        }

        public void LoadAll(Guid? selectId = null)
        {
            if (_repository == null) return;
            List<CaseRecord> loaded = _repository.LoadAll();
            Cases.Clear();
            foreach (CaseRecord item in loaded)
                Cases.Add(item);

            CaseRecord? match = selectId.HasValue
                ? Cases.FirstOrDefault(item => item.Id == selectId.Value)
                : null;
            _applyingSelection = true;
            try
            {
                SelectedCase = match;
            }
            finally
            {
                _applyingSelection = false;
            }
        }

        public ReportMetadata ToReportMetadata()
        {
            var metadata = new ReportMetadata
            {
                CaseNumber = (CaseNumber ?? string.Empty).Trim(),
                CaseTitle = (CaseTitle ?? string.Empty).Trim(),
                Location = (Location ?? string.Empty).Trim(),
                Notes = (Notes ?? string.Empty).Trim()
            };

            if (string.IsNullOrWhiteSpace(metadata.CaseNumber) && SelectedCase is CaseRecord selected)
                return ToReportMetadata(selected);

            return metadata;
        }

        public static ReportMetadata ToReportMetadata(CaseRecord record) => new()
        {
            CaseNumber = record.CaseNumber,
            CaseTitle = record.Title,
            Location = record.Location,
            Notes = record.Notes
        };

        public void ApplyCaseMetadata(CaseRecord record)
        {
            CaseNumber = record.CaseNumber;
            CaseTitle = record.Title;
            Location = record.Location;
            Notes = record.Notes;
        }

        partial void OnSelectedCaseChanged(CaseRecord? value)
        {
            if (_applyingSelection || value == null) return;
            ApplyCaseMetadata(value);
            StatusText = $"Wybrano sprawę {value.CaseNumber}. Powiązane nagrania: {value.SourceCount}. Otwarcie kolejnego pliku i zapis dołączy go do tej sprawy.";
        }

        public (bool Success, CaseRecord? Saved, string Message) SaveCurrentCase(string? sourceFilePath)
        {
            if (_repository == null)
                return (false, null, "Archiwum spraw nie zostało jeszcze zainicjalizowane.");

            if (string.IsNullOrWhiteSpace(sourceFilePath) || !File.Exists(sourceFilePath))
            {
                StatusText = "Najpierw otwórz nagranie, które ma zostać powiązane ze sprawą.";
                return (false, null, StatusText);
            }

            CaseRecord saved = _repository.Save(ToReportMetadata(), sourceFilePath);
            LoadAll(saved.Id);
            ApplyCaseMetadata(saved);
            StatusText = $"Zapisano sprawę {saved.CaseNumber}. Powiązane nagrania: {saved.SourceCount}.";
            return (true, saved, StatusText);
        }

        public (bool Found, CaseRecord? Record, string Message) ReloadSelectedCase()
        {
            if (_repository == null || SelectedCase == null)
            {
                StatusText = "Wybierz sprawę z listy.";
                return (false, null, StatusText);
            }

            CaseRecord? record = _repository.Load(SelectedCase.Id);
            if (record == null)
            {
                LoadAll();
                StatusText = "Wybrana sprawa nie istnieje już w bazie. Odświeżono listę.";
                return (false, null, StatusText);
            }

            ApplyCaseMetadata(record);
            StatusText = $"Wczytano sprawę {record.CaseNumber}. Powiązane nagrania: {record.SourceCount}. Zapisz ponownie, aby dołączyć bieżące nagranie.";
            return (true, record, StatusText);
        }

        public (bool Deleted, string Message) DeleteSelectedCase()
        {
            if (_repository == null || SelectedCase == null)
            {
                StatusText = "Wybierz sprawę, którą chcesz usunąć.";
                return (false, StatusText);
            }

            CaseRecord target = SelectedCase;
            bool deleted = _repository.Delete(target.Id);
            ClearFieldsOnly();
            LoadAll();
            StatusText = deleted
                ? $"Usunięto sprawę {target.CaseNumber}. Nagrania i ich dane pozostały bez zmian."
                : "Sprawa nie istnieje już w archiwum.";
            return (deleted, StatusText);
        }

        [RelayCommand]
        public void ClearSelection()
        {
            _applyingSelection = true;
            try
            {
                SelectedCase = null;
            }
            finally
            {
                _applyingSelection = false;
            }
            ClearFieldsOnly();
            StatusText = "Wybór sprawy wyczyszczono. Raport będzie dotyczył bieżącego nagrania.";
        }

        private void ClearFieldsOnly()
        {
            CaseNumber = string.Empty;
            CaseTitle = string.Empty;
            Location = string.Empty;
            Notes = string.Empty;
        }
    }
}
