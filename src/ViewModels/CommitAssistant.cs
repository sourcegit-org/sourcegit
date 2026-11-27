using System;
using System.Collections.Generic;
using System.Linq;

using CommunityToolkit.Mvvm.ComponentModel;

namespace SourceGit.ViewModels
{
    public class CommitAssistant : ObservableObject
    {
        public List<AI.Service> ApiServices { get; }
        public AI.ExternalCliProfile[] CliServices { get; }
        public bool HasApiServices => ApiServices.Count > 0;
        public bool HasCliServices => CliServices.Length > 0;
        public bool HasApiChoice => HasApiServices && (HasCliServices || ApiServices.Count > 1);
        public bool HasApiSelection => _selectedApiService != null;

        public AIAssistant Api
        {
            get => _api;
            private set => SetProperty(ref _api, value);
        }

        public CliAssistant Cli
        {
            get => _cli;
            private set => SetProperty(ref _cli, value);
        }

        public int SelectedTab
        {
            get => _selectedTab;
            set
            {
                if (SetProperty(ref _selectedTab, value))
                    StartApiIfNeeded();
            }
        }

        public AI.Service SelectedApiService
        {
            get => _selectedApiService;
            set
            {
                if (!SetProperty(ref _selectedApiService, value))
                    return;

                Api?.Cancel();
                Api = null;
                OnPropertyChanged(nameof(HasApiSelection));
                StartApiIfNeeded();
            }
        }

        public AI.ExternalCliProfile SelectedCliService
        {
            get => _selectedCliService;
            set
            {
                if (!SetProperty(ref _selectedCliService, value))
                    return;

                Cli?.Cancel();
                Cli = value == null ? null : new CliAssistant(_repo, value, _changes, _allStaged);
                OnPropertyChanged(nameof(HasCliSelection));
            }
        }

        public bool HasCliSelection => _selectedCliService != null;

        internal static AI.ExternalCliProfile[] AvailableCliProfiles() =>
            AI.ExternalCliProfile.Available().Where(static profile => profile.CanGenerateCommit).ToArray();

        public CommitAssistant(Repository repo, List<Models.Change> changes, AI.Service selected = null, bool allStaged = false)
        {
            _repo = repo;
            _changes = [.. changes];
            _allStaged = allStaged;
            ApiServices = repo.GetPreferredOpenAIServices();
            CliServices = AvailableCliProfiles();
            _selectedApiService = selected ?? (ApiServices.Count == 1 ? ApiServices[0] : null);
            SelectedCliService = Array.Find(CliServices, profile => profile.Id == repo.Settings.PreferredExternalCli) ?? CliServices.FirstOrDefault();
            _selectedTab = !HasApiServices && HasCliServices ? 1 : 0;
        }

        public void Open()
        {
            _opened = true;
            StartApiIfNeeded();
        }

        public void Close()
        {
            _opened = false;
            Api?.Cancel();
            Cli?.Cancel();
        }

        private void StartApiIfNeeded()
        {
            if (!_opened || _selectedTab != 0 || !HasApiSelection || Api != null)
                return;

            Api = new AIAssistant(_repo, _selectedApiService, _changes);
            _ = Api.GenAsync();
        }

        private readonly Repository _repo;
        private readonly List<Models.Change> _changes;
        private readonly bool _allStaged;
        private bool _opened;
        private int _selectedTab;
        private AI.Service _selectedApiService;
        private AI.ExternalCliProfile _selectedCliService;
        private AIAssistant _api;
        private CliAssistant _cli;
    }
}
