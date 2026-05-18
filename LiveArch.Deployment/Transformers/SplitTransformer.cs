using System;
using System.Collections.Immutable;

namespace LiveArch.Deployment.Transformers
{
    public class SplitTransformer : ITransformer
    {
        private readonly string separator;

        public Type OutputType => typeof(ImmutableArray<string>);

        public SplitTransformer(string separator)
        {
            this.separator = separator;
        }

        public object Transform(object input)
        {
            if (input == null)
            {
                return ImmutableArray<string>.Empty;
            }
            if (input is not string inputString)
            {
                throw new InvalidOperationException($"SplitTransformer can only be applied to string inputs, but got {input.GetType().FullName}");
            }
            return ImmutableArray.CreateRange(inputString.Split(separator,
                StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries));
        }
    }
}
