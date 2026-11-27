using System;
using System.Threading.Tasks;

using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;

namespace SourceGit.Views
{
    public partial class Launcher : ChromelessWindow
    {
        public static readonly DirectProperty<Launcher, GridLength> CaptionHeightProperty =
            AvaloniaProperty.RegisterDirect<Launcher, GridLength>(
                nameof(CaptionHeight),
                static o => o.CaptionHeight);

        public GridLength CaptionHeight
        {
            get => _captionHeight;
            set => SetAndRaise(CaptionHeightProperty, ref _captionHeight, value);
        }

        public bool HasRightCaptionButton
        {
            get
            {
                if (OperatingSystem.IsLinux())
                    return !Native.OS.UseSystemWindowFrame;

                return OperatingSystem.IsWindows();
            }
        }

        public Launcher()
        {
            if (OperatingSystem.IsMacOS())
                CaptionHeight = new GridLength(34);
            else if (UseSystemWindowFrame)
                CaptionHeight = new GridLength(30);
            else
                CaptionHeight = new GridLength(38);

            InitializeComponent();
            PositionChanged += OnPositionChanged;

            var layout = ViewModels.Preferences.Instance.Layout;
            Width = layout.LauncherWidth;
            Height = layout.LauncherHeight;

            WindowTrace.Write($"ctor: applied explicit width={layout.LauncherWidth} height={layout.LauncherHeight}, " +
                $"position=({layout.LauncherPositionX},{layout.LauncherPositionY}), savedState={layout.LauncherWindowState}");

            var x = layout.LauncherPositionX;
            var y = layout.LauncherPositionY;
            if (x != int.MinValue && y != int.MinValue && Screens is { } screens)
            {
                var position = new PixelPoint(x, y);
                var size = new PixelSize((int)layout.LauncherWidth, (int)layout.LauncherHeight);
                var desiredRect = new PixelRect(position, size);
                for (var i = 0; i < screens.ScreenCount; i++)
                {
                    var screen = screens.All[i];
                    if (screen.WorkingArea.Contains(desiredRect))
                    {
                        Position = position;
                        return;
                    }
                }
            }

            WindowStartupLocation = WindowStartupLocation.CenterScreen;
        }

        public void BringToTop()
        {
            if (WindowState == WindowState.Minimized)
                WindowState = _lastWindowState;
            else
                Activate();
        }

        protected override void OnOpened(EventArgs e)
        {
            base.OnOpened(e);

            var preferences = ViewModels.Preferences.Instance;
            WindowTrace.Write($"OnOpened(before): state={WindowState} bounds={Bounds} client={ClientSize} pos={Position}");
            WindowState = preferences.Layout.LauncherWindowState;
            WindowTrace.Write($"OnOpened(after): state={WindowState} bounds={Bounds} client={ClientSize} pos={Position}");
        }

        protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
        {
            base.OnPropertyChanged(change);

            if (change.Property == WindowStateProperty)
            {
                var state = (WindowState)change.NewValue!;
                var oldState = (WindowState)change.OldValue!;
                _lastWindowState = oldState;

                if (!OperatingSystem.IsMacOS() && !UseSystemWindowFrame)
                    CaptionHeight = new GridLength(state == WindowState.Maximized ? 30 : 38);

                // Do NOT persist yet. A modal dialog (preferences) makes X11 flip the owner from
                // Maximized to Normal without resizing anything; persisting that phantom state is
                // what made a maximized window come back small on the next start.
                // A genuine user maximize/restore always comes with a real resize, so the state
                // is only committed after a SizeChanged arrives (see OnSizeChanged), and is rolled
                // back below if no resize follows within PhantomStateWindowMs.
                WindowTrace.Write($"WindowState: {oldState} -> {state}, bounds={Bounds} client={ClientSize} explicitSize=({Width}x{Height})");

                if (state != oldState && !_revertingState)
                {
                    _pendingStateNew = state;
                    _pendingStateOld = oldState;
                    var generation = ++_stateCheckGeneration;
                    _ = ConfirmStateTransitionAsync(generation);
                }
                else if (_revertingState)
                {
                    PersistWindowState(state);
                }
            }
            else if (change.Property == IsActiveProperty)
            {
                WindowTrace.Write($"IsActive: {change.OldValue} -> {change.NewValue}, state={WindowState} bounds={Bounds}");

                if (!IsActive && DataContext is ViewModels.Launcher { CommandPalette: { } } vm)
                    vm.CommandPalette = null;

                // Re-activated means the modal dialog that produced the phantom restore is gone;
                // only now is it safe to put the owner back, otherwise the WM would keep flipping
                // it to Normal for every re-maximize and we would end up in a 2Hz tug of war.
                if (IsActive && _phantomRestoreTo.HasValue)
                    ApplyPhantomRestore();
            }

            if (OperatingSystem.IsMacOS() && WindowState != WindowState.FullScreen)
            {
                if (change.Property == WindowStateProperty ||
                    change.Property == BoundsProperty ||
                    change.Property == TitleProperty)
                    Native.MacOSUtilities.AdjustTrafficLightsForThickTitleBar(this);
            }
        }

        protected override void OnSizeChanged(SizeChangedEventArgs e)
        {
            base.OnSizeChanged(e);

            WindowTrace.Write($"SizeChanged: {e.PreviousSize} -> {e.NewSize}, state={WindowState} bounds={Bounds} " +
                $"client={ClientSize} explicitSize=({Width}x{Height}) workArea={Screens?.ScreenFromWindow(this)?.WorkingArea}");

            // A real resize proves the pending state transition came from an actual user
            // maximize/restore rather than from a modal dialog stealing activation.
            if (_pendingStateNew.HasValue && e.NewSize != e.PreviousSize)
            {
                var state = _pendingStateNew.Value;
                _pendingStateNew = null;
                _phantomRestoreTo = null;
                ++_stateCheckGeneration;
                PersistWindowState(state);
            }
            else if (_phantomRestoreTo.HasValue)
            {
                // The user resized the window themselves; the queued phantom undo is stale.
                _phantomRestoreTo = null;
            }

            if (WindowState == WindowState.Normal)
            {
                // Store what the window really is; "Width"/"Height" may still carry the
                // maximized geometry, which must not pollute the restore size.
                var layout = ViewModels.Preferences.Instance.Layout;
                layout.LauncherWidth = e.NewSize.Width;
                layout.LauncherHeight = e.NewSize.Height;
            }
        }

        protected override async void OnKeyDown(KeyEventArgs e)
        {
            if (DataContext is not ViewModels.Launcher vm)
                return;

            // Check for AltGr (which is detected as Ctrl+Alt)
            bool isAltGr = e.KeyModifiers.HasFlag(KeyModifiers.Control) &&
                           e.KeyModifiers.HasFlag(KeyModifiers.Alt);

            // Skip hotkey processing if AltGr is pressed
            if (isAltGr)
            {
                base.OnKeyDown(e);
                return;
            }

            // Register hotkeys for Windows/Linux (macOS has registered these keys in system menu bar)
            var isMacOS = OperatingSystem.IsMacOS();
            var cmdKey = isMacOS ? KeyModifiers.Meta : KeyModifiers.Control;
            if (!isMacOS)
            {
                if (e is { KeyModifiers: KeyModifiers.Control, Key: Key.OemComma })
                {
                    WindowTrace.Write($"Preferences open(cmd): state={WindowState} bounds={Bounds} client={ClientSize}");
                    await this.ShowDialogAsync(new Preferences());
                    WindowTrace.Write($"Preferences closed(cmd): state={WindowState} bounds={Bounds} client={ClientSize} " +
                        $"layout=({ViewModels.Preferences.Instance.Layout.LauncherWidth}x{ViewModels.Preferences.Instance.Layout.LauncherHeight}, " +
                        $"{ViewModels.Preferences.Instance.Layout.LauncherWindowState})");
                    e.Handled = true;
                    return;
                }

                if (e is { KeyModifiers: KeyModifiers.None, Key: Key.F1 })
                {
                    await this.ShowDialogAsync(new Hotkeys());
                    e.Handled = true;
                    return;
                }

                if (e is { KeyModifiers: KeyModifiers.Control, Key: Key.Q })
                {
                    App.Quit(0);
                    return;
                }
            }

            // Ctrl+` to open terminal. On macOS, Cmd+` is used to switch between windows
            if (e is { Key: Key.OemTilde, KeyModifiers: KeyModifiers.Control })
            {
                if (vm.ActivePage.Data is ViewModels.Repository repo)
                    Native.OS.OpenTerminal(repo.FullPath);
                else
                    ViewModels.Welcome.Instance.OpenTerminal();

                e.Handled = true;
                return;
            }

            if (vm.CommandPalette != null)
            {
                if (e.Key == Key.Escape)
                {
                    vm.CommandPalette = null;
                    e.Handled = true;
                }
                else if (vm.ActivePage.Data is ViewModels.Repository repo
                    && vm.CommandPalette is ViewModels.LauncherPagesCommandPalette
                    && e.Key == Key.P
                    && e.KeyModifiers == (cmdKey | KeyModifiers.Shift))
                {
                    vm.CommandPalette = new ViewModels.RepositoryCommandPalette(repo);
                    e.Handled = true;
                }

                return;
            }

            if (e.KeyModifiers.HasFlag(cmdKey))
            {
                if (e.Key == Key.W)
                {
                    vm.CloseTab(null);
                    e.Handled = true;
                    return;
                }

                if (e.Key == Key.R)
                {
                    if (vm.ActivePage.Data is not ViewModels.Welcome)
                        vm.AddNewTab();

                    ViewModels.Welcome.Instance.Clone();
                    e.Handled = true;
                    return;
                }

                if (e.Key == Key.L)
                {
                    if (vm.ActivePage.Data is not ViewModels.Welcome)
                        vm.AddNewTab();

                    ViewModels.Welcome.Instance.OpenLocalRepository();
                    e.Handled = true;
                    return;
                }

                if (e.Key == Key.T && e.KeyModifiers == cmdKey)
                {
                    vm.AddNewTab();
                    e.Handled = true;
                    return;
                }

                if ((isMacOS && e.KeyModifiers.HasFlag(KeyModifiers.Alt) && e.Key == Key.Right) ||
                    (!isMacOS && !e.KeyModifiers.HasFlag(KeyModifiers.Shift) && e.Key == Key.Tab))
                {
                    vm.GotoNextTab();
                    e.Handled = true;
                    return;
                }

                if ((isMacOS && e.KeyModifiers.HasFlag(KeyModifiers.Alt) && e.Key == Key.Left) ||
                    (!isMacOS && e.KeyModifiers.HasFlag(KeyModifiers.Shift) && e.Key == Key.Tab))
                {
                    vm.GotoPrevTab();
                    e.Handled = true;
                    return;
                }

                if (vm.ActivePage.Data is ViewModels.Repository repo)
                {
                    switch (e.Key)
                    {
                        case Key.D1 or Key.NumPad1:
                            repo.SelectedViewIndex = 0;
                            e.Handled = true;
                            return;
                        case Key.D2 or Key.NumPad2:
                            repo.SelectedViewIndex = 1;
                            e.Handled = true;
                            return;
                        case Key.D3 or Key.NumPad3:
                            repo.SelectedViewIndex = 2;
                            e.Handled = true;
                            return;
                        case Key.P when e.KeyModifiers.HasFlag(KeyModifiers.Shift):
                            vm.CommandPalette = new ViewModels.RepositoryCommandPalette(repo);
                            e.Handled = true;
                            return;
                        case Key.F when e.KeyModifiers.HasFlag(KeyModifiers.Shift):
                            repo.Histories.IsSearchingCommits = !repo.Histories.IsSearchingCommits;
                            e.Handled = true;
                            return;
                        case Key.B when e.KeyModifiers.HasFlag(KeyModifiers.Shift):
                            if (repo.CanCreatePopup() && repo.GetSelectedCommitInHistory() is { } bc)
                                repo.ShowPopup(new ViewModels.CreateBranch(repo, bc));
                            e.Handled = true;
                            return;
                        case Key.T when e.KeyModifiers.HasFlag(KeyModifiers.Shift):
                            if (repo.CanCreatePopup() && repo.GetSelectedCommitInHistory() is { } tc)
                                repo.ShowPopup(new ViewModels.CreateTag(repo, tc));
                            e.Handled = true;
                            return;
                        case Key.E:
                            Native.OS.OpenInFileManager(repo.FullPath);
                            e.Handled = true;
                            return;
                    }
                }
            }
            else if (e.Key == Key.Escape)
            {
                vm.ActivePage.CancelPopup();
                vm.ActivePage.Notifications.Clear();
                e.Handled = true;
                return;
            }
            else if (e.Key == Key.F5)
            {
                if (vm.ActivePage.Data is ViewModels.Repository repo)
                {
                    repo.RefreshAll();
                    e.Handled = true;
                    return;
                }
                else if (vm.ActivePage.Data is ViewModels.Welcome welcome)
                {
                    e.Handled = true;
                    await welcome.UpdateStatusAsync(true, null);
                    return;
                }
            }

            base.OnKeyDown(e);
        }

        protected override void OnPointerPressed(PointerPressedEventArgs e)
        {
            base.OnPointerPressed(e);

            if (!e.Handled)
            {
                if (e.Properties.PointerUpdateKind == PointerUpdateKind.XButton1Pressed)
                    (DataContext as ViewModels.Launcher)?.GotoPrevTab();
                else if (e.Properties.PointerUpdateKind == PointerUpdateKind.XButton2Pressed)
                    (DataContext as ViewModels.Launcher)?.GotoNextTab();
            }
        }

        protected override void OnClosing(WindowClosingEventArgs e)
        {
            base.OnClosing(e);

            if (!Design.IsDesignMode && DataContext is ViewModels.Launcher launcher)
                launcher.CloseAll();
        }

        protected override void OnClosed(EventArgs e)
        {
            base.OnClosed(e);

            WindowTrace.Write($"OnClosed: state={WindowState} bounds={Bounds} client={ClientSize} explicitSize=({Width}x{Height})");

            if (!Design.IsDesignMode)
                ViewModels.Preferences.Instance.Save();

            App.Quit(0);
        }

        private void OnPositionChanged(object sender, PixelPointEventArgs e)
        {
            WindowTrace.Write($"PositionChanged: {e.Point}, state={WindowState} bounds={Bounds}");

            // While a state transition is unconfirmed the window may be in the middle of a
            // phantom restore; the position reported here is not the user's layout yet.
            if (WindowState == WindowState.Normal && _pendingStateNew == null)
            {
                var layout = ViewModels.Preferences.Instance.Layout;
                layout.LauncherPositionX = Position.X;
                layout.LauncherPositionY = Position.Y;
            }
        }

        /// <summary>
        ///     A transition is only trusted once it is followed by a real resize. Modal dialogs on
        ///     X11 flip the owner from Maximized to Normal without resizing it, and persisting that
        ///     phantom state made a maximized window restart at its old, smaller size.
        /// </summary>
        private async Task ConfirmStateTransitionAsync(int generation)
        {
            await Task.Delay(PhantomStateWindowMs);

            if (generation != _stateCheckGeneration || !_pendingStateNew.HasValue)
                return;

            var oldState = _pendingStateOld;
            var newState = _pendingStateNew.Value;
            _pendingStateNew = null;

            if (newState == WindowState.Normal && oldState == WindowState.Maximized)
            {
                // Queue the undo instead of applying it now: while the modal dialog is still open
                // the WM turns every re-maximize straight back into a Normal flip, so acting here
                // produces an endless Maximized/Normal oscillation. It is applied once the owner
                // is re-activated after the dialog closes.
                WindowTrace.Write($"phantom transition ignored: {oldState} -> {newState} without a resize, queued restore to {oldState}");
                _phantomRestoreTo = oldState;
                PersistWindowState(oldState);
                return;
            }

            if (newState != WindowState.Maximized)
                _phantomRestoreTo = null;

            PersistWindowState(newState);
        }

        private void ApplyPhantomRestore()
        {
            var target = _phantomRestoreTo!.Value;
            _phantomRestoreTo = null;

            if (WindowState == target)
                return;

            WindowTrace.Write($"apply queued restore: {WindowState} -> {target}");
            _revertingState = true;
            try
            {
                WindowState = target;
            }
            finally
            {
                _revertingState = false;
            }

            PersistWindowState(target);
        }

        private void PersistWindowState(WindowState state)
        {
            var layout = ViewModels.Preferences.Instance.Layout;
            if (layout.LauncherWindowState != state)
            {
                layout.LauncherWindowState = state;
                WindowTrace.Write($"persist state={state}");
            }
        }

        private void OnOpenWorkspaceMenu(object sender, RoutedEventArgs e)
        {
            if (sender is Button btn && DataContext is ViewModels.Launcher launcher)
            {
                if (launcher.CommandPalette != null)
                    launcher.CommandPalette = null;

                var pref = ViewModels.Preferences.Instance;
                var menu = new ContextMenu();
                menu.Placement = PlacementMode.BottomEdgeAlignedLeft;
                menu.VerticalOffset = -6;

                var groupHeader = new TextBlock()
                {
                    Text = App.Text("Launcher.Workspaces"),
                    FontWeight = FontWeight.Bold,
                };

                var workspaces = new MenuItem();
                workspaces.Header = groupHeader;
                workspaces.IsEnabled = false;
                menu.Items.Add(workspaces);

                for (var i = 0; i < pref.Workspaces.Count; i++)
                {
                    var workspace = pref.Workspaces[i];

                    var icon = this.CreateMenuIcon(workspace.IsActive ? "Icons.Check" : "Icons.Workspace");
                    icon.Fill = workspace.Brush;

                    var item = new MenuItem();
                    item.Header = workspace.Name;
                    item.Icon = icon;
                    item.Click += (_, ev) =>
                    {
                        if (!workspace.IsActive)
                            launcher.SwitchWorkspace(workspace);

                        ev.Handled = true;
                    };

                    menu.Items.Add(item);
                }

                menu.Items.Add(new MenuItem() { Header = "-" });

                var configure = new MenuItem();
                configure.Header = App.Text("Workspace.Configure");
                configure.Click += async (_, ev) =>
                {
                    await this.ShowDialogAsync(new ViewModels.ConfigureWorkspace());
                    ev.Handled = true;
                };
                menu.Items.Add(configure);
                menu.Open(btn);
            }

            e.Handled = true;
        }

        private void OnOpenPagesCommandPalette(object sender, RoutedEventArgs e)
        {
            if (DataContext is ViewModels.Launcher vm)
                vm.CommandPalette = new ViewModels.LauncherPagesCommandPalette(vm);
            e.Handled = true;
        }

        private void OnCloseCommandPalette(object sender, PointerPressedEventArgs e)
        {
            if (e.Source == sender && DataContext is ViewModels.Launcher vm)
                vm.CommandPalette = null;
            e.Handled = true;
        }

        private async void OnShowNewVersion(object sender, RoutedEventArgs e)
        {
            if (DataContext is ViewModels.Launcher { NewVersion: { } ver } vm)
            {
                vm.NewVersion = null;

                var ctx = new ViewModels.SelfUpdate { Data = ver };
                var dialog = new SelfUpdate() { DataContext = ctx };
                await dialog.ShowDialog(this);
            }

            e.Handled = true;
        }

        private GridLength _captionHeight = new(32);
        private WindowState _lastWindowState = WindowState.Normal;

        // How long a WindowState change may wait for its resize before being treated as the
        // phantom flip a modal dialog causes on X11.
        private const int PhantomStateWindowMs = 400;

        private WindowState? _pendingStateNew = null;
        private WindowState _pendingStateOld = WindowState.Normal;
        private int _stateCheckGeneration = 0;
        private bool _revertingState = false;

        // A phantom restore detected while a modal dialog held the owner: applied once the
        // dialog closes and the owner is activated again.
        private WindowState? _phantomRestoreTo = null;
    }
}

