using System.Linq.Expressions;

namespace LiveArch.Deployment.ResourceHierarchy
{
    /// <summary>
    /// Maps resource CLR types to propagation rules that copy parent outputs into child inputs.
    /// </summary>
    public class ResourceHierarchyRegistry : Dictionary<Type, IReadOnlyCollection<ResourcePropagationRule>>
    {
        /// <summary>
        /// Initializes an empty registry.
        /// </summary>
        public ResourceHierarchyRegistry()
        {
        }

        /// <summary>
        /// Initializes a registry from an existing dictionary.
        /// </summary>
        /// <param name="other">Existing propagation map to copy.</param>
        public ResourceHierarchyRegistry(Dictionary<Type, IReadOnlyCollection<ResourcePropagationRule>> other) : base(other)
        {
        }

        /// <summary>
        /// Initializes a registry from the supplied key-value pairs.
        /// </summary>
        /// <param name="keyValuePairs">Propagation entries to copy.</param>
        public ResourceHierarchyRegistry(IEnumerable<KeyValuePair<Type, IReadOnlyCollection<ResourcePropagationRule>>> keyValuePairs) : base(keyValuePairs)
        {
        }

        /// <summary>
        /// Adds propagation rules for a specific resource type.
        /// </summary>
        /// <typeparam name="TResource">Parent resource CLR type.</typeparam>
        /// <param name="rules">Rules describing which outputs should be forwarded to which child inputs.</param>
        public void Add<TResource>(ResourcePropagationRules<TResource> rules)
        {
            Add(typeof(TResource),
                [.. rules.Select(x => new ResourcePropagationRule
                {
                    ParentOutputProperty = ToUntypedExpression(x.ParentOutputProperty),
                    TargetInputProperties = x.TargetInputProperties
                })]);
        }

        private static Expression<Func<object, object>> ToUntypedExpression<TResource>(Expression<Func<TResource, object>> expression)
        {
            var parameter = Expression.Parameter(typeof(object), expression.Parameters[0].Name);
            var body = new ReplaceExpressionVisitor(
                expression.Parameters[0],
                Expression.Convert(parameter, typeof(TResource)))
                .Visit(expression.Body)!;

            return Expression.Lambda<Func<object, object>>(Expression.Convert(body, typeof(object)), parameter);
        }

        private sealed class ReplaceExpressionVisitor(Expression source, Expression target) : ExpressionVisitor
        {
            public override Expression? Visit(Expression? node)
            {
                return node == source ? target : base.Visit(node);
            }
        }
    }

    /// <summary>
    /// Strongly typed collection of propagation rules for a specific parent resource type.
    /// </summary>
    public class ResourcePropagationRules<TResource> : List<ResourcePropagationRule<TResource>>
    {
        /// <summary>
        /// Adds a new strongly typed propagation rule.
        /// </summary>
        /// <param name="parentOutputProperty">Delegate that reads the parent output value.</param>
        /// <param name="targetInputProperties">Child input paths that should receive the value.</param>
        public void Add(Expression<Func<TResource, object>> parentOutputProperty, List<string> targetInputProperties)
        {
            Add(new ResourcePropagationRule<TResource>
            {
                ParentOutputProperty = parentOutputProperty,
                TargetInputProperties = targetInputProperties
            });
        }
    }

    /// <summary>
    /// Describes a single untyped propagation rule.
    /// </summary>
    public class ResourcePropagationRule
    {
        /// <summary>
        /// Gets or sets the delegate that reads the propagated value from the parent resource.
        /// </summary>
        public required Expression<Func<object, object>> ParentOutputProperty { get; set; }

        /// <summary>
        /// Gets or sets the child input paths that should receive the propagated value.
        /// </summary>
        public required List<string> TargetInputProperties { get; set; }

    }


    /// <summary>
    /// Describes a strongly typed propagation rule.
    /// </summary>
    public class ResourcePropagationRule<TResource>
    {
        /// <summary>
        /// Gets or sets the delegate that reads the propagated value from the parent resource.
        /// </summary>
        public required Expression<Func<TResource, object>> ParentOutputProperty { get; set; }

        /// <summary>
        /// Gets or sets the child input paths that should receive the propagated value.
        /// </summary>
        public required List<string> TargetInputProperties { get; set; }

    }

}
