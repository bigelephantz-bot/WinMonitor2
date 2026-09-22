namespace WinMonitor.Core;

/// <summary>
/// Immutable metadata for one measurement definition in this run. SourceId remains the stable
/// configuration/UI id; HistoryId separates calibration or quantity revisions in the spool.
/// Retired revisions keep their labels. Current-revision labels can be replaced on a cold
/// metadata refresh; already-captured exports retain these immutable records unchanged.
/// </summary>
public sealed record SessionSensorInfo(
    string SourceId, string HistoryId, string HardwareName, string Name, string DisplayName,
    SensorCategory Category, SensorQuantity Quantity, string MeasurementKey, int Revision);

/// <summary>
/// State of the current definition. A null sample is an observed failure; an omitted sample in
/// a sparse tick leaves the previous observation intact. Removal changes IsPresent to false.
/// </summary>
public readonly record struct SensorObservationState(
    bool IsPresent, bool IsAvailable, DateTime? LastObservedUtc, int Revision,
    SensorQuantity Quantity, string MeasurementKey);

/// <summary>Valid prefix of a caller-owned chart buffer; its tail must never be plotted.</summary>
public readonly record struct HistoryWindowReadResult(long Version, int Count);
