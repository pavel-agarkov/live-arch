using LiveArch.Deployment.Azure.Converters;
using LiveArch.Deployment.Converters;
using LiveArch.Deployment.Transformers;
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
            var parsed = TransformerPipeline.TryParse("get, list | split ,", out var sourceValue, out var transformers);

            parsed.Should().BeTrue();
            sourceValue.Should().Be("get, list");
            transformers.Should().HaveCount(1);
            transformers.Single().Should().BeOfType<SplitTransformer>();
        }

        [Fact]
        public void ShouldTreatPipeAsPlainValueWhenFirstTransformerIsUnknown()
        {
            var parsed = TransformerPipeline.TryParse("literal | still literal", out var sourceValue, out var transformers);

            parsed.Should().BeFalse();
            sourceValue.Should().Be("literal | still literal");
            transformers.Should().BeEmpty();
        }

        [Fact]
        public async Task ShouldConvertSplitResultToInputListThroughImplicitOperator()
        {
            var services = new ServiceCollection();
            services.AddDefaultValueConverters();
            services.AddAzureValueConverters();
            var engine = services.BuildServiceProvider().GetRequiredService<IConversionEngine>();

            TransformerPipeline.TryParse("get, list | split ,", out var sourceValue, out var transformers).Should().BeTrue();
            var transformed = TransformerPipeline.Apply(sourceValue, transformers);
            var result = engine.ConvertValue(typeof(InputList<string>), transformed, ConversionContext.Empty);

            var resolved = await ResolveInputAsync((Input<ImmutableArray<string>>)result);
            resolved.Should().Equal("get", "list");
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
            var act = () => new MultiplyTransformer("0", devider: true);

            act.Should().Throw<InvalidOperationException>();
        }

        [Fact]
        public void FormatTransformer_ShouldThrowForInvalidFormatString()
        {
            var transformer = new FormatTransformer("{0");

            var act = () => transformer.Transform("value");

            act.Should().Throw<InvalidOperationException>();
        }

        private static async Task<T> ResolveInputAsync<T>(Input<T> input)
        {
            var tcs = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
            await Pulumi.Deployment.TestAsync(new global::LiveArch.Deployment.Mocks(), new Pulumi.Testing.TestOptions { IsPreview = false }, () =>
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
    }
}
