namespace LiveArch.Deployment.Transformers
{
    public static class TransformerRegistry
    {
        public static Dictionary<string, Func<string, ITransformer>> Registry { get; } = new()
        {
            ["format"] = (format) => new FormatTransformer(format),
            ["split"] = (separator) => new SplitTransformer(separator),
            ["extractByRegEx"] = (regex) => new RegExTransformer(regex, RegExTransformer.RegExOperation.Extract),
            ["cleanByRegEx"] = (regex) => new RegExTransformer(regex, RegExTransformer.RegExOperation.Clean),
            ["splitByRegEx"] = (regex) => new RegExTransformer(regex, RegExTransformer.RegExOperation.Split),
            ["multiply"] = (multiplier) => new MultiplyTransformer(multiplier),
            ["divide"] = (divisor) => new MultiplyTransformer(divisor, devider: true)
        };
    }
}
