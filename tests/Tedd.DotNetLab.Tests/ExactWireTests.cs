using System.Text.Json;
using Tedd.DotNetLab.Contracts;
using Xunit;

namespace Tedd.DotNetLab.Tests;

public sealed class ExactWireTests
{
    private record IntegerEnvelope(long Signed,ulong Unsigned);
    [Fact, Case("CT-WIRE-EXACT", "FR-062", "M0", "Contract")]
    public void Int64AndSeed_ExtremaRoundTripAsDecimalStrings()
    {
        var value=new IntegerEnvelope(long.MinValue,ulong.MaxValue);
        string json=Wire.Serialize(value);
        Assert.Equal(value,Wire.Deserialize<IntegerEnvelope>(json));
        using var document=JsonDocument.Parse(json);
        Assert.Equal(JsonValueKind.String,document.RootElement.GetProperty("signed").ValueKind);
        Assert.Equal(JsonValueKind.String,document.RootElement.GetProperty("unsigned").ValueKind);
        Assert.Throws<JsonException>(()=>Wire.Deserialize<long>("9007199254740992"));
        Assert.Throws<JsonException>(()=>Wire.Deserialize<ulong>("18446744073709551615"));
        Assert.Equal(42UL,Wire.Deserialize<ulong>("42"));
        foreach(string text in new[]{"\"+1\"","\"01\"","\" 1\"","\"-0\""})
            Assert.Throws<JsonException>(()=>Wire.Deserialize<long>(text));
    }
}
