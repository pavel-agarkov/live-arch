using System;

namespace LiveArch.Deployment.Transformers
{
    public class FormatTransformer : ITransformer
    {
        private readonly string format;

        public FormatTransformer(string format)
        {
            this.format = format;
        }

        public Type InputType => typeof(string);

        public object Transform(object input)
        {
            return string.Format(format, input);
        }
    }
}
