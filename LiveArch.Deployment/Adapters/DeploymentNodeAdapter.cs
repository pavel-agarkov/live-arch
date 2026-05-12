using Structurizr;

namespace LiveArch.Deployment.Adapters
{
    public class DeploymentNodeAdapter : BaseDeploymentAdapter<DeploymentNode>
    {
        public DeploymentNodeAdapter(DeploymentNode node, Func<string, object> substituteVariables) : base(node, substituteVariables)
        {
        }
    }
}
