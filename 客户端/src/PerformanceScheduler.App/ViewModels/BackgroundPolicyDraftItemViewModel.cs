using PerformanceScheduler.Core.Models;

namespace PerformanceScheduler.App.ViewModels;

public sealed class BackgroundPolicyDraftItemViewModel : ObservableObject
{
    private string _category = "default";
    private PriorityLevel _targetPriority = PriorityLevel.BelowNormal;
    private bool _preferEfficiencyMode;
    private long? _targetAffinityMask;
    private IReadOnlyList<uint>? _targetCpuSets;
    private IoPriorityLevel? _targetIoPriority;
    private MemoryPriorityLevel? _targetMemoryPriority;
    private double? _targetJobCpuRatePercent;
    private ulong? _targetJobMemoryLimitBytes;
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

    public long? TargetAffinityMask
    {
        get => _targetAffinityMask;
        set => SetProperty(ref _targetAffinityMask, value);
    }

    public IReadOnlyList<uint>? TargetCpuSets
    {
        get => _targetCpuSets;
        set => SetProperty(ref _targetCpuSets, value);
    }

    public IoPriorityLevel? TargetIoPriority
    {
        get => _targetIoPriority;
        set => SetProperty(ref _targetIoPriority, value);
    }

    public MemoryPriorityLevel? TargetMemoryPriority
    {
        get => _targetMemoryPriority;
        set => SetProperty(ref _targetMemoryPriority, value);
    }

    public double? TargetJobCpuRatePercent
    {
        get => _targetJobCpuRatePercent;
        set => SetProperty(ref _targetJobCpuRatePercent, value);
    }

    public ulong? TargetJobMemoryLimitBytes
    {
        get => _targetJobMemoryLimitBytes;
        set => SetProperty(ref _targetJobMemoryLimitBytes, value);
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
            TargetAffinityMask = policy.TargetAffinityMask,
            TargetCpuSets = policy.TargetCpuSets,
            TargetIoPriority = policy.TargetIoPriority,
            TargetMemoryPriority = policy.TargetMemoryPriority,
            TargetJobCpuRatePercent = policy.TargetJobCpuRatePercent,
            TargetJobMemoryLimitBytes = policy.TargetJobMemoryLimitBytes,
            Notes = policy.Notes
        };

    public BackgroundProcessPolicy ToModel() =>
        new()
        {
            Category = Category.Trim(),
            TargetPriority = TargetPriority,
            PreferEfficiencyMode = PreferEfficiencyMode,
            TargetAffinityMask = TargetAffinityMask,
            TargetCpuSets = TargetCpuSets,
            TargetIoPriority = TargetIoPriority,
            TargetMemoryPriority = TargetMemoryPriority,
            TargetJobCpuRatePercent = TargetJobCpuRatePercent,
            TargetJobMemoryLimitBytes = TargetJobMemoryLimitBytes,
            Notes = Notes.Trim()
        };
}
