using Plainspoken.Core;

namespace Plainspoken.Core.Tests;

public class AppInfoTests
{
    [Fact]
    public void Version_comes_from_Directory_Build_props()
    {
        Assert.Matches(@"^\d+\.\d+\.\d+", AppInfo.Version);
        Assert.Equal("Plainspoken", AppInfo.Name);
    }
}
