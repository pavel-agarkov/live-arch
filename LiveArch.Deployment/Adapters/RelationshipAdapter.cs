using Structurizr;

namespace LiveArch.Deployment.Adapters
{
    public class RelationshipAdapter : IDeploymentAdapter
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

        public IReadOnlyCollection<IDeploymentAdapter> Parents => [
            new ElementAdapter(relationshipInstance.Source, substituteVariables),
            new ElementAdapter(relationshipInstance.Destination, substituteVariables),
        ];

        public IDictionary<string, string> Properties => relationshipModel.Properties;

        public ModelItem Node => relationshipInstance;

        public IReadOnlyCollection<RelationshipAdapter> Relationships { get; } = Array.Empty<RelationshipAdapter>();

        public virtual string Technology => substituteVariables(relationshipModel.Technology ?? string.Empty).ToString()!;
    }
}
