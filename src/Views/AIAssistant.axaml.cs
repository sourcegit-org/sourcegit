using System;
using System.Collections.Generic;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Interactivity;
using Avalonia.Media;

using AvaloniaEdit;
using AvaloniaEdit.Document;
using AvaloniaEdit.Editing;
using AvaloniaEdit.Rendering;
using AvaloniaEdit.TextMate;

namespace SourceGit.Views
{
    public class AIResponseView : TextEditor
    {
        public class LineStyleTransformer : DocumentColorizingTransformer
        {
            protected override void ColorizeLine(DocumentLine line)
            {
                var content = CurrentContext.Document.GetText(line);
                if (content.StartsWith("Read changes in file: ", StringComparison.Ordinal))
                {
                    ChangeLinePart(line.Offset + 22, line.EndOffset, v =>
                    {
                        v.TextRunProperties.SetForegroundBrush(Brushes.DeepSkyBlue);
                        v.TextRunProperties.SetTextDecorations(TextDecorations.Underline);
                    });
                }
            }
        }

        public static readonly DirectProperty<AIResponseView, string> ContentProperty =
            AvaloniaProperty.RegisterDirect<AIResponseView, string>(
                nameof(Content),
                static o => o.Content,
                static (o, v) => o.Content = v);

        public string Content
        {
            get => _content;
            set => SetAndRaise(ContentProperty, ref _content, value);
        }

        protected override Type StyleKeyOverride => typeof(TextEditor);

        public AIResponseView() : base(new TextArea(), new TextDocument())
        {
            IsReadOnly = true;
            ShowLineNumbers = false;
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto;
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto;

            TextArea.TextView.LineTransformers.Add(new LineStyleTransformer());
            TextArea.TextView.Margin = new Thickness(4, 0);
            TextArea.TextView.Options.EnableHyperlinks = false;
            TextArea.TextView.Options.EnableEmailHyperlinks = false;
        }

        protected override void OnLoaded(RoutedEventArgs e)
        {
            base.OnLoaded(e);

            TextArea.TextView.ContextRequested += OnTextViewContextRequested;

            if (_textMate == null)
            {
                _textMate = Models.TextMateHelper.CreateForEditor(this);
                Models.TextMateHelper.SetGrammarByFileName(_textMate, "README.md");
            }
        }

        protected override void OnUnloaded(RoutedEventArgs e)
        {
            base.OnUnloaded(e);

            TextArea.TextView.ContextRequested -= OnTextViewContextRequested;

            if (_textMate != null)
            {
                _textMate.Dispose();
                _textMate = null;
            }

            GC.Collect();
        }

        protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
        {
            base.OnPropertyChanged(change);

            if (change.Property == ContentProperty)
                Text = _content;
            else if (change.Property.Name == nameof(ActualThemeVariant) && change.NewValue != null)
                Models.TextMateHelper.SetThemeByApp(_textMate);
        }

        private void OnTextViewContextRequested(object sender, ContextRequestedEventArgs e)
        {
            if (DataContext is not ViewModels.IAssistant vm)
                return;

            var selected = SelectedText;
            if (string.IsNullOrEmpty(selected))
                return;

            var apply = new MenuItem() { Header = App.Text("AIAssistant.Use") };
            apply.Icon = this.CreateMenuIcon("Icons.Check");
            apply.Click += (_, ev) =>
            {
                vm.Use(selected);
                ev.Handled = true;
            };

            var copy = new MenuItem() { Header = App.Text("Copy") };
            copy.Icon = this.CreateMenuIcon("Icons.Copy");
            copy.Click += async (_, ev) =>
            {
                await this.CopyTextAsync(selected);
                ev.Handled = true;
            };

            var menu = new ContextMenu();
            menu.Items.Add(apply);
            menu.Items.Add(copy);
            menu.Open(TextArea.TextView);

            e.Handled = true;
        }

        private TextMate.Installation _textMate = null;
        private string _content = string.Empty;
    }

    public partial class AIAssistant : ChromelessWindow
    {
        public AIAssistant()
        {
            CloseOnESC = true;
            InitializeComponent();
        }

        public static void Open(Control control, ViewModels.Repository repo, List<Models.Change> changes, AI.Service selected = null, bool allStaged = false)
        {
            if (TopLevel.GetTopLevel(control) is not Window owner)
                return;

            var view = new AIAssistant() { DataContext = new ViewModels.CommitAssistant(repo, changes, selected, allStaged) };
            view.Show(owner);
            view.Activate();
        }

        protected override void OnOpened(EventArgs e)
        {
            base.OnOpened(e);
            (DataContext as ViewModels.CommitAssistant)?.Open();
        }

        protected override void OnClosing(WindowClosingEventArgs e)
        {
            base.OnClosing(e);
            if (DataContext is ViewModels.CommitAssistant { Cli.IsCommitting: true })
                e.Cancel = true;
            if (!e.Cancel)
                (DataContext as ViewModels.CommitAssistant)?.Close();
        }

        private void OnUseClicked(object sender, RoutedEventArgs e)
        {
            if (sender is Control { DataContext: ViewModels.AIAssistant { Response.Length: > 0 } api })
                api.Use(api.Response);
            else if (sender is Control { DataContext: ViewModels.CliAssistant { Response.Length: > 0 } cli })
                cli.Use(cli.Response);
            else
                return;

            Close();
            e.Handled = true;
        }

        private async void OnRegenClicked(object sender, RoutedEventArgs e)
        {
            if (sender is Control { DataContext: ViewModels.AIAssistant vm })
                await vm.GenAsync();

            e.Handled = true;
        }

        private async void OnGenerateClicked(object sender, RoutedEventArgs e)
        {
            if (sender is Control { DataContext: ViewModels.CliAssistant vm })
                await vm.GenAsync();

            e.Handled = true;
        }

        private void OnStopCliClicked(object sender, RoutedEventArgs e)
        {
            (DataContext as ViewModels.CommitAssistant)?.Cli?.Cancel();
            e.Handled = true;
        }

        private async void OnCreateBranchClicked(object sender, RoutedEventArgs e)
        {
            e.Handled = true;
            if (sender is not Control { DataContext: ViewModels.CliAssistant { CanCommitOnNewBranch: true } vm })
                return;

            var confirm = new Confirm();
            confirm.SetData(vm.BranchConfirmation, Models.ConfirmButtonType.OkCancel);
            if (await confirm.ShowDialog<bool>(this) && await vm.CommitOnNewBranchAsync())
                Close();
        }
    }
}
