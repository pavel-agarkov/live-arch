using LiveArch.Deployment.Azure.Converters;
using LiveArch.Deployment.Converters;
using LiveArch.Deployment.Transformers;
using LiveArch.Transformers;
using Microsoft.Extensions.DependencyInjection;
using Pulumi;
using System.Collections.Immutable;

namespace LiveArch.Deployment.TestRunner
{
    public class TransformerPipelineTests
    {
        [Fact]
        public void ShouldParseInlineTransformerPipeline()
        {
            var pipeline = CreateServices().BuildServiceProvider().GetRequiredService<TransformerPipeline>();
            var parsed = pipeline.TryParse("get, list | split ,", out var sourceValue, out var transformers);

            parsed.Should().BeTrue();
            sourceValue.Should().Be("get, list");
            transformers.Should().HaveCount(1);
            transformers.Single().Should().BeOfType<SplitTransformer>();
        }

        [Fact]
        public void ShouldTreatPipeAsPlainValueWhenFirstTransformerIsUnknown()
        {
            var pipeline = CreateServices().BuildServiceProvider().GetRequiredService<TransformerPipeline>();
            var parsed = pipeline.TryParse("literal | still literal", out var sourceValue, out var transformers);

            parsed.Should().BeFalse();
            sourceValue.Should().Be("literal | still literal");
            transformers.Should().BeEmpty();
        }

        [Fact]
        public async Task ShouldConvertSplitResultToInputListThroughImplicitOperator()
        {
            var services = CreateServices();
            var engine = services.BuildServiceProvider().GetRequiredService<IConversionEngine>();
            var pipeline = services.BuildServiceProvider().GetRequiredService<TransformerPipeline>();

            pipeline.TryParse("get, list | split ,", out var sourceValue, out var transformers).Should().BeTrue();
            var transformed = TransformerPipeline.Apply(sourceValue, transformers);
            var result = engine.ConvertValue(typeof(InputList<string>), transformed);

            var resolved = await ResolveInputAsync((Input<ImmutableArray<string>>)result);
            resolved.Should().Equal("get", "list");
        }

        [Fact]
        public void ShouldResolveCustomTransformerFromDependencyInjection()
        {
            var services = CreateServices();
            services.AddNamedTransformer<PrefixTransformerFactory>();
            var pipeline = services.BuildServiceProvider().GetRequiredService<TransformerPipeline>();

            var parsed = pipeline.TryParse("value | prefix item-", out var sourceValue, out var transformers);

            parsed.Should().BeTrue();
            var transformed = TransformerPipeline.Apply(sourceValue, transformers);
            transformed.Should().Be("item-value");
        }

        [Fact]
        public void ShouldUseBuiltInTransformersWhenOnlyCustomTransformerIsRegistered()
        {
            var services = new ServiceCollection();
            services.AddNamedTransformer<PrefixTransformerFactory>();
            var pipeline = services.BuildServiceProvider().GetRequiredService<TransformerPipeline>();

            var parsed = pipeline.TryParse("a,b | split ,", out var sourceValue, out var transformers);

            parsed.Should().BeTrue();
            var transformed = TransformerPipeline.Apply(sourceValue, transformers);
            transformed.Should().BeEquivalentTo(new[] { "a", "b" });
        }

        [Fact]
        public void ShouldAllowCustomTransformerToOverrideBuiltInTransformerName()
        {
            var services = new ServiceCollection();
            services.AddNamedTransformer("split", _ => new FormatTransformer("override-{0}"));
            var pipeline = services.BuildServiceProvider().GetRequiredService<TransformerPipeline>();

            var parsed = pipeline.TryParse("value | split ,", out var sourceValue, out var transformers);

            parsed.Should().BeTrue();
            var transformed = TransformerPipeline.Apply(sourceValue, transformers);
            transformed.Should().Be("override-value");
        }

        [Fact]
        public void SplitTransformer_ShouldThrowForNonStringInput()
        {
            var transformer = new SplitTransformer(",");

            var act = () => transformer.Transform(123);

            act.Should().Throw<InvalidOperationException>();
        }

        [Fact]
        public void RegExTransformer_ShouldThrowForNonStringInput()
        {
            var transformer = new RegExTransformer("[0-9]+", RegExTransformer.RegExOperation.Extract);

            var act = () => transformer.Transform(123);

            act.Should().Throw<InvalidOperationException>();
        }

        [Fact]
        public void MultiplyTransformer_ShouldThrowForInvalidMultiplier()
        {
            var act = () => new MultiplyTransformer("not-a-number");

            act.Should().Throw<InvalidOperationException>();
        }

        [Fact]
        public void MultiplyTransformer_ShouldThrowForInvalidInput()
        {
            var transformer = new MultiplyTransformer("2");

            var act = () => transformer.Transform("not-a-number");

            act.Should().Throw<InvalidOperationException>();
        }

        [Fact]
        public void DivideTransformer_ShouldThrowForZeroDivisor()
        {
            var act = () => new MultiplyTransformer("0", divider: true);

            act.Should().Throw<InvalidOperationException>();
        }

        [Fact]
        public void FormatTransformer_ShouldThrowForInvalidFormatString()
        {
            var transformer = new FormatTransformer("{0");

            var act = () => transformer.Transform("value");

            act.Should().Throw<InvalidOperationException>();
        }

        private static ServiceCollection CreateServices()
        {
            var services = new ServiceCollection();
            services.AddDefaultTransformers();
            services.AddDefaultValueConverters();
            services.AddAzureValueConverters();
            return services;
        }

        private static async Task<T> ResolveInputAsync<T>(Input<T> input)
        {
            var tcs = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
            await Pulumi.Deployment.TestAsync(new LiveArch.Deployment.Mocks(), new Pulumi.Testing.TestOptions { IsPreview = false }, () =>
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

        private sealed class PrefixTransformerFactory : INamedTransformerFactory
        {
            public string Name => "prefix";

            public ITransformer Create(string parameter)
            {
                return new FormatTransformer($"{parameter}{{0}}");
            }
        }
    }
}
