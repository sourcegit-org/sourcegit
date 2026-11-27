using System;
using System.Threading.Tasks;

using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;

namespace SourceGit.Views
{
    public partial class AIChatPanel : UserControl
    {
        internal event Action HideRequested;

        public AIChatPanel()
        {
            InitializeComponent();
            ChatBox.AddHandler(KeyDownEvent, OnDraftKeyDown, RoutingStrategies.Tunnel);
            ChatScroll.AttachmentClicked += attachment => (DataContext as ViewModels.AIChat)?.TogglePreview(attachment);
        }

        internal void FocusDraft() => Dispatcher.UIThread.Post(() => ChatBox.Focus(), DispatcherPriority.Loaded);

        private void OnHide(object sender, RoutedEventArgs e) => HideRequested?.Invoke();

        private void OnNewChat(object sender, RoutedEventArgs e)
        {
            (DataContext as ViewModels.AIChat)?.NewChat();
            FocusDraft();
        }

        private void OnHistorySelected(object sender, SelectionChangedEventArgs e)
        {
            if (sender is not ListBox { SelectedItem: Models.AIChatThread thread } list || DataContext is not ViewModels.AIChat chat)
                return;

            list.SelectedItem = null;
            HistoryButton.Flyout?.Hide();
            chat.OpenHistory(thread);
            Dispatcher.UIThread.Post(ChatScroll.ScrollToEnd, DispatcherPriority.Loaded);
        }

        private async void OnConfigure(object sender, RoutedEventArgs e)
        {
            if (DataContext is ViewModels.AIChat chat && TopLevel.GetTopLevel(this) is Window window)
            {
                await new ExternalCliSettings().ShowDialog(window);
                chat.RefreshProfiles();
            }
        }

        private void OnPreview(object sender, RoutedEventArgs e)
        {
            if (sender is Control { DataContext: Models.AIChatAttachment item })
                (DataContext as ViewModels.AIChat)?.TogglePreview(item);
        }

        private void OnClosePreview(object sender, RoutedEventArgs e) => (DataContext as ViewModels.AIChat)?.TogglePreview(null);

        private void OnRemove(object sender, RoutedEventArgs e)
        {
            if (sender is Control { DataContext: Models.AIChatAttachment item })
                (DataContext as ViewModels.AIChat)?.Remove(item);
        }

        private async void OnSend(object sender, RoutedEventArgs e) => await SendAsync();

        private async void OnDraftKeyDown(object sender, KeyEventArgs e)
        {
            var sendShortcut = OperatingSystem.IsMacOS() ? KeyModifiers.Meta : KeyModifiers.Control;
            if (e.Key == Key.Enter && (e.KeyModifiers == KeyModifiers.None || e.KeyModifiers == sendShortcut))
            {
                e.Handled = true;
                await SendAsync();
            }
        }

        private async Task SendAsync()
        {
            if (DataContext is ViewModels.AIChat { CanSend: true } chat)
            {
                ChatScroll.ScrollToEnd();
                await chat.SendAsync();
            }
        }

        private void OnScrollChanged(object sender, ScrollChangedEventArgs e)
        {
            if (DataContext is ViewModels.AIChat { IsSending: true } && e.ExtentDelta.Y > 0 &&
                ChatScroll.Extent.Height - e.ExtentDelta.Y - ChatScroll.Viewport.Height - ChatScroll.Offset.Y < 24)
                Dispatcher.UIThread.Post(ChatScroll.ScrollToEnd, DispatcherPriority.Loaded);
        }

        private void OnStop(object sender, RoutedEventArgs e) => (DataContext as ViewModels.AIChat)?.Stop();

        private async void OnCopySelection(object sender, RoutedEventArgs e) => await CopyAsync(ChatScroll.GetSelectedText());

        private async void OnCopyAnswer(object sender, RoutedEventArgs e) =>
            await CopyAsync((DataContext as ViewModels.AIChat)?.GetAnswerMarkdown());

        private async void OnCopyConversation(object sender, RoutedEventArgs e) =>
            await CopyAsync((DataContext as ViewModels.AIChat)?.GetTranscript());

        private async Task CopyAsync(string text)
        {
            if (!string.IsNullOrEmpty(text))
                await this.CopyTextAsync(text);
        }
    }
}
