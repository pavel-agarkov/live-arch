using System;

namespace LiveArch.Deployment.Transformers
{
    public interface ITransformer
    {
        /// <summary>
        /// Supported input type
        /// </summary>
        Type InputType { get; }

        object Transform(object input);
    }
}
