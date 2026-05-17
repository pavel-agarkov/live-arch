using System;
using System.Text.RegularExpressions;

namespace LiveArch.Deployment.Transformers
{
    public class RegExTransformer : ITransformer
    {
        public enum RegExOperation
        {
            Extract,
            Clean,
            Split
        }

        private readonly string regex;
        private readonly RegExOperation operation;

        public Type OutputType => operation == RegExOperation.Split ? typeof(string[]) : typeof(string);

        public RegExTransformer(string regex, RegExOperation operation)
        {
            this.regex = regex;
            this.operation = operation;
        }

        public object Transform(object input)
        {
            if (input == null)
            {
                return operation == RegExOperation.Split ? Array.Empty<string>() : string.Empty;
            }

            if (input is not string inputString)
            {
                throw new InvalidOperationException($"RegExTransformer can only be applied to string inputs, but got {input.GetType().FullName}");
            }

            var regex = new Regex(this.regex);
            switch (operation)
            {
                case RegExOperation.Extract:
                    var match = regex.Match(inputString);
                    if (match.Success)
                    {
                        return match.Value;
                    }

                    break;

                case RegExOperation.Clean:
                    return regex.Replace(inputString, string.Empty);

                case RegExOperation.Split:
                    return regex.Split(inputString);
            }

            return string.Empty;
        }
    }
}
