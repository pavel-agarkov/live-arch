using LiveArch.Deployment.Configuration;

namespace LiveArch.Deployment.TestRunner
{
    internal sealed class TestDeploymentVariablesProvider(IReadOnlyDictionary<string, object> values) : IDeploymentVariablesProvider
    {
        public IReadOnlyDictionary<string, object> GetVariables() => values;
    }
}
