using System.Collections.Immutable;

namespace LiveArch.Deployment.Export.Testing
{
    public sealed record RecordedInvoke(
        string Token,
        ImmutableDictionary<string, object> Arguments,
        ImmutableDictionary<string, object> Result);
}
