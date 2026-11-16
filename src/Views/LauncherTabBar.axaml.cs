using System;
using System.IO;
using System.Linq;

using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;

namespace SourceGit.Views
{
    public class LauncherTabSizeBox : Border
    {
        public static readonly DirectProperty<LauncherTabSizeBox, bool> UseFixedWidthProperty =
            AvaloniaProperty.RegisterDirect<LauncherTabSizeBox, bool>(
                nameof(UseFixedWidth),
                static o => o.UseFixedWidth,
                static (o, v) => o.UseFixedWidth = v);

        public bool UseFixedWidth
        {
            get => _useFixedWidth;
            set => SetAndRaise(UseFixedWidthProperty, ref _useFixedWidth, value);
        }

        public LauncherTabSizeBox()
        {
            Width = 200;
        }

        protected override Type StyleKeyOverride => typeof(Border);

        protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
        {
            base.OnPropertyChanged(change);

            if (change.Property == UseFixedWidthProperty)
            {
                if (_useFixedWidth)
                    Width = 200;
                else
                    Width = double.NaN;
            }
        }

        private bool _useFixedWidth = true;
    }

    public partial class LauncherTabBar : UserControl
    {
        public static readonly DirectProperty<LauncherTabBar, bool> IsScrollButtonVisibleProperty =
            AvaloniaProperty.RegisterDirect<LauncherTabBar, bool>(
                nameof(IsScrollButtonVisible),
                static o => o.IsScrollButtonVisible);

        public bool IsScrollButtonVisible
        {
            get => _isScrollButtonVisible;
            set => SetAndRaise(IsScrollButtonVisibleProperty, ref _isScrollButtonVisible, value);
        }

        public LauncherTabBar()
        {
            InitializeComponent();
        }

        public override void Render(DrawingContext context)
        {
            base.Render(context);

            if (LauncherTabsList == null || LauncherTabsList.SelectedIndex == -1)
                return;

            var startX = LauncherTabsScroller.Offset.X;
            var endX = startX + LauncherTabsScroller.Viewport.Width;
            var height = LauncherTabsScroller.Viewport.Height;

            var selectedIdx = LauncherTabsList.SelectedIndex;
            var count = LauncherTabsList.ItemCount;
            var separatorPen = new Pen(new SolidColorBrush(ActualThemeVariant == ThemeVariant.Dark ? Colors.White : Colors.Black, 0.2));
            var separatorY = (height - 18) * 0.5 + 1;

            if (!_isScrollButtonVisible && selectedIdx > 0)
            {
                var container = LauncherTabsList.ContainerFromIndex(0);
                if (container != null)
                {
                    var x = container.Bounds.Left - startX + LauncherTabsScroller.Bounds.X - 0.5;
                    context.DrawLine(separatorPen, new Point(x, separatorY), new Point(x, separatorY + 18));
                }
            }

            for (var i = 0; i < count; i++)
            {
                if (i == selectedIdx || i == selectedIdx - 1)
                    continue;

                var container = LauncherTabsList.ContainerFromIndex(i);
                if (container == null)
                    continue;

                var containerEndX = container.Bounds.Right;
                if (containerEndX < startX || containerEndX > endX)
                    continue;

                if (_isScrollButtonVisible && i == count - 1)
                    break;

                var separatorX = containerEndX - startX + LauncherTabsScroller.Bounds.X - 0.5;
                context.DrawLine(separatorPen, new Point(separatorX, separatorY), new Point(separatorX, separatorY + 18));
            }

            var selected = LauncherTabsList.ContainerFromIndex(selectedIdx);
            if (selected == null)
                return;

            var activeStartX = selected.Bounds.X;
            var activeEndX = activeStartX + selected.Bounds.Width;
            if (activeStartX > endX + 5 || activeEndX < startX - 5)
                return;

            var geo = new StreamGeometry();
            const double angle = Math.PI / 2;
            var bottom = height + 0.5;
            var cornerSize = new Size(5, 5);

            using (var ctx = geo.Open())
            {
                var drawLeftX = activeStartX - startX + LauncherTabsScroller.Bounds.X;
                if (drawLeftX < LauncherTabsScroller.Bounds.X)
                {
                    ctx.BeginFigure(new Point(LauncherTabsScroller.Bounds.X - 0.5, bottom), true);
                    ctx.LineTo(new Point(LauncherTabsScroller.Bounds.X - 0.5, 0.5));
                }
                else
                {
                    ctx.BeginFigure(new Point(drawLeftX - 5.5, bottom), true);
                    ctx.ArcTo(new Point(drawLeftX - 0.5, bottom - 5), cornerSize, angle, false, SweepDirection.CounterClockwise);
                    ctx.LineTo(new Point(drawLeftX - 0.5, 5.5));
                    ctx.ArcTo(new Point(drawLeftX + 4.5, 0.5), cornerSize, angle, false, SweepDirection.Clockwise);
                }

                var drawRightX = activeEndX - startX + LauncherTabsScroller.Bounds.X;
                if (drawRightX <= LauncherTabsScroller.Bounds.Right)
                {
                    ctx.LineTo(new Point(drawRightX - 5.5, 0.5));
                    ctx.ArcTo(new Point(drawRightX - 0.5, 5.5), cornerSize, angle, false, SweepDirection.Clockwise);
                    ctx.LineTo(new Point(drawRightX - 0.5, bottom - 5));
                    ctx.ArcTo(new Point(drawRightX + 4.5, bottom), cornerSize, angle, false, SweepDirection.CounterClockwise);
                }
                else
                {
                    ctx.LineTo(new Point(LauncherTabsScroller.Bounds.Right - 0.5, 0.5));
                    ctx.LineTo(new Point(LauncherTabsScroller.Bounds.Right - 0.5, bottom));
                }
            }

            var fill = GetActiveTabFill();
            var stroke = new Pen(this.FindResource("Brush.Border0") as IBrush);
            context.DrawGeometry(fill, stroke, geo);

            // The 2px highlight bar along the active tab's bottom edge is drawn here rather than set
            // as a BorderThickness on the item container: changing a border changes the measured
            // size, and doing that from a layout callback re-triggers layout and can hang the UI.
            var barLeft = Math.Max(activeStartX - startX + LauncherTabsScroller.Bounds.X, LauncherTabsScroller.Bounds.X);
            var barRight = Math.Min(activeEndX - startX + LauncherTabsScroller.Bounds.X, LauncherTabsScroller.Bounds.Right);
            if (barRight > barLeft)
            {
                var barRect = new Rect(barLeft + 1, height - 2, Math.Max(0, barRight - barLeft - 2), 2);
                context.DrawRectangle(GetHighlightBrush(), null, barRect);
            }
        }

        /// <summary>
        ///     Fixed red instead of the system accent colour: the accent follows the OS theme and on
        ///     Windows resolves to a blue that reads as "not highlighted at all" against the title
        ///     bar. Cached so repeated layout passes hand out the same instance.
        /// </summary>
        private IBrush GetHighlightBrush()
        {
            var color = ActualThemeVariant == ThemeVariant.Light ? Color.FromRgb(0xC5, 0x2F, 0x2F) : Colors.Red;
            if (_highlightBrush == null || _highlightBrushColor != color)
            {
                _highlightBrushColor = color;
                _highlightBrush = new SolidColorBrush(color);
                _highlightTint = new SolidColorBrush(color, 0.16);
            }

            return _highlightBrush;
        }

        private ISolidColorBrush GetHighlightTint()
        {
            GetHighlightBrush();
            return _highlightTint;
        }

        /// <summary>
        ///     The active tab used to be filled with Brush.ToolBar. On themes where the title bar and
        ///     the tool bar resolve to the same colour the shape was drawn on top of itself and
        ///     disappeared, so fall back to the window brush whenever the two are close.
        /// </summary>
        private IBrush GetActiveTabFill()
        {
            var toolbar = this.FindResource("Brush.ToolBar") as ISolidColorBrush;
            if (toolbar == null)
                return Brushes.Transparent;

            var titleBar = this.FindResource("Brush.TitleBar") as ISolidColorBrush;
            if (titleBar != null && AreClose(titleBar.Color, toolbar.Color))
            {
                var window = this.FindResource("Brush.Window") as ISolidColorBrush;
                if (window != null && !AreClose(window.Color, titleBar.Color))
                    return window;
            }

            return toolbar;
        }

        private static bool AreClose(Color a, Color b)
        {
            const byte tolerance = 8;
            return Math.Abs(a.R - b.R) <= tolerance &&
                   Math.Abs(a.G - b.G) <= tolerance &&
                   Math.Abs(a.B - b.B) <= tolerance &&
                   Math.Abs(a.A - b.A) <= tolerance;
        }

        protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
        {
            base.OnPropertyChanged(change);

            if (change.Property.Name == nameof(ActualThemeVariant) && change.NewValue != null)
                InvalidateVisual();
        }

        private void ScrollTabs(object _, PointerWheelEventArgs e)
        {
            if (Math.Abs(e.Delta.X) < Math.Abs(e.Delta.Y))
            {
                var x = LauncherTabsScroller.Offset.X;
                var extent = LauncherTabsScroller.Extent.Width;
                var viewport = LauncherTabsScroller.Viewport.Width;
                var delta = e.Delta.Y;

                if (extent > viewport)
                {
                    x += -delta * 64; // Use the same logic with vertical scrolling in `ScrollContentPresenter`
                    x = Math.Min(Math.Max(x, 0), extent - viewport);
                }

                LauncherTabsScroller.Offset = new Vector(x, 0);
                e.Handled = true;
            }
        }

        private void ScrollTabsLeft(object _, RoutedEventArgs e)
        {
            LauncherTabsScroller.Offset -= _scrollStep;
            e.Handled = true;
        }

        private void ScrollTabsRight(object _, RoutedEventArgs e)
        {
            LauncherTabsScroller.Offset += _scrollStep;
            e.Handled = true;
        }

        private void OnTabsLayoutUpdated(object _1, EventArgs _2)
        {
            IsScrollButtonVisible = LauncherTabsScroller.Extent.Width > LauncherTabsScroller.Viewport.Width;
            ScheduleActiveSync();
            InvalidateVisual();
        }

        private void OnTabsSelectionChanged(object _1, SelectionChangedEventArgs _2)
        {
            ScheduleActiveSync();
            InvalidateVisual();
        }

        /// <summary>
        ///     Never decorates the tab bar synchronously from a layout callback: touching the item
        ///     containers there re-enters layout, and the UI thread can end up looping until the
        ///     window stops responding. The work is posted at background priority and coalesced,
        ///     so at most one decoration pass is pending at any time.
        /// </summary>
        private void ScheduleActiveSync()
        {
            if (_activeSyncPending)
                return;

            _activeSyncPending = true;
            Dispatcher.UIThread.Post(() =>
            {
                _activeSyncPending = false;
                SyncActiveClass();
            }, DispatcherPriority.Background);
        }

        /// <summary>
        ///     Applies the active-page decoration directly, as local values on the item container and
        ///     on the title TextBlock.
        ///
        ///     Every style-based route was either invisible on Windows or impossible to verify from
        ///     here: a ListBoxItem:selected style has to reach into the item template, and a
        ///     descendant selector has to cross the DataTemplate boundary, while the class-keyed
        ///     styles still depend on where the styles collection is applied. Local values outrank
        ///     theme, application and control setters alike, so what is computed here is exactly
        ///     what gets rendered on every platform.
        /// </summary>
        private void SyncActiveClass()
        {
            if (LauncherTabsList == null)
                return;

            var selected = LauncherTabsList.SelectedIndex;
            var count = LauncherTabsList.ItemCount;
            var highlightBrush = GetHighlightBrush();
            var tint = GetHighlightTint();
            var realized = 0;
            var titles = 0;

            for (var i = 0; i < count; i++)
            {
                var container = LauncherTabsList.ContainerFromIndex(i) as ContentControl;
                if (container == null)
                    continue;

                realized++;

                var active = i == selected;
                container.Classes.Set("active", active);
                if (active)
                {
                    // Render-only properties: no measured size changes, so this cannot start a
                    // layout loop. The bottom bar is painted by Render().
                    if (!ReferenceEquals(container.Background, tint))
                        container.Background = tint;

                    if (!ReferenceEquals(container.BorderBrush, highlightBrush))
                        container.BorderBrush = highlightBrush;

                    if (container.Opacity != 1)
                        container.Opacity = 1;
                }
                else
                {
                    if (container.Background != null)
                        container.ClearValue(TemplatedControl.BackgroundProperty);

                    if (container.BorderBrush != null)
                        container.ClearValue(TemplatedControl.BorderBrushProperty);

                    if (container.BorderThickness != default(Thickness))
                        container.ClearValue(TemplatedControl.BorderThicknessProperty);

                    container.ClearValue(Visual.OpacityProperty);
                }

                foreach (var title in container.GetVisualDescendants().OfType<TextBlock>())
                {
                    if (!title.Classes.Contains("tab_title"))
                        continue;

                    titles++;
                    title.Classes.Set("active", active);
                    if (active)
                    {
                        if (title.FontWeight != FontWeight.Bold)
                            title.FontWeight = FontWeight.Bold;

                        if (!ReferenceEquals(title.Foreground, highlightBrush))
                            title.Foreground = highlightBrush;
                    }
                    else
                    {
                        title.ClearValue(TextBlock.FontWeightProperty);
                        title.ClearValue(TextBlock.ForegroundProperty);
                    }
                }
            }

            TraceActiveSync(selected, count, realized, titles);
        }

        /// <summary>
        ///     Writes one line per distinct outcome, so a report of "nothing changed" can be turned
        ///     into a fact: either no container was realized (containers == 0) or the decoration was
        ///     applied and the problem lies elsewhere.
        /// </summary>
        private void TraceActiveSync(int selected, int count, int realized, int titles)
        {
            var state = $"{selected}/{count}/{realized}/{titles}";
            if (state == _lastActiveTrace)
                return;

            _lastActiveTrace = state;
            try
            {
                File.AppendAllText(_activeTraceFile,
                    $"{DateTime.Now:HH:mm:ss.fff} tab=active selected={selected} items={count} containers={realized} titles={titles}{Environment.NewLine}");
            }
            catch
            {
                // Tracing must never break the UI.
            }
        }

        private void OnPointerPressedTab(object sender, PointerPressedEventArgs e)
        {
            if (sender is Border border)
            {
                var point = e.GetCurrentPoint(border);
                if (point.Properties.IsMiddleButtonPressed && border.DataContext is ViewModels.LauncherPage page)
                {
                    (DataContext as ViewModels.Launcher)?.CloseTab(page);
                    e.Handled = true;
                }
                else if (point.Properties.IsLeftButtonPressed)
                {
                    _pressedTabEvent = e;
                    _startDragTab = false;
                }
                else
                {
                    _pressedTabEvent = null;
                    _startDragTab = false;
                }
            }
        }

        private void OnPointerReleasedTab(object _1, PointerReleasedEventArgs _2)
        {
            _pressedTabEvent = null;
            _startDragTab = false;
        }

        private async void OnPointerMovedOverTab(object sender, PointerEventArgs e)
        {
            if (_pressedTabEvent != null && !_startDragTab && sender is Border { DataContext: ViewModels.LauncherPage page } border)
            {
                var delta = e.GetPosition(border) - _pressedTabEvent.GetPosition(border);
                var sizeSquired = delta.X * delta.X + delta.Y * delta.Y;
                if (sizeSquired < 64)
                    return;

                _startDragTab = true;

                var data = new DataTransfer();
                data.Add(DataTransferItem.Create(_dndMainTabFormat, page.Node.Id));
                await DragDrop.DoDragDropAsync(_pressedTabEvent, data, DragDropEffects.Move);
            }
            e.Handled = true;
        }

        private void DropTab(object sender, DragEventArgs e)
        {
            if (e.DataTransfer.TryGetValue(_dndMainTabFormat) is not { Length: > 0 } id)
                return;

            if (DataContext is not ViewModels.Launcher launcher)
                return;

            ViewModels.LauncherPage target = null;
            foreach (var page in launcher.Pages)
            {
                if (page.Node.Id.Equals(id, StringComparison.Ordinal))
                {
                    target = page;
                    break;
                }
            }

            if (target == null)
                return;

            if (sender is not Border { DataContext: ViewModels.LauncherPage to })
                return;

            if (target == to)
                return;

            launcher.MoveTab(target, to);

            _pressedTabEvent = null;
            _startDragTab = false;
            e.Handled = true;
        }

        private void OnTabContextRequested(object sender, ContextRequestedEventArgs e)
        {
            if (sender is Border { DataContext: ViewModels.LauncherPage page } border &&
                DataContext is ViewModels.Launcher vm)
            {
                var menu = new ContextMenu();

                if (vm.ActivePage.Data is ViewModels.Repository repo)
                {
                    var explore = new MenuItem();
                    explore.Header = App.Text("Repository.Explore");
                    explore.Icon = this.CreateMenuIcon("Icons.Explore");
                    explore.Tag = OperatingSystem.IsMacOS() ? "⌘+E" : "Ctrl+E";
                    explore.Click += (_, e) =>
                    {
                        Native.OS.OpenInFileManager(repo.FullPath);
                        e.Handled = true;
                    };
                    menu.Items.Add(explore);

                    var refresh = new MenuItem();
                    refresh.Header = App.Text("PageTabBar.Tab.Refresh");
                    refresh.Icon = this.CreateMenuIcon("Icons.Loading");
                    refresh.Tag = "F5";
                    refresh.Click += (_, ev) =>
                    {
                        repo.RefreshAll();
                        ev.Handled = true;
                    };
                    menu.Items.Add(refresh);

                    var copyPath = new MenuItem();
                    copyPath.Header = App.Text("PageTabBar.Tab.CopyPath");
                    copyPath.Icon = this.CreateMenuIcon("Icons.Copy");
                    copyPath.Click += async (_, ev) =>
                    {
                        var dir = new DirectoryInfo(repo.FullPath);
                        await this.CopyTextAsync(dir.FullName);
                        ev.Handled = true;
                    };
                    menu.Items.Add(copyPath);
                    menu.Items.Add(new MenuItem() { Header = "-" });

                    var edit = new MenuItem();
                    edit.Header = App.Text("PageTabBar.Tab.Edit");
                    edit.Icon = this.CreateMenuIcon("Icons.Edit");
                    edit.Click += (_, ev) =>
                    {
                        page.Node.Edit();
                        ev.Handled = true;
                    };
                    menu.Items.Add(edit);

                    var workspaces = ViewModels.Preferences.Instance.Workspaces;
                    if (workspaces.Count > 1)
                    {
                        var moveTo = new MenuItem();
                        moveTo.Header = App.Text("PageTabBar.Tab.MoveToWorkspace");
                        moveTo.Icon = this.CreateMenuIcon("Icons.MoveTo");

                        foreach (var ws in workspaces)
                        {
                            var dupWs = ws;
                            var isCurrent = dupWs == vm.ActiveWorkspace;
                            var icon = this.CreateMenuIcon(isCurrent ? "Icons.Check" : "Icons.Workspace");
                            icon.Fill = dupWs.Brush;

                            var target = new MenuItem();
                            target.Header = ws.Name;
                            target.Icon = icon;
                            target.Click += (_, ev) =>
                            {
                                if (!isCurrent)
                                {
                                    vm.CloseTab(page);
                                    dupWs.Repositories.Add(repo.FullPath);
                                }

                                ev.Handled = true;
                            };
                            moveTo.Items.Add(target);
                        }

                        menu.Items.Add(moveTo);
                    }

                    menu.Items.Add(new MenuItem() { Header = "-" });
                }

                var close = new MenuItem();
                close.Header = App.Text("PageTabBar.Tab.Close");
                close.Tag = OperatingSystem.IsMacOS() ? "⌘+W" : "Ctrl+W";
                close.Click += (_, ev) =>
                {
                    vm.CloseTab(page);
                    ev.Handled = true;
                };
                menu.Items.Add(close);

                var closeOthers = new MenuItem();
                closeOthers.Header = App.Text("PageTabBar.Tab.CloseOther");
                closeOthers.Click += (_, ev) =>
                {
                    vm.CloseOtherTabs();
                    ev.Handled = true;
                };
                menu.Items.Add(closeOthers);

                var closeRight = new MenuItem();
                closeRight.Header = App.Text("PageTabBar.Tab.CloseRight");
                closeRight.Click += (_, ev) =>
                {
                    vm.CloseRightTabs();
                    ev.Handled = true;
                };
                menu.Items.Add(closeRight);
                menu.Open(border);
            }

            e.Handled = true;
        }

        private void OnCloseTab(object sender, RoutedEventArgs e)
        {
            if (sender is Button btn && DataContext is ViewModels.Launcher vm)
                vm.CloseTab(btn.DataContext as ViewModels.LauncherPage);

            e.Handled = true;
        }

        private bool _isScrollButtonVisible = false;
        private readonly Vector _scrollStep = new(64, 0);
        private PointerPressedEventArgs _pressedTabEvent = null;
        private bool _startDragTab = false;
        private readonly DataFormat<string> _dndMainTabFormat = DataFormat.CreateStringApplicationFormat("sourcegit-dnd-main-tab");
        private readonly string _activeTraceFile = Path.Combine(Native.OS.BasicDirectories.ConfigDir, "tabbar-trace.log");
        private string _lastActiveTrace = null;
        private bool _activeSyncPending;
        private IBrush _highlightBrush;
        private ISolidColorBrush _highlightTint;
        private Color _highlightBrushColor;
    }
}
