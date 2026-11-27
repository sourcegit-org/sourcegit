using System;

using Avalonia.Interactivity;

namespace SourceGit.Views
{
    public partial class EditReviewCredential : ChromelessWindow
    {
        public EditReviewCredential() : this(null, false) { }

        public EditReviewCredential(string server, bool isGitHub)
        {
            CloseOnESC = true;
            InitializeComponent();
            Server.Text = server ?? string.Empty;
            Platform.SelectedIndex = isGitHub ? 1 : 0;
        }

        private void OnSave(object sender, RoutedEventArgs e)
        {
            var server = Models.ReviewCredentials.ParseServer(Server.Text);
            var token = Token.Text?.Trim();
            if (server == null || string.IsNullOrWhiteSpace(token))
            {
                Error.Text = App.Text("PublishReview.InvalidCredential");
                return;
            }
            if (!Models.ReviewCredentials.IsAvailable)
            {
                Error.Text = App.Text("EditApiKey.InvalidStorage");
                return;
            }

            try
            {
                var preferences = ViewModels.Preferences.Instance;
                var address = server.GetLeftPart(UriPartial.Authority);
                var connection = new Models.ReviewConnection(address, Platform.SelectedIndex == 1);
                foreach (var existing in preferences.ReviewConnections)
                {
                    if (existing.Server == address && existing.IsGitHub != connection.IsGitHub)
                    {
                        Error.Text = App.Text("PublishReview.DuplicateInstance");
                        return;
                    }
                }
                Models.ReviewCredentials.Store(connection, token);
                if (!preferences.ReviewConnections.Contains(connection))
                    preferences.ReviewConnections.Add(connection);
                preferences.Save();
                Token.Clear();
                Close(true);
            }
            catch (Exception)
            {
                Error.Text = App.Text("PublishReview.StoreFailed");
            }
        }

        private void OnCancel(object sender, RoutedEventArgs e) => Close(false);
    }
}
