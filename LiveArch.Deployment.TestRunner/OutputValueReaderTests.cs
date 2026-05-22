using LiveArch.Deployment;
using Pulumi;
using Pulumi.Testing;

namespace LiveArch.Deployment.TestRunner
{
    public class OutputValueReaderTests
    {
        [Fact]
        public async Task ShouldProjectNestedValueFromOutputProperty()
        {
            var reader = new OutputValueReader();
            var source = new FakeResource
            {
                Identity = Output.Create(new FakeIdentityResponse
                {
                    PrincipalId = "principal-1",
                }),
            };

            var value = reader.GetValue(source, "identity.principalId");

            value.Should().BeAssignableTo<Output<string>>();
            var resolved = await ResolveOutputAsync((Output<string>)value!);
            resolved.Should().Be("principal-1");
        }

        [Fact]
        public async Task ShouldProjectDeepNestedValueFromOutputProperty()
        {
            var reader = new OutputValueReader();
            var source = new FakeResource
            {
                Identity = Output.Create(new FakeIdentityResponse
                {
                    Profile = new FakeProfileResponse
                    {
                        ClientId = "client-1",
                    },
                }),
            };

            var value = reader.GetValue(source, "identity.profile.clientId");

            value.Should().BeAssignableTo<Output<string>>();
            var resolved = await ResolveOutputAsync((Output<string>)value!);
            resolved.Should().Be("client-1");
        }

        [Fact]
        public async Task ShouldProjectValueFromRootOutputObject()
        {
            var reader = new OutputValueReader();
            var source = Output.Create(new FakeIdentityResponse
            {
                PrincipalId = "principal-1",
            });

            var value = reader.GetValue(source, "principalId");

            value.Should().BeAssignableTo<Output<string>>();
            var resolved = await ResolveOutputAsync((Output<string>)value!);
            resolved.Should().Be("principal-1");
        }

        private static async Task<T> ResolveOutputAsync<T>(Output<T> output)
        {
            var tcs = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
            await Pulumi.Deployment.TestAsync(new LiveArch.Deployment.Mocks(), new TestOptions { IsPreview = false }, () =>
            {
                output.Apply(value =>
                {
                    tcs.TrySetResult(value!);
                    return value!;
                });

                return Task.CompletedTask;
            });

            return await tcs.Task.WaitAsync(TimeSpan.FromSeconds(5));
        }

        private sealed class FakeResource
        {
            [Output("identity")]
            public Output<FakeIdentityResponse> Identity { get; init; } = null!;
        }

        [OutputType]
        private sealed class FakeIdentityResponse
        {
            public string? PrincipalId { get; init; }

            public FakeProfileResponse? Profile { get; init; }
        }

        [OutputType]
        private sealed class FakeProfileResponse
        {
            public string? ClientId { get; init; }
        }
    }
}
