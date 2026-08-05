using System.Text;
using Microsoft.Win32;

internal static class AmlEcFieldScanner
{
    private const byte ExtOpPrefix = 0x5B;
    private const byte OperationRegionOp = 0x80;
    private const byte FieldOp = 0x81;
    private const byte EmbeddedControlSpace = 0x03;

    public static string CreateReport()
    {
        var tables = new List<(string Name, byte[] Data)>();
        ReadTables(@"HARDWARE\ACPI\DSDT", tables);
        ReadTables(@"HARDWARE\ACPI\SSDT", tables);

        var regions = new HashSet<string>(StringComparer.Ordinal);
        var regionRows = new List<string>();
        foreach ((string tableName, byte[] data) in tables)
        {
            for (int i = 0; i + 8 < data.Length; i++)
            {
                if (data[i] != ExtOpPrefix || data[i + 1] != OperationRegionOp) continue;
                int cursor = i + 2;
                if (!TryReadNameString(data, ref cursor, data.Length, out string path)) continue;
                if (cursor >= data.Length || data[cursor] != EmbeddedControlSpace) continue;
                string name = LastNameSegment(path);
                if (name.Length != 4) continue;
                regions.Add(name);
                regionRows.Add($"{tableName}: OperationRegion {path} @ AML 0x{i:X}");
            }
        }

        var fields = new List<FieldRow>();
        foreach ((string tableName, byte[] data) in tables)
            ScanFields(tableName, data, regions, fields);

        var output = new StringBuilder();
        output.AppendLine($"ACPI tables: {tables.Count}");
        output.AppendLine($"EmbeddedControl regions: {string.Join(", ", regions.OrderBy(x => x))}");
        foreach (string row in regionRows) output.AppendLine(row);
        output.AppendLine();
        output.AppendLine("Fields backed by EmbeddedControl (byte.bit, width bits):");
        foreach (FieldRow row in fields
            .OrderBy(f => f.ByteOffset)
            .ThenBy(f => f.BitOffset)
            .ThenBy(f => f.Name, StringComparer.Ordinal))
        {
            output.AppendLine(
                $"0x{row.ByteOffset:X2}.{row.BitOffset}  {row.WidthBits,4}  {row.Name,-8}"
                + $" region={row.Region} table={row.Table}");
        }
        return output.ToString();
    }

    private static void ReadTables(string registryPath, List<(string Name, byte[] Data)> tables)
    {
        try
        {
            using RegistryKey? root = Registry.LocalMachine.OpenSubKey(registryPath);
            if (root is not null) Walk(root, registryPath, tables);
        }
        catch
        {
            // ACPI registry visibility differs across Windows policies; missing tables are benign.
        }
    }

    private static void Walk(
        RegistryKey key,
        string path,
        List<(string Name, byte[] Data)> tables)
    {
        foreach (string valueName in key.GetValueNames())
        {
            if (key.GetValue(valueName) is byte[] data && data.Length >= 36)
                tables.Add(($"{path}\\{valueName}", data));
        }
        foreach (string subName in key.GetSubKeyNames())
        {
            try
            {
                using RegistryKey? child = key.OpenSubKey(subName);
                if (child is not null) Walk(child, path + "\\" + subName, tables);
            }
            catch
            {
                // Best-effort diagnostic: one denied table must not hide the readable ones.
            }
        }
    }

    private static void ScanFields(
        string tableName,
        byte[] data,
        HashSet<string> regions,
        List<FieldRow> output)
    {
        for (int i = 0; i + 8 < data.Length; i++)
        {
            if (data[i] != ExtOpPrefix || data[i + 1] != FieldOp) continue;
            int packageStart = i + 2;
            if (!TryReadPackageLength(data, packageStart, out int packageLength, out int lengthBytes))
                continue;
            int packageEnd = packageStart + packageLength;
            if (packageEnd > data.Length || packageEnd <= packageStart + lengthBytes) continue;

            int cursor = packageStart + lengthBytes;
            if (!TryReadNameString(data, ref cursor, packageEnd, out string regionPath)) continue;
            string region = LastNameSegment(regionPath);
            if (!regions.Contains(region) || cursor >= packageEnd) continue;

            cursor++; // FieldFlags
            int bitPosition = 0;
            while (cursor < packageEnd)
            {
                byte op = data[cursor];
                if (op == 0x00) // ReservedField
                {
                    cursor++;
                    if (!TryReadPackageLength(data, cursor, out int bits, out int bytes)) break;
                    cursor += bytes;
                    bitPosition += bits;
                    continue;
                }
                if (op == 0x01) // AccessField
                {
                    if (cursor + 3 > packageEnd) break;
                    cursor += 3;
                    continue;
                }
                if (op == 0x02) // ConnectField: uncommon in EC maps; stop this package safely.
                    break;
                if (op == 0x03) // ExtendedAccessField
                {
                    if (cursor + 4 > packageEnd) break;
                    cursor += 4;
                    continue;
                }
                if (cursor + 4 > packageEnd || !IsNameSegment(data, cursor)) break;

                string name = Encoding.ASCII.GetString(data, cursor, 4);
                cursor += 4;
                if (!TryReadPackageLength(data, cursor, out int widthBits, out int widthBytes)) break;
                cursor += widthBytes;
                output.Add(new FieldRow(
                    tableName,
                    region,
                    name,
                    bitPosition / 8,
                    bitPosition % 8,
                    widthBits));
                bitPosition += widthBits;
            }
        }
    }

    private static bool TryReadPackageLength(
        byte[] data,
        int offset,
        out int length,
        out int bytesRead)
    {
        length = 0;
        bytesRead = 0;
        if ((uint)offset >= (uint)data.Length) return false;
        byte lead = data[offset];
        int follow = lead >> 6;
        bytesRead = follow + 1;
        if (offset + bytesRead > data.Length) return false;
        if (follow == 0)
        {
            length = lead & 0x3F;
            return true;
        }

        length = lead & 0x0F;
        int shift = 4;
        for (int i = 0; i < follow; i++)
        {
            length |= data[offset + 1 + i] << shift;
            shift += 8;
        }
        return true;
    }

    private static bool TryReadNameString(
        byte[] data,
        ref int cursor,
        int end,
        out string path)
    {
        path = "";
        if (cursor >= end) return false;
        var text = new StringBuilder();
        if (data[cursor] == 0x5C)
        {
            text.Append('\\');
            cursor++;
        }
        while (cursor < end && data[cursor] == 0x5E)
        {
            text.Append('^');
            cursor++;
        }
        if (cursor >= end) return false;
        if (data[cursor] == 0x00)
        {
            cursor++;
            path = text.ToString();
            return true;
        }

        int segmentCount = 1;
        if (data[cursor] == 0x2E)
        {
            segmentCount = 2;
            cursor++;
        }
        else if (data[cursor] == 0x2F)
        {
            if (++cursor >= end) return false;
            segmentCount = data[cursor++];
        }
        if (segmentCount <= 0 || cursor + segmentCount * 4 > end) return false;

        for (int i = 0; i < segmentCount; i++)
        {
            if (!IsNameSegment(data, cursor)) return false;
            if (i > 0) text.Append('.');
            text.Append(Encoding.ASCII.GetString(data, cursor, 4));
            cursor += 4;
        }
        path = text.ToString();
        return true;
    }

    private static bool IsNameSegment(byte[] data, int offset)
    {
        if (offset + 4 > data.Length) return false;
        if (!IsLeadNameChar(data[offset])) return false;
        for (int i = 1; i < 4; i++)
            if (!IsNameChar(data[offset + i])) return false;
        return true;
    }

    private static bool IsLeadNameChar(byte value)
        => value == (byte)'_' || value is >= (byte)'A' and <= (byte)'Z';

    private static bool IsNameChar(byte value)
        => IsLeadNameChar(value) || value is >= (byte)'0' and <= (byte)'9';

    private static string LastNameSegment(string path)
    {
        int dot = path.LastIndexOf('.');
        string segment = dot >= 0 ? path[(dot + 1)..] : path;
        return segment.TrimStart('\\', '^');
    }

    private sealed record FieldRow(
        string Table,
        string Region,
        string Name,
        int ByteOffset,
        int BitOffset,
        int WidthBits);
}
