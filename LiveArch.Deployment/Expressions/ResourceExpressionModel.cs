using Structurizr;
using System.Reflection;
using Type = System.Type;

namespace LiveArch.Deployment.Expressions
{
    public abstract record ResourceExpressionModel(ModelItem Node, int ScopeId, string ResourceName)
    {
        public Dictionary<string, ValueExpressionModel> Assignments { get; } = [];
    }

    public sealed record CreatedResourceExpressionModel(
        ModelItem Node,
        int ScopeId,
        string ResourceName,
        Type ResourceType) : ResourceExpressionModel(Node, ScopeId, ResourceName);

    public sealed record ReferencedResourceExpressionModel(
        ModelItem Node,
        int ScopeId,
        string ResourceName,
        MethodInfo InvokeMethod) : ResourceExpressionModel(Node, ScopeId, ResourceName);

    public abstract record ValueExpressionModel;

    public sealed record TransformerExpressionModel(string Name, string Parameter, Type ImplementationType)
    {
        public bool IsBuiltIn => ImplementationType.Namespace?.StartsWith("LiveArch.", StringComparison.Ordinal) == true;
    }

    public sealed record DirectValueExpressionModel(
        object? Value,
        bool ParseInlineTransformers,
        string? ConverterName,
        IReadOnlyCollection<TransformerExpressionModel> InlineTransformers) : ValueExpressionModel;

    public sealed record DependencyValueExpressionModel(
        object SourceResource,
        string SourcePath,
        IReadOnlyCollection<TransformerExpressionModel> Transformers,
        string? ConverterName) : ValueExpressionModel;
}
