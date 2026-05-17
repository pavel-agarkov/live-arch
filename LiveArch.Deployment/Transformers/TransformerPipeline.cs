namespace LiveArch.Deployment.Transformers
{
    public static class TransformerPipeline
    {
        public static bool TryParse(string value, out string sourceValue, out IReadOnlyCollection<ITransformer> transformers)
        {
            sourceValue = value;
            transformers = Array.Empty<ITransformer>();

            var parts = value.Split('|', StringSplitOptions.TrimEntries);
            if (parts.Length < 2)
            {
                return false;
            }

            if (!TryCreateTransformer(parts[1], out var firstTransformer))
            {
                return false;
            }

            var parsedTransformers = new List<ITransformer> { firstTransformer };
            for (var i = 2; i < parts.Length; i++)
            {
                if (!TryCreateTransformer(parts[i], out var transformer))
                {
                    throw new InvalidOperationException($"Transformer '{parts[i]}' is not registered.");
                }

                parsedTransformers.Add(transformer);
            }

            sourceValue = parts[0].Trim();
            transformers = parsedTransformers;
            return true;
        }

        public static object Apply(object value, IReadOnlyCollection<ITransformer> transformers)
        {
            var current = value;
            foreach (var transformer in transformers)
            {
                current = transformer.Transform(current);
            }

            return current;
        }

        private static bool TryCreateTransformer(string value, out ITransformer transformer)
        {
            var specification = value.Trim();
            if (string.IsNullOrWhiteSpace(specification))
            {
                throw new InvalidOperationException("Transformer specification cannot be empty.");
            }

            var parts = specification.Split(' ', 2, StringSplitOptions.TrimEntries);
            var transformerName = parts[0];
            var transformerParameter = parts.Length > 1 ? parts[1] : string.Empty;

            if (!TransformerRegistry.Registry.TryGetValue(transformerName, out var factory))
            {
                transformer = null!;
                return false;
            }

            transformer = factory(transformerParameter);
            return true;
        }
    }
}
