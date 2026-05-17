namespace LiveArch.Deployment.Transformers
{
    /// <summary>
    /// Defines a named transformer factory that can be registered in dependency injection.
    /// </summary>
    public interface INamedTransformerFactory
    {
        /// <summary>
        /// Gets the DSL name used to reference the transformer.
        /// </summary>
        string Name { get; }

        /// <summary>
        /// Creates a transformer instance for the supplied DSL parameter.
        /// </summary>
        /// <param name="parameter">Transformer parameter payload.</param>
        /// <returns>A configured transformer instance.</returns>
        ITransformer Create(string parameter);
    }
}
