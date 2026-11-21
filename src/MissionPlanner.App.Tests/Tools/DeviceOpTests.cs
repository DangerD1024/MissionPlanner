using MissionPlanner.App.Core.ViewModels.Tools;

namespace MissionPlanner.App.Tests.Tools;

[TestClass]
public class DeviceOpTests
{
    [TestMethod]
    public void RegisterBytesAreTypedInHexOrDecimal()
    {
        Assert.AreEqual((byte)0x1E, DeviceOpViewModel.ParseByte("0x1E"));
        Assert.AreEqual((byte)0x1E, DeviceOpViewModel.ParseByte("1eh"));
        Assert.AreEqual((byte)30, DeviceOpViewModel.ParseByte(" 30 "));
        Assert.IsNull(DeviceOpViewModel.ParseByte("0x1FF"));
        Assert.IsNull(DeviceOpViewModel.ParseByte("x"));
    }
}
