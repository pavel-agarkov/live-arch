using LiveArch.Deployment.Expressions;
using Structurizr;
using System.Reflection;
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

        public void RecordDirectAssignment(object target, string path, object? value, bool parseInlineTransformers, string? converterName, IReadOnlyCollection<TransformerExpressionModel>? inlineTransformers = null)
        {
            if (!trackedTargets.TryGetValue(target, out var tracked))
            {
                return;
            }

            var fullPath = CombinePath(tracked.PathPrefix, path);
            var assignmentTarget = new PropertyAssignmentTargetModel(fullPath);
            var valueModel = new DirectValueExpressionModel(value, parseInlineTransformers, converterName, inlineTransformers ?? []);
            UpsertAssignment(tracked.Model, assignmentTarget, valueModel);
        }

        public void RecordKeyedCollectionAssignment(object target, string collectionPath, string key, object? value, bool parseInlineTransformers, string? converterName, IReadOnlyCollection<TransformerExpressionModel>? inlineTransformers = null)
        {
            if (!trackedTargets.TryGetValue(target, out var tracked))
            {
                return;
            }

            var fullPath = CombinePath(tracked.PathPrefix, collectionPath);
            var assignmentTarget = new KeyedCollectionAssignmentTargetModel(fullPath, key);
            var valueModel = new DirectValueExpressionModel(value, parseInlineTransformers, converterName, inlineTransformers ?? []);
            UpsertAssignment(tracked.Model, assignmentTarget, valueModel);
        }

        public void RecordAppendCollectionAssignment(object target, string collectionPath, object? value, bool parseInlineTransformers, string? converterName, IReadOnlyCollection<TransformerExpressionModel>? inlineTransformers = null)
        {
            if (!trackedTargets.TryGetValue(target, out var tracked))
            {
                return;
            }

            var fullPath = CombinePath(tracked.PathPrefix, collectionPath);
            var assignmentTarget = new AppendCollectionAssignmentTargetModel(fullPath);
            var valueModel = new DirectValueExpressionModel(value, parseInlineTransformers, converterName, inlineTransformers ?? []);
            UpsertAssignment(tracked.Model, assignmentTarget, valueModel);
        }

        public void RecordDependencyAssignment(object target, string targetPath, object sourceResource, string sourcePath, IReadOnlyCollection<TransformerExpressionModel> transformers, string? converterName)
        {
            if (!trackedTargets.TryGetValue(target, out var tracked))
            {
                return;
            }

            var fullPath = CombinePath(tracked.PathPrefix, targetPath);
            var assignmentTarget = new PropertyAssignmentTargetModel(fullPath);
            var valueModel = new DependencyValueExpressionModel(sourceResource, sourcePath, transformers, converterName);
            UpsertAssignment(tracked.Model, assignmentTarget, valueModel);
        }

        public void RecordKeyedCollectionDependencyAssignment(object target, string collectionPath, string key, object sourceResource, string sourcePath, IReadOnlyCollection<TransformerExpressionModel> transformers, string? converterName)
        {
            if (!trackedTargets.TryGetValue(target, out var tracked))
            {
                return;
            }

            var fullPath = CombinePath(tracked.PathPrefix, collectionPath);
            var assignmentTarget = new KeyedCollectionAssignmentTargetModel(fullPath, key);
            var valueModel = new DependencyValueExpressionModel(sourceResource, sourcePath, transformers, converterName);
            UpsertAssignment(tracked.Model, assignmentTarget, valueModel);
        }

        private static void UpsertAssignment(ResourceExpressionModel model, AssignmentTargetModel target, ValueExpressionModel value)
        {
            var assignment = new AssignmentExpressionModel(target, value);
            var existingIndex = model.Assignments.FindIndex(current => current.Target == target);
            if (existingIndex >= 0)
            {
                model.Assignments[existingIndex] = assignment;
                return;
            }

            model.Assignments.Add(assignment);
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
