using Structurizr;

namespace LiveArch.Deployment.Adapters
{
    public class ContainerInstanceAdapter : BaseDeploymentAdapter<ContainerInstance>
    {
        public ContainerInstanceAdapter(ContainerInstance node, Func<string, object> substituteVariables) : base(node, substituteVariables)
        {
        }
    }
}
