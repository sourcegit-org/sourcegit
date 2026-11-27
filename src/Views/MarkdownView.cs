using System;

using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Controls.Primitives;
using Avalonia.Markup.Xaml.MarkupExtensions;
using Avalonia.Media;

using Markdig;
using Markdig.Extensions.Tables;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;

namespace SourceGit.Views
{
    public class MarkdownView : ContentControl
    {
        public static readonly StyledProperty<string> MarkdownProperty =
            AvaloniaProperty.Register<MarkdownView, string>(nameof(Markdown), string.Empty);

        public string Markdown
        {
            get => GetValue(MarkdownProperty);
            set => SetValue(MarkdownProperty, value);
        }

        protected override Type StyleKeyOverride => typeof(ContentControl);

        protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
        {
            base.OnPropertyChanged(change);
            if (change.Property != MarkdownProperty)
                return;

            var document = Markdig.Markdown.Parse(Markdown ?? string.Empty, Pipeline);
            var blocks = new StackPanel() { Spacing = 10 };
            AppendBlocks(blocks, document);
            Content = blocks;
        }

        private static void AppendBlocks(StackPanel target, ContainerBlock container)
        {
            foreach (var block in container)
            {
                switch (block)
                {
                    case HeadingBlock heading:
                        var header = Text(heading.Inline);
                        header.FontSize = heading.Level switch { 1 => 22, 2 => 19, 3 => 16, _ => 14 };
                        header.FontWeight = FontWeight.SemiBold;
                        target.Children.Add(header);
                        break;
                    case ParagraphBlock paragraph:
                        target.Children.Add(Text(paragraph.Inline));
                        break;
                    case CodeBlock code:
                        var codeText = new SelectableTextBlock() { Text = code.Lines.ToString(), Margin = new Thickness(8) };
                        codeText.Bind(TextBlock.FontFamilyProperty, new DynamicResourceExtension("Fonts.Monospace"));
                        var codeScroll = new ScrollViewer()
                        {
                            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
                            VerticalScrollBarVisibility = ScrollBarVisibility.Disabled,
                            Content = codeText,
                        };
                        target.Children.Add(Framed(codeScroll, new Thickness(1)));
                        break;
                    case ListBlock list:
                        target.Children.Add(RenderList(list));
                        break;
                    case QuoteBlock quote:
                        var quoteContent = new StackPanel() { Spacing = 6, Margin = new Thickness(10, 2) };
                        AppendBlocks(quoteContent, quote);
                        target.Children.Add(Framed(quoteContent, new Thickness(2, 0, 0, 0)));
                        break;
                    case ThematicBreakBlock:
                        target.Children.Add(Framed(null, new Thickness(0, 1, 0, 0)));
                        break;
                    case Table table:
                        target.Children.Add(RenderTable(table));
                        break;
                    case ContainerBlock nested:
                        AppendBlocks(target, nested);
                        break;
                }
            }
        }

        private static StackPanel RenderList(ListBlock list)
        {
            var result = new StackPanel() { Spacing = 4 };
            var number = int.TryParse(list.OrderedStart, out var start) ? start : 1;
            foreach (var item in list)
            {
                if (item is not ListItemBlock entry)
                    continue;

                var bullet = new TextBlock()
                {
                    Text = list.IsOrdered ? $"{number++}." : "•",
                    Margin = new Thickness(6, 0, 8, 0),
                };
                var text = new StackPanel() { Spacing = 4 };
                AppendBlocks(text, entry);

                var row = new Grid() { ColumnDefinitions = new ColumnDefinitions("Auto,*") };
                Grid.SetColumn(text, 1);
                row.Children.Add(bullet);
                row.Children.Add(text);
                result.Children.Add(row);
            }

            return result;
        }

        private static Grid RenderTable(Table table)
        {
            var grid = new Grid();
            for (var i = 0; i < table.ColumnDefinitions.Count; i++)
                grid.ColumnDefinitions.Add(new ColumnDefinition(1, GridUnitType.Star));

            foreach (var block in table)
            {
                if (block is not TableRow row)
                    continue;

                var rowIndex = grid.RowDefinitions.Count;
                grid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
                var column = 0;
                foreach (var item in row)
                {
                    if (item is not TableCell cell)
                        continue;

                    var content = new StackPanel() { Margin = new Thickness(6, 4) };
                    if (row.IsHeader)
                        content.SetValue(TextElement.FontWeightProperty, FontWeight.SemiBold);
                    AppendBlocks(content, cell);

                    var border = Framed(content, new Thickness(column == 0 ? 1 : 0, rowIndex == 0 ? 1 : 0, 1, 1));
                    Grid.SetRow(border, rowIndex);
                    Grid.SetColumn(border, column++);
                    grid.Children.Add(border);
                }
            }

            return grid;
        }

        private static Border Framed(Control content, Thickness thickness)
        {
            var border = new Border() { BorderThickness = thickness, Child = content };
            border.Bind(Border.BorderBrushProperty, new DynamicResourceExtension("Brush.Border2"));
            return border;
        }

        private static SelectableTextBlock Text(ContainerInline inlines)
        {
            var text = new SelectableTextBlock() { TextWrapping = TextWrapping.Wrap };
            if (inlines != null)
                AppendInlines(text.Inlines, inlines);
            return text;
        }

        private static void AppendInlines(InlineCollection target, ContainerInline container)
        {
            foreach (var inline in container)
            {
                switch (inline)
                {
                    case LiteralInline literal:
                        target.Add(new Run(literal.Content.ToString()));
                        break;
                    case HtmlEntityInline entity:
                        target.Add(new Run(entity.Transcoded.ToString()));
                        break;
                    case AutolinkInline autolink:
                        target.Add(new Run(autolink.Url));
                        break;
                    case CodeInline code:
                        var monospace = new Run(code.Content);
                        monospace.Bind(TextElement.FontFamilyProperty, new DynamicResourceExtension("Fonts.Monospace"));
                        target.Add(monospace);
                        break;
                    case LineBreakInline lineBreak:
                        target.Add(lineBreak.IsHard ? new LineBreak() : new Run(" "));
                        break;
                    case EmphasisInline emphasis:
                        var style = emphasis.DelimiterCount == 2 ? (Span)new Bold() : new Italic();
                        AppendInlines(style.Inlines, emphasis);
                        target.Add(style);
                        break;
                    case LinkInline link when !link.IsImage && Uri.TryCreate(link.Url, UriKind.Absolute, out var uri) &&
                        (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps):
                        var linkText = Text(link);
                        var button = new HyperlinkButton() { NavigateUri = uri, Content = linkText, Padding = new Thickness(0) };
                        target.Add(new InlineUIContainer(button));
                        break;
                    case ContainerInline nested:
                        AppendInlines(target, nested);
                        break;
                }
            }
        }

        private static readonly MarkdownPipeline Pipeline = new MarkdownPipelineBuilder().UsePipeTables().DisableHtml().Build();
    }
}
