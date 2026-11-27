using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;

using AvaloniaEdit;
using AvaloniaEdit.Editing;

namespace SourceGit.Views
{
    public partial class AnalysisConversation : ScrollViewer
    {
        public static readonly DirectProperty<AnalysisConversation, bool> HasSelectionProperty =
            AvaloniaProperty.RegisterDirect<AnalysisConversation, bool>(nameof(HasSelection), view => view.HasSelection);

        public bool HasSelection => _hasSelection;

        internal event Action<Models.AIChatAttachment> AttachmentClicked;

        protected override Type StyleKeyOverride => typeof(ScrollViewer);

        public AnalysisConversation()
        {
            InitializeComponent();
            Focusable = true;
            AddHandler(PointerPressedEvent, OnSelectionPressed, RoutingStrategies.Tunnel);
            AddHandler(KeyDownEvent, OnSelectionKeyDown, RoutingStrategies.Tunnel);
            ContextRequested += OnSelectionContextRequested;
            _dragScroll.Tick += OnDragScroll;
        }

        internal string GetSelectedText() => string.Join(Environment.NewLine,
            _sections.Where(section => section.IsAvailable && section.HasSelection).Select(section => section.GetSelectedText()));

        private void OnAttachmentClicked(object sender, RoutedEventArgs e)
        {
            if (sender is Control { DataContext: Models.AIChatAttachment attachment })
                AttachmentClicked?.Invoke(attachment);
        }

        private async void OnCopyReply(object sender, RoutedEventArgs e)
        {
            if (sender is Button { DataContext: ViewModels.DiffAnalysisEntry { AnswerMarkdown.Length: > 0 } reply })
                await this.CopyTextAsync(reply.AnswerMarkdown);
        }

        protected override void OnPointerMoved(PointerEventArgs e)
        {
            base.OnPointerMoved(e);
            if (e.Pointer != _pointer)
                return;

            _dragPoint = e.GetPosition(this);
            UpdateSelection();
            if (GetScrollDelta() != 0)
                _dragScroll.Start();
            else
                _dragScroll.Stop();
            e.Handled = true;
        }

        protected override void OnPointerReleased(PointerReleasedEventArgs e)
        {
            base.OnPointerReleased(e);
            if (e.Pointer != _pointer || e.InitialPressMouseButton != MouseButton.Left)
                return;

            _dragPoint = e.GetPosition(this);
            UpdateSelection();
            e.Pointer.Capture(null);
            e.Handled = true;
        }

        protected override void OnPointerCaptureLost(PointerCaptureLostEventArgs e)
        {
            base.OnPointerCaptureLost(e);
            if (e.Pointer == _pointer)
            {
                _pointer = null;
                _dragScroll.Stop();
            }
        }

        private void OnSelectionPressed(object sender, PointerPressedEventArgs e)
        {
            if (e.Pointer.Type == PointerType.Touch ||
                e.GetCurrentPoint(this).Properties.PointerUpdateKind != PointerUpdateKind.LeftButtonPressed ||
                e.Source is not Visual source)
                return;

            foreach (var section in _sections)
                section.Select(0, 0);
            _sections.Clear();
            SetAndRaise(HasSelectionProperty, ref _hasSelection, false);

            // Let links and native double-click selection keep their own input behavior.
            if (e.ClickCount != 1 || source.FindAncestorOfType<HyperlinkButton>(includeSelf: true) != null)
                return;

            Control target = source.FindAncestorOfType<SelectableTextBlock>(includeSelf: true);
            if (target == null && source.FindAncestorOfType<TextArea>(includeSelf: true) != null)
                target = source.FindAncestorOfType<TextEditor>(includeSelf: true);
            if (target == null || Content is not Control content)
                return;

            CollectSections(content);
            _anchorIndex = _sections.FindIndex(section => section.Target == target);
            if (_anchorIndex < 0)
                return;

            _anchorOffset = _sections[_anchorIndex].HitTest(e.GetPosition(_sections[_anchorIndex].Surface));
            Focus();
            _pointer = e.Pointer;
            _pointer.Capture(this);
            e.Handled = true;
        }

        private void CollectSections(Visual visual)
        {
            if (!visual.IsEffectivelyVisible)
                return;

            if (visual is SelectableTextBlock block)
                _sections.Add(new TextSection(block, block));
            else if (visual is TextEditor { Document.TextLength: > 0 } editor)
                _sections.Add(new TextSection(editor, editor.TextArea.TextView));
            else
                foreach (var child in visual.GetVisualChildren())
                    CollectSections(child);
        }

        private void UpdateSelection()
        {
            if (!_sections[_anchorIndex].IsAvailable || Content is not Control content ||
                this.TranslatePoint(_dragPoint, content) is not { } point)
                return;

            var endIndex = _anchorIndex;
            var endPoint = default(Point);
            var bestDistance = double.PositiveInfinity;
            for (var i = 0; i < _sections.Count; i++)
            {
                var section = _sections[i];
                if (!section.IsAvailable || section.Surface.TranslatePoint(default, content) is not { } origin)
                    continue;

                var local = point - origin;
                var size = section.Surface.Bounds.Size;
                var dx = Math.Max(0, Math.Max(-local.X, local.X - size.Width));
                var dy = Math.Max(0, Math.Max(-local.Y, local.Y - size.Height));
                var distance = dy * dy + dx * dx;
                if (distance >= bestDistance)
                    continue;

                endIndex = i;
                endPoint = new Point(local.X, local.Y);
                bestDistance = distance;
                if (distance == 0)
                    break;
            }

            var firstIndex = _anchorIndex;
            var firstOffset = _anchorOffset;
            var lastIndex = endIndex;
            var lastOffset = _sections[endIndex].HitTest(endPoint);
            if (firstIndex > lastIndex || (firstIndex == lastIndex && firstOffset > lastOffset))
            {
                (firstIndex, lastIndex) = (lastIndex, firstIndex);
                (firstOffset, lastOffset) = (lastOffset, firstOffset);
            }

            var hasSelection = false;
            for (var i = 0; i < _sections.Count; i++)
            {
                var section = _sections[i];
                if (i < firstIndex || i > lastIndex || !section.IsAvailable)
                    section.Select(0, 0);
                else
                    section.Select(i == firstIndex ? firstOffset : 0, i == lastIndex ? lastOffset : section.Length);
                hasSelection |= section.HasSelection;
            }
            SetAndRaise(HasSelectionProperty, ref _hasSelection, hasSelection);
        }

        private double GetScrollDelta() => _dragPoint.Y < 0 ? _dragPoint.Y : Math.Max(0, _dragPoint.Y - Bounds.Height);

        private void OnDragScroll(object sender, EventArgs e)
        {
            var delta = Math.Clamp(GetScrollDelta() / 3, -30, 30);
            Offset = new Vector(Offset.X, Offset.Y + delta);
            UpdateSelection();
        }

        private async void OnSelectionKeyDown(object sender, KeyEventArgs e)
        {
            if (HasSelection && Application.Current?.PlatformSettings?.HotkeyConfiguration.Copy.Any(gesture => gesture.Matches(e)) == true)
            {
                e.Handled = true;
                await this.CopyTextAsync(GetSelectedText());
            }
        }

        private void OnSelectionContextRequested(object sender, ContextRequestedEventArgs e)
        {
            var selected = GetSelectedText();
            if (selected.Length == 0)
                return;

            var copy = new MenuItem() { Header = App.Text("Copy") };
            copy.Click += async (_, _) => await this.CopyTextAsync(selected);
            new ContextMenu() { Items = { copy } }.Open(this);
            e.Handled = true;
        }

        private sealed class TextSection
        {
            public Control Target { get; }
            public Control Surface { get; }
            public bool IsAvailable => Surface.IsEffectivelyVisible && Surface.IsAttachedToVisualTree();
            public bool HasSelection => _start != _end;
            public int Length { get; }

            public TextSection(Control target, Control surface)
            {
                Target = target;
                Surface = surface;
                if (target is SelectableTextBlock block)
                {
                    _runs = block.Text is { } text ? [(text, text.Length)] : ReadInlines(block.Inlines).ToArray();
                    Length = _runs.Sum(run => run.Length);
                }
                else
                {
                    Length = ((TextEditor)target).Document.TextLength;
                }
            }

            public int HitTest(Point point)
            {
                if (point.Y < 0)
                    return 0;
                if (point.Y >= Surface.Bounds.Height)
                    return Length;
                if (Target is TextEditor editor)
                    return editor.GetPositionFromPoint(Surface.TranslatePoint(point, editor) ?? point) is { } position
                        ? editor.Document.GetOffset(position.Location) : Length;

                var text = (SelectableTextBlock)Target;
                point -= new Point(text.Padding.Left, text.Padding.Top);
                return Math.Clamp(text.TextLayout.HitTestPoint(point).TextPosition, 0, Length);
            }

            public void Select(int start, int end)
            {
                if (_start == start && _end == end)
                    return;

                _start = start;
                _end = end;
                if (Target is SelectableTextBlock text)
                {
                    text.SelectionStart = start;
                    text.SelectionEnd = end;
                }
                else if (Target is TextEditor { Document: { } document } editor && end <= document.TextLength)
                {
                    editor.Select(start, end - start);
                }
            }

            public string GetSelectedText()
            {
                if (Target is TextEditor editor)
                    return editor.SelectedText;

                var result = new StringBuilder();
                var offset = 0;
                foreach (var (text, length) in _runs)
                {
                    var first = Math.Max(_start - offset, 0);
                    var last = Math.Min(_end - offset, length);
                    if (first < last)
                    {
                        // An embedded link occupies one text layout position.
                        if (length == text.Length)
                            result.Append(text, first, last - first);
                        else
                            result.Append(text);
                    }
                    offset += length;
                    if (offset >= _end)
                        break;
                }
                return result.ToString();
            }

            private static IEnumerable<(string Text, int Length)> ReadInlines(InlineCollection inlines)
            {
                foreach (var inline in inlines)
                {
                    switch (inline)
                    {
                        case Run { Text: { } text }:
                            yield return (text, text.Length);
                            break;
                        case LineBreak:
                            yield return (Environment.NewLine, Environment.NewLine.Length);
                            break;
                        case Span span:
                            foreach (var part in ReadInlines(span.Inlines))
                                yield return part;
                            break;
                        case InlineUIContainer { Child: HyperlinkButton { Content: SelectableTextBlock link } }:
                            yield return (link.Inlines.Text, 1);
                            break;
                    }
                }
            }

            private readonly (string Text, int Length)[] _runs;
            private int _start;
            private int _end;
        }

        private readonly List<TextSection> _sections = [];
        private readonly DispatcherTimer _dragScroll = new() { Interval = TimeSpan.FromMilliseconds(30) };
        private IPointer _pointer;
        private Point _dragPoint;
        private int _anchorIndex;
        private int _anchorOffset;
        private bool _hasSelection;
    }
}
