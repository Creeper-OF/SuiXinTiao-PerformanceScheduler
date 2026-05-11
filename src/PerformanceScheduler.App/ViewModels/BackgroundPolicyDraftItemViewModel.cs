using PerformanceScheduler.Core.Models;

namespace PerformanceScheduler.App.ViewModels;

public sealed class BackgroundPolicyDraftItemViewModel : ObservableObject
{
    private string _category = "default";
    private PriorityLevel _targetPriority = PriorityLevel.BelowNormal;
    private bool _preferEfficiencyMode;
    private string _notes = string.Empty;

    public string Category
    {
        get => _category;
        set => SetProperty(ref _category, value);
    }

    public PriorityLevel TargetPriority
    {
        get => _targetPriority;
        set => SetProperty(ref _targetPriority, value);
    }

    public bool PreferEfficiencyMode
    {
        get => _preferEfficiencyMode;
        set => SetProperty(ref _preferEfficiencyMode, value);
    }

    public string Notes
    {
        get => _notes;
        set => SetProperty(ref _notes, value);
    }

    public static BackgroundPolicyDraftItemViewModel FromModel(BackgroundProcessPolicy policy) =>
        new()
        {
            Category = policy.Category,
            TargetPriority = policy.TargetPriority,
            PreferEfficiencyMode = policy.PreferEfficiencyMode,
            Notes = policy.Notes
        };

    public BackgroundProcessPolicy ToModel() =>
        new()
        {
            Category = Category.Trim(),
            TargetPriority = TargetPriority,
            PreferEfficiencyMode = PreferEfficiencyMode,
            Notes = Notes.Trim()
        };
}
