using System;

namespace LiveArch.Deployment.Transformers
{
    public class SplitTransformer : ITransformer
    {
        private readonly string separator;

        public SplitTransformer(string separator)
        {
            this.separator = separator;
        }

        public Type InputType => typeof(string);

        public object Transform(object input)
        {
            if (input == null)
            {
                return Array.Empty<string>();
            }
            if (input is not string inputString)
            {
                throw new InvalidOperationException($"SplitTransformer can only be applied to string inputs, but got {input.GetType().FullName}");
            }
            return inputString.Split(separator,
                StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        }
    }
}
