using System;
using System.Reactive.Linq;
using System.Windows.Input;
using ReactiveUI;
using Singularity.Models;

namespace Singularity.ViewModels.Library
{
    public class CreateSmartPlaylistViewModel : ReactiveObject
    {
        private string _name = "New Smart Playlist";
        public string Name
        {
            get => _name;
            set => this.RaiseAndSetIfChanged(ref _name, value);
        }

        private double? _minEnergy;
        public double? MinEnergy
        {
            get => _minEnergy;
            set
            {
                this.RaiseAndSetIfChanged(ref _minEnergy, value);
            }
        }

        private double? _maxEnergy;
        public double? MaxEnergy
        {
            get => _maxEnergy;
            set
            {
                this.RaiseAndSetIfChanged(ref _maxEnergy, value);
            }
        }
        
        private double? _minValence;
        public double? MinValence
        {
            get => _minValence;
            set
            {
                this.RaiseAndSetIfChanged(ref _minValence, value);
            }
        }

        private double? _maxValence;
        public double? MaxValence
        {
            get => _maxValence;
            set
            {
                this.RaiseAndSetIfChanged(ref _maxValence, value);
            }
        }
        
        private double? _minBpm;
        public double? MinBpm
        {
            get => _minBpm;
            set => this.RaiseAndSetIfChanged(ref _minBpm, value);
        }

        private double? _maxBpm;
        public double? MaxBpm
        {
            get => _maxBpm;
            set => this.RaiseAndSetIfChanged(ref _maxBpm, value);
        }

        private double? _minDanceability;
        public double? MinDanceability
        {
            get => _minDanceability;
            set => this.RaiseAndSetIfChanged(ref _minDanceability, value);
        }

        private double? _maxDanceability;
        public double? MaxDanceability
        {
            get => _maxDanceability;
            set => this.RaiseAndSetIfChanged(ref _maxDanceability, value);
        }

        private int? _minRating;
        public int? MinRating
        {
            get => _minRating;
            set => this.RaiseAndSetIfChanged(ref _minRating, value);
        }

        private bool _onlyLiked;
        public bool OnlyLiked
        {
            get => _onlyLiked;
            set => this.RaiseAndSetIfChanged(ref _onlyLiked, value);
        }

        private string? _genre;
        public string? Genre
        {
            get => _genre;
            set => this.RaiseAndSetIfChanged(ref _genre, value);
        }

        public ICommand SaveCommand { get; }
        public ICommand CancelCommand { get; }

        public event EventHandler<SmartPlaylistCriteria>? OnSave;
        public event EventHandler? OnCancel;

        public CreateSmartPlaylistViewModel()
        {
            SaveCommand = ReactiveCommand.Create(Save);
            CancelCommand = ReactiveCommand.Create(Cancel);
        }

        private void Save()
        {
            var criteria = new SmartPlaylistCriteria
            {
                MinEnergy = MinEnergy,
                MaxEnergy = MaxEnergy,
                MinValence = MinValence,
                MaxValence = MaxValence,
                MinDanceability = MinDanceability,
                MaxDanceability = MaxDanceability,
                MinBPM = MinBpm,
                MaxBPM = MaxBpm,
                MinRating = MinRating,
                IsLiked = OnlyLiked ? true : null,
                Genre = Genre
            };

            OnSave?.Invoke(this, criteria);
        }

        private void Cancel()
        {
            OnCancel?.Invoke(this, EventArgs.Empty);
        }
    }
}
