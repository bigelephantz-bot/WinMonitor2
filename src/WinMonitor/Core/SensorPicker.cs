namespace WinMonitor.Core;

/// <summary>
/// Shared auto-selection logic for tray icons / compact view with no explicit sensor
/// configured. All consumers must use this so smart polling stays in sync with rendering.
/// </summary>
public static class SensorPicker
{
    /// <summary>Auto-pick shown sensor: CPU temp named *Package* -> any CPU temp -> any temp -> null.</summary>
    public static string? PickAuto(IReadOnlyList<SensorDescriptor> descriptors)
    {
        string? cpuTemp = null;
        string? anyTemp = null;
        for (int i = 0; i < descriptors.Count; i++)
        {
            var d = descriptors[i];
            if (d.Quantity != SensorQuantity.Temperature) continue;
            anyTemp ??= d.Id;
            if (d.Category != SensorCategory.Cpu) continue;
            cpuTemp ??= d.Id;
            if (d.Name.Contains("Package", StringComparison.OrdinalIgnoreCase))
                return d.Id;
        }
        return cpuTemp ?? anyTemp;
    }
}
