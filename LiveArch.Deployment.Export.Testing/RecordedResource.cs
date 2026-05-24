using System.Collections.Immutable;

namespace LiveArch.Deployment.Export.Testing
{
    public sealed record RecordedResource(
        string Name,
        string Type,
        string? Id,
        ImmutableDictionary<string, object> Inputs,
        ImmutableDictionary<string, object> State);
}
