using Microsoft.Win32;
using WinMonitor.Core;

namespace WinMonitor.Config;

/// <summary>
/// Exact-machine EC defaults backed by firmware field names and live read validation.
/// Never use broad vendor-only matching: EC maps can change between closely related models.
/// </summary>
internal static class KnownEcProfiles
{
    private const string Lg16T90RProfile = "lg-16t90r-gram360-gp";

    public static void ApplyOnce(EcConfig config)
    {
        if (!string.IsNullOrEmpty(config.AppliedDefaultProfile)) return;
        if (!IsLg16T90R()) return;

        // Mark the model even when the user already has custom sensors. This prevents a later
        // deletion of those sensors from unexpectedly injecting the catalog default.
        config.AppliedDefaultProfile = Lg16T90RProfile;
        if (config.Sensors.Count != 0) return;

        // DSDT declares ERAM 0xB0/0xB1 as RPM1/RPM2. Focused reads on BIOS GP121 produced
        // 0x10D0 +/- a few counts (about 4,300 RPM); BE16 is physically impossible (>53k RPM).
        config.Sensors.Add(new EcSensorDef
        {
            Enabled = true,
            Register = 0xB0,
            Name = "CPU Fan",
            NameKey = "ec.default_name",
            Kind = EcValueKind.RpmDirect,
            BigEndian = false,
            Scale = 1f,
            Offset = 0f,
            Quantity = SensorQuantity.Fan,
        });
        config.Enabled = true;
    }

    private static bool IsLg16T90R()
    {
        try
        {
            using RegistryKey? bios =
                Registry.LocalMachine.OpenSubKey(@"HARDWARE\DESCRIPTION\System\BIOS");
            string manufacturer = bios?.GetValue("SystemManufacturer")?.ToString() ?? "";
            string product = bios?.GetValue("SystemProductName")?.ToString() ?? "";
            string family = bios?.GetValue("SystemFamily")?.ToString() ?? "";
            string board = bios?.GetValue("BaseBoardProduct")?.ToString() ?? "";
            string version = bios?.GetValue("BIOSVersion")?.ToString() ?? "";

            return manufacturer.Equals("LG Electronics", StringComparison.OrdinalIgnoreCase)
                && product.StartsWith("16T90R-", StringComparison.OrdinalIgnoreCase)
                && family.Equals("gram360", StringComparison.OrdinalIgnoreCase)
                && board.Equals("16T90R", StringComparison.OrdinalIgnoreCase)
                && version.StartsWith("GP", StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }
}
