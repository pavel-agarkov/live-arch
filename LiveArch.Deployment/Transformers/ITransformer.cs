using System;

namespace LiveArch.Deployment.Transformers
{
    /// <summary>
    /// Transforms a value before it is bound into a target resource input.
    /// Implement this contract for custom DSL transformers.
    /// </summary>
    public interface ITransformer
    {
        /// <summary>
        /// Gets the CLR type produced by the transformer.
        /// </summary>
        Type OutputType { get; }

        /// <summary>
        /// Transforms the supplied input value.
        /// </summary>
        /// <param name="input">Source value.</param>
        /// <returns>The transformed value.</returns>
        object Transform(object input);
    }
}
