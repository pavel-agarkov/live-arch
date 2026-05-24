using LiveArch.Deployment.Export.Testing;
using Xunit;

namespace LiveArch.Order.Deployment.Tests;

public class ExportedDeploymentTests
{
    [Fact]
    public async Task ProcessAsync_Should_Create_Resources()
    {
        var mocks = await ExportedDeploymentTestHost.ExecuteAsync(() => global::LiveArch.Order.Deployment.ExportedDeployment.ProcessAsync());

        Assert.NotEmpty(mocks.Resources);
    }
}
