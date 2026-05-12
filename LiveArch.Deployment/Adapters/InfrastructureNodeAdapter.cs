using Structurizr;

namespace LiveArch.Deployment.Adapters
{
    public class InfrastructureNodeAdapter : BaseDeploymentAdapter<InfrastructureNode>
    {
        public InfrastructureNodeAdapter(InfrastructureNode node, Func<string, object> substituteVariables) : base(node, substituteVariables)
        {
        }
    }
}
