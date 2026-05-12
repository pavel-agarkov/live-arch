using Structurizr;

namespace LiveArch.Deployment.Adapters
{
    public interface IDeploymentAdapter
    {
        ModelItem Node { get; }
        string Technology { get; }
        IDictionary<string, string> Properties { get; }
        IReadOnlyCollection<RelationshipAdapter> Relationships { get; }
        IReadOnlyCollection<IDeploymentAdapter> Parents { get; }

        bool IsDisabled { get; }

    }
}
