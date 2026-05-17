using System;
using System.Globalization;

namespace LiveArch.Deployment.Transformers
{
    public class MultiplyTransformer : ITransformer
    {
        private readonly double multiplier;

        public Type OutputType => typeof(double);

        public MultiplyTransformer(string multiplier)
        {
            if (!double.TryParse(multiplier, NumberStyles.Float | NumberStyles.AllowThousands, CultureInfo.InvariantCulture, out this.multiplier) &&
                !double.TryParse(multiplier, NumberStyles.Float | NumberStyles.AllowThousands, CultureInfo.CurrentCulture, out this.multiplier))
            {
                throw new InvalidOperationException($"MultiplyTrunsformer requires a numeric multiplier, but got '{multiplier}'.");
            }
        }

        public MultiplyTransformer(string multiplier, bool divider) : this(multiplier)
        {
            if (divider)
            {
                if (this.multiplier == 0)
                {
                    throw new InvalidOperationException("MultiplyTransformer cannot divide by zero.");
                }
                this.multiplier = 1 / this.multiplier;
            }
        }

        public object Transform(object input)
        {
            if (input == null)
            {
                throw new InvalidOperationException("MultiplyTransformer can only be applied to numeric inputs, but got null.");
            }

            if (!double.TryParse(input.ToString(), NumberStyles.Float | NumberStyles.AllowThousands, CultureInfo.InvariantCulture, out var inputNumber) &&
                !double.TryParse(input.ToString(), NumberStyles.Float | NumberStyles.AllowThousands, CultureInfo.CurrentCulture, out inputNumber))
            {
                throw new InvalidOperationException($"MultiplyTransformer can only be applied to numeric inputs, but got {input.GetType().FullName}.");
            }

            return inputNumber * multiplier;
        }
    }
}
