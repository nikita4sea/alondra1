using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using Alondra1.Core.Tasks;
using Microsoft.Win32;

namespace Alondra1.App.Tasks;

public partial class TodoView : UserControl
{
    private const string TaskFormat = "alondra1.task-ids";
    private const string ListFormat = "alondra1.list-id";
    private Point _dragStart;
    private bool _dragArmed;
    private bool _syncingSelection;
    private bool _restoringSelection;
    private bool _listHadFocus;
    private bool _narrow;

    public TodoView()
    {
        InitializeComponent();
        DataContextChanged += (_, _) =>
        {
            if (Model is not { } model)
                return;
            model.FocusQuickAddRequested += () => QuickAddBox.Focus();
            model.PrintRequested += Print;
            model.TaskOpened += id => Dispatcher.InvokeAsync(() => SelectTaskRow(id));
            model.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName == nameof(TodoViewModel.CurrentKey))
                    SyncSidebarSelection();
                if (e.PropertyName is nameof(TodoViewModel.HasDetail) or nameof(TodoViewModel.IsCalendarView))
                    ApplyResponsiveLayout();
            };
            model.Sidebar.CollectionChanged += (_, _) => SyncSidebarSelection();
            model.ItemsRebuilding += () => _listHadFocus = TaskList.IsKeyboardFocusWithin;
            model.ItemsRebuilt += RestoreSelection;
            TaskGrouping.IsChecked = model.Host.Config.Appearance.GroupTasksByFolder;
            SetAccountGrouping(model.Host.Config.Appearance.GroupTasksByFolder);
        };
    }

    private TodoViewModel? Model => DataContext as TodoViewModel;
    private Window Owner => Window.GetWindow(this);

    // ---------- Keyboard ----------

    /// <summary>Shortcuts while the To-do workspace is open. Called from the main window before its own shortcuts.</summary>
    public bool HandleKey(KeyEventArgs e)
    {
        if (Model is not { IsAvailable: true } model)
            return false;
        var mods = Keyboard.Modifiers;
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        var typing = Keyboard.FocusedElement is TextBox;
        var ids = model.TargetIds();
        switch (mods, key)
        {
            case (ModifierKeys.Control, Key.N):
                QuickAddBox.Focus();
                return true;
            case (ModifierKeys.Control | ModifierKeys.Shift, Key.N):
                NewList_Click(this, new RoutedEventArgs());
                return true;
            case (ModifierKeys.Control, Key.F):
            case (ModifierKeys.Control, Key.E):
                if (!model.IsTaskView)
                    model.Select("smart:All");
                model.RequestSearchFocus();
                return true;
            case (ModifierKeys.Control, Key.Z) when !typing:
                model.UndoCommand.Execute(null);
                return true;
            case (ModifierKeys.Control, Key.P):
                Print();
                return true;
            case (ModifierKeys.Control, Key.Enter) when ids.Count > 0:
                _ = model.ToggleCompleteAsync(ids);
                return true;
            case (ModifierKeys.Control, Key.I) when ids.Count > 0 && !typing:
                _ = model.SetImportantAsync(ids);
                return true;
            case (ModifierKeys.Control, Key.T) when ids.Count > 0:
                _ = model.ToggleMyDayAsync(ids);
                return true;
            case (ModifierKeys.Control, Key.D) when ids.Count > 0 && !typing:
                _ = model.DuplicateAsync(ids);
                return true;
            case (ModifierKeys.None, Key.F2) when model.DetailId is not null:
                DetailTitleBox.Focus();
                DetailTitleBox.SelectAll();
                return true;
            case (ModifierKeys.Alt, Key.Left) when model.IsCalendarView:
                model.CalendarPrevCommand.Execute(null);
                return true;
            case (ModifierKeys.Alt, Key.Right) when model.IsCalendarView:
                model.CalendarNextCommand.Execute(null);
                return true;
            case (ModifierKeys.None, Key.Escape) when model.HasDetail && !typing:
                model.CloseDetailCommand.Execute(null);
                return true;
            case (ModifierKeys.None, Key.F5):
                model.Refresh();
                return true;
        }

        return false;
    }

    private async void TaskList_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (Model is not { } model)
            return;
        var ids = SelectedTaskIds();
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        if (key == Key.Delete && ids.Count > 0)
        {
            e.Handled = true;
            await model.DeleteAsync(ids);
        }
        else if (key == Key.Space && Keyboard.Modifiers == ModifierKeys.None && ids.Count > 0)
        {
            e.Handled = true;
            await model.ToggleCompleteAsync(ids);
        }
        else if (key == Key.Enter && Keyboard.Modifiers == ModifierKeys.None && ids.Count == 1)
        {
            e.Handled = true;
            model.ShowDetail(ids[0]);
            DetailTitleBox.Focus();
        }
        else if (Keyboard.Modifiers == ModifierKeys.Alt && key is Key.Up or Key.Down && ids.Count == 1)
        {
            e.Handled = true;
            await model.NudgeAsync(ids[0], key == Key.Up ? -1 : 1);
            SelectTaskRow(ids[0]);
        }
        else if (key == Key.Apps || (key == Key.F10 && Keyboard.Modifiers == ModifierKeys.Shift))
        {
            e.Handled = true;
            if (TaskList.ContextMenu is { } menu)
            {
                BuildTaskMenu(menu);
                menu.PlacementTarget = TaskList;
                menu.IsOpen = true;
            }
        }
    }

    private void Sidebar_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (SidebarList.SelectedItem is not SidebarItem item)
            return;
        if (e.Key == Key.F2 && item.IsList)
        {
            e.Handled = true;
            RenameList(item.ListId!);
        }
        else if (e.Key == Key.Delete && item.IsList)
        {
            e.Handled = true;
            _ = DeleteList(item.ListId!);
        }
        else if (e.Key is Key.Left or Key.Right && item.IsGroup)
        {
            e.Handled = true;
            var collapse = e.Key == Key.Left;
            if (item.Collapsed != collapse)
                Model?.ToggleGroupCommand.Execute(item);
        }
    }

    // ---------- Sidebar ----------

    private void SidebarList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_syncingSelection || Model is not { } model || SidebarList.SelectedItem is not SidebarItem item)
            return;
        if (item.IsHeader || item.IsGroup)
        {
            SyncSidebarSelection();
            return;
        }

        if (item.Key == model.CurrentKey)
            return;
        if (model.IsSearching)
            model.ClearSearchCommand.Execute(null);
        model.Select(item.Key);
        if (_narrow)
            SidebarToggle.IsChecked = false;
    }

    private void SyncSidebarSelection()
    {
        if (Model is not { } model)
            return;
        _syncingSelection = true;
        SidebarList.SelectedItem = model.Sidebar.FirstOrDefault(item => item.Key == model.CurrentKey);
        _syncingSelection = false;
    }

    private async void NewList_Click(object sender, RoutedEventArgs e)
    {
        if (Model is not { } model)
            return;
        var group = (SidebarList.SelectedItem as SidebarItem)?.GroupId;
        var name = TodoDialogs.Prompt(Owner, "New list", "Name (emoji welcome)", "Untitled list");
        if (name is null)
            return;
        if (await model.AddListAsync(name, group) is { } list)
            model.Select("list:" + list.Id);
    }

    private async void NewGroup_Click(object sender, RoutedEventArgs e)
    {
        if (Model is not { } model)
            return;
        var name = TodoDialogs.Prompt(Owner, "New group", "Group name", "Untitled group");
        if (name is not null)
            await model.AddGroupAsync(name);
    }

    private void Sidebar_ContextMenuOpening(object sender, ContextMenuEventArgs e)
    {
        var menu = SidebarList.ContextMenu!;
        menu.Items.Clear();
        var item = ItemFromSource<SidebarItem>(e.OriginalSource as DependencyObject);
        if (Model is not { } model || item is null)
        {
            e.Handled = true;
            return;
        }

        if (item.IsList && model.Board!.FindList(item.ListId!) is { } list)
        {
            menu.Items.Add(Menu("Rename", () => RenameList(list.Id), "F2"));
            menu.Items.Add(Menu("Color, background, and group…", () => EditList(list.Id)));
            menu.Items.Add(Menu("Duplicate list", () => _ = model.DuplicateListAsync(list.Id)));
            menu.Items.Add(Menu("Print list", () => { model.Select("list:" + list.Id); Print(); }));
            menu.Items.Add(new Separator());
            var delete = Menu("Delete list", () => _ = DeleteList(list.Id), "Del");
            delete.IsEnabled = !list.IsDefault;
            delete.ToolTip = list.IsDefault ? "The Tasks list holds tasks from smart views and cannot be deleted." : null;
            menu.Items.Add(delete);
        }
        else if (item.IsGroup && model.Board!.FindGroup(item.GroupId!) is { } group)
        {
            menu.Items.Add(Menu("New list in this group", async () =>
            {
                var name = TodoDialogs.Prompt(Owner, "New list", "Name", "Untitled list");
                if (name is not null && await model.AddListAsync(name, group.Id) is { } created)
                    model.Select("list:" + created.Id);
            }));
            menu.Items.Add(Menu("Rename group", () =>
            {
                var name = TodoDialogs.Prompt(Owner, "Rename group", "Group name", group.Name);
                if (name is not null)
                    _ = model.RenameGroupAsync(group.Id, name);
            }));
            menu.Items.Add(Menu(group.Collapsed ? "Expand" : "Collapse", () => model.ToggleGroupCommand.Execute(item)));
            menu.Items.Add(new Separator());
            menu.Items.Add(Menu("Ungroup (keep lists)", () => _ = model.DeleteGroupAsync(group.Id)));
        }
        else if (item.Kind == SidebarKind.Smart)
        {
            menu.Items.Add(Menu("Print this view", () => { model.Select(item.Key); Print(); }));
            var counts = new MenuItem { Header = "Show task counts", IsCheckable = true, IsChecked = model.ShowCounts };
            counts.Click += (_, _) => model.SetShowCounts(counts.IsChecked);
            menu.Items.Add(counts);
        }
        else
        {
            e.Handled = true;
        }
    }

    private void RenameList(string id)
    {
        if (Model is not { } model || model.Board!.FindList(id) is not { } list)
            return;
        var name = TodoDialogs.Prompt(Owner, "Rename list", "Name", list.Name);
        if (name is not null)
            _ = model.RenameListAsync(id, name);
    }

    private void EditList(string id)
    {
        if (Model is not { } model || model.Board!.FindList(id) is not { } list)
            return;
        if (TodoDialogs.ListProperties(Owner, list, model.Board.Groups) is { } result)
            _ = model.UpdateListAsync(id, result.Name, result.Color, result.Tint, result.GroupId);
    }

    private async Task DeleteList(string id)
    {
        if (Model is not { } model || model.Board!.FindList(id) is not { } list)
            return;
        if (list.IsDefault)
        {
            model.Toast("The Tasks list cannot be deleted.");
            return;
        }

        var count = model.Board.Tasks.Count(task => task.ListId == id);
        var message = count == 0
            ? $"Delete the list \"{list.Name}\"?"
            : $"Delete the list \"{list.Name}\" and its {count} task{(count == 1 ? "" : "s")}? You can undo this right after.";
        if (TodoDialogs.Confirm(Owner, "Delete list", message, "Delete"))
            await model.DeleteListAsync(id);
    }

    private void SidebarToggle_Changed(object sender, RoutedEventArgs e) => ApplyResponsiveLayout();

    // ---------- Responsive layout ----------

    private void View_SizeChanged(object sender, SizeChangedEventArgs e) => ApplyResponsiveLayout();

    private void ApplyResponsiveLayout()
    {
        var width = ActualWidth;
        if (width < 1 || !IsVisible)
            return; // Collapsed (another workspace is open); lay out when the view is shown and measured.
        _narrow = width < 720;
        var compactDetail = width < 1000;
        SidebarToggle.Visibility = _narrow ? Visibility.Visible : Visibility.Collapsed;
        if (_narrow)
        {
            SidebarColumn.Width = new GridLength(0);
            SidebarPane.Visibility = SidebarToggle.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
            Grid.SetColumnSpan(SidebarPane, 2);
            SidebarPane.Width = Math.Clamp(width - 40, 160, 260);
            SidebarPane.HorizontalAlignment = HorizontalAlignment.Left;
        }
        else
        {
            SidebarToggle.IsChecked = false;
            SidebarColumn.Width = new GridLength(width < 900 ? 180 : 214);
            SidebarPane.Visibility = Visibility.Visible;
            Grid.SetColumnSpan(SidebarPane, 1);
            SidebarPane.Width = double.NaN;
        }

        // On narrow windows the detail pane floats over the list instead of squeezing it.
        if (compactDetail)
        {
            DetailColumn.Width = new GridLength(0);
            Grid.SetColumn(DetailPane, 1);
            DetailPane.HorizontalAlignment = HorizontalAlignment.Right;
            DetailPane.Width = Math.Min(320, Math.Max(240, width - 40));
            DetailPane.Effect = new System.Windows.Media.Effects.DropShadowEffect { BlurRadius = 16, Opacity = 0.25, ShadowDepth = 2 };
        }
        else
        {
            DetailColumn.Width = GridLength.Auto;
            Grid.SetColumn(DetailPane, 2);
            DetailPane.HorizontalAlignment = HorizontalAlignment.Stretch;
            DetailPane.Width = 320;
            DetailPane.Effect = null;
        }

        BacklogColumn.Width = new GridLength(width < 900 ? 0 : 190);
        BacklogPane.Visibility = width < 900 ? Visibility.Collapsed : Visibility.Visible;
    }

    // ---------- Quick add ----------

    private void QuickAddBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            e.Handled = true;
            Model?.QuickAddCommand.Execute(null);
        }
        else if (e.Key == Key.Escape)
        {
            QuickAddBox.Clear();
            TaskList.Focus();
        }
    }

    private void QuickAddBox_TextChanged(object sender, TextChangedEventArgs e) => Model?.UpdateQuickAddHint();

    // ---------- Task list selection, bulk, context menu ----------

    private void TaskList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_restoringSelection || Model is not { IsRebuilding: false } model)
            return;
        model.SetSelection(SelectedTaskIds());
    }

    /// <summary>Reselects the same tasks after the rows were rebuilt, and gives keyboard focus back to the list if it had it.</summary>
    private void RestoreSelection()
    {
        if (Model is not { } model)
            return;
        _restoringSelection = true;
        try
        {
            TaskList.SelectedItems.Clear();
            foreach (var row in model.Items.Where(item => model.SelectedIds.Contains(item.Id)))
                TaskList.SelectedItems.Add(row);
        }
        finally
        {
            _restoringSelection = false;
        }

        if (_listHadFocus)
        {
            _listHadFocus = false;
            var target = TaskList.SelectedItem ?? (TaskList.Items.Count > 0 ? TaskList.Items[0] : null);
            Dispatcher.InvokeAsync(() =>
            {
                if (target is not null && TaskList.ItemContainerGenerator.ContainerFromItem(target) is ListBoxItem container)
                    container.Focus();
                else
                    TaskList.Focus();
            }, System.Windows.Threading.DispatcherPriority.Loaded);
        }
    }

    private List<string> SelectedTaskIds() => TaskList.SelectedItems.OfType<TaskItemVm>().Select(item => item.Id).ToList();

    private void SelectTaskRow(string id)
    {
        if (Model?.Items.FirstOrDefault(item => item.Id == id) is not { } row)
            return;
        TaskList.SelectedItem = row;
        TaskList.ScrollIntoView(row);
        if (TaskList.ItemContainerGenerator.ContainerFromItem(row) is ListBoxItem container)
            container.Focus();
    }

    private async void BulkComplete_Click(object sender, RoutedEventArgs e) { if (Model is { } m) await m.CompleteAsync(SelectedTaskIds(), true); }
    private async void BulkImportant_Click(object sender, RoutedEventArgs e) { if (Model is { } m) await m.SetImportantAsync(SelectedTaskIds()); }
    private async void BulkMyDay_Click(object sender, RoutedEventArgs e) { if (Model is { } m) await m.ToggleMyDayAsync(SelectedTaskIds()); }
    private async void BulkDelete_Click(object sender, RoutedEventArgs e) { if (Model is { } m) await m.DeleteAsync(SelectedTaskIds()); }
    private void ClearSelection_Click(object sender, RoutedEventArgs e) => TaskList.UnselectAll();

    private void BulkMove_Click(object sender, RoutedEventArgs e)
    {
        if (Model is not { } model || BulkMoveButton.ContextMenu is not { } menu)
            return;
        menu.Items.Clear();
        var ids = SelectedTaskIds();
        foreach (var list in model.Board!.Lists)
            menu.Items.Add(Menu(list.Name, () => _ = model.MoveAsync(ids, list.Id)));
        menu.PlacementTarget = BulkMoveButton;
        menu.IsOpen = true;
    }

    private void TaskList_ContextMenuOpening(object sender, ContextMenuEventArgs e)
    {
        var row = ItemFromSource<TaskItemVm>(e.OriginalSource as DependencyObject);
        if (row is null)
        {
            e.Handled = true;
            return;
        }

        if (!TaskList.SelectedItems.Contains(row))
            TaskList.SelectedItem = row;
        BuildTaskMenu(TaskList.ContextMenu!);
    }

    private void BuildTaskMenu(ContextMenu menu)
    {
        menu.Items.Clear();
        if (Model is not { } model)
            return;
        var ids = SelectedTaskIds();
        if (ids.Count == 0)
            return;
        var tasks = ids.Select(id => model.Board!.FindTask(id)).Where(task => task is not null).Cast<TodoTask>().ToList();
        var allDone = tasks.All(task => task.Completed);
        var allImportant = tasks.All(task => task.Important);
        var allMyDay = tasks.All(task => task.MyDayDate == model.Today);
        menu.Items.Add(Menu(allDone ? "Mark as not completed" : "Mark as completed", () => _ = model.CompleteAsync(ids, !allDone), "Ctrl+Enter"));
        menu.Items.Add(Menu(allImportant ? "Remove importance" : "Mark as important", () => _ = model.SetImportantAsync(ids, !allImportant), "Ctrl+I"));
        menu.Items.Add(Menu(allMyDay ? "Remove from My Day" : "Add to My Day", () => _ = model.ToggleMyDayAsync(ids), "Ctrl+T"));
        menu.Items.Add(new Separator());
        menu.Items.Add(Menu("Due today", () => _ = model.SetDueAsync(ids, model.Today)));
        menu.Items.Add(Menu("Due tomorrow", () => _ = model.SetDueAsync(ids, model.Today.AddDays(1))));
        menu.Items.Add(Menu("Due next week", () => _ = model.SetDueAsync(ids, Recurrence.WeekStart(model.Today).AddDays(7))));
        menu.Items.Add(Menu("Remove due date", () => _ = model.SetDueAsync(ids, null)));
        menu.Items.Add(new Separator());
        var move = new MenuItem { Header = "Move to" };
        foreach (var list in model.Board!.Lists)
            move.Items.Add(Menu(list.Name, () => _ = model.MoveAsync(ids, list.Id)));
        menu.Items.Add(move);
        if (model.CanReorder && ids.Count == 1)
        {
            menu.Items.Add(Menu("Move up", () => _ = model.NudgeAsync(ids[0], -1), "Alt+Up"));
            menu.Items.Add(Menu("Move down", () => _ = model.NudgeAsync(ids[0], 1), "Alt+Down"));
        }

        menu.Items.Add(Menu(ids.Count == 1 ? "Duplicate task" : "Duplicate tasks", () => _ = model.DuplicateAsync(ids), "Ctrl+D"));
        menu.Items.Add(new Separator());
        var delete = Menu(ids.Count == 1 ? "Delete task" : $"Delete {ids.Count} tasks", () => _ = model.DeleteAsync(ids), "Del");
        delete.Foreground = (Brush)FindResource("DangerBrush");
        menu.Items.Add(delete);
    }

    // ---------- Drag and drop ----------

    private void TaskList_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        _dragStart = e.GetPosition(null);
        _dragArmed = ItemFromSource<TaskItemVm>(e.OriginalSource as DependencyObject) is not null && !IsInside<ButtonBase>(e.OriginalSource as DependencyObject);
    }

    private void TaskList_PreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (!_dragArmed || e.LeftButton != MouseButtonState.Pressed || !MovedEnough(e.GetPosition(null)))
            return;
        _dragArmed = false;
        var row = ItemFromSource<TaskItemVm>(e.OriginalSource as DependencyObject);
        if (row is null)
            return;
        var ids = TaskList.SelectedItems.Contains(row) ? SelectedTaskIds() : [row.Id];
        DragDrop.DoDragDrop(TaskList, new DataObject(TaskFormat, string.Join("\n", ids)), DragDropEffects.Move);
        DropLine.Visibility = Visibility.Collapsed;
    }

    private void TaskList_DragOver(object sender, DragEventArgs e)
    {
        if (!e.Data.GetDataPresent(TaskFormat) || Model is not { CanReorder: true })
        {
            e.Effects = e.Data.GetDataPresent(TaskFormat) ? DragDropEffects.None : DragDropEffects.None;
            DropLine.Visibility = Visibility.Collapsed;
            e.Handled = true;
            return;
        }

        e.Effects = DragDropEffects.Move;
        var (index, y) = DropIndex(e);
        _ = index;
        DropLine.Margin = new Thickness(0, Math.Max(0, y - 1), 0, 0);
        DropLine.Visibility = Visibility.Visible;
        e.Handled = true;
    }

    private void TaskList_DragLeave(object sender, DragEventArgs e) => DropLine.Visibility = Visibility.Collapsed;

    private async void TaskList_Drop(object sender, DragEventArgs e)
    {
        DropLine.Visibility = Visibility.Collapsed;
        if (Model is not { } model || DraggedTasks(e) is not { Count: 1 } ids)
        {
            if (DraggedTasks(e) is { Count: > 1 })
                Model?.Toast("Drag one task at a time to reorder.");
            return;
        }

        var (index, _) = DropIndex(e);
        var current = model.Items.ToList().FindIndex(item => item.Id == ids[0]);
        if (current >= 0 && index > current)
            index--;
        await model.ReorderAsync(ids[0], index);
        SelectTaskRow(ids[0]);
    }

    private (int Index, double Y) DropIndex(DragEventArgs e)
    {
        var count = TaskList.Items.Count;
        for (var i = 0; i < count; i++)
        {
            if (TaskList.ItemContainerGenerator.ContainerFromIndex(i) is not ListBoxItem container)
                continue;
            var top = container.TranslatePoint(new Point(0, 0), TaskList).Y;
            var position = e.GetPosition(container).Y;
            if (position < container.ActualHeight / 2)
                return (i, top);
        }

        if (count > 0 && TaskList.ItemContainerGenerator.ContainerFromIndex(count - 1) is ListBoxItem last)
            return (count, last.TranslatePoint(new Point(0, last.ActualHeight), TaskList).Y);
        return (count, 0);
    }

    private void Sidebar_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        _dragStart = e.GetPosition(null);
        _dragArmed = ItemFromSource<SidebarItem>(e.OriginalSource as DependencyObject) is { IsList: true };
    }

    private void Sidebar_PreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (!_dragArmed || e.LeftButton != MouseButtonState.Pressed || !MovedEnough(e.GetPosition(null)))
            return;
        _dragArmed = false;
        if (ItemFromSource<SidebarItem>(e.OriginalSource as DependencyObject) is { IsList: true, ListId: { } listId })
            DragDrop.DoDragDrop(SidebarList, new DataObject(ListFormat, listId), DragDropEffects.Move);
    }

    private void Sidebar_DragOver(object sender, DragEventArgs e)
    {
        var target = ItemFromSource<SidebarItem>(e.OriginalSource as DependencyObject);
        e.Effects = DragDropEffects.None;
        if (target is not null && e.Data.GetDataPresent(TaskFormat))
        {
            if (target.IsList || target.Key is "smart:MyDay" or "smart:Important" or "smart:Completed")
                e.Effects = DragDropEffects.Move;
        }
        else if (target is not null && e.Data.GetDataPresent(ListFormat) && (target.IsList || target.IsGroup))
        {
            e.Effects = DragDropEffects.Move;
        }

        e.Handled = true;
    }

    private async void Sidebar_Drop(object sender, DragEventArgs e)
    {
        if (Model is not { } model || ItemFromSource<SidebarItem>(e.OriginalSource as DependencyObject) is not { } target)
            return;
        if (DraggedTasks(e) is { Count: > 0 } ids)
        {
            switch (target.Key)
            {
                case "smart:MyDay":
                    if (!ids.All(id => model.Board!.FindTask(id)?.MyDayDate == model.Today))
                        await model.ToggleMyDayAsync(ids);
                    break;
                case "smart:Important":
                    await model.SetImportantAsync(ids, true);
                    break;
                case "smart:Completed":
                    await model.CompleteAsync(ids, true);
                    break;
                default:
                    if (target.ListId is { } listId)
                        await model.MoveAsync(ids, listId);
                    break;
            }
        }
        else if (e.Data.GetData(ListFormat) is string listId && listId != target.ListId)
        {
            var group = target.IsGroup ? target.GroupId : target.GroupId;
            var siblings = model.Board!.Lists.Where(list => list.GroupId == group && list.Id != listId).Select(list => list.Id).ToList();
            var index = target.IsGroup ? siblings.Count : Math.Max(0, siblings.IndexOf(target.ListId!));
            await model.MoveListAsync(listId, group, index);
        }
    }

    private void Chip_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        _dragStart = e.GetPosition(null);
        _dragArmed = true;
        if (sender is FrameworkElement { Tag: string id })
        {
            Model?.ShowDetail(id);
            e.Handled = true;
        }
    }

    private void Chip_MouseMove(object sender, MouseEventArgs e)
    {
        if (!_dragArmed || e.LeftButton != MouseButtonState.Pressed || !MovedEnough(e.GetPosition(null)) || sender is not FrameworkElement { Tag: string id } element)
            return;
        _dragArmed = false;
        DragDrop.DoDragDrop(element, new DataObject(TaskFormat, id), DragDropEffects.Move);
    }

    private void CalendarCell_DragOver(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(TaskFormat) ? DragDropEffects.Move : DragDropEffects.None;
        if (sender is Border border && e.Effects != DragDropEffects.None)
            border.SetResourceReference(Border.BorderBrushProperty, "BlueBrush");
        e.Handled = true;
    }

    private void CalendarCell_DragLeave(object sender, DragEventArgs e)
    {
        if (sender is Border border)
            border.ClearValue(Border.BorderBrushProperty);
    }

    private async void CalendarCell_Drop(object sender, DragEventArgs e)
    {
        CalendarCell_DragLeave(sender, e);
        if (Model is not { } model || sender is not FrameworkElement { Tag: CalendarCellVm cell } || DraggedTasks(e) is not { Count: > 0 } ids)
            return;
        foreach (var id in ids)
            await model.RescheduleAsync(id, cell.Date);
        e.Handled = true;
    }

    private void DaySlot_DragOver(object sender, DragEventArgs e) => CalendarCell_DragOver(sender, e);

    private async void DaySlot_Drop(object sender, DragEventArgs e)
    {
        CalendarCell_DragLeave(sender, e);
        if (Model is not { } model || sender is not FrameworkElement { Tag: DaySlotVm slot } || DraggedTasks(e) is not { Count: > 0 } ids)
            return;
        var day = model.CalendarCells.FirstOrDefault()?.Date ?? model.Today;
        foreach (var id in ids)
            await model.RescheduleAsync(id, day, slot.Hour, allDay: slot.Hour is null);
        e.Handled = true;
    }

    private void CalendarCell_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (Model is not { } model || sender is not FrameworkElement { Tag: CalendarCellVm cell } element)
            return;
        element.Focus();
        if (e.ClickCount == 2)
            model.OpenDayCommand.Execute(cell);
        else
            model.SelectCalendarDateCommand.Execute(cell);
    }

    private void CalendarCell_KeyDown(object sender, KeyEventArgs e)
    {
        if (Model is not { } model || sender is not FrameworkElement { Tag: CalendarCellVm cell })
            return;
        var delta = e.Key switch { Key.Left => -1, Key.Right => 1, Key.Up => -7, Key.Down => 7, _ => 0 };
        if (e.Key == Key.Enter)
        {
            model.OpenDayCommand.Execute(cell);
            e.Handled = true;
        }
        else if (delta != 0)
        {
            var target = model.CalendarCells.FirstOrDefault(item => item.Date == cell.Date.AddDays(delta));
            if (target is not null)
            {
                model.SelectCalendarDateCommand.Execute(target);
                Dispatcher.InvokeAsync(() => FocusCalendarCell(target.Date));
            }

            e.Handled = true;
        }
    }

    private void FocusCalendarCell(DateOnly date)
    {
        if (Model?.CalendarCells.FirstOrDefault(item => item.Date == date) is not { } cell)
            return;
        if (CalendarGrid.ItemContainerGenerator.ContainerFromItem(cell) is ContentPresenter presenter && VisualTreeHelper.GetChildrenCount(presenter) > 0 && VisualTreeHelper.GetChild(presenter, 0) is UIElement border)
            border.Focus();
    }

    private void DetailPane_DragOver(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private async void DetailPane_Drop(object sender, DragEventArgs e)
    {
        if (Model is { } model && e.Data.GetData(DataFormats.FileDrop) is string[] files)
            await model.AttachFilesAsync(files.Where(File.Exists));
    }

    private static List<string>? DraggedTasks(DragEventArgs e) =>
        e.Data.GetData(TaskFormat) is string text ? text.Split('\n', StringSplitOptions.RemoveEmptyEntries).ToList() : null;

    private bool MovedEnough(Point current) =>
        Math.Abs(current.X - _dragStart.X) > SystemParameters.MinimumHorizontalDragDistance ||
        Math.Abs(current.Y - _dragStart.Y) > SystemParameters.MinimumVerticalDragDistance;

    // ---------- Detail editing ----------

    private void DetailTitle_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (DetailTitleBox.IsKeyboardFocusWithin)
            Model?.MarkTitleEdited();
    }

    private async void DetailTitle_LostFocus(object sender, RoutedEventArgs e) { if (Model is { } m) await m.CommitTitleAsync(); }

    private async void DetailTitle_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && Model is { } model)
        {
            e.Handled = true;
            await model.CommitTitleAsync();
        }
    }

    private void Notes_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (NotesBox.IsKeyboardFocusWithin)
            Model?.MarkNotesEdited();
    }

    private async void Notes_LostFocus(object sender, RoutedEventArgs e) { if (Model is { } m) await m.CommitNotesAsync(); }

    private async void Due_LostFocus(object sender, RoutedEventArgs e) { if (Model is { } m) await m.CommitDueAsync(); }
    private async void Time_LostFocus(object sender, RoutedEventArgs e) { if (Model is { } m) await m.CommitTimeAsync(); }
    private async void Start_LostFocus(object sender, RoutedEventArgs e) { if (Model is { } m) await m.CommitStartAsync(); }

    private void CommitOnEnter(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && sender is TextBox box)
        {
            e.Handled = true;
            box.GetBindingExpression(TextBox.TextProperty)?.UpdateSource();
            Keyboard.ClearFocus();
            box.Focus();
            box.RaiseEvent(new RoutedEventArgs(LostFocusEvent, box));
        }
    }

    private async void NewStep_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && Model is { } model)
        {
            e.Handled = true;
            await model.AddStepCommand.ExecuteAsync(null);
            NewStepBox.Focus();
        }
    }

    private async void Step_LostFocus(object sender, RoutedEventArgs e)
    {
        if (Model is { } model && sender is TextBox { Tag: string id } box && box.DataContext is SubtaskVm step && box.Text != step.Title)
            await model.RenameStepAsync(id, box.Text);
    }

    private void Step_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && sender is TextBox box)
        {
            e.Handled = true;
            box.RaiseEvent(new RoutedEventArgs(LostFocusEvent, box));
            NewStepBox.Focus();
        }
    }

    private async void RepeatBox_DropDownClosed(object sender, EventArgs e)
    {
        if (Model is not { } model || RepeatBox.SelectedIndex < 0 || RepeatBox.SelectedIndex == model.DetailRecurrenceIndex && RepeatBox.SelectedIndex != 6)
            return;
        await model.SetRecurrenceAsync(RepeatBox.SelectedIndex, current => TodoDialogs.CustomRecurrence(Owner, current));
    }

    private async void DetailListBox_DropDownClosed(object sender, EventArgs e)
    {
        if (Model is { } model)
            await model.MoveDetailToListAsync(DetailListBox.SelectedItem as ListChoice);
    }

    private void AddReminder_Click(object sender, RoutedEventArgs e)
    {
        if (Model is not { } model || AddReminderButton.ContextMenu is not { } menu || model.Board!.FindTask(model.DetailId ?? "") is not { } task)
            return;
        menu.Items.Clear();
        var now = model.Board.LocalNow;
        menu.Items.Add(Menu("Later today (" + TodoFormat.Time(TimeOnly.FromDateTime(new DateTime(now.Year, now.Month, now.Day, now.Hour, 0, 0).AddHours(3))) + ")", () => model.AddReminderCommand.Execute("later")));
        menu.Items.Add(Menu("Tomorrow (" + TodoFormat.Time(new TimeOnly(9, 0)) + ")", () => model.AddReminderCommand.Execute("tomorrow")));
        menu.Items.Add(Menu("Next week", () => model.AddReminderCommand.Execute("nextweek")));
        if (task.DueDate is not null)
            menu.Items.Add(Menu("At the due time", () => model.AddReminderCommand.Execute("due")));
        menu.Items.Add(new Separator());
        menu.Items.Add(Menu("Pick a date and time…", () =>
        {
            if (TodoDialogs.CustomReminder(Owner, now.Date.AddDays(1).AddHours(9)) is { } at)
                _ = model.AddReminderAsync(at);
        }));
        menu.PlacementTarget = AddReminderButton;
        menu.IsOpen = true;
    }

    private async void AddFile_Click(object sender, RoutedEventArgs e)
    {
        if (Model is not { } model)
            return;
        var dialog = new OpenFileDialog { Title = "Attach files", Multiselect = true };
        if (dialog.ShowDialog(Owner) == true)
            await model.AttachFilesAsync(dialog.FileNames);
    }

    private void OpenAttachment_Click(object sender, RoutedEventArgs e)
    {
        if (Model is not { } model || sender is not FrameworkElement { Tag: string id })
            return;
        var path = model.ResolveAttachment(id);
        if (path is null)
        {
            model.Error("The attached file is missing from Alondra1's data folder.");
            return;
        }

        try
        {
            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or IOException)
        {
            model.Error("Windows could not open the file: " + ex.Message);
        }
    }

    private async void RemoveAttachment_Click(object sender, RoutedEventArgs e)
    {
        if (Model is { } model && sender is FrameworkElement { Tag: string id })
            await model.RemoveAttachmentAsync(id);
    }

    private async void DetailDelete_Click(object sender, RoutedEventArgs e)
    {
        if (Model is { DetailId: { } id } model)
            await model.DeleteAsync([id]);
    }

    private async void DetailDuplicate_Click(object sender, RoutedEventArgs e)
    {
        if (Model is { DetailId: { } id } model)
            await model.DuplicateAsync([id]);
    }

    // ---------- Account tasks (existing provider view) ----------

    private void TaskGrouping_Changed(object sender, RoutedEventArgs e)
    {
        if (!IsLoaded || Model is not { } model)
            return;
        var enabled = TaskGrouping.IsChecked == true;
        model.Host.Config.Appearance.GroupTasksByFolder = enabled;
        SetAccountGrouping(enabled);
        model.Host.ConfigStoreSave();
    }

    private void SetAccountGrouping(bool enabled)
    {
        if (Model is not { } model)
            return;
        var view = CollectionViewSource.GetDefaultView(model.Host.Tasks);
        using var refresh = view.DeferRefresh();
        view.GroupDescriptions.Clear();
        if (enabled)
            view.GroupDescriptions.Add(new PropertyGroupDescription(nameof(ViewModels.TaskRow.Folder)));
    }

    // ---------- Print ----------

    public void Print()
    {
        if (Model is not { } model)
            return;
        var tasks = model.Items.Select(item => model.Board!.FindTask(item.Id)).Where(task => task is not null).Cast<TodoTask>().ToList();
        if (model.IsCalendarView)
        {
            var dates = model.CalendarCells.Select(cell => cell.Date).ToHashSet();
            tasks = model.Board!.Tasks.Where(task => task.DueDate is { } due && dates.Contains(due)).OrderBy(task => task.DueDate).ThenBy(task => task.DueTime).ToList();
        }

        TodoPrinter.Print(Owner, model.IsCalendarView ? model.CalendarTitle : model.ViewTitle, tasks, model.Board!, model.Today);
    }

    // ---------- Helpers ----------

    private static MenuItem Menu(string header, Action action, string? gesture = null)
    {
        var item = new MenuItem { Header = header, InputGestureText = gesture ?? "" };
        item.Click += (_, _) => action();
        return item;
    }

    private static MenuItem Menu(string header, Func<Task> action)
    {
        var item = new MenuItem { Header = header };
        item.Click += async (_, _) => await action();
        return item;
    }

    private static T? ItemFromSource<T>(DependencyObject? source) where T : class
    {
        while (source is not null)
        {
            if (source is FrameworkElement { DataContext: T item } and (ListBoxItem or ContentPresenter or Border or Grid))
                return item;
            source = source is Visual or System.Windows.Media.Media3D.Visual3D ? VisualTreeHelper.GetParent(source) : LogicalTreeHelper.GetParent(source);
        }

        return null;
    }

    private static bool IsInside<T>(DependencyObject? source) where T : DependencyObject
    {
        while (source is not null)
        {
            if (source is T)
                return true;
            source = source is Visual ? VisualTreeHelper.GetParent(source) : LogicalTreeHelper.GetParent(source);
        }

        return false;
    }
}
