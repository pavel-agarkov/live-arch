using LiveArch.Deployment.Azure.Converters;
using LiveArch.Deployment.Converters;
using Microsoft.Extensions.DependencyInjection;
using Pulumi;
using Pulumi.AzureNative.Web;
using Pulumi.AzureNative.Web.Inputs;
using Pulumi.Testing;
using System.Collections.Immutable;
using ManagedServiceIdentityType = Pulumi.AzureNative.Web.ManagedServiceIdentityType;

namespace LiveArch.Deployment.TestRunner
{
    public class ConversionEngineTests
    {
        [Fact]
        public void ShouldResolveStringVariablesBeforePrimitiveConversion()
        {
            var engine = CreateEngine();
            var context = new ConversionContext(value => value == "${FLAG}" ? "true" : value);

            var result = engine.ConvertValue(typeof(bool), "${FLAG}", context);

            result.Should().Be(true);
        }

        [Fact]
        public void ShouldConvertStringToPulumiEnum()
        {
            var engine = CreateEngine();

            var result = engine.ConvertValue(typeof(ManagedServiceIdentityType), "UserAssigned", ConversionContext.Empty);

            result.Should().Be(ManagedServiceIdentityType.UserAssigned);
        }

        [Fact]
        public async Task ShouldConvertStringToInputOfPulumiEnum()
        {
            var engine = CreateEngine();

            var result = engine.ConvertValue(typeof(Input<ManagedServiceIdentityType>), "UserAssigned", ConversionContext.Empty);

            result.Should().BeAssignableTo<Input<ManagedServiceIdentityType>>();
            var resolved = await ResolveInputAsync((Input<ManagedServiceIdentityType>)result);
            resolved.Should().Be(ManagedServiceIdentityType.UserAssigned);
        }

        [Fact]
        public async Task ShouldConvertCommaSeparatedStringToInputListOfStrings()
        {
            var engine = CreateEngine();

            var result = engine.ConvertValue(typeof(InputList<string>), "a, b, c", ConversionContext.Empty);

            result.Should().BeAssignableTo<InputList<string>>();
            var resolved = await ResolveInputAsync((Input<ImmutableArray<string>>)result);
            resolved.Should().Equal("a, b, c");
        }

        [Fact]
        public async Task ShouldConvertDictionaryToInputMapOfStrings()
        {
            var engine = CreateEngine();
            var source = new Dictionary<string, object>
            {
                ["one"] = "1",
                ["two"] = "2",
            };

            var result = engine.ConvertValue(typeof(InputMap<string>), source, ConversionContext.Empty);

            result.Should().BeAssignableTo<InputMap<string>>();
            var resolved = await ResolveInputAsync((Input<ImmutableDictionary<string, string>>)result);
            resolved.Should().ContainKey("one").WhoseValue.Should().Be("1");
            resolved.Should().ContainKey("two").WhoseValue.Should().Be("2");
        }

        [Fact]
        public async Task ShouldUseImplicitConversionForOutputImmutableArrayToInputList()
        {
            var engine = CreateEngine();
            var source = Output.Create(ImmutableArray.Create("x", "y"));

            var result = engine.ConvertValue(typeof(InputList<string>), source, ConversionContext.Empty);

            result.Should().BeAssignableTo<InputList<string>>();
            var resolved = await ResolveInputAsync((Input<ImmutableArray<string>>)result);
            resolved.Should().Equal("x", "y");
        }

        [Fact]
        public async Task ShouldProjectOutputStringToInputPulumiEnum()
        {
            var engine = CreateEngine();
            var source = Output.Create("UserAssigned");

            var result = engine.ConvertValue(typeof(Input<ManagedServiceIdentityType>), source, ConversionContext.Empty);

            result.Should().BeAssignableTo<Input<ManagedServiceIdentityType>>();
            var resolved = await ResolveInputAsync((Input<ManagedServiceIdentityType>)result);
            resolved.Should().Be(ManagedServiceIdentityType.UserAssigned);
        }

        [Fact]
        public void ShouldUseNamedValuePropertyConverter()
        {
            var engine = CreateEngine();

            var result = engine.ConvertValue(typeof(NamedValueArgs), "hello", ConversionContext.Empty, KnownNamedValueConverters.DefaultKeyedListValue);

            result.Should().BeOfType<NamedValueArgs>();
            var typed = (NamedValueArgs)result;
            typed.Name.Should().BeNull();
            typed.Value.Should().NotBeNull();
        }

        [Fact]
        public async Task ShouldPopulateNamedValuePropertyConverterPayload()
        {
            var engine = CreateEngine();

            var result = (NamedValueArgs)engine.ConvertValue(typeof(NamedValueArgs), "hello", ConversionContext.Empty, KnownNamedValueConverters.DefaultKeyedListValue);

            var resolved = await ResolveInputAsync(result.Value!);
            resolved.Should().Be("hello");
        }

        [Fact]
        public async Task ShouldUseAzureSqlConnectionStringConverter()
        {
            var engine = CreateEngine();

            var result = engine.ConvertValue(typeof(ConnStringInfoArgs), "Server=tcp:test.database.windows.net;Initial Catalog=db;", ConversionContext.Empty, AzureKnownConverterNames.AzureSqlConnectionString);

            result.Should().BeOfType<ConnStringInfoArgs>();
            var typed = (ConnStringInfoArgs)result;
            var connectionString = await ResolveInputAsync(typed.ConnectionString!);
            var connectionType = await ResolveInputAsync(typed.Type!);
            connectionString.Should().Be("Server=tcp:test.database.windows.net;Initial Catalog=db;");
            connectionType.Should().Be(ConnectionStringType.SQLAzure);
        }

        [Fact]
        public async Task ShouldRespectTypedConverterRegistrationOrder()
        {
            var services = new ServiceCollection();
            services.AddTypedValueConverter<OverrideStringInputConverter>();
            services.AddDefaultValueConverters();
            services.AddAzureValueConverters();
            var engine = services.BuildServiceProvider().GetRequiredService<IConversionEngine>();

            var result = engine.ConvertValue(typeof(Input<string>), "original", ConversionContext.Empty);

            var resolved = await ResolveInputAsync((Input<string>)result);
            resolved.Should().Be("overridden");
        }

        [Fact]
        public async Task ShouldProjectOutputThroughAzureSqlConnectionStringConverter()
        {
            var engine = CreateEngine();
            var source = Output.Create("Server=tcp:test.database.windows.net;Initial Catalog=db;");

            var result = engine.ConvertValue(typeof(ConnStringInfoArgs), source, ConversionContext.Empty, AzureKnownConverterNames.AzureSqlConnectionString);

            result.Should().BeAssignableTo<Output<ConnStringInfoArgs>>();
            var typed = await ResolveOutputAsync((Output<ConnStringInfoArgs>)result);
            var connectionString = await ResolveInputAsync(typed.ConnectionString!);
            var connectionType = await ResolveInputAsync(typed.Type!);
            connectionString.Should().Be("Server=tcp:test.database.windows.net;Initial Catalog=db;");
            connectionType.Should().Be(ConnectionStringType.SQLAzure);
        }

        [Fact]
        public void ShouldThrowWhenNamedConverterIsMissing()
        {
            var engine = CreateEngine();
            var act = () => engine.ConvertValue(typeof(string), "value", ConversionContext.Empty, "missing-converter");

            act.Should().Throw<NotSupportedException>();
        }

        [Fact]
        public void ShouldThrowWhenAutomaticConverterIsMissing()
        {
            var engine = CreateEngine();
            var act = () => engine.ConvertValue(typeof(DateTime), "2025-01-01", ConversionContext.Empty);

            act.Should().Throw<NotSupportedException>();
        }

        private static IConversionEngine CreateEngine()
        {
            var services = new ServiceCollection();
            services.AddDefaultValueConverters();
            services.AddAzureValueConverters();
            return services.BuildServiceProvider().GetRequiredService<IConversionEngine>();
        }

        private static async Task<T> ResolveInputAsync<T>(Input<T> input)
        {
            var tcs = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
            await Pulumi.Deployment.TestAsync(new global::LiveArch.Deployment.Mocks(), new TestOptions { IsPreview = false }, () =>
            {
                input.Apply(value =>
                {
                    tcs.TrySetResult(value!);
                    return value!;
                });

                return Task.CompletedTask;
            });

            return await tcs.Task.WaitAsync(TimeSpan.FromSeconds(5));
        }

        private static async Task<T> ResolveOutputAsync<T>(Output<T> output)
        {
            var tcs = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
            await Pulumi.Deployment.TestAsync(new global::LiveArch.Deployment.Mocks(), new TestOptions { IsPreview = false }, () =>
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

        private sealed class NamedValueArgs
        {
            public Input<string>? Name { get; set; }

            public Input<string>? Value { get; set; }
        }

        private sealed class OverrideStringInputConverter : ITypedValueConverter
        {
            public bool CanConvert(ConversionRequest request)
            {
                return request.SourceType == typeof(string) && request.TargetType == typeof(Input<string>);
            }

            public object Convert(ConversionRequest request, IConversionEngine engine)
            {
                return (Input<string>)"overridden";
            }
        }
    }
}
