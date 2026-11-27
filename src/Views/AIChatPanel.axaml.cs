using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using Avalonia;
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
            DataContextChanged += (_, _) => CloseSkillMenu();
            ChatScroll.AttachmentClicked += attachment => (DataContext as ViewModels.AIChat)?.TogglePreview(attachment);
        }

        internal void FocusDraft() => Dispatcher.UIThread.Post(() => ChatBox.Focus(), DispatcherPriority.Loaded);

        protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
        {
            CloseSkillMenu();
            base.OnDetachedFromVisualTree(e);
        }

        private void OnHide(object sender, RoutedEventArgs e)
        {
            CloseSkillMenu();
            HideRequested?.Invoke();
        }

        private void OnNewChat(object sender, RoutedEventArgs e)
        {
            CloseSkillMenu();
            (DataContext as ViewModels.AIChat)?.NewChat();
            FocusDraft();
        }

        private void OnHistorySelected(object sender, SelectionChangedEventArgs e)
        {
            if (sender is not ListBox { SelectedItem: Models.AIChatThread thread } list || DataContext is not ViewModels.AIChat chat)
                return;

            list.SelectedItem = null;
            HistoryButton.Flyout?.Hide();
            CloseSkillMenu();
            chat.OpenHistory(thread);
            Dispatcher.UIThread.Post(ChatScroll.ScrollToEnd, DispatcherPriority.Loaded);
        }

        private async void OnConfigure(object sender, RoutedEventArgs e)
        {
            if (DataContext is ViewModels.AIChat chat && TopLevel.GetTopLevel(this) is Window window)
            {
                CloseSkillMenu();
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

        private void OnSkillProfileChanged(object sender, SelectionChangedEventArgs e) => CloseSkillMenu();

        private async void OnSkillQueryChanged(object sender, TextChangedEventArgs e)
        {
            if (SkillMenu == null || DataContext is not ViewModels.AIChat { CanChooseSkill: true } chat ||
                ChatBox.Text is not { } text || !text.StartsWith('/') || text.Any(char.IsWhiteSpace))
            {
                CloseSkillMenu();
                return;
            }

            if (SkillMenu.IsVisible)
            {
                FilterSkills(text[1..]);
                return;
            }

            using var request = new CancellationTokenSource();
            _skillRequest = request;
            SkillMenu.IsVisible = true;
            FilterSkills(text[1..]);
            try
            {
                var result = await chat.ListSkillsAsync(request.Token);
                if (!request.IsCancellationRequested)
                    _skills = result;
            }
            catch (OperationCanceledException) when (request.IsCancellationRequested)
            {
                return;
            }
            catch (Exception error)
            {
                if (!request.IsCancellationRequested)
                    _skillError = App.Text("AIChat.SkillsError", error.Message);
            }
            finally
            {
                if (_skillRequest == request)
                    _skillRequest = null;
            }

            if (!request.IsCancellationRequested)
                FilterSkills(ChatBox.Text is { Length: > 0 } query ? query[1..] : string.Empty);
        }

        private void FilterSkills(string filter)
        {
            var matches = _skills?.Where(skill => skill.Id.Contains(filter, StringComparison.OrdinalIgnoreCase) ||
                skill.Label.Contains(filter, StringComparison.CurrentCultureIgnoreCase) ||
                skill.Description?.Contains(filter, StringComparison.CurrentCultureIgnoreCase) == true).ToArray() ?? [];
            SkillChoices.ItemsSource = matches;
            SkillChoices.SelectedIndex = matches.Length == 0 ? -1 : 0;
            SkillChoices.IsVisible = matches.Length > 0;
            SkillMenuStatus.IsVisible = matches.Length == 0;
            SkillMenuStatus.Text = _skillError ?? App.Text(_skills == null ? "AIChat.SkillsLoading" : "AIChat.SkillsEmpty");
        }

        private void CloseSkillMenu()
        {
            _skillRequest?.Cancel();
            _skillRequest = null;
            _skills = null;
            _skillError = null;
            if (SkillMenu != null)
                SkillMenu.IsVisible = false;
        }

        private void OnSkillClicked(object sender, RoutedEventArgs e)
        {
            if (sender is Button { DataContext: AI.ExternalCliSkill skill })
                SelectSkill(skill);
        }

        private void SelectSkill(AI.ExternalCliSkill skill)
        {
            if (DataContext is ViewModels.AIChat { CanChooseSkill: true } chat)
            {
                CloseSkillMenu();
                chat.SetSkill(skill);
                chat.Draft = string.Empty;
                FocusDraft();
            }
        }

        private void OnRemoveSkill(object sender, RoutedEventArgs e) => (DataContext as ViewModels.AIChat)?.SetSkill(null);

        private async void OnSend(object sender, RoutedEventArgs e)
        {
            if (SkillMenu.IsVisible)
            {
                if (SkillChoices.SelectedItem is AI.ExternalCliSkill skill)
                    SelectSkill(skill);
            }
            else
            {
                await SendAsync();
            }
        }

        private async void OnDraftKeyDown(object sender, KeyEventArgs e)
        {
            var sendShortcut = OperatingSystem.IsMacOS() ? KeyModifiers.Meta : KeyModifiers.Control;
            if (SkillMenu.IsVisible && (e.KeyModifiers == KeyModifiers.None || e.Key == Key.Enter && e.KeyModifiers == sendShortcut))
            {
                switch (e.Key)
                {
                    case Key.Escape:
                        CloseSkillMenu();
                        e.Handled = true;
                        return;
                    case Key.Enter:
                        if (SkillChoices.SelectedItem is AI.ExternalCliSkill skill)
                            SelectSkill(skill);
                        e.Handled = true;
                        return;
                    case Key.Up or Key.Down when SkillChoices.ItemCount > 0:
                        SkillChoices.SelectedIndex = Math.Clamp(SkillChoices.SelectedIndex + (e.Key == Key.Down ? 1 : -1), 0, SkillChoices.ItemCount - 1);
                        SkillChoices.ScrollIntoView(SkillChoices.SelectedIndex);
                        e.Handled = true;
                        return;
                }
            }

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

        private CancellationTokenSource _skillRequest;
        private AI.ExternalCliSkill[] _skills;
        private string _skillError;

        private async Task CopyAsync(string text)
        {
            if (!string.IsNullOrEmpty(text))
                await this.CopyTextAsync(text);
        }
    }
}
