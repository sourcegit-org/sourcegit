using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;

using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Avalonia.VisualTree;

namespace SourceGit.Views
{
    public partial class ProjectTree : UserControl
    {
        public ProjectTree()
        {
            InitializeComponent();

            // Without this the bindings below resolve against the inherited DataContext
            // (Launcher's view model), so the tree renders empty even when Projects is full.
            DataContext = ViewModels.Projects.Instance;

            Tree.PropertyChanged += OnTreePropertyChanged;

            Avalonia.Threading.Dispatcher.UIThread.Post(() =>
            {
                ViewModels.Projects.Instance.StartRestore();
                TryHookLauncher();
            });

            AttachedToVisualTree += OnAttachedToVisualTree;
            DetachedFromVisualTree += OnDetachedFromVisualTree;
        }

        /// <summary>
        ///     The active tab is owned by the launcher, so the tree needs a reference to it
        ///     to follow tab switches. App.GetLauncher() can still be null while the launcher
        ///     window is being built, so this is retried when the control enters the visual tree.
        /// </summary>
        private void TryHookLauncher()
        {
            HookWindow();

            if (_launcher != null)
                return;

            _launcher = App.GetLauncher();
            if (_launcher == null)
                return;

            _launcher.PropertyChanged += OnLauncherPropertyChanged;
            ViewModels.Projects.Instance.HookLauncher();
            SyncSelection();
        }

        /// <summary>
        ///     Watches the host window: when the user returns to the app, files may have been
        ///     changed outside SourceGit, so the tree statuses are re-queried.
        /// </summary>
        private void HookWindow()
        {
            var window = TopLevel.GetTopLevel(this) as Window;
            if (window == null || ReferenceEquals(_window, window))
                return;

            if (_window != null)
                _window.Activated -= OnWindowActivated;

            _window = window;
            _window.Activated += OnWindowActivated;
        }

        private void OnWindowActivated(object sender, EventArgs e)
        {
            // Throttled: alt-tabbing or closing a child window fires this repeatedly.
            var now = DateTime.UtcNow;
            if (now - _lastStatusRefresh < TimeSpan.FromSeconds(3))
                return;

            _lastStatusRefresh = now;
            _ = ViewModels.Projects.Instance.RefreshStatusAsync();
        }

        private void OnAttachedToVisualTree(object sender, VisualTreeAttachmentEventArgs e)
        {
            TryHookLauncher();
        }

        private void OnDetachedFromVisualTree(object sender, VisualTreeAttachmentEventArgs e)
        {
            if (_window != null)
            {
                _window.Activated -= OnWindowActivated;
                _window = null;
            }

            if (_launcher == null)
                return;

            _launcher.PropertyChanged -= OnLauncherPropertyChanged;
            _launcher = null;
        }

        private void OnLauncherPropertyChanged(object sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(ViewModels.Launcher.ActivePage))
                SyncSelection();
        }

        /// <summary>
        ///     Follows the active tab: highlights the repository in the tree and expands the
        ///     directories on the way to it. A welcome tab (its node id is just a generated
        ///     id) or a repository that belongs to no project clears the selection instead.
        /// </summary>
        private void SyncSelection()
        {
            var node = App.GetLauncher()?.ActivePage?.Node;
            if (node == null)
                return;

            var target = ViewModels.Projects.Instance.FindRepository(node.Id);
            if (target == null)
            {
                Tree.SelectedItem = null;
                return;
            }

            ViewModels.Projects.Instance.RevealRepository(target);
            Tree.SelectedItem = target;
            ScrollTo(target);
        }

        /// <summary>
        ///     TreeView has no ScrollIntoView in Avalonia, so the container of the node is
        ///     looked up after the pending layout pass and asked to bring itself into view.
        /// </summary>
        private void ScrollTo(ViewModels.ProjectTreeNode target)
        {
            Avalonia.Threading.Dispatcher.UIThread.Post(() =>
            {
                foreach (var item in Tree.GetVisualDescendants().OfType<TreeViewItem>())
                {
                    if (!ReferenceEquals(item.DataContext, target))
                        continue;

                    item.BringIntoView();
                    break;
                }
            }, Avalonia.Threading.DispatcherPriority.Background);
        }

        private async void OnAddProject(object sender, RoutedEventArgs e)
        {
            await AddProjectAsync();
        }

        private async System.Threading.Tasks.Task AddProjectAsync()
        {
            var dirs = await PickDirectoriesAsync("选择工程目录");
            if (dirs.Count == 0)
                return;

            var node = ViewModels.Projects.Instance.CreateProject(dirs);
            if (node == null)
                return;

            Tree.SelectedItem = node;
            await ViewModels.Projects.Instance.RebuildAsync(node);
        }

        /// <summary>
        ///     Blank-area counterpart of the "+ 工程" button: takes the path the user already
        ///     copied elsewhere (explorer, terminal, an issue) instead of opening a picker.
        /// </summary>
        private async System.Threading.Tasks.Task AddProjectFromClipboardAsync()
        {
            var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
            if (clipboard == null)
                return;

            var text = await clipboard.TryGetTextAsync();
            if (string.IsNullOrWhiteSpace(text))
            {
                ViewModels.Projects.Instance.StatusText = "剪贴板里没有可用的目录路径";
                return;
            }

            var candidates = text
                .Split(new[] { '\r', '\n', ';', '"' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(line => line.Trim())
                .Where(line => line.Length > 0 && Directory.Exists(line))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            if (candidates.Count == 0)
            {
                ViewModels.Projects.Instance.StatusText = "剪贴板里没有可用的目录路径";
                return;
            }

            var node = ViewModels.Projects.Instance.CreateProject(candidates);
            if (node == null)
                return;

            Tree.SelectedItem = node;
            await ViewModels.Projects.Instance.RebuildAsync(node);
        }

        private async System.Threading.Tasks.Task RescanAllAsync()
        {
            foreach (var root in ViewModels.Projects.Instance.Roots.ToList())
                await ViewModels.Projects.Instance.RebuildAsync(root);
        }

        private async void OnAddDirectory(object sender, RoutedEventArgs e)
        {
            var projectNode = FindProjectNode(Tree.SelectedItem as ViewModels.ProjectTreeNode);
            if (projectNode == null)
            {
                ViewModels.Projects.Instance.StatusText = "请先选中一个工程，再添加目录";
                return;
            }

            await AddDirectoryToAsync(projectNode);
        }

        private async void OnRescan(object sender, RoutedEventArgs e)
        {
            var projectNode = FindProjectNode(Tree.SelectedItem as ViewModels.ProjectTreeNode);
            if (projectNode == null)
            {
                ViewModels.Projects.Instance.StatusText = "请先选中一个工程，再重新扫描";
                return;
            }

            await ViewModels.Projects.Instance.RebuildAsync(projectNode);
        }

        /// <summary>
        ///     Removes exactly what is selected: the project itself, the configured directory
        ///     under it, or one discovered node below that. Nothing on disk is touched.
        /// </summary>
        private async void OnRemoveSelection(object sender, RoutedEventArgs e)
        {
            var node = Tree.SelectedItem as ViewModels.ProjectTreeNode;
            if (node == null)
            {
                ViewModels.Projects.Instance.StatusText = "请先选中要移除的工程、目录或仓库";
                return;
            }

            await RemoveWithConfirmAsync(node);
        }

        /// <summary>
        ///     One removal path shared by the toolbar button and the context menu, so the same
        ///     node can no longer behave differently depending on which entry point is used.
        ///     Bulk removals (a whole project, or a first-level directory that takes its
        ///     repositories with it) ask for confirmation; a single repository does not.
        /// </summary>
        private async System.Threading.Tasks.Task RemoveWithConfirmAsync(ViewModels.ProjectTreeNode node)
        {
            if (node == null)
                return;

            if (node.IsProject)
            {
                var ok = await App.AskConfirmAsync($"移除工程「{node.Name}」？（仅从列表中移除，不删除磁盘文件）");
                if (!ok)
                    return;

                ViewModels.Projects.Instance.RemoveProject(node);
                return;
            }

            if (node.Parent?.IsProject == true)
            {
                var repositories = CountRepositories(node);
                var message = repositories > 0
                    ? $"移除目录「{node.Name}」及其下的 {repositories} 个仓库？（不删除磁盘文件）"
                    : $"移除目录「{node.Name}」？（不删除磁盘文件）";

                var ok = await App.AskConfirmAsync(message);
                if (!ok)
                    return;

                ViewModels.Projects.Instance.RemoveDirectory(node.Parent, node);
                return;
            }

            ViewModels.Projects.Instance.RemoveNode(node);
        }

        private void OnTreePropertyChanged(object sender, AvaloniaPropertyChangedEventArgs e)
        {
            if (e.Property == TreeView.SelectedItemProperty)
                UpdateRemoveButton();
        }

        /// <summary>
        ///     Keeps the toolbar button honest about what it is going to remove, so the same
        ///     button no longer silently deletes a whole project while a repository is selected.
        /// </summary>
        private void UpdateRemoveButton()
        {
            var node = Tree.SelectedItem as ViewModels.ProjectTreeNode;
            RemoveButton.Content = node == null ? "移除" : GetRemoveLabel(node);
        }

        /// <summary>
        ///     The label is derived from the node kind only, so the context menu and the toolbar
        ///     can never disagree about what a click is going to remove.
        /// </summary>
        private static string GetRemoveLabel(ViewModels.ProjectTreeNode node)
        {
            if (node.IsProject)
                return "移除工程";

            if (node.Parent?.IsProject == true)
                return "移除目录";

            return node.IsRepository ? "移除仓库" : "移除目录";
        }

        private static int CountRepositories(ViewModels.ProjectTreeNode node)
        {
            var count = node.IsRepository ? 1 : 0;
            foreach (var child in node.Children)
                count += CountRepositories(child);

            return count;
        }

        private void OnItemDoubleTapped(object sender, TappedEventArgs e)
        {
            if (Tree.SelectedItem is ViewModels.ProjectTreeNode node && node.IsRepository)
                node.Open();
        }

        /// <summary>
        ///     A context menu is attached to the whole TreeView, so a right-click that hits no
        ///     row lands here. Leaving the menu empty made Avalonia open an empty popup, which
        ///     reads as "right-click does nothing"; the blank area now gets its own menu of
        ///     global actions instead.
        /// </summary>
        private void OnContextRequested(object sender, ContextRequestedEventArgs e)
        {
            var item = (e.Source as Visual)?.FindAncestorOfType<TreeViewItem>();
            var node = item?.DataContext as ViewModels.ProjectTreeNode;
            if (node == null)
            {
                BuildBlankContextMenu();
                return;
            }

            Tree.SelectedItem = node;
            BuildContextMenu(node);
        }

        private void BuildContextMenu(ViewModels.ProjectTreeNode node)
        {
            NodeMenu.Items.Clear();

            var projectNode = FindProjectNode(node);

            if (node.IsRepository)
            {
                AddItem("打开", node.Open);
                AddItem("在文件管理器打开", () => Native.OS.OpenInFileManager(node.FullPath));
                AddItem("在终端打开", () => Native.OS.OpenTerminal(node.FullPath));
                AddTaskItem("复制路径", () => CopyPathAsync(node.FullPath));
                AddSeparator();
            }

            if (node.IsProject)
            {
                AddTaskItem("添加目录…", () => AddDirectoryToAsync(node));
                AddTaskItem("重命名…", () => RenameAsync(node));
                AddSeparator();
            }

            // Same three cases as the toolbar: project, first-level directory, everything below.
            // Previously only first-level directories had a removal item at all, so a repository
            // or a nested directory could be removed with the button but not from the menu.
            AddTaskItem(GetRemoveLabel(node), () => RemoveWithConfirmAsync(node));

            if (projectNode != null && !node.IsProject)
            {
                AddSeparator();
                AddTaskItem("重新扫描此工程", () => ViewModels.Projects.Instance.RebuildAsync(projectNode));
            }
        }

        /// <summary>
        ///     Menu for the empty part of the tree. Every action is global and every header says
        ///     so ("全部"), because the selected node may be anything and must not be re-targeted
        ///     by a click that did not land on it. Nothing here removes anything.
        /// </summary>
        private void BuildBlankContextMenu()
        {
            NodeMenu.Items.Clear();

            var projects = ViewModels.Projects.Instance;
            var hasProjects = projects.Roots.Count > 0;

            AddTaskItem("添加工程…", AddProjectAsync);
            AddTaskItem("从剪贴板路径添加工程", AddProjectFromClipboardAsync);
            AddSeparator();
            AddTaskItem("刷新全部状态", () => projects.RefreshStatusAsync(), hasProjects);
            AddTaskItem("重新扫描全部工程", RescanAllAsync, hasProjects);
            AddSeparator();
            AddItem("全部展开", () => projects.SetAllExpanded(true), hasProjects);
            AddItem("全部折叠", () => projects.SetAllExpanded(false), hasProjects);
            AddSeparator();
            AddItem("打开配置目录", () => Native.OS.OpenInFileManager(Native.OS.BasicDirectories.ConfigDir));
        }

        private void AddItem(string header, Action action, bool enabled = true)
        {
            var item = new MenuItem { Header = header, IsEnabled = enabled };
            item.Click += (_, _) => action();
            NodeMenu.Items.Add(item);
        }

        private void AddTaskItem(string header, Func<System.Threading.Tasks.Task> action, bool enabled = true)
        {
            var item = new MenuItem { Header = header, IsEnabled = enabled };
            item.Click += async (_, _) => await action();
            NodeMenu.Items.Add(item);
        }

        private void AddSeparator()
        {
            NodeMenu.Items.Add(new MenuItem { Header = "-", IsEnabled = false });
        }

        private async System.Threading.Tasks.Task AddDirectoryToAsync(ViewModels.ProjectTreeNode projectNode)
        {
            if (projectNode?.Project == null)
                return;

            var dirs = await PickDirectoriesAsync("选择要添加的目录");
            if (dirs.Count == 0)
                return;

            if (ViewModels.Projects.Instance.AddDirectories(projectNode, dirs))
                await ViewModels.Projects.Instance.RebuildAsync(projectNode);
        }

        private async System.Threading.Tasks.Task RenameAsync(ViewModels.ProjectTreeNode projectNode)
        {
            if (projectNode?.Project == null)
                return;

            var owner = TopLevel.GetTopLevel(this) as Window;
            if (owner == null)
                return;

            var dialog = new ProjectRename();
            dialog.SetName(projectNode.Name);

            var result = await dialog.ShowDialog<string>(owner);
            if (!string.IsNullOrEmpty(result))
                ViewModels.Projects.Instance.RenameProject(projectNode, result);
        }

        private async System.Threading.Tasks.Task CopyPathAsync(string path)
        {
            var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
            if (clipboard != null)
                await clipboard.SetTextAsync(path);
        }

        private static ViewModels.ProjectTreeNode FindProjectNode(ViewModels.ProjectTreeNode node)
        {
            var cursor = node;
            while (cursor != null && !cursor.IsProject)
                cursor = cursor.Parent;
            return cursor;
        }

        private async System.Threading.Tasks.Task<List<string>> PickDirectoriesAsync(string title)
        {
            var result = new List<string>();

            var top = TopLevel.GetTopLevel(this);
            if (top?.StorageProvider == null)
                return result;

            var folders = await top.StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
            {
                Title = title,
                AllowMultiple = true,
            });

            foreach (var folder in folders)
            {
                var local = folder.TryGetLocalPath();
                if (!string.IsNullOrEmpty(local))
                    result.Add(local);
            }

            return result;
        }

        private ViewModels.Launcher _launcher = null;
        private Window _window = null;
        private DateTime _lastStatusRefresh = DateTime.MinValue;
    }
}
