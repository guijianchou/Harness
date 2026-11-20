// Copyright (c) Lanstack @openclaw. All rights reserved.

using Harness.Helpers;
using Harness.Models;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Harness.Views;

public sealed partial class SettingsDialog
{
    private void OnNavSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (sender is ListView navigation && navigation.SelectedItem is ListViewItem item && item.Tag is string tag)
        {
            ShowPanel(tag);
            if (CompactNavigation is not null && CompactNavigation.SelectedIndex != navigation.SelectedIndex)
            {
                CompactNavigation.SelectedIndex = navigation.SelectedIndex;
            }
        }
    }

    private void OnCompactNavigationChanged(object sender, SelectionChangedEventArgs e)
    {
        if (NavList is not null && CompactNavigation.SelectedIndex >= 0 && NavList.SelectedIndex != CompactNavigation.SelectedIndex)
        {
            NavList.SelectedIndex = CompactNavigation.SelectedIndex;
        }
    }

    private void ShowPanel(string tag)
    {
        PanelLanguage.Visibility = ToPanelVisibility(tag == LanguagePanelTag);
        PanelShell.Visibility = ToPanelVisibility(tag == ShellPanelTag);
        PanelEnvironments.Visibility = ToPanelVisibility(tag == EnvironmentsPanelTag);
        PanelSessions.Visibility = ToPanelVisibility(tag == SessionsPanelTag);
        PanelDevTools.Visibility = ToPanelVisibility(tag == DevToolsPanelTag);
        PanelAbout.Visibility = ToPanelVisibility(tag == AboutPanelTag);
        PanelNotifications.Visibility = ToPanelVisibility(tag == "Notifications");
        SettingsContentScroll.ChangeView(null, 0, null, disableAnimation: true);
    }

    private void SetLanguageSelection(string language)
    {
        if (LanguageComboBox.Items.Count == 0)
        {
            PopulateLanguageOptions();
        }

        _isSyncingLanguageSelection = true;
        try
        {
            for (int i = 0; i < LanguageComboBox.Items.Count; i++)
            {
                if (LanguageComboBox.Items[i] is ComboBoxItem item && item.Tag is string tag && tag == language)
                {
                    if (LanguageComboBox.SelectedIndex != i)
                    {
                        LanguageComboBox.SelectedIndex = i;
                    }

                    return;
                }
            }

            if (LanguageComboBox.SelectedIndex != 0)
            {
                LanguageComboBox.SelectedIndex = 0;
            }
        }
        finally
        {
            _isSyncingLanguageSelection = false;
        }
    }

    private void PopulateLanguageOptions()
    {
        if (LanguageComboBox.Items.Count != 0)
        {
            return;
        }

        _isSyncingLanguageSelection = true;
        try
        {
            AddLanguageOption(StringResources.SettingsLanguageSystem, "System");
            AddLanguageOption(StringResources.SettingsLanguageEnglish, "en-US");
            AddLanguageOption(StringResources.SettingsLanguageChineseSimplified, "zh-CN");
        }
        finally
        {
            _isSyncingLanguageSelection = false;
        }
    }

    private void AddLanguageOption(string content, string tag)
    {
        LanguageComboBox.Items.Add(new ComboBoxItem
        {
            Content = content,
            Tag = tag,
        });
    }

    private void OnLanguageSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_isSyncingLanguageSelection)
        {
            return;
        }

        if (LanguageComboBox.SelectedItem is ComboBoxItem item && item.Tag is string langTag)
        {
            ViewModel.SelectedLanguage = langTag;
        }
    }

    private void OnEnvironmentSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_isRestoringEnvironmentSelection) { return; }
        if (EnvironmentList.SelectedItem is EnvironmentConfig environment)
        {
            if (ViewModel.SelectedEnvironment is not null && ViewModel.SelectedEnvironment != environment && ViewModel.IsEditing && !TryApplyEdit())
            {
                _isRestoringEnvironmentSelection = true;
                EnvironmentList.SelectedItem = ViewModel.SelectedEnvironment;
                _isRestoringEnvironmentSelection = false;
                return;
            }
            ViewModel.SelectedEnvironment = environment;
        }
    }

    private void OnAddClick(object sender, RoutedEventArgs e)
    {
        if (ViewModel.SelectedEnvironment is not null && ViewModel.IsEditing && !TryApplyEdit()) { return; }
        ViewModel.AddEnvironment();
        SelectEnvironment(ViewModel.SelectedEnvironment);
    }

    private void OnRemoveClick(object sender, RoutedEventArgs e)
    {
        ViewModel.RemoveEnvironment();
        SelectFirstEnvironment();
    }

    private static Visibility ToPanelVisibility(bool isVisible) =>
        isVisible ? Visibility.Visible : Visibility.Collapsed;

    private void SelectEnvironment(EnvironmentConfig? environment)
    {
        EnvironmentList.SelectedItem = environment;
    }

    private void SelectFirstEnvironment()
    {
        if (ViewModel.Environments.Count <= 0)
        {
            EnvironmentList.SelectedItem = null;
            return;
        }

        EnvironmentList.SelectedIndex = 0;
    }
}
