namespace WrapTuneMacOS.Packaging.Tests;

/// <summary>
/// Serialize→parse round-trips for Detection.xml. The parser feeds the reader
/// half of the verification ladder, so every field the writer emits must
/// survive a round-trip — otherwise the ladder is blind to serialization bugs.
/// </summary>
public sealed class DetectionXmlTests
{
    private static EncryptionInfo SampleEncryptionInfo() => new()
    {
        EncryptionKey = Convert.ToBase64String(new byte[32]),
        MacKey = Convert.ToBase64String(new byte[32]),
        InitializationVector = Convert.ToBase64String(new byte[16]),
        Mac = Convert.ToBase64String(new byte[32]),
        FileDigest = Convert.ToBase64String(new byte[32]),
    };

    [Fact]
    public void MsiInfo_survives_serialize_then_parse()
    {
        var original = new DetectionXml
        {
            Name = "app.msi",
            UnencryptedContentSize = 12345,
            SetupFile = "app.msi",
            EncryptionInfo = SampleEncryptionInfo(),
            MsiInfo = new MsiInfo
            {
                MsiProductCode = "{11111111-2222-3333-4444-555555555555}",
                MsiProductVersion = "1.2.3",
                MsiPackageCode = "{66666666-7777-8888-9999-AAAAAAAAAAAA}",
                MsiUpgradeCode = "{BBBBBBBB-CCCC-DDDD-EEEE-FFFFFFFFFFFF}",
                MsiPublisher = "Acme Corp",
                MsiExecutionContext = 2,
                MsiRequiresLogon = false,
                MsiRequiresReboot = true,
                MsiIsMachineInstall = true,
                MsiIsUserInstall = true,
                MsiIncludesServices = false,
                MsiContainsSystemRegistryKeys = false,
                MsiContainsSystemFolders = false,
            },
        };

        var parsed = DetectionXml.Parse(original.ToBytes());

        Assert.NotNull(parsed.MsiInfo);
        var m = parsed.MsiInfo!;
        Assert.Equal("{11111111-2222-3333-4444-555555555555}", m.MsiProductCode);
        Assert.Equal("1.2.3", m.MsiProductVersion);
        Assert.Equal("{66666666-7777-8888-9999-AAAAAAAAAAAA}", m.MsiPackageCode);
        Assert.Equal("{BBBBBBBB-CCCC-DDDD-EEEE-FFFFFFFFFFFF}", m.MsiUpgradeCode);
        Assert.Equal("Acme Corp", m.MsiPublisher);
        Assert.Equal(2, m.MsiExecutionContext);
        Assert.False(m.MsiRequiresLogon);
        Assert.True(m.MsiRequiresReboot);
        Assert.True(m.MsiIsMachineInstall);
        Assert.True(m.MsiIsUserInstall);
        Assert.False(m.MsiIncludesServices);
        Assert.False(m.MsiContainsSystemRegistryKeys);
        Assert.False(m.MsiContainsSystemFolders);
    }

    [Fact]
    public void Parse_without_MsiInfo_yields_null()
    {
        var original = new DetectionXml
        {
            Name = "setup.exe",
            UnencryptedContentSize = 1,
            SetupFile = "setup.exe",
            EncryptionInfo = SampleEncryptionInfo(),
        };

        Assert.Null(DetectionXml.Parse(original.ToBytes()).MsiInfo);
    }
}
