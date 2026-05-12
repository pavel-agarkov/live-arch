using Structurizr;

namespace LiveArch.Deployment.Adapters
{
    public abstract class BaseDeploymentAdapter<TNode> : IDeploymentAdapter where TNode : Element
    {
        protected readonly TNode node;
        protected readonly Func<string, object> substituteVariables;
        private readonly IReadOnlyCollection<RelationshipAdapter> relationships;

        protected BaseDeploymentAdapter(TNode node, Func<string, object> substituteVariables)
        {
            this.node = node;
            this.substituteVariables = substituteVariables;
            relationships = [..
                node.Relationships
                    .Select(relationship => new RelationshipAdapter(relationship, substituteVariables))
                    .Where(relationship => !relationship.IsDisabled)];
        }

        public bool IsDisabled =>
            Properties.TryGetValue("isDisabled", out var isDisabledString) &&
            bool.TryParse(substituteVariables(isDisabledString).ToString(), out var isDisabled) &&
            isDisabled;

        public IReadOnlyCollection<IDeploymentAdapter> Parents => node.Parent != null ? [new ElementAdapter(node.Parent, substituteVariables)] : Array.Empty<IDeploymentAdapter>();
        public IDictionary<string, string> Properties => node.Properties;

        public ModelItem Node => node;

        public IReadOnlyCollection<RelationshipAdapter> Relationships => relationships;
        public virtual string Technology => substituteVariables(
              node is DeploymentNode dn ? dn.Technology
            : node is InfrastructureNode ind ? ind.Technology
            : node is ContainerInstance ctn ? ctn.Container.Technology
            : string.Empty).ToString()!;
    }
}
