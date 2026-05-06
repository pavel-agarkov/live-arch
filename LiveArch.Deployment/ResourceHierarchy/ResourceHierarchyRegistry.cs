namespace LiveArch.Deployment.ResourceHierarchy
{
    public class ResourceHierarchyRegistry : Dictionary<Type, IReadOnlyCollection<ResourcePropagationRule>>
    {
        public ResourceHierarchyRegistry()
        {
        }

        public ResourceHierarchyRegistry(Dictionary<Type, IReadOnlyCollection<ResourcePropagationRule>> other) : base(other)
        {
        }

        public ResourceHierarchyRegistry(IEnumerable<KeyValuePair<Type, IReadOnlyCollection<ResourcePropagationRule>>> keyValuePairs) : base(keyValuePairs)
        {
        }

        public void Add<TResource>(ResourcePropagationRules<TResource> rules)
        {
            Add(typeof(TResource),
                [.. rules.Select(x => new ResourcePropagationRule
                {
                    ParentOutputProperty = o => x.ParentOutputProperty((TResource)o),
                    TargetInputProperties = x.TargetInputProperties
                })]);
        }
    }

    public class ResourcePropagationRules<TResource> : List<ResourcePropagationRule<TResource>>
    {
        public void Add(Func<TResource, object> parentOutputProperty, List<string> targetInputProperties)
        {
            Add(new ResourcePropagationRule<TResource>
            {
                ParentOutputProperty = parentOutputProperty,
                TargetInputProperties = targetInputProperties
            });
        }
    }

    public class ResourcePropagationRule
    {
        public required Func<object, object> ParentOutputProperty { get; set; }

        public required List<string> TargetInputProperties { get; set; }
    }


    public class ResourcePropagationRule<TResource>
    {
        public required Func<TResource, object> ParentOutputProperty { get; set; }

        public required List<string> TargetInputProperties { get; set; }
    }

}
