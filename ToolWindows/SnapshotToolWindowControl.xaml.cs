using DebugCapture.Models;
using DebugCapture.Services;
using System;
using System.Windows;
using System.Windows.Controls;

namespace DebugCapture.ToolWindows;

public partial class SnapshotToolWindowControl : UserControl
{
    private readonly SnapshotToolWindowViewModel viewModel = new();
    private readonly SnapshotContextMenuService contextMenuService = new();
    private bool isSynchronizingSelection;

    public SnapshotToolWindowControl()
    {
        this.InitializeComponent();
        this.DataContext = this.viewModel;
        this.Loaded += this.SnapshotToolWindowControl_Loaded;
        this.Unloaded += this.SnapshotToolWindowControl_Unloaded;
    }

    private async void SnapshotToolWindowControl_Loaded(object sender, RoutedEventArgs e)
    {
        await this.viewModel.InitializeAsync().ConfigureAwait(true);
    }

    private void SnapshotToolWindowControl_Unloaded(object sender, RoutedEventArgs e)
    {
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
            this.viewModel.SelectSnapshot((sender as ListView)?.SelectedItem as SnapshotListItem);
        }
        finally
        {
            this.isSynchronizingSelection = false;
        }
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
        if (sender is FrameworkElement { DataContext: SnapshotProperty property } element)
        {
            element.ContextMenu = this.contextMenuService.CreatePropertyContextMenu(property);
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
