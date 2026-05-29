namespace WrapTuneMacOS.Packaging.Msi;

/// <summary>
/// Extracts the MSI metadata Intune records in Detection.xml — pure managed, no
/// Windows Installer APIs. Returns null on any read failure; callers must treat
/// null as "no metadata available" and never substitute guessed values.
/// </summary>
public static class MsiPropertyReader
{
    public static MsiInfo? TryRead(string msiPath)
    {
        try
        {
            var db = new MsiDatabase(File.ReadAllBytes(msiPath));
            var props = db.ReadProperties();
            if (props.Count == 0) return null;

            string? Get(string key) => props.TryGetValue(key, out var v) && v.Length > 0 ? v : null;

            return new MsiInfo
            {
                MsiProductCode = Get("ProductCode"),
                MsiProductVersion = Get("ProductVersion"),
                MsiUpgradeCode = Get("UpgradeCode"),
                MsiPublisher = Get("Manufacturer"),
                MsiPackageCode = db.ReadPackageCode(),
                MsiExecutionContext = 0,
                MsiIsMachineInstall = true,
            };
        }
        catch
        {
            return null;
        }
    }
}
