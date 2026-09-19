using DebugCapture.Models;
using DebugCapture.Services;
using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;

namespace DebugCapture.ToolWindows;

public partial class SnapshotToolWindowControl : UserControl, IDisposable
{
    private readonly SnapshotToolWindowViewModel viewModel = new();
    private readonly SnapshotContextMenuService contextMenuService = new();
    private ListView? snapshotListView;
    private Grid? snapshotContentGrid;
    private GridSplitter? snapshotLayoutSplitter;
    private ScrollViewer? snapshotDetailsScrollViewer;
    private bool layoutControlsInitialized;
    private bool isSynchronizingSelection;
    private bool disposed;

    public SnapshotToolWindowControl()
    {
        this.InitializeComponent();
        this.DataContext = this.viewModel;
        this.Loaded += this.SnapshotToolWindowControl_Loaded;
        this.Unloaded += this.SnapshotToolWindowControl_Unloaded;
        this.IsVisibleChanged += this.SnapshotToolWindowControl_IsVisibleChanged;
    }

    private async void SnapshotToolWindowControl_Loaded(object sender, RoutedEventArgs e)
    {
        this.viewModel.IsWindowVisible = this.IsVisible;

        if (this.disposed)
        {
            return;
        }

        try
        {
            await this.viewModel.InitializeAsync().ConfigureAwait(true);
            this.InitializeLayoutControls();
            this.ApplySnapshotLayout();
        }
        catch (ObjectDisposedException)
        {
        }
    }

    private void SnapshotToolWindowControl_Unloaded(object sender, RoutedEventArgs e)
    {
        this.viewModel.IsWindowVisible = false;
    }

    private void SnapshotToolWindowControl_IsVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        this.viewModel.IsWindowVisible = this.IsVisible;
    }

    public void Dispose()
    {
        if (this.disposed)
        {
            return;
        }

        this.disposed = true;
        this.viewModel.Dispose();
    }

    private void SnapshotList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (this.isSynchronizingSelection)
        {
            return;
        }

        this.isSynchronizingSelection = true;
        try
        {
            this.snapshotListView = sender as ListView;
            this.viewModel.SelectSnapshot(this.snapshotListView?.SelectedItem as SnapshotListItem);
        }
        finally
        {
            this.isSynchronizingSelection = false;
        }

        this.ScrollSelectedSnapshotIntoView();
    }

    private void SnapshotSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (this.isSynchronizingSelection)
        {
            return;
        }

        this.isSynchronizingSelection = true;
        try
        {
            this.viewModel.SelectSnapshotByIndex(Convert.ToInt32(e.NewValue));
        }
        finally
        {
            this.isSynchronizingSelection = false;
        }

        this.ScrollSelectedSnapshotIntoView();
    }

    private void OpenSource_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is SnapshotListItem item)
        {
            this.viewModel.SelectSnapshot(item);
        }

        if (this.viewModel.OpenSourceCommand.CanExecute(null))
        {
            this.viewModel.OpenSourceCommand.Execute(null);
        }
    }

    private void OpenInExplorer_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is SnapshotListItem item)
        {
            this.viewModel.SelectSnapshot(item);
        }

        if (this.viewModel.OpenInExplorerCommand.CanExecute(null))
        {
            this.viewModel.OpenInExplorerCommand.Execute(null);
        }
    }

    private void ScrollSelectedSnapshotIntoView()
    {
        if (this.viewModel.State.SelectedSnapshot is not null && this.snapshotListView is not null)
        {
            this.snapshotListView.ScrollIntoView(this.viewModel.State.SelectedSnapshot);
        }
    }

    private void SnapshotDetailsScrollViewer_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (sender is not ScrollViewer scrollViewer)
        {
            return;
        }

        if (e.Delta < 0)
        {
            scrollViewer.LineDown();
        }
        else
        {
            scrollViewer.LineUp();
        }

        e.Handled = true;
    }

    private void ToggleLayout_Click(object sender, RoutedEventArgs e)
    {
        if (this.viewModel.ToggleLayoutCommand.CanExecute(null))
        {
            this.viewModel.ToggleLayoutCommand.Execute(null);
        }

        this.ApplySnapshotLayout();
    }

    private void InitializeLayoutControls()
    {
        if (this.layoutControlsInitialized || this.Content is not DockPanel dockPanel || dockPanel.Children.Count < 2)
        {
            return;
        }

        if (dockPanel.Children[0] is Border { Child: Grid toolbarGrid })
        {
            this.AddLayoutToggleButton(toolbarGrid);
        }

        this.snapshotContentGrid = dockPanel.Children[1] as Grid;
        if (this.snapshotContentGrid is not null)
        {
            foreach (UIElement child in this.snapshotContentGrid.Children)
            {
                switch (child)
                {
                    case ListView listView:
                        this.snapshotListView = listView;
                        break;
                    case GridSplitter splitter:
                        this.snapshotLayoutSplitter = splitter;
                        break;
                    case ScrollViewer scrollViewer:
                        this.snapshotDetailsScrollViewer = scrollViewer;
                        break;
                }
            }
        }

        this.layoutControlsInitialized = true;
    }

    private void AddLayoutToggleButton(Grid toolbarGrid)
    {
        foreach (UIElement child in toolbarGrid.Children)
        {
            if (child is Button { Content: "↔" })
            {
                return;
            }
        }

        var refreshButtonColumn = toolbarGrid.ColumnDefinitions.Count - 1;
        toolbarGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(36) });

        foreach (UIElement child in toolbarGrid.Children)
        {
            if (child is Button { Content: "⟳" })
            {
                Grid.SetColumn(child, refreshButtonColumn + 1);
                break;
            }
        }

        var layoutButton = new Button { Margin = new Thickness(0, 0, 4, 0) };
        layoutButton.SetBinding(ContentControl.ContentProperty, new Binding(nameof(SnapshotToolWindowViewModel.LayoutToggleText)));
        layoutButton.SetBinding(ToolTipProperty, new Binding(nameof(SnapshotToolWindowViewModel.LayoutToggleToolTip)));
        layoutButton.Click += this.ToggleLayout_Click;
        Grid.SetColumn(layoutButton, refreshButtonColumn);
        toolbarGrid.Children.Add(layoutButton);
    }

    private void ApplySnapshotLayout()
    {
        if (this.snapshotContentGrid is null || this.snapshotListView is null || this.snapshotLayoutSplitter is null || this.snapshotDetailsScrollViewer is null)
        {
            return;
        }

        this.EnsureLayoutColumns();

        if (this.viewModel.State.IsSideBySideLayout)
        {
            this.ApplySideBySideLayout();
        }
        else
        {
            this.ApplyStackedLayout();
        }
    }

    private void EnsureLayoutColumns()
    {
        if (this.snapshotContentGrid is null || this.snapshotContentGrid.ColumnDefinitions.Count > 0)
        {
            return;
        }

        this.snapshotContentGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(330), MinWidth = 220 });
        this.snapshotContentGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(4) });
        this.snapshotContentGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
    }

    private void ApplySideBySideLayout()
    {
        Grid.SetRow(this.snapshotListView, 0);
        Grid.SetRowSpan(this.snapshotListView, 3);
        Grid.SetColumn(this.snapshotListView, 0);
        Grid.SetColumnSpan(this.snapshotListView, 1);

        Grid.SetRow(this.snapshotLayoutSplitter, 0);
        Grid.SetRowSpan(this.snapshotLayoutSplitter, 3);
        Grid.SetColumn(this.snapshotLayoutSplitter, 1);
        Grid.SetColumnSpan(this.snapshotLayoutSplitter, 1);
        this.snapshotLayoutSplitter.Width = 4;
        this.snapshotLayoutSplitter.Height = double.NaN;
        this.snapshotLayoutSplitter.HorizontalAlignment = HorizontalAlignment.Stretch;
        this.snapshotLayoutSplitter.ResizeDirection = GridResizeDirection.Columns;

        Grid.SetRow(this.snapshotDetailsScrollViewer, 0);
        Grid.SetRowSpan(this.snapshotDetailsScrollViewer, 3);
        Grid.SetColumn(this.snapshotDetailsScrollViewer, 2);
        Grid.SetColumnSpan(this.snapshotDetailsScrollViewer, 1);
    }

    private void ApplyStackedLayout()
    {
        Grid.SetRow(this.snapshotListView, 0);
        Grid.SetRowSpan(this.snapshotListView, 1);
        Grid.SetColumn(this.snapshotListView, 0);
        Grid.SetColumnSpan(this.snapshotListView, 3);

        Grid.SetRow(this.snapshotLayoutSplitter, 1);
        Grid.SetRowSpan(this.snapshotLayoutSplitter, 1);
        Grid.SetColumn(this.snapshotLayoutSplitter, 0);
        Grid.SetColumnSpan(this.snapshotLayoutSplitter, 3);
        this.snapshotLayoutSplitter.Width = double.NaN;
        this.snapshotLayoutSplitter.Height = 4;
        this.snapshotLayoutSplitter.HorizontalAlignment = HorizontalAlignment.Stretch;
        this.snapshotLayoutSplitter.ResizeDirection = GridResizeDirection.Rows;

        Grid.SetRow(this.snapshotDetailsScrollViewer, 2);
        Grid.SetRowSpan(this.snapshotDetailsScrollViewer, 1);
        Grid.SetColumn(this.snapshotDetailsScrollViewer, 0);
        Grid.SetColumnSpan(this.snapshotDetailsScrollViewer, 3);
    }

    private void ListViewItem_ContextMenuOpening(object sender, ContextMenuEventArgs e)
    {
        if (sender is not FrameworkElement element)
        {
            return;
        }

        switch (element.DataContext)
        {
            case SnapshotListItem snapshotItem:
                this.viewModel.SelectSnapshot(snapshotItem);
                element.ContextMenu = this.contextMenuService.CreateSnapshotContextMenu(snapshotItem);
                break;

            case SnapshotCallStackFrame frame:
                element.ContextMenu = this.contextMenuService.CreateCallStackContextMenu(frame, this.viewModel.CallStack);
                break;
        }
    }

    private void TreeViewItem_ContextMenuOpening(object sender, ContextMenuEventArgs e)
    {
        if (sender is not FrameworkElement element)
        {
            return;
        }

        switch (element.DataContext)
        {
            case SnapshotProperty property:
                element.ContextMenu = this.contextMenuService.CreatePropertyContextMenu(property);
                break;

            case SnapshotDetailTreeNode { SnapshotProperty: not null } node:
                element.ContextMenu = this.contextMenuService.CreatePropertyContextMenu(node.SnapshotProperty);
                break;
        }
    }

    private void Exception_ContextMenuOpening(object sender, ContextMenuEventArgs e)
    {
        if (sender is FrameworkElement element && this.viewModel.SelectedSnapshot is not null)
        {
            element.ContextMenu = this.contextMenuService.CreateExceptionContextMenu(this.viewModel.SelectedSnapshot);
        }
    }
}
