namespace LiveArch.Deployment.Transformers
{
    /// <summary>
    /// Parses inline transformer pipelines and applies transformer chains to values.
    /// </summary>
    public sealed class TransformerPipeline(ITransformerRegistry transformerRegistry)
    {
        /// <summary>
        /// Parses an inline pipeline expression such as <c>value | split , | format x{0}</c>.
        /// </summary>
        /// <param name="value">Raw pipeline expression.</param>
        /// <param name="sourceValue">The source value segment that precedes the first transformer.</param>
        /// <param name="transformers">Created transformer chain when parsing succeeds.</param>
        /// <returns><c>true</c> when the expression starts with a registered transformer; otherwise <c>false</c>.</returns>
        public bool TryParse(string value, out string sourceValue, out IReadOnlyCollection<ITransformer> transformers)
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

        /// <summary>
        /// Applies each transformer in order to the supplied value.
        /// </summary>
        /// <param name="value">Initial value.</param>
        /// <param name="transformers">Transformer chain to execute.</param>
        /// <returns>The final transformed value.</returns>
        public static object Apply(object value, IReadOnlyCollection<ITransformer> transformers)
        {
            var current = value;
            foreach (var transformer in transformers)
            {
                current = transformer.Transform(current);
            }

            return current;
        }

        /// <summary>
        /// Parses a single transformer specification and tries to create the corresponding transformer.
        /// </summary>
        /// <param name="value">Single transformer specification.</param>
        /// <param name="transformer">Created transformer when the specification name is registered.</param>
        /// <returns><c>true</c> when the transformer name is known; otherwise <c>false</c>.</returns>
        private bool TryCreateTransformer(string value, out ITransformer transformer)
        {
            var specification = value.Trim();
            if (string.IsNullOrWhiteSpace(specification))
            {
                throw new InvalidOperationException("Transformer specification cannot be empty.");
            }

            var parts = specification.Split(' ', 2, StringSplitOptions.TrimEntries);
            var transformerName = parts[0];
            var transformerParameter = parts.Length > 1 ? parts[1] : string.Empty;

            if (!transformerRegistry.TryCreate(transformerName, transformerParameter, out transformer))
            {
                return false;
            }

            return true;
        }
    }
}
