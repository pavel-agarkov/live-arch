using Pulumi;
using System.Collections.Immutable;

namespace LiveArch.Deployment.Controls
{
    [ResourceType(Technology, "1")]
    public class ForEachSource
    {
        public const string Technology = "foreach:source";

        [Output("source")]
        public Output<ImmutableArray<object>> Source { get; private set; } = null!;

        public ForEachSource(string name, ForEachSourceArgs args, CustomResourceOptions? options = null)
        {
            Source = args.Source;
        }
    }

    public class ForEachSourceArgs
    {
        [Input("source", true, false)]
        private InputList<object>? source;

        public required InputList<object> Source
        {
            get => source ?? (source = new InputList<object>());
            set => source = value;
        }
    }
}
