using System.IO;
using System.Windows;
using Microsoft.Win32;
using PerformanceScheduler.App.Services;
using PerformanceScheduler.App.Views;
using PerformanceScheduler.Core.Models;
using PerformanceScheduler.Core.Profiles.Validation;

namespace PerformanceScheduler.App.ViewModels;

public sealed partial class MainWindowViewModel
{
    private async Task LoadProfilesAsync()
    {
        await LoadProfilesAsync(CancellationToken.None);
    }

    private async Task LoadLocalLibraryAsync()
    {
        await LoadLocalLibraryAsync(CancellationToken.None);
    }

    private async Task LoadLocalLibraryAsync(CancellationToken cancellationToken)
    {
        await LoadProfilesAsync(cancellationToken);
        await LoadDeviceStrategyPackagesAsync(cancellationToken);
    }

    private async Task LoadCommunityProfilesAsync()
    {
        await LoadCommunityProfilesAsync(CancellationToken.None);
    }

    private async Task LoadProfilesAsync(CancellationToken cancellationToken)
    {
        var profiles = await _profileManager.LoadProfilesAsync(cancellationToken);
        ReportConfigurationLoadIssues(_profileManager);
        var currentSelectedId = SelectedProfile?.Profile.Id;
        RebuildProfileItems(profiles, currentSelectedId);

        if (Profiles.Count == 0)
        {
            SelectedProfile = null;
            ProfileDraft.Reset(PowerPlanOptions);
            ApplyDefaultDraftName();
            SetProfileEditorStatus("Status.NoSavedProfiles");
            ApplyValidationIssues(ProfileDraft.GetValidationMessages());
        }
        else
        {
            SelectedProfile = Profiles.FirstOrDefault(item => item.Profile.Id == currentSelectedId) ?? Profiles[0];
        }

        RefreshStorageSummary();
    }

    private async Task LoadDeviceStrategyPackagesAsync(CancellationToken cancellationToken)
    {
        var packages = await _deviceStrategyPackageStore.LoadPackagesAsync(cancellationToken);
        ReportConfigurationLoadIssues(_deviceStrategyPackageStore);
        var currentSelectedId = SelectedDeviceStrategyPackage?.Package.PackageId;
        RebuildDeviceStrategyPackageItems(packages, currentSelectedId);

        if (DeviceStrategyPackages.Count == 0)
        {
            SelectedDeviceStrategyPackage = null;
        }
        else
        {
            SelectedDeviceStrategyPackage = DeviceStrategyPackages.FirstOrDefault(item => item.Package.PackageId == currentSelectedId);
        }

        RefreshStorageSummary();
    }

    private void ReportConfigurationLoadIssues(object source)
    {
        if (source is not PerformanceScheduler.Core.Abstractions.IConfigurationLoadIssueSource issueSource)
        {
            return;
        }

        var newIssues = issueSource.LoadIssues
            .Where(issue => _reportedConfigurationIssues.Add($"{issue.SourcePath}|{issue.Message}"))
            .ToArray();
        if (newIssues.Length == 0)
        {
            return;
        }

        foreach (var issue in newIssues)
        {
            _logger.Warn($"Configuration file skipped: {issue.SourcePath}. {issue.Message}");
        }

        AddStatusCenterEvent(
            "Section.ProfileLibrary",
            "Status.ConfigurationFilesSkipped",
            [newIssues.Length],
            "Warning");
    }

    private async Task LoadCommunityProfilesAsync(CancellationToken cancellationToken)
    {
        _allCommunityEntries = await _communityProfileCatalog.LoadProfilesAsync(cancellationToken);
        _allCommunityDeviceStrategyEntries = await _communityDeviceStrategyCatalog.LoadStrategiesAsync(cancellationToken);
        ReportConfigurationLoadIssues(_communityProfileCatalog);
        ReportConfigurationLoadIssues(_communityDeviceStrategyCatalog);
        ApplyCommunityFilter();

        SetCommunityCatalogStatus(
            _allCommunityEntries.Count == 0 && _allCommunityDeviceStrategyEntries.Count == 0
                ? "Status.CommunityCatalogEmpty"
                : "Status.CommunityCatalogLoadedWithStrategies",
            _allCommunityDeviceStrategyEntries.Count,
            _allCommunityEntries.Count);
    }

    private async Task CreateDeviceStrategyPackageAsync()
    {
        var profiles = await _profileManager.LoadProfilesAsync();
        if (profiles.Count == 0)
        {
            SetProfileEditorStatus("Status.DeviceStrategyRequiresProfiles");
            return;
        }

        var device = await _deviceFingerprintProvider.GetCurrentAsync();
        var deviceName = string.IsNullOrWhiteSpace(device.Model)
            ? T("Status.Unknown")
            : device.Model;
        var package = _deviceStrategyComposer.CreateFromProfiles(
            F("Status.DefaultDeviceStrategyNameFormat", deviceName),
            device,
            profiles);
        await _deviceStrategyPackageStore.SavePackageAsync(package);
        await LoadDeviceStrategyPackagesAsync(CancellationToken.None);
        SelectedDeviceStrategyPackage = DeviceStrategyPackages.FirstOrDefault(item =>
            string.Equals(item.Package.PackageId, package.PackageId, StringComparison.OrdinalIgnoreCase));
        SetProfileEditorStatus("Status.CreatedDeviceStrategyPackage", package.Name, package.Profiles.Count);
    }

    private async Task AddSelectedProfileToDeviceStrategyAsync()
    {
        if (SelectedDeviceStrategyPackage is null || SelectedProfile is null)
        {
            SetProfileEditorStatus("Status.SelectPackageAndProfile");
            return;
        }

        var selectedProfile = SelectedProfile.Profile;
        var package = _deviceStrategyComposer.AddOrReplaceProfile(
            SelectedDeviceStrategyPackage.Package,
            selectedProfile);
        await _deviceStrategyPackageStore.SavePackageAsync(package);
        await ReloadSelectedDeviceStrategyPackageAsync(package.PackageId);
        SetProfileEditorStatus(
            "Status.AddedProfileToDeviceStrategy",
            selectedProfile.Name,
            package.Name);
    }

    private async Task RemoveSelectedProfileFromDeviceStrategyAsync()
    {
        if (SelectedDeviceStrategyPackage is null || SelectedDeviceStrategyProfile is null)
        {
            SetProfileEditorStatus("Status.SelectPackageProfile");
            return;
        }

        var removedProfileName = SelectedDeviceStrategyProfile.Name;
        var package = _deviceStrategyComposer.RemoveProfile(
            SelectedDeviceStrategyPackage.Package,
            SelectedDeviceStrategyProfile.Item.Profile.Id);
        await _deviceStrategyPackageStore.SavePackageAsync(package);
        await ReloadSelectedDeviceStrategyPackageAsync(package.PackageId);
        SetProfileEditorStatus(
            "Status.RemovedProfileFromDeviceStrategy",
            removedProfileName,
            package.Name);
    }

    private async Task ReloadSelectedDeviceStrategyPackageAsync(string packageId)
    {
        await LoadDeviceStrategyPackagesAsync(CancellationToken.None);
        SelectedDeviceStrategyPackage = DeviceStrategyPackages.FirstOrDefault(item =>
            string.Equals(item.Package.PackageId, packageId, StringComparison.OrdinalIgnoreCase));
    }

    private async Task LoadPowerPlansAsync(CancellationToken cancellationToken = default)
    {
        var plans = await _powerPlanManager.GetAvailablePlansAsync(cancellationToken);
        var previousSelection = ProfileDraft.SelectedPowerPlan?.SchemeGuid;

        PowerPlanOptions.Clear();
        PowerPlanOptions.Add(new PowerPlanOptionViewModel(null, T("Common.KeepCurrentPlan")));

        foreach (var plan in plans)
        {
            PowerPlanOptions.Add(PowerPlanOptionViewModel.FromModel(plan));
        }

        ProfileDraft.SelectedPowerPlan = PowerPlanOptions.FirstOrDefault(option => option.SchemeGuid == previousSelection) ??
                                         PowerPlanOptions.FirstOrDefault();
    }

    private async Task CreateNewProfileAsync()
    {
        await LoadPowerPlansAsync();
        SelectedProfile = null;
        ProfileDraft.Reset(PowerPlanOptions);
        ApplyDefaultDraftName();
        SetProfileEditorStatus("Status.CreatingNewProfile");
        ApplyValidationIssues(ProfileDraft.GetValidationMessages());
    }

    private async Task SaveProfileAsync()
    {
        var validationIssues = ProfileDraft.GetValidationMessages();

        if (ProfileDraft.IsGlobalDefault)
        {
            var existingProfiles = await _profileManager.LoadProfilesAsync();
            var conflictingGlobalDefault = existingProfiles.FirstOrDefault(profile =>
                profile.IsEnabled &&
                profile.IsGlobalDefault &&
                !string.Equals(profile.Id, ProfileDraft.Id, StringComparison.OrdinalIgnoreCase));

            if (conflictingGlobalDefault is not null)
            {
                validationIssues = validationIssues
                    .Concat(new[] { F("Validation.OnlyOneGlobalDefaultProfile", conflictingGlobalDefault.Name) })
                    .ToArray();
            }
        }

        ApplyValidationIssues(validationIssues);
        if (validationIssues.Count > 0)
        {
            SetProfileEditorStatus("Status.ProfileHasValidationIssues");
            MessageBox.Show(
                string.Join(Environment.NewLine, validationIssues),
                T("Dialog.ProfileValidationTitle"),
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            return;
        }

        var profile = ProfileDraft.ToProfile();
        var safetyValidation = ProfileSafetyValidator.Evaluate(profile);
        if (!safetyValidation.CanPersist)
        {
            var safetyErrors = safetyValidation.Errors
                .Select(GetProfileSafetyIssueText)
                .ToArray();
            ApplyValidationIssues(safetyErrors);
            SetProfileEditorStatus("Status.ProfileHasValidationIssues");
            MessageBox.Show(
                string.Join(Environment.NewLine, safetyErrors),
                T("Dialog.ProfileValidationTitle"),
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            return;
        }

        if (safetyValidation.Warnings.Count > 0)
        {
            var warningText = string.Join(
                Environment.NewLine,
                safetyValidation.Warnings.Select(GetProfileSafetyIssueText));
            var confirmed = MessageBox.Show(
                F("Dialog.ProfileSafetyWarningQuestion", warningText),
                T("Dialog.ProfileSafetyWarningTitle"),
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning);
            if (confirmed != MessageBoxResult.Yes)
            {
                SetProfileEditorStatus("Status.ProfileSaveCancelledForSafety");
                return;
            }
        }

        await _profileManager.SaveProfileAsync(profile);
        _logger.Info($"Profile saved: {profile.Name} ({profile.Id})");
        await LoadProfilesAsync();
        SelectedProfile = Profiles.FirstOrDefault(item => item.Profile.Id == profile.Id);
        SetProfileEditorStatus("Status.SavedProfile", profile.Name);
        SetValidationSummary("Validation.Passed");
    }

    private async Task ImportProfilesAsync()
    {
        var dialog = new OpenFileDialog
        {
            Filter = "JSON Files (*.json)|*.json|All Files (*.*)|*.*",
            Title = T("Dialog.ImportProfilesTitle")
        };

        if (dialog.ShowDialog() != true)
        {
            return;
        }

        var overwriteExisting = MessageBox.Show(
            T("Dialog.ImportOverwriteQuestion"),
            T("Dialog.ImportProfilesTitle"),
            MessageBoxButton.YesNo,
            MessageBoxImage.Question) == MessageBoxResult.Yes;

        await _profileManager.ImportProfilesAsync(dialog.FileName, overwriteExisting);
        _logger.Info($"Profiles imported from {dialog.FileName}.");
        await LoadProfilesAsync();
        SetProfileEditorStatus("Status.ImportedProfiles", dialog.FileName);
    }

    private async Task ExportProfilesAsync()
    {
        var dialog = new SaveFileDialog
        {
            Filter = "JSON Files (*.json)|*.json|All Files (*.*)|*.*",
            FileName = $"profiles-export-{DateTime.Now:yyyyMMdd-HHmmss}.json",
            Title = T("Dialog.ExportProfilesTitle")
        };

        if (dialog.ShowDialog() != true)
        {
            return;
        }

        await _profileManager.ExportProfilesAsync(dialog.FileName);
        _logger.Info($"Profiles exported to {dialog.FileName}.");
        SetProfileEditorStatus("Status.ExportedProfiles", dialog.FileName);
    }

    private async Task ExportMigrationPackageAsync()
    {
        var dialog = new SaveFileDialog
        {
            Filter = "Migration Package (*.sxtpkg.json)|*.sxtpkg.json|JSON Files (*.json)|*.json|All Files (*.*)|*.*",
            FileName = $"suixindiao-migration-{DateTime.Now:yyyyMMdd-HHmmss}.sxtpkg.json",
            Title = T("Dialog.ExportMigrationPackageTitle")
        };

        if (dialog.ShowDialog() != true)
        {
            return;
        }

        await _migrationPackageService.ExportAsync(
            dialog.FileName,
            new MigrationPackageExportOptions
            {
                IncludeProfiles = true,
                IncludeDeviceStrategyPackages = true,
                IncludeSettings = true
            });
        _logger.Info($"Migration package exported to {dialog.FileName}.");
        SetProfileEditorStatus("Status.ExportedMigrationPackage", dialog.FileName);
    }

    private async Task ImportMigrationPackageAsync()
    {
        var dialog = new OpenFileDialog
        {
            Filter = "Migration Package (*.sxtpkg.json)|*.sxtpkg.json|JSON Files (*.json)|*.json|All Files (*.*)|*.*",
            Title = T("Dialog.ImportMigrationPackageTitle")
        };

        if (dialog.ShowDialog() != true)
        {
            return;
        }

        var preview = await _migrationPackageService.PreviewImportAsync(dialog.FileName);
        var compatibilityMessage = T(preview.Compatibility.ReasonKey);
        var confirmation = MessageBox.Show(
            F(
                "Dialog.ImportMigrationPackageQuestion",
                preview.ProfileCount,
                preview.DeviceStrategyPackageCount,
                preview.HasSettings ? T("Common.Yes") : T("Common.No"),
                compatibilityMessage),
            T("Dialog.ImportMigrationPackageTitle"),
            MessageBoxButton.YesNo,
            preview.Compatibility.ShouldWarn ? MessageBoxImage.Warning : MessageBoxImage.Question);

        if (confirmation != MessageBoxResult.Yes)
        {
            SetProfileEditorStatus("Status.MigrationImportCancelled");
            return;
        }

        var overwriteExisting = MessageBox.Show(
            T("Dialog.ImportOverwriteQuestion"),
            T("Dialog.ImportMigrationPackageTitle"),
            MessageBoxButton.YesNo,
            MessageBoxImage.Question) == MessageBoxResult.Yes;

        var result = await _migrationPackageService.ImportAsync(
            dialog.FileName,
            new MigrationPackageImportOptions
            {
                ImportProfiles = true,
                ImportDeviceStrategyPackages = true,
                ImportSettings = true,
                OverwriteExistingProfiles = overwriteExisting
            });

        _logger.Info($"Migration package imported from {dialog.FileName}.");
        if (result.ImportedSettings)
        {
            _startupRegistrationService.SetEnabled(_settingsStore.Current.LaunchAtStartup);
            LoadSettings();
            NotifySettingsChanged();
            if (!string.IsNullOrWhiteSpace(_settingsStore.Current.LanguageCode))
            {
                _localizationService.SetLanguage(_settingsStore.Current.LanguageCode);
            }
        }

        await LoadLocalLibraryAsync();
        RebuildLocalizedViewState();
        SetProfileEditorStatus(
            "Status.ImportedMigrationPackage",
            result.ImportedProfiles,
            result.ImportedDeviceStrategyPackages,
            result.ImportedSettings ? T("Common.Yes") : T("Common.No"),
            T(result.Compatibility.ReasonKey));
    }

    private async Task ImportSelectedCommunityProfileAsync()
    {
        if (SelectedCommunityProfile is null)
        {
            SetCommunityCatalogStatus("Status.SelectCommunityProfile");
            return;
        }

        var entry = SelectedCommunityProfile.Entry;
        if (!ConfirmProfilesForImport([entry.Profile]))
        {
            SetCommunityCatalogStatus("Status.CommunityImportCancelled", entry.Name);
            return;
        }

        var existingProfile = (await _profileManager.LoadProfilesAsync())
            .FirstOrDefault(profile => string.Equals(profile.Id, entry.Profile.Id, StringComparison.OrdinalIgnoreCase));

        if (existingProfile is not null)
        {
            var overwrite = MessageBox.Show(
                F("Dialog.ImportCommunityOverwriteQuestion", existingProfile.Name),
                T("Dialog.ImportCommunityTitle"),
                MessageBoxButton.YesNo,
                MessageBoxImage.Question);

            if (overwrite != MessageBoxResult.Yes)
            {
                SetCommunityCatalogStatus("Status.CommunityImportCancelled", entry.Name);
                return;
            }
        }

        await _profileManager.SaveProfileAsync(entry.Profile);
        _logger.Info($"Community profile imported: {entry.Name} ({entry.Profile.Id}) from {entry.Author}.");
        await LoadProfilesAsync();
        SelectedProfile = Profiles.FirstOrDefault(item => item.Profile.Id == entry.Profile.Id);
        SetCommunityCatalogStatus("Status.ImportedCommunityProfile", entry.Name);
        SetProfileEditorStatus("Status.ImportedCommunityProfile", entry.Name);
    }

    private async Task ImportSelectedCommunityDeviceStrategyAsync()
    {
        if (SelectedCommunityDeviceStrategy is null)
        {
            SetCommunityCatalogStatus("Status.SelectCommunityDeviceStrategy");
            return;
        }

        var entry = SelectedCommunityDeviceStrategy.Entry;
        var package = entry.Package;
        var packageItems = package.Profiles
            .Where(item => !string.IsNullOrWhiteSpace(item.Profile.Id))
            .GroupBy(item => item.Profile.Id, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .ToArray();

        if (packageItems.Length == 0)
        {
            SetCommunityCatalogStatus("Status.DeviceStrategyPackageEmpty", entry.Name);
            return;
        }

        if (!ConfirmProfilesForImport(packageItems.Select(item => item.Profile)))
        {
            SetCommunityCatalogStatus("Status.DeviceStrategyImportCancelled", entry.Name);
            return;
        }

        var currentDevice = await _deviceFingerprintProvider.GetCurrentAsync();
        var compatibility = _deviceCompatibilityEvaluator.Evaluate(package.TargetDevice, currentDevice);
        if (compatibility.ShouldWarn)
        {
            var continueImport = MessageBox.Show(
                F("Dialog.ImportDeviceStrategyCompatibilityQuestion", entry.Name, T(compatibility.ReasonKey)),
                T("Dialog.ImportDeviceStrategyTitle"),
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning);

            if (continueImport != MessageBoxResult.Yes)
            {
                SetCommunityCatalogStatus("Status.DeviceStrategyImportCancelled", entry.Name);
                return;
            }
        }

        var localProfiles = await _profileManager.LoadProfilesAsync();
        var existingIds = localProfiles
            .Select(profile => profile.Id)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var conflictCount = packageItems.Count(item => existingIds.Contains(item.Profile.Id));
        var overwriteExisting = conflictCount == 0;

        if (conflictCount > 0)
        {
            var overwrite = MessageBox.Show(
                F("Dialog.ImportDeviceStrategyOverwriteQuestion", conflictCount),
                T("Dialog.ImportDeviceStrategyTitle"),
                MessageBoxButton.YesNo,
                MessageBoxImage.Question);

            overwriteExisting = overwrite == MessageBoxResult.Yes;
        }

        var prospectiveProfiles = localProfiles.ToDictionary(
            profile => profile.Id,
            StringComparer.OrdinalIgnoreCase);
        foreach (var item in packageItems)
        {
            if (existingIds.Contains(item.Profile.Id) && !overwriteExisting)
            {
                continue;
            }

            prospectiveProfiles[item.Profile.Id] = item.Profile;
        }

        var prospectiveValidation = ProfileSafetyValidator.EvaluateLibrary(prospectiveProfiles.Values);
        if (!prospectiveValidation.CanPersist)
        {
            MessageBox.Show(
                string.Join(Environment.NewLine, prospectiveValidation.Errors.Select(GetProfileSafetyIssueText)),
                T("Dialog.ProfileValidationTitle"),
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            SetCommunityCatalogStatus("Status.DeviceStrategyImportCancelled", entry.Name);
            return;
        }

        var importedIds = new List<string>();
        var skippedCount = 0;
        foreach (var item in packageItems)
        {
            var profile = item.Profile;
            if (existingIds.Contains(profile.Id) && !overwriteExisting)
            {
                skippedCount++;
                continue;
            }

            var profileToImport = profile with
            {
                IsEnabled = profile.IsEnabled && item.IsEnabledByDefault
            };

            await _profileManager.SaveProfileAsync(profileToImport);
            importedIds.Add(profile.Id);
        }

        var localPackage = package with
        {
            Author = string.Equals(package.Author, "Unknown", StringComparison.OrdinalIgnoreCase) ? entry.Author : package.Author,
            Source = entry.Source,
            UpdatedAt = entry.UpdatedAt,
            Profiles = packageItems
                .Select(item => item with
                {
                    Profile = item.Profile with
                    {
                        IsEnabled = item.Profile.IsEnabled && item.IsEnabledByDefault
                    }
                })
                .ToArray()
        };
        await _deviceStrategyPackageStore.SavePackageAsync(localPackage);

        _logger.Info(
            $"Community device strategy imported: {entry.Name} ({package.PackageId}). Imported {importedIds.Count}, skipped {skippedCount}.");
        await LoadLocalLibraryAsync();
        SelectedDeviceStrategyPackage = DeviceStrategyPackages.FirstOrDefault(item =>
            string.Equals(item.Package.PackageId, localPackage.PackageId, StringComparison.OrdinalIgnoreCase));
        if (importedIds.Count > 0)
        {
            SelectedProfile = Profiles.FirstOrDefault(item => importedIds.Contains(item.Profile.Id, StringComparer.OrdinalIgnoreCase));
        }

        SetCommunityCatalogStatus(
            "Status.ImportedCommunityDeviceStrategy",
            entry.Name,
            importedIds.Count,
            skippedCount,
            T(compatibility.ReasonKey));
        SetProfileEditorStatus(
            "Status.ImportedCommunityDeviceStrategy",
            entry.Name,
            importedIds.Count,
            skippedCount,
            T(compatibility.ReasonKey));
    }

    private async Task RollbackProfileAsync()
    {
        if (SelectedProfile is null || SelectedProfileRevision is null)
        {
            SetProfileEditorStatus("Status.SelectProfileRevision");
            return;
        }

        var selectedProfile = SelectedProfile;
        var selectedRevision = SelectedProfileRevision;

        var result = MessageBox.Show(
            F("Dialog.RollbackProfileQuestion", selectedProfile.Profile.Name, selectedRevision.DisplayName),
            T("Dialog.RollbackProfileTitle"),
            MessageBoxButton.YesNo,
            MessageBoxImage.Question);

        if (result != MessageBoxResult.Yes)
        {
            return;
        }

        var restoredProfile = await _profileManager.RollbackProfileAsync(
            selectedProfile.Profile.Id,
            selectedRevision.RevisionId);

        if (restoredProfile is null)
        {
            SetProfileEditorStatus("Status.ProfileRollbackFailed");
            return;
        }

        _logger.Info($"Profile rolled back: {restoredProfile.Name} ({restoredProfile.Id}) from {selectedRevision.RevisionId}.");
        await LoadProfilesAsync();
        SelectedProfile = Profiles.FirstOrDefault(item => item.Profile.Id == restoredProfile.Id);
        SetProfileEditorStatus("Status.RolledBackProfile", restoredProfile.Name, selectedRevision.DisplayName);
        SetValidationSummary("Status.ValidationReady");
    }

    private void AddBackgroundPolicy()
    {
        var suggestedCategory = BackgroundPolicyCategoryOptions
            .Select(option => option.Value)
            .FirstOrDefault(category => ProfileDraft.BackgroundPolicies.All(
                existing => !string.Equals(existing.Category, category, StringComparison.OrdinalIgnoreCase)))
            ?? "default";

        ProfileDraft.BackgroundPolicies.Add(new BackgroundPolicyDraftItemViewModel
        {
            Category = suggestedCategory,
            TargetPriority = PriorityLevel.BelowNormal
        });

        SetProfileEditorStatus("Status.AddedBackgroundPolicy", GetCategoryLabel(suggestedCategory));
    }

    private string GetProfileSafetyIssueText(ProfileSafetyIssue issue)
    {
        var key = $"Validation.ProfileSafety.{issue.Code}";
        var translated = T(key);
        return string.Equals(translated, key, StringComparison.Ordinal)
            ? issue.Message
            : translated;
    }

    private bool ConfirmProfilesForImport(IEnumerable<PerformanceProfile> profiles)
    {
        var validation = ProfileSafetyValidator.EvaluateLibrary(profiles);
        if (!validation.CanPersist)
        {
            MessageBox.Show(
                string.Join(Environment.NewLine, validation.Errors.Select(GetProfileSafetyIssueText)),
                T("Dialog.ProfileValidationTitle"),
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            return false;
        }

        if (validation.Warnings.Count == 0)
        {
            return true;
        }

        var warnings = validation.Warnings
            .GroupBy(issue => issue.Code, StringComparer.Ordinal)
            .Select(group => GetProfileSafetyIssueText(group.First()));
        return MessageBox.Show(
            F("Dialog.ProfileSafetyWarningQuestion", string.Join(Environment.NewLine, warnings)),
            T("Dialog.ProfileSafetyWarningTitle"),
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning) == MessageBoxResult.Yes;
    }

    private void RemoveBackgroundPolicy(BackgroundPolicyDraftItemViewModel? policy)
    {
        if (policy is null)
        {
            return;
        }

        ProfileDraft.BackgroundPolicies.Remove(policy);
        SetProfileEditorStatus("Status.RemovedBackgroundPolicy", GetCategoryLabel(policy.Category));
    }

    private void ResumeScheduling()
    {
        IsSchedulingSuspended = false;
        SetMonitoringStatus("Status.SchedulingResumed");
    }

    private void SelectCommunitySection(string? section)
    {
        SelectedCommunityCatalogSection = string.Equals(section, "Profiles", StringComparison.OrdinalIgnoreCase)
            ? CommunityCatalogSection.Profiles
            : CommunityCatalogSection.DeviceStrategies;
    }


    private void RebuildProfileItems(IEnumerable<PerformanceProfile> profiles, string? selectedId)
    {
        var mappedItems = profiles
            .Select(CreateProfileListItem)
            .ToArray();

        Profiles.Clear();
        foreach (var item in mappedItems)
        {
            Profiles.Add(item);
        }

        if (!string.IsNullOrWhiteSpace(selectedId))
        {
            SelectedProfile = Profiles.FirstOrDefault(item => item.Profile.Id == selectedId);
        }

        OnPropertyChanged(nameof(LocalProfileCount));
        OnPropertyChanged(nameof(NavigationProfilesSummary));
    }

    private void RebuildDeviceStrategyPackageItems(IEnumerable<DeviceStrategyPackage> packages, string? selectedPackageId)
    {
        var mappedItems = packages
            .Select(CreateDeviceStrategyPackageListItem)
            .ToArray();

        DeviceStrategyPackages.Clear();
        foreach (var item in mappedItems)
        {
            DeviceStrategyPackages.Add(item);
        }

        if (!string.IsNullOrWhiteSpace(selectedPackageId))
        {
            SelectedDeviceStrategyPackage = DeviceStrategyPackages.FirstOrDefault(item =>
                string.Equals(item.Package.PackageId, selectedPackageId, StringComparison.OrdinalIgnoreCase));
        }

        OnPropertyChanged(nameof(LocalDeviceStrategyCount));
        OnPropertyChanged(nameof(NavigationProfilesSummary));
    }

    private void RebuildSelectedDeviceStrategyProfiles(DeviceStrategyPackage package)
    {
        var items = package.Profiles
            .OrderBy(item => item.Profile.Name, StringComparer.OrdinalIgnoreCase)
            .Select(item => new DeviceStrategyProfileItemViewModel
            {
                Item = item,
                Name = item.Profile.Name,
                Meta = F(
                    "Status.DeviceStrategyProfileMetaFormat",
                    T($"Enum.DeviceStrategyProfileRole.{item.Role}"),
                    item.Profile.Version,
                    item.IsEnabledByDefault ? T("Common.Yes") : T("Common.No"))
            })
            .ToArray();

        SelectedDeviceStrategyProfiles.Clear();
        foreach (var item in items)
        {
            SelectedDeviceStrategyProfiles.Add(item);
        }

        SelectedDeviceStrategyProfile = SelectedDeviceStrategyProfiles.FirstOrDefault();
    }

    private void RebuildCommunityProfileItems(IEnumerable<CommunityProfileEntry> entries, string? selectedEntryId)
    {
        var mappedItems = entries
            .Select(CreateCommunityProfileListItem)
            .ToArray();

        CommunityProfiles.Clear();
        foreach (var item in mappedItems)
        {
            CommunityProfiles.Add(item);
        }

        if (!string.IsNullOrWhiteSpace(selectedEntryId))
        {
            SelectedCommunityProfile = CommunityProfiles.FirstOrDefault(item => item.Entry.EntryId == selectedEntryId);
        }

        OnPropertyChanged(nameof(CommunityProfileCount));
        OnPropertyChanged(nameof(NavigationCommunitySummary));
    }

    private void RebuildCommunityDeviceStrategyItems(IEnumerable<CommunityDeviceStrategyEntry> entries, string? selectedEntryId)
    {
        var mappedItems = entries
            .Select(CreateCommunityDeviceStrategyListItem)
            .ToArray();

        CommunityDeviceStrategies.Clear();
        foreach (var item in mappedItems)
        {
            CommunityDeviceStrategies.Add(item);
        }

        if (!string.IsNullOrWhiteSpace(selectedEntryId))
        {
            SelectedCommunityDeviceStrategy = CommunityDeviceStrategies.FirstOrDefault(item => item.Entry.EntryId == selectedEntryId);
        }

        if (SelectedCommunityDeviceStrategy is null)
        {
            SelectedCommunityDeviceStrategy = CommunityDeviceStrategies.FirstOrDefault();
        }

        OnPropertyChanged(nameof(CommunityDeviceStrategyCount));
        OnPropertyChanged(nameof(NavigationCommunitySummary));
    }

    private void ApplyCommunityFilter()
    {
        var selectedEntryId = SelectedCommunityProfile?.Entry.EntryId;
        var selectedDeviceStrategyId = SelectedCommunityDeviceStrategy?.Entry.EntryId;
        var filteredEntries = _communityProfileCatalogFilter.Apply(_allCommunityEntries, new CommunityProfileFilterCriteria
        {
            SearchText = CommunitySearchText,
            SourceFilter = SelectedCommunitySourceFilter,
            PowerSourceFilter = SelectedCommunityPowerFilter,
            ClassificationFilter = SelectedCommunityClassificationFilter
        });
        var filteredDeviceStrategies = FilterCommunityDeviceStrategies();

        RebuildCommunityProfileItems(filteredEntries, selectedEntryId);
        RebuildCommunityDeviceStrategyItems(filteredDeviceStrategies, selectedDeviceStrategyId);
        if (SelectedCommunityProfile is null)
        {
            SelectedCommunityProfile = CommunityProfiles.FirstOrDefault();
        }

        CommunityFilterSummary = IsCommunityDeviceStrategiesSelected
            ? F("Status.CommunityDeviceStrategyFilterSummaryFormat", CommunityDeviceStrategies.Count, _allCommunityDeviceStrategyEntries.Count)
            : F("Status.CommunityFilterSummaryFormat", CommunityProfiles.Count, _allCommunityEntries.Count);
    }

    private IReadOnlyList<CommunityDeviceStrategyEntry> FilterCommunityDeviceStrategies()
    {
        var expectedSource = SelectedCommunitySourceFilter switch
        {
            CommunitySourceFilter.Official => CommunityProfileSource.Official,
            CommunitySourceFilter.Community => CommunityProfileSource.Community,
            _ => (CommunityProfileSource?)null
        };
        var searchText = CommunitySearchText.Trim();

        return _allCommunityDeviceStrategyEntries
            .Where(entry => expectedSource is null || entry.Source == expectedSource)
            .Where(entry => string.IsNullOrWhiteSpace(searchText) ||
                            entry.Name.Contains(searchText, StringComparison.OrdinalIgnoreCase) ||
                            entry.Summary.Contains(searchText, StringComparison.OrdinalIgnoreCase) ||
                            entry.Package.Name.Contains(searchText, StringComparison.OrdinalIgnoreCase) ||
                            entry.Package.TargetDevice.Model.Contains(searchText, StringComparison.OrdinalIgnoreCase) ||
                            entry.Package.TargetDevice.CpuName.Contains(searchText, StringComparison.OrdinalIgnoreCase) ||
                            entry.Package.TargetDevice.GpuNames.Any(gpu => gpu.Contains(searchText, StringComparison.OrdinalIgnoreCase)))
            .ToArray();
    }

    private ProfileListItemViewModel CreateProfileListItem(PerformanceProfile profile) =>
        new()
        {
            Profile = profile,
            Name = profile.Name,
            Summary = F(
                "Status.ProfileListSummaryFormat",
                BuildProfileSummaryTag(profile),
                GetPriorityLabel(profile.Priority.ForegroundPriority),
                profile.Version),
            EnabledText = F("Status.EnabledFormat", profile.IsEnabled ? T("Common.Yes") : T("Common.No"))
        };

    private DeviceStrategyPackageListItemViewModel CreateDeviceStrategyPackageListItem(DeviceStrategyPackage package) =>
        new()
        {
            Package = package,
            Name = package.Name,
            Summary = string.IsNullOrWhiteSpace(package.Summary) ? T("Status.DeviceStrategyNoSummary") : package.Summary,
            Meta = F(
                "Status.LocalDeviceStrategyPackageMetaFormat",
                package.Profiles.Count,
                GetCommunitySourceLabel(package.Source),
                FormatDeviceTarget(package.TargetDevice))
        };

    private CommunityProfileListItemViewModel CreateCommunityProfileListItem(CommunityProfileEntry entry) =>
        new()
        {
            Entry = entry,
            Name = entry.Name,
            Summary = F(
                "Status.CommunityProfileSummaryFormat",
                GetCommunitySourceLabel(entry.Source),
                GetClassificationLabel(entry.Profile.TargetClassification),
                GetPowerSourceLabel(entry.Profile.PowerSourceMode)),
            Description = string.IsNullOrWhiteSpace(entry.Summary)
                ? entry.Profile.Notes
                : entry.Summary,
            Meta = F(
                "Status.CommunityProfileMetaFormat",
                entry.Author,
                entry.Downloads,
                entry.UpdatedAt.LocalDateTime)
        };

    private CommunityDeviceStrategyListItemViewModel CreateCommunityDeviceStrategyListItem(CommunityDeviceStrategyEntry entry) =>
        new()
        {
            Entry = entry,
            Name = entry.Name,
            Summary = F(
                "Status.CommunityDeviceStrategySummaryFormat",
                GetCommunitySourceLabel(entry.Source),
                FormatDeviceTarget(entry.Package.TargetDevice),
                entry.Package.Profiles.Count),
            Description = string.IsNullOrWhiteSpace(entry.Summary)
                ? entry.Package.Summary
                : entry.Summary,
            Meta = F(
                "Status.CommunityProfileMetaFormat",
                entry.Author,
                entry.Downloads,
                entry.UpdatedAt.LocalDateTime)
        };

    private string FormatDeviceTarget(DeviceFingerprint device)
    {
        var model = string.Join(
            " ",
            new[] { device.Manufacturer, device.Model }.Where(static value => !string.IsNullOrWhiteSpace(value)));
        var gpu = device.GpuNames.FirstOrDefault() ?? T("Status.Unknown");

        return string.IsNullOrWhiteSpace(model) && string.IsNullOrWhiteSpace(device.CpuName)
            ? T("Status.Unknown")
            : F(
                "Status.DeviceStrategyTargetFormat",
                string.IsNullOrWhiteSpace(model) ? T("Status.Unknown") : model,
                string.IsNullOrWhiteSpace(device.CpuName) ? T("Status.Unknown") : device.CpuName,
                gpu);
    }

}
