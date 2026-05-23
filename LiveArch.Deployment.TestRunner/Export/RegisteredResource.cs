using LiveArch.Deployment.Expressions;
using Structurizr;
using System.Reflection;

namespace LiveArch.Deployment.TestRunner.Export
{
    internal sealed record RegisteredResource(
        ModelItem Node,
        int ScopeId,
        object Resource,
        IReadOnlyCollection<Pulumi.Resource> DependsOn,
        string? ResourceName,
        global::System.Type? ResourceType,
        MethodInfo? InvokeMethod,
        object? Options,
        ResourceExpressionModel ExpressionModel);
}
