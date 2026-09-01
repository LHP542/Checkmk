using System.Collections.Generic;
using System.Linq;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Checkmk.App;
using Checkmk.App.Controls;
using Checkmk.App.Services;
using Checkmk.App.ViewModels;
using Checkmk.Core.Models;
using Microsoft.Extensions.DependencyInjection;

namespace Checkmk.App.Views;

public partial class HostDetailWindow : ChromeWindow
{
    private readonly HostDetailViewModel? _vm;

    public HostDetailWindow(HostDetailViewModel vm)
    {
        AvaloniaXamlLoader.Load(this);
        _vm = vm;
        DataContext = vm;
        // Initial-Load, sobald das Fenster steht. AutoLoad ist nur im
        // Screenshot-Werkzeug aus — dort sind die Daten schon gesetzt und
        // duerfen nicht ueberschrieben werden.
        Opened += async (_, _) =>
        {
            if (vm.AutoLoad) await vm.RefreshAsync();
        };
    }

    // Parameterloser ctor nur fuer den XAML-Designer.
    public HostDetailWindow() => AvaloniaXamlLoader.Load(this);

    private async void OnServiceAckClick(object? sender, RoutedEventArgs e)
        => await ShowServiceActionAsync(ServiceActionMode.Acknowledge);

    private async void OnServiceDowntimeClick(object? sender, RoutedEventArgs e)
        => await ShowServiceActionAsync(ServiceActionMode.Downtime);

    private async void OnHostAckClick(object? sender, RoutedEventArgs e)
        => await ShowHostActionAsync(ServiceActionMode.Acknowledge);

    private async void OnHostDowntimeClick(object? sender, RoutedEventArgs e)
        => await ShowHostActionAsync(ServiceActionMode.Downtime);

    private async void OnHostCommentClick(object? sender, RoutedEventArgs e)
        => await ShowCommentDialogAsync(onSelectedService: false);

    private async void OnServiceCommentClick(object? sender, RoutedEventArgs e)
        => await ShowCommentDialogAsync(onSelectedService: true);

    private async void OnDeleteCommentClick(object? sender, RoutedEventArgs e)
    {
        if (_vm is null || !_vm.CanWrite) return;
        if (sender is not Button { Tag: string id } || string.IsNullOrEmpty(id)) return;
        await _vm.PerformDeleteCommentAsync(id);
    }

    private void OnOpenServiceInWebClick(object? sender, RoutedEventArgs e)
    {
        if (_vm?.SelectedService is null) return;
        var web = App.Services!.GetRequiredService<CheckmkWebLinker>();
        web.OpenServiceView(_vm.SelectedService.HostName, _vm.SelectedService.Description);
    }

    private void OnOpenHostInWebClick(object? sender, RoutedEventArgs e)
    {
        // Kein CanWrite-Guard: das oeffnet nur den Browser, schreibt nichts.
        if (_vm is null) return;
        var web = App.Services!.GetRequiredService<CheckmkWebLinker>();
        web.OpenHostView(_vm.HostName);
    }

    private async System.Threading.Tasks.Task ShowCommentDialogAsync(bool onSelectedService)
    {
        if (_vm is null || !_vm.CanWrite) return;

        string target;
        if (onSelectedService)
        {
            if (_vm.SelectedService is null) return;
            target = $"{_vm.HostName} / {_vm.SelectedService.Description}";
        }
        else
        {
            target = $"{_vm.HostName} (gesamter Host)";
        }

        var dialog = new CommentInputDialog(target);
        var result = await dialog.ShowDialog<CommentInputResult?>(this);
        if (result is null) return;

        await _vm.PerformAddCommentAsync(result.Comment, result.Persistent, onSelectedService);
    }

    private async System.Threading.Tasks.Task ShowServiceActionAsync(ServiceActionMode mode)
    {
        if (_vm is null || !_vm.CanWrite) return;
        var selected = GetSelectedServices();
        if (selected.Count == 0) return;

        var dialogVm = selected.Count == 1
            ? new ServiceActionDialogViewModel(mode, selected[0].HostName, selected[0].Description)
            : new ServiceActionDialogViewModel(mode, $"{selected.Count} Services auf {_vm.HostName}");

        var dialog = new ServiceActionDialog(dialogVm);
        var confirmed = await dialog.ShowDialog<bool>(this);
        if (!confirmed) return;

        if (mode == ServiceActionMode.Acknowledge)
        {
            if (selected.Count == 1) await _vm.PerformServiceAcknowledgeAsync(dialogVm.Comment);
            else await _vm.PerformBulkServiceAcknowledgeAsync(selected, dialogVm.Comment);
        }
        else
        {
            var (start, end) = dialogVm.Window();
            if (selected.Count == 1) await _vm.PerformServiceDowntimeAsync(dialogVm.Comment, start, end);
            else await _vm.PerformBulkServiceDowntimeAsync(selected, dialogVm.Comment, start, end);
        }
    }

    private IReadOnlyList<ServiceStatus> GetSelectedServices()
    {
        var grid = this.FindControl<DataGrid>("ServiceGrid");
        if (grid is null) return [];
        return grid.SelectedItems.OfType<ServiceStatus>().ToList();
    }

    private async System.Threading.Tasks.Task ShowHostActionAsync(ServiceActionMode mode)
    {
        if (_vm is null || !_vm.CanWrite) return;

        // Fuer Host-Aktionen zeigen wir denselben Dialog, aber ohne Service-Description im Target.
        var dialogVm = new ServiceActionDialogViewModel(mode, _vm.HostName, serviceDescription: null);
        var dialog = new ServiceActionDialog(dialogVm);
        var confirmed = await dialog.ShowDialog<bool>(this);
        if (!confirmed) return;

        if (mode == ServiceActionMode.Acknowledge)
        {
            await _vm.PerformHostAcknowledgeAsync(dialogVm.Comment);
        }
        else
        {
            var (start, end) = dialogVm.Window();
            await _vm.PerformHostDowntimeAsync(dialogVm.Comment, start, end);
        }
    }
}
