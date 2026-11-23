using System;
using CommunityToolkit.Mvvm.ComponentModel;

namespace VideoPlayer.ViewModels
{
    /// <summary>
    /// Owns persistence of per-recording playback and review session state.
    /// </summary>
    internal sealed class ReviewSessionViewModel : ObservableObject
    {
        private ReviewRepository? _repository;

        public bool IsReady => _repository != null;

        public void AttachRepository(ReviewRepository repository)
        {
            _repository = repository ?? throw new ArgumentNullException(nameof(repository));
            OnPropertyChanged(nameof(IsReady));
        }

        public ReviewSession? Load(string sourcePath)
        {
            if (_repository == null)
                throw new InvalidOperationException("Repozytorium sesji nie zostało zainicjalizowane.");
            if (string.IsNullOrWhiteSpace(sourcePath))
                throw new ArgumentException("Ścieżka nagrania jest wymagana.", nameof(sourcePath));

            return _repository.Load(sourcePath);
        }

        public void Save(string sourcePath, ReviewSession session)
        {
            if (_repository == null)
                throw new InvalidOperationException("Repozytorium sesji nie zostało zainicjalizowane.");
            ArgumentNullException.ThrowIfNull(session);
            if (string.IsNullOrWhiteSpace(sourcePath))
                throw new ArgumentException("Ścieżka nagrania jest wymagana.", nameof(sourcePath));

            _repository.Save(sourcePath, session);
        }
    }
}