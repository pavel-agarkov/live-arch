using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace LiveArch.Deployment.Transformers
{
    public static class TransformerRegistry
    {
        public static Dictionary<string, Func<string, ITransformer>> Registry { get; } = new()
        {
            ["format"] = (format) => new FormatTransformer(format),
            ["extract"] = (regex) => new RegExTransformer(regex, RegExTransformer.RegExOperation.Extract),
            ["clean"] = (regex) => new RegExTransformer(regex, RegExTransformer.RegExOperation.Clean),
            ["split"] = (regex) => new RegExTransformer(regex, RegExTransformer.RegExOperation.Split),
            ["multiply"] = (multiplier) => new MultiplyTrunsformer(multiplier),
            ["divide"] = (divisor) => new MultiplyTrunsformer(divisor, devider: true)
        };
    }
}
