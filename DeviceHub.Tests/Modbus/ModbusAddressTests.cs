using DeviceHub.Drivers.Modbus;
using Xunit;

namespace DeviceHub.Tests.Modbus;

public class ModbusAddressTests
{
    [Theory]
    [InlineData("HR0", (ushort)0)]
    [InlineData("HR10", (ushort)10)]
    [InlineData("hr65535", (ushort)65535)]
    [InlineData("40001", (ushort)0)]
    [InlineData("40011", (ushort)10)]
    [InlineData("49999", (ushort)9998)]
    [InlineData(" 40001 ", (ushort)0)]
    public void Parse_ValidAddress_ShouldReturnRegister(string address, ushort expected)
    {
        Assert.Equal(expected, ModbusAddress.Parse(address));
    }

    [Theory]
    [InlineData("HR")]
    [InlineData("HR-1")]
    [InlineData("40000")]
    [InlineData("50001")]
    [InlineData("DB1.DBD0")]
    [InlineData("IR0")]
    public void Parse_InvalidAddress_ShouldThrowFormat(string address)
    {
        Assert.Throws<FormatException>(() => ModbusAddress.Parse(address));
    }
}
