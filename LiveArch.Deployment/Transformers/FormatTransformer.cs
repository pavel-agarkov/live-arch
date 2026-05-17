using System;

namespace LiveArch.Deployment.Transformers
{
    public class FormatTransformer : ITransformer
    {
        private readonly string format;

        public Type OutputType => typeof(string);

        public FormatTransformer(string format)
        {
            this.format = format;
        }

        public object Transform(object input)
        {
            return string.Format(format, input);
        }
    }
}
