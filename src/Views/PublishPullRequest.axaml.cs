using System;

using Avalonia.Controls;
using Avalonia.Interactivity;

namespace SourceGit.Views
{
    public partial class PublishPullRequest : ChromelessWindow
    {
        public PublishPullRequest()
        {
            CloseOnESC = true;
            InitializeComponent();
        }

        internal static void AddMenu(Control anchor, ItemCollection items, ViewModels.Repository repository, Models.Branch source)
        {
            if (source is not { IsLocal: true, IsDetachedHead: false } || repository.Remotes.Count == 0 ||
                TopLevel.GetTopLevel(anchor) is not Window owner)
                return;

            var publisher = ViewModels.PublishPullRequest.ConfiguredPublisher(repository);
            if (publisher == null && !repository.Remotes.Exists(remote => Models.ReviewCredentials.LooksLikeSupportedRemote(remote.URL) ||
                Models.ReviewCredentials.Find(remote.URL, ViewModels.Preferences.Instance.ReviewConnections) != null))
                return;

            var item = new MenuItem() { Header = App.Text("PublishReview.CreateMenu"), Icon = anchor.CreateMenuIcon("Icons.CreatePR") };
            item.Click += (_, e) =>
            {
                if (repository.CanCreatePopup())
                {
                    var window = new PublishPullRequest() { DataContext = new ViewModels.PublishPullRequest(repository, source, publisher) };
                    window.Show(owner);
                    window.Activate();
                }
                e.Handled = true;
            };
            items.Add(anchor.CreateAIMenu(item));
        }

        protected override async void OnOpened(EventArgs e)
        {
            base.OnOpened(e);
            if (DataContext is ViewModels.PublishPullRequest model)
                await model.OpenAsync();
        }

        protected override void OnClosing(WindowClosingEventArgs e)
        {
            base.OnClosing(e);
            if (DataContext is not ViewModels.PublishPullRequest model || e.Cancel)
                return;
            if (model.IsPublishing)
                e.Cancel = true;
            else
                model.Stop();
        }

        private async void OnRefresh(object sender, RoutedEventArgs e)
        {
            if (DataContext is ViewModels.PublishPullRequest model)
                await model.ReloadAsync();
        }

        private async void OnGenerate(object sender, RoutedEventArgs e)
        {
            if (DataContext is ViewModels.PublishPullRequest model)
                await model.GenerateDraftAsync();
        }

        private async void OnPublish(object sender, RoutedEventArgs e)
        {
            if (DataContext is ViewModels.PublishPullRequest model)
                await model.PublishAsync();
        }

        private void OnStop(object sender, RoutedEventArgs e) => (DataContext as ViewModels.PublishPullRequest)?.Stop();

        private async void OnConfigureCredentials(object sender, RoutedEventArgs e)
        {
            if (DataContext is ViewModels.PublishPullRequest model &&
                await new EditReviewCredential(model.SuggestedServer, model.SuggestedGitHub).ShowDialog<bool>(this))
                await model.ReloadAsync();
        }
    }
}
