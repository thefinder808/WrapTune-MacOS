using System.Text;
using System.Xml;
using System.Xml.Linq;

namespace WrapTuneMacOS.Packaging;

/// <summary>Crypto material recorded in <c>Detection.xml</c> (all values base64).</summary>
public sealed class EncryptionInfo
{
    public required string EncryptionKey { get; init; }
    public required string MacKey { get; init; }
    public required string InitializationVector { get; init; }
    public required string Mac { get; init; }
    public string ProfileIdentifier { get; init; } = "ProfileVersion1";
    public required string FileDigest { get; init; }
    public string FileDigestAlgorithm { get; init; } = "SHA256";
}

/// <summary>
/// MSI metadata, emitted only when the setup file is a <c>.msi</c>.
/// Populated by <c>Msi/MsiPropertyReader</c> (a later phase); null otherwise.
/// </summary>
public sealed class MsiInfo
{
    public string? MsiProductCode { get; init; }
    public string? MsiProductVersion { get; init; }
    public string? MsiPackageCode { get; init; }
    public string? MsiUpgradeCode { get; init; }
    public string? MsiPublisher { get; init; }
    public int MsiExecutionContext { get; init; }
    public bool MsiRequiresLogon { get; init; }
    public bool MsiRequiresReboot { get; init; }
    public bool MsiIsMachineInstall { get; init; } = true;
    public bool MsiIsUserInstall { get; init; }
    public bool MsiIncludesServices { get; init; }
    public bool MsiContainsSystemRegistryKeys { get; init; }
    public bool MsiContainsSystemFolders { get; init; }
}

/// <summary>
/// The <c>Detection.xml</c> document: the metadata Intune reads from a
/// <c>.intunewin</c> package. Serializes to / parses from the exact element
/// shape the official Content Prep Tool produces.
/// </summary>
public sealed class DetectionXml
{
    public const string ContentFileName = "IntunePackage.intunewin";

    public required string Name { get; init; }
    public required long UnencryptedContentSize { get; init; }
    public string FileName { get; init; } = ContentFileName;
    public required string SetupFile { get; init; }
    public string ToolVersion { get; init; } = IntuneWinWriter.ToolVersion;
    public required EncryptionInfo EncryptionInfo { get; init; }
    public MsiInfo? MsiInfo { get; init; }

    private static readonly XNamespace Xsi = "http://www.w3.org/2001/XMLSchema-instance";
    private static readonly XNamespace Xsd = "http://www.w3.org/2001/XMLSchema";

    /// <summary>Serialize to a UTF-8 byte array (matches the official tool's encoding).</summary>
    public byte[] ToBytes()
    {
        var enc = new XElement("EncryptionInfo",
            new XElement("EncryptionKey", EncryptionInfo.EncryptionKey),
            new XElement("MacKey", EncryptionInfo.MacKey),
            new XElement("InitializationVector", EncryptionInfo.InitializationVector),
            new XElement("Mac", EncryptionInfo.Mac),
            new XElement("ProfileIdentifier", EncryptionInfo.ProfileIdentifier),
            new XElement("FileDigest", EncryptionInfo.FileDigest),
            new XElement("FileDigestAlgorithm", EncryptionInfo.FileDigestAlgorithm));

        var root = new XElement("ApplicationInfo",
            new XAttribute(XNamespace.Xmlns + "xsi", Xsi.NamespaceName),
            new XAttribute(XNamespace.Xmlns + "xsd", Xsd.NamespaceName),
            new XAttribute("ToolVersion", ToolVersion),
            new XElement("Name", Name),
            new XElement("UnencryptedContentSize", UnencryptedContentSize),
            new XElement("FileName", FileName),
            new XElement("SetupFile", SetupFile),
            enc);

        if (MsiInfo is { } msi)
            root.Add(MsiElement(msi));

        var doc = new XDocument(new XDeclaration("1.0", "utf-8", null), root);

        using var ms = new MemoryStream();
        var settings = new XmlWriterSettings { Encoding = new UTF8Encoding(false), Indent = true };
        using (var writer = XmlWriter.Create(ms, settings))
            doc.Save(writer);
        return ms.ToArray();
    }

    private static XElement MsiElement(MsiInfo m) => new("MsiInfo",
        new XElement("MsiProductCode", m.MsiProductCode),
        new XElement("MsiProductVersion", m.MsiProductVersion),
        new XElement("MsiPackageCode", m.MsiPackageCode),
        new XElement("MsiUpgradeCode", m.MsiUpgradeCode),
        new XElement("MsiExecutionContext", m.MsiExecutionContext),
        new XElement("MsiRequiresLogon", m.MsiRequiresLogon),
        new XElement("MsiRequiresReboot", m.MsiRequiresReboot),
        new XElement("MsiIsMachineInstall", m.MsiIsMachineInstall),
        new XElement("MsiIsUserInstall", m.MsiIsUserInstall),
        new XElement("MsiIncludesServices", m.MsiIncludesServices),
        new XElement("MsiContainsSystemRegistryKeys", m.MsiContainsSystemRegistryKeys),
        new XElement("MsiContainsSystemFolders", m.MsiContainsSystemFolders),
        new XElement("MsiPublisher", m.MsiPublisher));

    /// <summary>Parse a Detection.xml document (used by the reader / round-trip verifier).</summary>
    public static DetectionXml Parse(byte[] xml)
    {
        var doc = XDocument.Load(new MemoryStream(xml));
        var root = doc.Root ?? throw new FormatException("Detection.xml has no root element.");
        var enc = root.Element("EncryptionInfo")
            ?? throw new FormatException("Detection.xml is missing EncryptionInfo.");

        string Req(XElement parent, string name) =>
            parent.Element(name)?.Value
            ?? throw new FormatException($"Detection.xml is missing {name}.");

        return new DetectionXml
        {
            Name = root.Element("Name")?.Value ?? "",
            UnencryptedContentSize = long.Parse(Req(root, "UnencryptedContentSize")),
            FileName = root.Element("FileName")?.Value ?? ContentFileName,
            SetupFile = root.Element("SetupFile")?.Value ?? "",
            ToolVersion = root.Attribute("ToolVersion")?.Value ?? "",
            EncryptionInfo = new EncryptionInfo
            {
                EncryptionKey = Req(enc, "EncryptionKey"),
                MacKey = Req(enc, "MacKey"),
                InitializationVector = Req(enc, "InitializationVector"),
                Mac = Req(enc, "Mac"),
                ProfileIdentifier = enc.Element("ProfileIdentifier")?.Value ?? "ProfileVersion1",
                FileDigest = Req(enc, "FileDigest"),
                FileDigestAlgorithm = enc.Element("FileDigestAlgorithm")?.Value ?? "SHA256",
            },
            MsiInfo = ParseMsiInfo(root.Element("MsiInfo")),
        };
    }

    private static MsiInfo? ParseMsiInfo(XElement? el)
    {
        if (el is null) return null;

        string? S(string name) => el.Element(name)?.Value;
        int I(string name) => el.Element(name)?.Value is { } v ? XmlConvert.ToInt32(v) : 0;
        // XmlConvert (not bool.Parse): XML booleans are "true"/"false"/"1"/"0".
        bool B(string name, bool whenAbsent = false) =>
            el.Element(name)?.Value is { } v ? XmlConvert.ToBoolean(v) : whenAbsent;

        return new MsiInfo
        {
            MsiProductCode = S("MsiProductCode"),
            MsiProductVersion = S("MsiProductVersion"),
            MsiPackageCode = S("MsiPackageCode"),
            MsiUpgradeCode = S("MsiUpgradeCode"),
            MsiPublisher = S("MsiPublisher"),
            MsiExecutionContext = I("MsiExecutionContext"),
            MsiRequiresLogon = B("MsiRequiresLogon"),
            MsiRequiresReboot = B("MsiRequiresReboot"),
            MsiIsMachineInstall = B("MsiIsMachineInstall", whenAbsent: true),
            MsiIsUserInstall = B("MsiIsUserInstall"),
            MsiIncludesServices = B("MsiIncludesServices"),
            MsiContainsSystemRegistryKeys = B("MsiContainsSystemRegistryKeys"),
            MsiContainsSystemFolders = B("MsiContainsSystemFolders"),
        };
    }
}
