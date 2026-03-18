using System;

namespace LiveArch.Deployment.Transformers
{
    public class MultiplyTrunsformer : ITransformer
    {
        private readonly double multiplier;

        public Type InputType => typeof(double);

        public MultiplyTrunsformer(string multiplier)
        {
            this.multiplier = double.Parse(multiplier);
        }

        public MultiplyTrunsformer(string multiplier, bool devider) : this(multiplier)
        {
            this.multiplier = 1 / this.multiplier;
        }

        public object Transform(object input)
        {
            var inputNumber = double.Parse(input.ToString()!);
            return inputNumber * multiplier;
        }
    }
}
