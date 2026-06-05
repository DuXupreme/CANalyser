namespace CanAnalyzer.App.Models;

/// <summary>
/// One row in the actuator (butterfly) tracking matrix: a metric compared across Left/Right/Front.
/// </summary>
public sealed record ActuatorMetricRow(string Metric, string Left, string Right, string Front);
