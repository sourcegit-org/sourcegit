using Avalonia.Interactivity;

namespace SourceGit.Views
{
    public partial class ExternalCliSettings : ChromelessWindow
    {
        public ExternalCliSettings()
        {
            DataContext = ViewModels.Preferences.Instance;
            CloseOnESC = true;
            InitializeComponent();
            Profiles.SelectedIndex = 0;
        }

        private void OnAddProfile(object sender, RoutedEventArgs e)
        {
            var profile = new AI.ExternalCliProfile() { Name = App.Text("ExternalCLI.NewProfile") };
            ViewModels.Preferences.Instance.ExternalCliProfiles.Add(profile);
            Profiles.SelectedItem = profile;
            e.Handled = true;
        }

        private void OnRemoveProfile(object sender, RoutedEventArgs e)
        {
            if (Profiles.SelectedItem is AI.ExternalCliProfile profile)
            {
                var index = Profiles.SelectedIndex;
                var profiles = ViewModels.Preferences.Instance.ExternalCliProfiles;
                profiles.Remove(profile);
                Profiles.SelectedIndex = System.Math.Min(index, profiles.Count - 1);
            }
            e.Handled = true;
        }

        private void OnResetCommitMessageRule(object sender, RoutedEventArgs e)
        {
            ViewModels.Preferences.Instance.CommitMessageRule = ViewModels.Preferences.DefaultCommitMessageRule;
            e.Handled = true;
        }

        private void OnClose(object sender, RoutedEventArgs e) => Close();
    }
}
