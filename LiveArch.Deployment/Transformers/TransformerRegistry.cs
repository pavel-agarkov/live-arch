using LiveArch.Transformers;

namespace LiveArch.Deployment.Transformers
{
    /// <summary>
    /// Stores named transformer factories and creates transformer instances on demand.
    /// </summary>
    public sealed class TransformerRegistry : ITransformerRegistry
    {
        private readonly Dictionary<string, INamedTransformerFactory> factories;

        /// <summary>
        /// Initializes a registry from the supplied named transformer factories.
        /// </summary>
        /// <param name="factories">Factories to register. Later registrations override built-in transformers and earlier custom registrations with the same name.</param>
        public TransformerRegistry(IEnumerable<INamedTransformerFactory> factories)
        {
            ArgumentNullException.ThrowIfNull(factories);

            this.factories = new Dictionary<string, INamedTransformerFactory>(StringComparer.OrdinalIgnoreCase);
            foreach (var factory in GetBuiltInFactories())
            {
                this.factories[factory.Name] = factory;
            }

            foreach (var factory in factories)
            {
                this.factories[factory.Name] = factory;
            }
        }

        /// <inheritdoc />
        public bool TryCreate(string name, string parameter, out ITransformer transformer)
        {
            if (!factories.TryGetValue(name, out var factory))
            {
                transformer = null!;
                return false;
            }

            transformer = factory.Create(parameter);
            return true;
        }

        /// <summary>
        /// Returns the built-in transformer factory set that is available by default.
        /// </summary>
        /// <returns>Built-in named transformer factories.</returns>
        private static IReadOnlyCollection<INamedTransformerFactory> GetBuiltInFactories()
        {
            return [
                new NamedTransformerFactory("format", format => new FormatTransformer(format)),
                new NamedTransformerFactory("split", separator => new SplitTransformer(separator)),
                new NamedTransformerFactory("extractByRegEx", regex => new RegExTransformer(regex, RegExTransformer.RegExOperation.Extract)),
                new NamedTransformerFactory("cleanByRegEx", regex => new RegExTransformer(regex, RegExTransformer.RegExOperation.Clean)),
                new NamedTransformerFactory("splitByRegEx", regex => new RegExTransformer(regex, RegExTransformer.RegExOperation.Split)),
                new NamedTransformerFactory("multiply", multiplier => new MultiplyTransformer(multiplier)),
                new NamedTransformerFactory("divide", divisor => new MultiplyTransformer(divisor, divider: true))
            ];
        }
    }
}
