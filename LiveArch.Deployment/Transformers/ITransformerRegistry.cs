using LiveArch.Transformers;

namespace LiveArch.Deployment.Transformers
{
    /// <summary>
    /// Resolves transformer factories by DSL name and creates configured transformer instances.
    /// </summary>
    public interface ITransformerRegistry
    {
        /// <summary>
        /// Tries to create a transformer for the specified DSL name and parameter.
        /// </summary>
        /// <param name="name">Transformer name used in the DSL.</param>
        /// <param name="parameter">Transformer parameter payload.</param>
        /// <param name="transformer">Created transformer instance when the name is registered.</param>
        /// <returns><c>true</c> when the transformer name is known; otherwise <c>false</c>.</returns>
        bool TryCreate(string name, string parameter, out ITransformer transformer);
    }
}
