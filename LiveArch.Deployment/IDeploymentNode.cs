using Structurizr;
using System;
using System.Collections.Generic;
using System.Linq;

namespace LiveArch.Deployment
{
    public interface IDeploymentNode
    {
        Element Node { get; }
        string Name { get; }
        string Technology { get; }
        IDictionary<string, string> Properties { get; }
        ISet<Relationship> Relationships { get; }
        IDeploymentNode? Parent { get; }

        bool IsDisabled { get; }

    }

    public abstract class DeploymentAdapter<TNode> : IDeploymentNode where TNode : Element
    {
        protected readonly TNode node;

        protected DeploymentAdapter(TNode node)
        {
            this.node = node;
        }

        public bool IsDisabled =>
            Properties.TryGetValue("isDisabled", out var isDisabledString) &&
            bool.TryParse(isDisabledString, out var isDisabled) &&
            isDisabled;

        public string Name => node.Name;

        public Element Node => node;

        public IDeploymentNode? Parent => node.Parent != null ? new ElementAdapter(node.Parent) : null;
        public IDictionary<string, string> Properties => node.Properties;

        public ISet<Relationship> Relationships => node.Relationships;
        public virtual string Technology =>
              node is DeploymentNode dn ? dn.Technology
            : node is InfrastructureNode ind ? ind.Technology
            : node is ContainerInstance ctn ? ctn.Container.Technology
            : string.Empty;
    }

    public class DeploymentNodeAdapter : DeploymentAdapter<DeploymentNode>
    {
        public DeploymentNodeAdapter(DeploymentNode node) : base(node)
        {
        }
    }

    public class ElementAdapter : DeploymentAdapter<Element>
    {
        public ElementAdapter(Element node) : base(node)
        {
        }
    }

    public class InfrastructureNodeAdapter : DeploymentAdapter<InfrastructureNode>
    {
        public InfrastructureNodeAdapter(InfrastructureNode node) : base(node)
        {
        }
    }

    public class ContainerInstanceAdapter : DeploymentAdapter<ContainerInstance>
    {
        public ContainerInstanceAdapter(ContainerInstance node) : base(node)
        {
        }
    }

    public class ContainerBuildAdapter : DeploymentAdapter<Container>
    {
        public ContainerBuildAdapter(Container node):base(node)
        {
        }

        public override string Technology => node.Properties.FirstOrDefault(x => x.Key == "buildTechnology").Value ?? string.Empty;
    }
}
