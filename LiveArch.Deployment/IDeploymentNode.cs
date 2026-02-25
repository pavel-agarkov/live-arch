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

    }

    public class DeploymentNodeAdapter : IDeploymentNode
    {
        private readonly DeploymentNode node;

        public DeploymentNodeAdapter(DeploymentNode node)
        {
            this.node = node;
        }

        public Element Node => node;
        public string Name => node.Name;
        public string Technology => node.Technology;
        public IDictionary<string, string> Properties => node.Properties;

        public ISet<Relationship> Relationships => node.Relationships;

        public IDeploymentNode? Parent => node.Parent != null ? new ElementAdapter(node.Parent) : null;
    }

    public class ElementAdapter : IDeploymentNode
    {
        private readonly Element node;

        public Element Node => node;

        public string Name => node.Name;

        public string Technology =>
              node is DeploymentNode dn ? dn.Technology
            : node is InfrastructureNode ind ? ind.Technology
            : node is ContainerInstance ctn ? ctn.Container.Technology
            : string.Empty;

        public IDictionary<string, string> Properties => node.Properties;

        public ISet<Relationship> Relationships => node.Relationships;

        public IDeploymentNode? Parent => node.Parent != null ? new ElementAdapter(node.Parent) : null;

        public ElementAdapter(Element node)
        {
            this.node = node;
        }
    }

    public class InfrastructureNodeAdapter : IDeploymentNode
    {
        private readonly InfrastructureNode node;

        public InfrastructureNodeAdapter(InfrastructureNode node)
        {
            this.node = node;
        }

        public Element Node => node;
        public string Name => node.Name;
        public string Technology => node.Technology;
        public IDictionary<string, string> Properties => node.Properties;

        public ISet<Relationship> Relationships => node.Relationships;

        public IDeploymentNode? Parent => node.Parent != null ? new ElementAdapter(node.Parent) : null;
    }

    public class ContainerInstanceAdapter : IDeploymentNode
    {
        private readonly ContainerInstance node;

        public ContainerInstanceAdapter(ContainerInstance node)
        {
            this.node = node;
        }

        public Element Node => node;
        public string Name => node.Name;
        public string Technology => node.Container.Technology;
        public IDictionary<string, string> Properties => node.Properties;

        public ISet<Relationship> Relationships => node.Relationships;

        public IDeploymentNode? Parent => node.Parent != null ? new ElementAdapter(node.Parent) : null;
    }

    public class ContainerBuildAdapter : IDeploymentNode
    {
        private readonly Container node;

        public ContainerBuildAdapter(Container node)
        {
            this.node = node;
        }

        public Element Node => node;
        public string Name => node.Name;
        public string Technology => node.Properties.FirstOrDefault(x => x.Key == "buildTechnology").Value ?? string.Empty;
        public IDictionary<string, string> Properties => node.Properties;

        public ISet<Relationship> Relationships => node.Relationships;

        public IDeploymentNode? Parent => node.Parent != null ? new ElementAdapter(node.Parent) : null;
    }
}
