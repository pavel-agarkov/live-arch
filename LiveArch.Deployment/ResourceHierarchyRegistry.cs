namespace LiveArch.Deployment
{
    public class ResourceHierarchyRegistry : Dictionary<Type, IReadOnlyCollection<ResourcePropagationRule>>
    {
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
