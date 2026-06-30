using FluentAssertions;
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
        public void ShouldNotResolveStringVariablesInsideConversionEngine()
        {
            var engine = CreateEngine();

            var act = () => engine.ConvertValue(typeof(bool), "${FLAG}");

            act.Should().Throw<FormatException>();
        }

        [Fact]
        public void ShouldConvertStringToPulumiEnum()
        {
            var engine = CreateEngine();

            var result = engine.ConvertValue(typeof(ManagedServiceIdentityType), "UserAssigned");

            result.Should().Be(ManagedServiceIdentityType.UserAssigned);
        }

        [Fact]
        public async Task ShouldConvertStringToInputOfPulumiEnum()
        {
            var engine = CreateEngine();

            var result = engine.ConvertValue(typeof(Input<ManagedServiceIdentityType>), "UserAssigned");

            result.Should().BeAssignableTo<Input<ManagedServiceIdentityType>>();
            var resolved = await ResolveInputAsync((Input<ManagedServiceIdentityType>)result);
            resolved.Should().Be(ManagedServiceIdentityType.UserAssigned);
        }

        [Fact]
        public async Task ShouldConvertStringArrayToInputListOfStrings()
        {
            var engine = CreateEngine();

            var result = engine.ConvertValue(typeof(InputList<string>), new[] { "a", "b", "c" });

            result.Should().BeAssignableTo<InputList<string>>();
            var resolved = await ResolveInputAsync((Input<ImmutableArray<string>>)result);
            resolved.Should().Equal("a", "b", "c");
        }

        [Fact]
        public async Task ShouldConvertStringArrayToInputListOfObjectsElementByElement()
        {
            var engine = CreateEngine();

            var result = engine.ConvertValue(typeof(InputList<object>), new[] { "a", "b", "c" });

            result.Should().BeAssignableTo<InputList<object>>();
            var resolved = await ResolveInputAsync((Input<ImmutableArray<object>>)result);
            resolved.Should().HaveCount(3);
            resolved[0].Should().Be("a");
            resolved[1].Should().Be("b");
            resolved[2].Should().Be("c");
        }

        [Fact]
        public async Task ShouldConvertDictionaryToInputMapOfStrings()
        {
            var engine = CreateEngine();
            var source = new Dictionary<string, string>
            {
                ["one"] = "1",
                ["two"] = "2",
            };

            var result = engine.ConvertValue(typeof(InputMap<string>), source);

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

            var result = engine.ConvertValue(typeof(InputList<string>), source);

            result.Should().BeAssignableTo<InputList<string>>();
            var resolved = await ResolveInputAsync((Input<ImmutableArray<string>>)result);
            resolved.Should().Equal("x", "y");
        }

        [Fact]
        public async Task ShouldPreferCollectionImplicitOperatorOverUnaryObjectOperatorForInputListOfObjects()
        {
            var engine = CreateEngine();
            var source = ImmutableArray.Create("a", "b", "c");

            var result = engine.ConvertValue(typeof(InputList<object>), source);

            result.Should().BeAssignableTo<InputList<object>>();
            var resolved = await ResolveInputAsync((Input<ImmutableArray<object>>)result);
            resolved[0].Should().NotBeNull();
            resolved[0].GetType().FullName.Should().Be("System.String");
            resolved.Length.Should().Be(3);
            resolved[0].Should().Be("a");
            resolved[1].Should().Be("b");
            resolved[2].Should().Be("c");
        }

        [Fact(Skip = "Not supported yet")]
        public async Task ShouldPreferCollectionImplicitOperatorOverUnaryObjectOperatorForInputListOfObjectsFromOutput()
        {
            var engine = CreateEngine();
            var source = Output.Create(ImmutableArray.Create("a", "b", "c"));

            var result = engine.ConvertValue(typeof(InputList<object>), source);

            result.Should().BeAssignableTo<InputList<object>>();
            var resolved = await ResolveInputAsync((Input<ImmutableArray<object>>)result);
            resolved[0].Should().NotBeNull();
            resolved[0].GetType().FullName.Should().Be("System.String");
            resolved.Length.Should().Be(3);
            resolved[0].Should().Be("a");
            resolved[1].Should().Be("b");
            resolved[2].Should().Be("c");
        }

        [Fact]
        public async Task ShouldPreferCollectionImplicitOperatorOverUnaryObjectOperatorForInputListOfNumbersFromOutput()
        {
            var engine = CreateEngine();
            var source = Output.Create(ImmutableArray.Create(1, 2, 3));

            var result = engine.ConvertValue(typeof(InputList<double>), source);

            result.Should().BeAssignableTo<InputList<double>>();
            var resolved = await ResolveInputAsync((Input<ImmutableArray<double>>)result);
            resolved[0].GetType().FullName.Should().Be("System.Double");
            resolved.Length.Should().Be(3);
            resolved[0].Should().Be(1);
            resolved[1].Should().Be(2);
            resolved[2].Should().Be(3);
        }

        [Fact]
        public async Task ShouldConvertInputOfDoubleFromOutputOfInt()
        {
            var engine = CreateEngine();
            var source = Output.Create(1);

            var result = engine.ConvertValue(typeof(Input<double>), source);

            result.Should().BeAssignableTo<Input<double>>();
            var resolved = await ResolveInputAsync((Input<double>)result);
            resolved.Should().Be(1);
        }

        [Fact]
        public async Task ShouldProjectOutputStringToInputPulumiEnum()
        {
            var engine = CreateEngine();
            var source = Output.Create("UserAssigned");

            var result = engine.ConvertValue(typeof(Input<ManagedServiceIdentityType>), source);

            result.Should().BeAssignableTo<Input<ManagedServiceIdentityType>>();
            var resolved = await ResolveInputAsync((Input<ManagedServiceIdentityType>)result);
            resolved.Should().Be(ManagedServiceIdentityType.UserAssigned);
        }

        [Fact]
        public void ShouldUseNamedValuePropertyConverter()
        {
            var (resolver, engine) = CreateResolverAndEngine();

            var plan = resolver.CreateKeyedListItemPlan(typeof(NamedValueArgs), typeof(string));

            var result = engine.ConvertValue(plan, typeof(NamedValueArgs), "hello");

            result.Should().BeOfType<NamedValueArgs>();
            var typed = (NamedValueArgs)result;
            typed.Name.Should().BeNull();
            typed.Value.Should().NotBeNull();
        }

        [Fact]
        public async Task ShouldPopulateNamedValuePropertyConverterPayload()
        {
            var (resolver, engine) = CreateResolverAndEngine();

            var plan = resolver.CreateKeyedListItemPlan(typeof(NamedValueArgs), typeof(string));

            var result = (NamedValueArgs)engine.ConvertValue(plan, typeof(NamedValueArgs), "hello");

            var resolved = await ResolveInputAsync(result.Value!);
            resolved.Should().Be("hello");
        }

        [Fact]
        public async Task ShouldUseAzureSqlConnectionStringConverter()
        {
            var engine = CreateEngine();

            var result = engine.ConvertValue(typeof(ConnStringInfoArgs), "Server=tcp:test.database.windows.net;Initial Catalog=db;", AzureKnownConverterNames.AzureSqlConnectionString);

            result.Should().BeOfType<ConnStringInfoArgs>();
            var typed = (ConnStringInfoArgs)result;
            var connectionString = await ResolveInputAsync(typed.ConnectionString!);
            var connectionType = await ResolveInputAsync(typed.Type!);
            connectionString.Should().Be("Server=tcp:test.database.windows.net;Initial Catalog=db;");
            connectionType.Should().Be(ConnectionStringType.SQLAzure);
        }

        [Fact]
        public void ResolverShouldSelectNamedConverterCaseInsensitively()
        {
            var services = new ServiceCollection();
            services.AddDefaultValueConverters();
            services.AddAzureValueConverters();
            var resolver = services.BuildServiceProvider().GetRequiredService<IConversionResolver>();

            var plan = resolver.Resolve(new ConversionRequest(typeof(ConnStringInfoArgs), "Server=tcp:test.database.windows.net;Initial Catalog=db;"), "AZURE-SQL-CONNECTION-STRING");

            plan.Should().NotBeNull();
            plan!.RootStep.Should().Be(new NamedConverterStep(typeof(AzureSqlConnectionStringConverter), typeof(ConnStringInfoArgs)));
        }

        [Fact]
        public void ResolverShouldReturnUnresolvedForMissingNamedConverter()
        {
            var resolver = CreateResolver();

            var plan = resolver.Resolve(new ConversionRequest(typeof(string), "value"), "missing-converter");

            plan.Should().BeNull();
        }

        [Fact]
        public void ResolverShouldReturnUnresolvedWhenAutomaticConverterIsMissing()
        {
            var resolver = CreateResolver();

            var plan = resolver.Resolve(new ConversionRequest(typeof(DateTime), "2025-01-01"));

            plan.Should().BeNull();
        }

        [Fact]
        public void ResolverShouldPlanBuiltInInputConversionStructurally()
        {
            var resolver = CreateResolver();

            var plan = resolver.Resolve(new ConversionRequest(typeof(Input<ManagedServiceIdentityType>), "UserAssigned"));

            plan.Should().NotBeNull();
            plan!.RootStep.Should().BeOfType<InputConversionStep>();
            var inputStep = (InputConversionStep)plan.RootStep;
            inputStep.InnerType.Should().Be(typeof(ManagedServiceIdentityType));
            inputStep.InnerStep.Should().BeOfType<PulumiEnumConversionStep>();
        }

        [Fact]
        public void ResolverShouldPlanNamedOutputProjectionStructurally()
        {
            var resolver = CreateResolver();
            var source = Output.Create("Server=tcp:test.database.windows.net;Initial Catalog=db;");

            var plan = resolver.Resolve(new ConversionRequest(typeof(ConnStringInfoArgs), source), AzureKnownConverterNames.AzureSqlConnectionString);

            plan.Should().NotBeNull();
            plan!.RootStep.Should().BeOfType<ProjectedOutputConversionStep>();
            var projected = (ProjectedOutputConversionStep)plan.RootStep;
            projected.ProjectedTargetType.Should().Be(typeof(ConnStringInfoArgs));
            projected.InnerStep.Should().Be(new NamedConverterStep(typeof(AzureSqlConnectionStringConverter), typeof(ConnStringInfoArgs)));
        }

        [Fact]
        public void ResolverShouldThrowForDuplicateNamedConverterRegistrations()
        {
            var services = new ServiceCollection();
            services.AddNamedValueConverter<DuplicateNamedValueConverterA>();
            services.AddNamedValueConverter<DuplicateNamedValueConverterB>();

            var act = () => services.BuildServiceProvider().GetRequiredService<IConversionResolver>();

            act.Should().Throw<InvalidOperationException>();
        }

        [Fact]
        public async Task ShouldProjectOutputThroughAzureSqlConnectionStringConverter()
        {
            var engine = CreateEngine();
            var source = Output.Create("Server=tcp:test.database.windows.net;Initial Catalog=db;");

            var result = engine.ConvertValue(typeof(ConnStringInfoArgs), source, AzureKnownConverterNames.AzureSqlConnectionString);

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
            var act = () => engine.ConvertValue(typeof(string), "value", "missing-converter");

            act.Should().Throw<NotSupportedException>();
        }

        [Fact]
        public void ShouldThrowWhenAutomaticConverterIsMissing()
        {
            var engine = CreateEngine();
            var act = () => engine.ConvertValue(typeof(DateTime), "2025-01-01");

            act.Should().Throw<NotSupportedException>();
        }

        private static IConversionEngine CreateEngine()
        {
            return CreateResolverAndEngine().engine;
        }

        private static IConversionResolver CreateResolver()
        {
            return CreateResolverAndEngine().resolver;
        }

        private static (IConversionResolver resolver, IConversionEngine engine) CreateResolverAndEngine()
        {
            var services = new ServiceCollection();
            services.AddDefaultValueConverters();
            services.AddAzureValueConverters();
            var provider = services.BuildServiceProvider();
            return (provider.GetRequiredService<IConversionResolver>(), provider.GetRequiredService<IConversionEngine>());
        }

        private static async Task<T> ResolveInputAsync<T>(Input<T> input)
        {
            var tcs = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
            await Pulumi.Deployment.TestAsync(new LiveArch.Deployment.Mocks(), new TestOptions { IsPreview = false }, () =>
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

        private sealed class NamedValueArgs
        {
            public Input<string>? Name { get; set; }

            public Input<string>? Value { get; set; }
        }

        private sealed class DuplicateNamedValueConverterA : INamedValueConverter
        {
            public string Name => "duplicate-named";

            public bool CanConvert(IConversionRequest request)
            {
                return false;
            }

            public object Convert(ConversionRequest request)
            {
                throw new NotSupportedException();
            }
        }

        private sealed class DuplicateNamedValueConverterB : INamedValueConverter
        {
            public string Name => "DUPLICATE-NAMED";

            public bool CanConvert(IConversionRequest request)
            {
                return false;
            }

            public object Convert(ConversionRequest request)
            {
                throw new NotSupportedException();
            }
        }
    }
}
