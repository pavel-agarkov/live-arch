using Pulumi;

namespace LiveArch.Deployment.Controls
{
    [ResourceType(Technology, "1")]
    public class ForEachLoop
    {
        public const string Technology = "foreach:loop";

        public ForEachLoop(string name, ForEachLoopArgs args, CustomResourceOptions? options = null)
        {
            Name = name;
        }

        public string Name { get; }
    }

    public class ForEachLoopArgs
    {

    }
}
