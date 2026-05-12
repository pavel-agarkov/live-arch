using Structurizr;

namespace LiveArch.Deployment.Adapters
{
    public class ElementAdapter : BaseDeploymentAdapter<Element>
    {
        public ElementAdapter(Element node, Func<string, object> substituteVariables) : base(node, substituteVariables)
        {
        }
    }
}
