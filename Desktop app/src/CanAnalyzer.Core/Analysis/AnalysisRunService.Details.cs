using CanAnalyzer.Core.Domain;

namespace CanAnalyzer.Core.Analysis;

public sealed partial class AnalysisRunService
{
    private sealed class DetailCollector
    {
        public Dictionary<string, JoystickUsageResult> Joysticks { get; } = [];
        public ActiveUsageResult? ActiveUsage { get; set; }
    }

    private static ModuleResult<TDetail> Project<TSummary, TDetail>(ModuleResult<TSummary> source, TDetail? value) => new()
    {
        ResultId = source.ResultId, ModuleId = source.ModuleId, SubjectId = source.SubjectId,
        MethodVersion = source.MethodVersion, Status = source.Status, ReasonCodes = source.ReasonCodes,
        Explanation = source.Explanation, Unit = source.Unit, ValueUnits = source.ValueUnits,
        Inputs = source.Inputs, Coverage = source.Coverage, Value = value
    };
}

/// <summary>Interactive details share the standard calculation and metadata. Source points never enter the standard export contract.</summary>
public sealed record AnalysisDetailRun(StandardAnalysisRun Summary,
    IReadOnlyList<ModuleResult<JoystickUsageResult>> Joysticks, ModuleResult<ActiveUsageResult> ActiveUsage);
