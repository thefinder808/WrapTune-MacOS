using WrapTuneMacOS.Packaging.Msi;

namespace WrapTuneMacOS.Packaging.Tests;

/// <summary>
/// Deterministic unit tests for the MSI stream-name de-mangler — no fixture
/// needed. Inputs are built from explicit code points so the vectors are
/// unambiguous; they pin the encoding against accidental regression.
/// </summary>
public sealed class MsiStreamNameTests
{
    private static string Ch(int codePoint) => ((char)codePoint).ToString();

    [Fact]
    public void Single_encoded_char_decodes_one_alphabet_char()
    {
        Assert.Equal("0", MsiStreamName.Decode(Ch(0x4800)));   // index 0  → '0'
        Assert.Equal("A", MsiStreamName.Decode(Ch(0x480A)));   // index 10 → 'A'
        Assert.Equal("_", MsiStreamName.Decode(Ch(0x483F)));   // index 63 → '_'
    }

    [Fact]
    public void Two_encoded_chars_decode_low_then_high()
    {
        Assert.Equal("00", MsiStreamName.Decode(Ch(0x3800)));  // low 0, high 0
        // low=10 ('A'), high=11 ('B') ⇒ 0x3800 + (10 | (11<<6)) = 0x3ACA
        Assert.Equal("AB", MsiStreamName.Decode(Ch(0x3ACA)));
    }

    [Fact]
    public void Literal_and_control_chars_pass_through()
    {
        Assert.Equal("AB", MsiStreamName.Decode("AB"));
        Assert.Equal("SummaryInformation", MsiStreamName.Decode("SummaryInformation"));
    }
}
