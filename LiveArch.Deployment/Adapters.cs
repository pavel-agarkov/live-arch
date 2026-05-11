using Structurizr;
using System;
using System.Collections.Generic;
using System.Linq;

namespace LiveArch.Deployment
{
    public interface IDeploymentNode
    {
        ModelItem Node { get; }
        string Technology { get; }
        IDictionary<string, string> Properties { get; }
        IReadOnlyCollection<RelationshipAdapter> Relationships { get; }
        IReadOnlyCollection<IDeploymentNode> Parents { get; }

        bool IsDisabled { get; }

    }

    public abstract class DeploymentAdapter<TNode> : IDeploymentNode where TNode : Element
    {
        protected readonly TNode node;
        protected readonly Func<string, object> substituteVariables;
        private readonly IReadOnlyCollection<RelationshipAdapter> relationships;

        protected DeploymentAdapter(TNode node, Func<string, object> substituteVariables)
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

        public IReadOnlyCollection<IDeploymentNode> Parents => node.Parent != null ? [new ElementAdapter(node.Parent, substituteVariables)] : Array.Empty<IDeploymentNode>();
        public IDictionary<string, string> Properties => node.Properties;

        public ModelItem Node => node;

        public IReadOnlyCollection<RelationshipAdapter> Relationships => relationships;
        public virtual string Technology => substituteVariables(
              node is DeploymentNode dn ? dn.Technology
            : node is InfrastructureNode ind ? ind.Technology
            : node is ContainerInstance ctn ? ctn.Container.Technology
            : string.Empty).ToString()!;
    }

    public class DeploymentNodeAdapter : DeploymentAdapter<DeploymentNode>
    {
        public DeploymentNodeAdapter(DeploymentNode node, Func<string, object> substituteVariables) : base(node, substituteVariables)
        {
        }
    }

    public class ElementAdapter : DeploymentAdapter<Element>
    {
        public ElementAdapter(Element node, Func<string, object> substituteVariables) : base(node, substituteVariables)
        {
        }
    }

    public class InfrastructureNodeAdapter : DeploymentAdapter<InfrastructureNode>
    {
        public InfrastructureNodeAdapter(InfrastructureNode node, Func<string, object> substituteVariables) : base(node, substituteVariables)
        {
        }
    }

    public class ContainerInstanceAdapter : DeploymentAdapter<ContainerInstance>
    {
        public ContainerInstanceAdapter(ContainerInstance node, Func<string, object> substituteVariables) : base(node, substituteVariables)
        {
        }
    }

    public class ContainerBuildAdapter : DeploymentAdapter<Container>
    {
        public ContainerBuildAdapter(Container node, Func<string, object> substituteVariables) : base(node, substituteVariables)
        {
        }

        public override string Technology => substituteVariables(node.Properties.FirstOrDefault(x => x.Key == "buildTechnology").Value ?? string.Empty).ToString()!;
    }

    public class RelationshipAdapter : IDeploymentNode
    {
        protected readonly Func<string, object> substituteVariables;
        private readonly Relationship relationshipInstance;
        private readonly Relationship relationshipModel;

        public RelationshipAdapter(Relationship relationship, Func<string, object> substituteVariables)
        {
            this.relationshipModel = string.IsNullOrEmpty(relationship.LinkedRelationshipId) ? relationship
                : relationship.Source.Model.Relationships.First(r => r.Id == relationship.LinkedRelationshipId);
            relationshipInstance = relationship;
            this.substituteVariables = substituteVariables;
        }

        public bool IsDisabled =>
            Properties.TryGetValue("isDisabled", out var isDisabledString) &&
            bool.TryParse(substituteVariables(isDisabledString).ToString(), out var isDisabled) &&
            isDisabled;

        public IReadOnlyCollection<IDeploymentNode> Parents => [
            new ElementAdapter(relationshipInstance.Source, substituteVariables),
            new ElementAdapter(relationshipInstance.Destination, substituteVariables),
        ];

        public IDictionary<string, string> Properties => relationshipModel.Properties;

        public ModelItem Node => relationshipInstance;

        public IReadOnlyCollection<RelationshipAdapter> Relationships { get; } = Array.Empty<RelationshipAdapter>();

        public virtual string Technology => substituteVariables(relationshipModel.Technology ?? string.Empty).ToString()!;
    }
}
