using System;

namespace LiveArch.Deployment.Transformers
{
    public class FormatTransformer : ITransformer
    {
        private readonly string format;

        public Type OutputType => typeof(string);

        public FormatTransformer(string format)
        {
            if (string.IsNullOrWhiteSpace(format))
            {
                throw new InvalidOperationException("FormatTransformer requires a non-empty format string.");
            }

            this.format = format;
        }

        public object Transform(object input)
        {
            try
            {
                return string.Format(format, input);
            }
            catch (FormatException ex)
            {
                throw new InvalidOperationException($"FormatTransformer received an invalid format string '{format}'.", ex);
            }
        }
    }
}
