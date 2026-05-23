using LiveArch.Deployment.Expressions;
using Structurizr;
using System.Reflection;
using LiveArch.Transformers;
using Type = System.Type;

namespace LiveArch.Deployment.Observability
{
    internal sealed class ResourceExpressionRecorder
    {
        private readonly Dictionary<object, ResourceExpressionModel> resourcesByArgs = new(ReferenceEqualityComparer.Instance);
        private readonly Dictionary<object, TrackedTarget> trackedTargets = new(ReferenceEqualityComparer.Instance);

        public CreatedResourceExpressionModel BeginCreatedResource(ModelItem node, int scopeId, string resourceName, object args, Type resourceType)
        {
            var model = new CreatedResourceExpressionModel(node, scopeId, resourceName, resourceType);
            RegisterRoot(args, model);
            return model;
        }

        public ReferencedResourceExpressionModel BeginReferencedResource(ModelItem node, int scopeId, string resourceName, object args, MethodInfo invokeMethod)
        {
            var model = new ReferencedResourceExpressionModel(node, scopeId, resourceName, invokeMethod);
            RegisterRoot(args, model);
            return model;
        }

        public void RegisterNestedTarget(object parentTarget, object nestedTarget, string pathSegment)
        {
            if (!trackedTargets.TryGetValue(parentTarget, out var parent))
            {
                return;
            }

            trackedTargets[nestedTarget] = new TrackedTarget(parent.Model, CombinePath(parent.PathPrefix, pathSegment));
        }

        public void RecordDirectAssignment(object target, string path, object? value, bool parseInlineTransformers, string? converterName)
        {
            if (!trackedTargets.TryGetValue(target, out var tracked))
            {
                return;
            }

            tracked.Model.Assignments[CombinePath(tracked.PathPrefix, path)] = new DirectValueExpressionModel(value, parseInlineTransformers, converterName);
        }

        public void RecordDependencyAssignment(object target, string targetPath, object sourceResource, string sourcePath, IReadOnlyCollection<ITransformer> transformers, string? converterName)
        {
            if (!trackedTargets.TryGetValue(target, out var tracked))
            {
                return;
            }

            tracked.Model.Assignments[CombinePath(tracked.PathPrefix, targetPath)] = new DependencyValueExpressionModel(
                sourceResource,
                sourcePath,
                [.. transformers.Select(transformer => transformer.GetType().Name)],
                converterName);
        }

        private void RegisterRoot(object args, ResourceExpressionModel model)
        {
            resourcesByArgs[args] = model;
            trackedTargets[args] = new TrackedTarget(model, string.Empty);
        }

        private static string CombinePath(string prefix, string path)
        {
            return string.IsNullOrWhiteSpace(prefix) ? path : $"{prefix}.{path}";
        }

        private sealed record TrackedTarget(ResourceExpressionModel Model, string PathPrefix);
    }
}
