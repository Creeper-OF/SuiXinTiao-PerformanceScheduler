using PerformanceScheduler.Core.Models;
using PerformanceScheduler.Infrastructure.Persistence;
using PerformanceScheduler.Core.Profiles.Validation;
using PerformanceScheduler.Core.Services;

namespace PerformanceScheduler.Tests;

public sealed class JsonProfileManagerTests : IDisposable
{
    private readonly string _tempDirectory;

    public JsonProfileManagerTests()
    {
        _tempDirectory = Path.Combine(Path.GetTempPath(), "PerformanceScheduler.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDirectory);
    }

    [Fact]
    public async Task SaveProfileAsync_ReplacesPreviousFileWhenNameChanges()
    {
        var manager = new JsonProfileManager(_tempDirectory);
        var profile = new PerformanceProfile
        {
            Id = "stable-id",
            Name = "First Name",
            Match = MatchProcess("stable")
        };

        await manager.SaveProfileAsync(profile);
        await manager.SaveProfileAsync(profile with { Name = "Renamed Profile" });

        var files = Directory.GetFiles(_tempDirectory, "*.json");

        Assert.Single(files);
        Assert.Contains("stable-id-Renamed Profile.json", files[0], StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task LoadProfilesAsync_SkipsCorruptProfileJson()
    {
        var manager = new JsonProfileManager(_tempDirectory);
        await manager.SaveProfileAsync(new PerformanceProfile
        {
            Id = "valid-id",
            Name = "Valid Profile",
            Match = MatchProcess("valid")
        });
        await File.WriteAllTextAsync(Path.Combine(_tempDirectory, "broken.json"), "{ this is not json");

        var profiles = await manager.LoadProfilesAsync();

        var profile = Assert.Single(profiles);
        Assert.Equal("valid-id", profile.Id);
    }

    [Fact]
    public async Task LoadProfilesAsync_SkipsFutureSchemaAndReportsIssue()
    {
        await File.WriteAllTextAsync(
            Path.Combine(_tempDirectory, "future.json"),
            """
            { "schemaVersion": 2, "id": "future", "name": "Future Profile" }
            """);
        var manager = new JsonProfileManager(_tempDirectory);

        var profiles = await manager.LoadProfilesAsync();

        Assert.Empty(profiles);
        var issue = Assert.Single(manager.LoadIssues);
        Assert.Contains("schema version 2", issue.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ImportProfilesAsync_RejectsFutureSchema()
    {
        var sourcePath = Path.Combine(_tempDirectory, "future-import.json");
        await File.WriteAllTextAsync(
            sourcePath,
            """
            { "schemaVersion": 2, "id": "future", "name": "Future Profile" }
            """);
        var manager = new JsonProfileManager(_tempDirectory);

        await Assert.ThrowsAsync<UnsupportedConfigurationSchemaException>(() =>
            manager.ImportProfilesAsync(sourcePath, overwriteExisting: true));
    }

    [Fact]
    public async Task ImportProfilesAsync_SupportsSingleProfileJsonPayload()
    {
        var manager = new JsonProfileManager(_tempDirectory);
        var importDirectory = Path.Combine(_tempDirectory, "imports");
        Directory.CreateDirectory(importDirectory);
        var sourcePath = Path.Combine(importDirectory, "import.json");
        var payload =
            """
            {
              "schemaVersion": 1,
              "id": "single-import",
              "name": "Single Import",
              "version": 1,
              "isEnabled": true,
              "targetClassification": "Game",
              "match": {
                "executableNames": [ "game.exe" ],
                "processNames": [ "game" ],
                "windowTitleContains": [],
                "classifications": [ "Game" ]
              },
              "powerPlan": {
                "schemeGuid": null,
                "preferredPlanName": "Balanced",
                "restoreOnExit": true
              },
              "priority": {
                "foregroundPriority": "AboveNormal",
                "lowerBackgroundProcesses": false,
                "preferEfficiencyModeForBackground": false
              },
              "backgroundPolicies": [],
              "notes": "single object payload"
            }
            """;

        await File.WriteAllTextAsync(sourcePath, payload);
        await manager.ImportProfilesAsync(sourcePath, overwriteExisting: true);

        var profiles = await manager.LoadProfilesAsync();

        Assert.Single(profiles);
        Assert.Equal("single-import", profiles[0].Id);
    }

    [Fact]
    public async Task ImportProfilesAsync_DoesNotOverwriteSameIdWhenOverwriteDisabled()
    {
        var manager = new JsonProfileManager(_tempDirectory);
        var importDirectory = Path.Combine(_tempDirectory, "imports-same-id");
        Directory.CreateDirectory(importDirectory);
        var sourcePath = Path.Combine(importDirectory, "import.json");
        await manager.SaveProfileAsync(new PerformanceProfile
        {
            Id = "same-id",
            Name = "Existing Name",
            Match = MatchProcess("existing"),
            Notes = "keep me"
        });

        var payload =
            """
            {
              "schemaVersion": 1,
              "id": "same-id",
              "name": "Imported Different Name",
              "version": 1,
              "isEnabled": true,
              "match": { "processNames": [ "game" ] },
              "notes": "do not import"
            }
            """;

        await File.WriteAllTextAsync(sourcePath, payload);
        await manager.ImportProfilesAsync(sourcePath, overwriteExisting: false);

        var profile = Assert.Single(await manager.LoadProfilesAsync());

        Assert.Equal("Existing Name", profile.Name);
        Assert.Equal("keep me", profile.Notes);
    }

    [Fact]
    public async Task SaveProfileAsync_RejectsRealTimePriorityWithoutWritingAFile()
    {
        var manager = new JsonProfileManager(_tempDirectory);
        var profile = new PerformanceProfile
        {
            Id = "unsafe-realtime",
            Name = "Unsafe RealTime",
            Match = MatchProcess("unsafe"),
            Priority = new PriorityPreference { ForegroundPriority = PriorityLevel.RealTime }
        };

        var exception = await Assert.ThrowsAsync<InvalidProfileConfigurationException>(() =>
            manager.SaveProfileAsync(profile));

        Assert.Contains(exception.Issues, issue => issue.Code == "RealTimePriorityBlocked");
        Assert.Empty(Directory.GetFiles(_tempDirectory, "*.json"));
    }

    [Fact]
    public async Task ImportProfilesAsync_ValidatesEntirePayloadBeforeWriting()
    {
        var manager = new JsonProfileManager(_tempDirectory);
        var sourcePath = Path.Combine(_tempDirectory, "unsafe-import.json");
        await File.WriteAllTextAsync(
            sourcePath,
            """
            [
              {
                "schemaVersion": 1,
                "id": "safe-first",
                "name": "Safe First",
                "match": { "processNames": [ "safe" ] }
              },
              {
                "schemaVersion": 1,
                "id": "unsafe-second",
                "name": "Unsafe Second",
                "match": { "processNames": [ "unsafe" ] },
                "priority": { "foregroundPriority": "RealTime" }
              }
            ]
            """);

        await Assert.ThrowsAsync<InvalidProfileConfigurationException>(() =>
            manager.ImportProfilesAsync(sourcePath, overwriteExisting: true));

        Assert.Empty(await manager.LoadProfilesAsync());
    }

    [Fact]
    public async Task ImportProfilesAsync_DisablesHighRiskProfileUntilReviewed()
    {
        var manager = new JsonProfileManager(_tempDirectory);
        var sourcePath = Path.Combine(_tempDirectory, "high-risk-import.json");
        await File.WriteAllTextAsync(
            sourcePath,
            $$"""
            {
              "schemaVersion": 1,
              "id": "high-risk",
              "name": "High Risk",
              "isEnabled": true,
              "match": { "processNames": [ "game" ] },
              "powerPlan": {
                "schemeGuid": "{{Guid.NewGuid()}}",
                "restoreOnExit": false
              }
            }
            """);

        await manager.ImportProfilesAsync(sourcePath, overwriteExisting: true);

        var profile = Assert.Single(await manager.LoadProfilesAsync());
        Assert.False(profile.IsEnabled);
    }

    [Fact]
    public async Task SaveProfileAsync_CreatesHistoryRevisionWhenUpdatingExistingProfile()
    {
        var manager = new JsonProfileManager(_tempDirectory);
        var initialProfile = new PerformanceProfile
        {
            Id = "history-id",
            Name = "History Profile",
            Version = 1,
            Match = MatchProcess("history"),
            Notes = "initial"
        };

        await manager.SaveProfileAsync(initialProfile);
        await Task.Delay(10);
        await manager.SaveProfileAsync(initialProfile with { Version = 2, Notes = "updated" });

        var history = await manager.LoadProfileHistoryAsync("history-id");

        Assert.Single(history);
        Assert.Equal(1, history[0].Profile.Version);
        Assert.Equal("initial", history[0].Profile.Notes);
    }

    [Fact]
    public async Task RollbackProfileAsync_RestoresRevisionAsNewCurrentVersion()
    {
        var manager = new JsonProfileManager(_tempDirectory);
        var initialProfile = new PerformanceProfile
        {
            Id = "rollback-id",
            Name = "Rollback Profile",
            Version = 2,
            Match = MatchProcess("rollback"),
            Notes = "before rollback"
        };

        await manager.SaveProfileAsync(initialProfile);
        await Task.Delay(10);
        await manager.SaveProfileAsync(initialProfile with { Version = 3, Notes = "newer version" });
        var history = await manager.LoadProfileHistoryAsync("rollback-id");

        var restored = await manager.RollbackProfileAsync("rollback-id", history[0].RevisionId);
        var profiles = await manager.LoadProfilesAsync();

        Assert.NotNull(restored);
        Assert.Equal("before rollback", restored!.Notes);
        Assert.Equal(3, restored.Version);
        Assert.Equal("before rollback", Assert.Single(profiles).Notes);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDirectory))
        {
            Directory.Delete(_tempDirectory, recursive: true);
        }
    }

    private static ProfileMatchCriteria MatchProcess(string processName) => new()
    {
        ProcessNames = new[] { processName }
    };
}
