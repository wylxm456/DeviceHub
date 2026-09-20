using DeviceHub.Drivers.S7;
using Xunit;

namespace DeviceHub.Tests.S7;

public class S7AddressTests
{
    [Theory]
    [InlineData("DB1.DBD0", 1, 0, null)]
    [InlineData("DB5.DBW100", 5, 100, null)]
    [InlineData("db1.dbd8", 1, 8, null)]
    [InlineData("DB1.DBX12.0", 1, 12, 0)]
    [InlineData("DB1.DBX12.7", 1, 12, 7)]
    public void Parse_ValidAddress_ShouldReturnExpectedLocation(
        string address, int db, int byteOffset, int? bit)
    {
        var location = S7Address.Parse(address);

        Assert.Equal(db, location.Db);
        Assert.Equal(byteOffset, location.ByteOffset);
        Assert.Equal(bit, location.Bit);
    }

    [Theory]
    [InlineData("DB")]
    [InlineData("DB1")]
    [InlineData("DB1.")]
    [InlineData("MD100")]
    [InlineData("DB1.DBD")]
    [InlineData("DB1.DBDX")]
    [InlineData("DB1.DBX12")]
    [InlineData("DB1.DBX12.8")]
    [InlineData("DB1.DBU0")]
    public void Parse_InvalidAddress_ShouldThrowFormatException(string address)
    {
        Assert.Throws<FormatException>(() => S7Address.Parse(address));
    }
}
