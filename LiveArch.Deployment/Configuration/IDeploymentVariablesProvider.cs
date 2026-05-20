namespace LiveArch.Deployment.Configuration
{
    public interface IDeploymentVariablesProvider
    {
        IReadOnlyDictionary<string, object> GetVariables();
    }
}
