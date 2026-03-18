using System;

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

        public Type InputType => typeof(string);

        public RegExTransformer(string regex, RegExOperation operation)
        {
            this.regex = regex;
            this.operation = operation;
        }

        public object Transform(object input)
        {
            if (input is not null)
            {
                var regex = new System.Text.RegularExpressions.Regex(this.regex);
                var inputStr = input.ToString()!;
                switch (operation)
                {
                    case RegExOperation.Extract:
                        var match = regex.Match(inputStr);
                        if (match.Success)
                        {
                            return match.Value;
                        }
                        break;

                    case RegExOperation.Clean:
                        return regex.Replace(inputStr, string.Empty);

                    case RegExOperation.Split:
                        return regex.Split(inputStr);
                }
            }
            return string.Empty;
        }
    }
}
