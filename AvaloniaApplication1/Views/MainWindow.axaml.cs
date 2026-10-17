using System;
using System.Collections.Generic;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Platform.Storage;
using AvaloniaApplication1.ViewModels;

namespace AvaloniaApplication1.Views;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        DragDrop.SetAllowDrop(this, true);
        DragDrop.AddDragOverHandler(this, OnDragOver);
        DragDrop.AddDropHandler(this, OnDrop);
    }

    private void OnDragOver(object? sender, DragEventArgs e)
    {
        var canAcceptDrop = DataContext is MainWindowViewModel viewModel
            && !viewModel.IsProcessing
            && e.DataTransfer.Contains(DataFormat.File);
        e.DragEffects = canAcceptDrop
            ? DragDropEffects.Copy
            : DragDropEffects.None;
        e.Handled = true;
    }

    private void OnDrop(object? sender, DragEventArgs e)
    {
        e.Handled = true;
        if (DataContext is not MainWindowViewModel viewModel)
            return;

        IStorageItem[]? items = null;
        try
        {
            items = e.DataTransfer.TryGetFiles();
            if (items is null || items.Length == 0)
            {
                viewModel.ReportDropError("The drop did not contain local files or folders.");
                return;
            }

            var paths = new List<string>(items.Length);
            foreach (var item in items)
            {
                var path = item.TryGetLocalPath();
                if (path is null)
                    throw new InvalidOperationException($"'{item.Name}' is not available as a local file-system path.");
                paths.Add(path);
            }

            viewModel.AcceptDroppedPaths(paths);
        }
        catch (Exception ex)
        {
            viewModel.ReportDropError(ex.Message);
        }
        finally
        {
            if (items is not null)
            {
                foreach (var item in items)
                    item.Dispose();
            }
        }
    }
}