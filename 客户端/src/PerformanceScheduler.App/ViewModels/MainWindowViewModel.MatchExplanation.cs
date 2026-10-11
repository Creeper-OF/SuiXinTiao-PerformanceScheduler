using System.Collections.ObjectModel;
using PerformanceScheduler.Core.Models;

namespace PerformanceScheduler.App.ViewModels;

public sealed partial class MainWindowViewModel
{
    public ObservableCollection<ProfileMatchReasonItemViewModel> ProfileMatchReasons { get; } = new();

    private void RefreshProfileMatchReasons(ProfileMatchResult? matchResult)
    {
        ProfileMatchReasons.Clear();
        if (matchResult is null)
        {
            return;
        }

        foreach (var reason in matchResult.Reasons)
        {
            var reasonLabel = T($"ProfileMatchReason.Kind.{reason.Kind}");
            ProfileMatchReasons.Add(new ProfileMatchReasonItemViewModel
            {
                Label = F("Status.MatchReasonChipFormat", reasonLabel, reason.ScoreContribution),
                Detail = F(
                    "Status.MatchReasonDetailFormat",
                    FormatProfileMatchValue(reason.Kind, reason.RuleValue),
                    FormatProfileMatchValue(reason.Kind, reason.ActualValue))
            });
        }
    }

    private string FormatProfileMatchValue(ProfileMatchReasonKind kind, string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return T("Status.Unknown");
        }

        if (kind == ProfileMatchReasonKind.PowerSource &&
            Enum.TryParse<PowerSourceMode>(value, ignoreCase: true, out var powerSource))
        {
            return GetPowerSourceLabel(powerSource);
        }

        if ((kind == ProfileMatchReasonKind.Classification ||
             kind == ProfileMatchReasonKind.TargetClassification) &&
            Enum.TryParse<ProcessClassification>(value, ignoreCase: true, out var classification))
        {
            return GetClassificationLabel(classification);
        }

        return value;
    }
}
