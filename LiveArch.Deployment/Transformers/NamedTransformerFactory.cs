using LiveArch.Transformers;

namespace LiveArch.Deployment.Transformers
{
    /// <summary>
    /// Adapts a delegate into a named transformer factory registration.
    /// </summary>
    public sealed class NamedTransformerFactory(string name, Func<string, ITransformer> factory) : INamedTransformerFactory
    {
        /// <inheritdoc />
        public string Name { get; } = string.IsNullOrWhiteSpace(name)
            ? throw new ArgumentException("Transformer name cannot be null or whitespace.", nameof(name))
            : name;

        /// <inheritdoc />
        public ITransformer Create(string parameter)
        {
            ArgumentNullException.ThrowIfNull(factory);
            return factory(parameter);
        }
    }
}
