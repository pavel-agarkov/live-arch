using Pulumi.Testing;

namespace LiveArch.Deployment.Export.Testing
{
    public static class ExportedDeploymentTestHost
    {
        public static async Task<RecordingMocks> ExecuteAsync(Func<Task> processAsync, RecordingMocks? mocks = null, TestOptions? options = null)
        {
            mocks ??= new RecordingMocks();
            options ??= new TestOptions { IsPreview = false };

            await Pulumi.Deployment.TestAsync(mocks, options, processAsync);
            return mocks;
        }
    }
}
