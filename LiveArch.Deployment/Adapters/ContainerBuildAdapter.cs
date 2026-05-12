using Structurizr;

namespace LiveArch.Deployment.Adapters
{
    public class ContainerBuildAdapter : BaseDeploymentAdapter<Container>
    {
        public ContainerBuildAdapter(Container node, Func<string, object> substituteVariables) : base(node, substituteVariables)
        {
        }

        public override string Technology => substituteVariables(node.Properties.FirstOrDefault(x => x.Key == "buildTechnology").Value ?? string.Empty).ToString()!;
    }
}
