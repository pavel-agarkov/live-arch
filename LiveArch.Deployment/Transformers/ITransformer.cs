using System;

namespace LiveArch.Deployment.Transformers
{
    public interface ITransformer
    {
        Type OutputType { get; }

        object Transform(object input);
    }
}
