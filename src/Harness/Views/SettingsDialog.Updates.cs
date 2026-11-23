// Copyright (c) Lanstack @openclaw. All rights reserved.

using Harness.Helpers;
using Harness.ViewModels;
using Microsoft.UI.Xaml;

namespace Harness.Views;

public sealed partial class SettingsDialog
{
    public UpdateViewModel Updates { get; } = new(App.Logger);
    private Task _updateCleanupTask = Task.CompletedTask;

    private void OnRestartUpdateClick(object sender, RoutedEventArgs args)
    {
        if (_isClosed || _isSaving || _isClearingSession || !Updates.CanRestart || (ViewModel.IsEditing && !TryApplyEdit())) { return; }
        _settingsSaveTask = SaveAndRestartForUpdateAsync();
    }

    private async Task SaveAndRestartForUpdateAsync()
    {
        if (!await SaveSettingsAsync(closeWindow: false) || _isClosed) { return; }
        _isSaving = true;
        SetSettingsBusyState(true, UpdateLocalization.Restarting);
        var ready = false;
        try
        {
            await Updates.HandOffAsync();
            ready = true;
        }
        catch (Exception ex) { App.Logger.Warning($"Update handoff failed: {ex.GetType().Name}"); }
        finally
        {
            _isSaving = false;
            SetSettingsBusyState(false);
        }
        if (ready && !_isClosed && App.MainWindow is MainWindow mainWindow) { mainWindow.ExitForPortableUpdate(); }
    }
}
