using LiveArch.Deployment.Expressions;
using LiveArch.Deployment.Export.CSharp;
using LiveArch.Transformers;

namespace LiveArch.Deployment.TestRunner.Export
{
    public class CSharpPulumiProjectExporterTests : DeploymentTestBase
    {
        private static string ExportProjectDirectory => Path.GetFullPath(Path.Combine(
            AppContext.BaseDirectory,
            "..",
            "..",
            "..",
            "..",
            "LiveArch.Order.Deployment"));
        private static string ExportTestProjectDirectory => Path.GetFullPath(Path.Combine(
            AppContext.BaseDirectory,
            "..",
            "..",
            "..",
            "..",
            "LiveArch.Order.Deployment.Tests"));

        [Fact]
        public async Task ShouldNotifyObserverForCreatedAndReferencedResources()
        {
            var observer = new RecordingStructurizrDeploymentObserver();

            var ws = await ProcessDeployment("order-env", observer);

            observer.CreatedResources.Count.Should().Be(ws.CreatedResources.Count);
            observer.ReferencedResources.Count.Should().Be(ws.ReferencedResources.Count);
            observer.CreatedResources.Any(resource => resource.DependsOn.Count > 0).Should().BeTrue();
            observer.CreatedResources.Any(resource => resource.ExpressionModel.Assignments.Count > 0).Should().BeTrue();
            observer.ReferencedResources.Any(resource => resource.ExpressionModel.Assignments.Count > 0).Should().BeTrue();
        }

        [Fact]
        public async Task ShouldPreserveTransformerMetadataStructurally()
        {
            var observer = new RecordingStructurizrDeploymentObserver();

            await ProcessDeployment("order-env", observer);

            var dependencyTransformers = observer.CreatedResources
                .SelectMany(resource => resource.ExpressionModel.Assignments)
                .Select(assignment => assignment.Value)
                .OfType<DependencyValueExpressionModel>()
                .SelectMany(expression => expression.Transformers)
                .ToArray();

            dependencyTransformers.Should().Contain(transformer =>
                transformer.Name == "split" &&
                transformer.Parameter == "," &&
                transformer.ImplementationType == typeof(SplitTransformer) &&
                transformer.IsBuiltIn);
        }

        [Fact]
        public async Task ShouldRenderInlineTransformerCollectionValuesInGeneratedCode()
        {
            var exporter = new CSharpPulumiProjectExporter();

            await ProcessDeployment("order-env", exporter);
            var export = exporter.Export("order-env", new CSharpPulumiProjectExporterOptions(
                ProjectName: "LiveArch.Order.Deployment",
                RootNamespace: "LiveArch.Order.Deployment",
                OutputDirectory: ExportProjectDirectory,
                VariableValues: variables,
                CleanOutputDirectories: false,
                AdditionalNamespaces: ["System.Linq"]));

            var deploymentText = File.ReadAllText(export.DeploymentFilePath);

            deploymentText.Should().Contain("order-service-kv-access-policy");
            deploymentText.Should().Contain("PermissionsArgs");
            deploymentText.Should().Contain("Secrets =");
            deploymentText.Should().Contain("\"get\"");
            deploymentText.Should().Contain("\"list\"");
        }

        [Fact]
        public async Task ShouldPreserveInlineTransformerMetadataForDirectValues()
        {
            var observer = new RecordingStructurizrDeploymentObserver();

            await ProcessDeployment("order-env", observer);

            var kvAccessPolicy = observer.CreatedResources
                .FirstOrDefault(resource => resource.ResourceName == "order-service-kv-access-policy");

            kvAccessPolicy.Should().NotBeNull();

            var allAssignments = string.Join(", ", kvAccessPolicy!.ExpressionModel.Assignments
                .Select(a => a.Target switch
                {
                    PropertyAssignmentTargetModel p => p.Path,
                    KeyedCollectionAssignmentTargetModel k => $"{k.CollectionPath}:{k.Key}",
                    AppendCollectionAssignmentTargetModel app => $"{app.CollectionPath}+=",
                    _ => "<unknown>"
                }));

            var secretsAssignment = kvAccessPolicy!.ExpressionModel.Assignments
                .FirstOrDefault(a => a.Target is PropertyAssignmentTargetModel p && p.Path.Contains("secrets", StringComparison.OrdinalIgnoreCase));

            secretsAssignment.Should().NotBeNull($"Expected to find 'secrets' assignment. All assignments: {allAssignments}");
            secretsAssignment!.Value.Should().BeOfType<DirectValueExpressionModel>();

            var directExpression = (DirectValueExpressionModel)secretsAssignment.Value;
            directExpression.InlineTransformers.Should().HaveCount(1);
            directExpression.InlineTransformers.First().Name.Should().Be("split");
            directExpression.InlineTransformers.First().Parameter.Should().Be(",");
            directExpression.InlineTransformers.First().IsBuiltIn.Should().BeTrue();
            directExpression.Value.Should().NotBeNull();
        }

        [Fact]
        public void ShouldDeriveTransformerClassificationFromNamespace()
        {
            var builtIn = new TransformerExpressionModel("format", "item-{0}", typeof(FormatTransformer));
            var custom = new TransformerExpressionModel("custom", string.Empty, typeof(object));

            builtIn.IsBuiltIn.Should().BeTrue();
            custom.IsBuiltIn.Should().BeFalse();
            typeof(TransformerExpressionModel).GetProperty("Classification").Should().BeNull();
            typeof(TransformerExpressionModel).GetProperty("BuiltIn").Should().BeNull();
        }

        [Fact]
        public async Task ShouldExportCSharpPulumiProjectLibraryToTestResults()
        {
            var exporter = new CSharpPulumiProjectExporter();

            var ws = await ProcessDeployment("order-env", exporter);
            var export = exporter.Export("order-env", new CSharpPulumiProjectExporterOptions(
                ProjectName: "LiveArch.Order.Deployment",
                RootNamespace: "LiveArch.Order.Deployment",
                OutputDirectory: ExportProjectDirectory,
                VariableValues: variables,
                CleanOutputDirectories: false,
                AdditionalNamespaces: ["System.Linq"]));

            Directory.Exists(export.DirectoryPath).Should().BeTrue();
            File.Exists(export.ProjectFilePath).Should().BeTrue();
            File.Exists(export.DeploymentFilePath).Should().BeTrue();
            export.TestDirectoryPath.Should().Be(ExportTestProjectDirectory);
            export.TestProjectFilePath.Should().NotBeNull();
            export.TestFilePath.Should().NotBeNull();
            Directory.Exists(export.TestDirectoryPath!).Should().BeTrue();
            File.Exists(export.TestProjectFilePath!).Should().BeTrue();
            File.Exists(export.TestFilePath!).Should().BeTrue();

            export.Model.CreatedCount.Should().Be(ws.CreatedResources.Count - 2);
            export.Model.ReferencedCount.Should().Be(ws.ReferencedResources.Count);
            export.Model.Resources.Should().NotBeEmpty();
            export.Model.Resources.Should().Contain(resource => resource.Kind == "Created");
            export.Model.Resources.Should().Contain(resource => resource.Kind == "Referenced");
            export.Model.ProjectName.Should().Be("LiveArch.Order.Deployment");
            export.Model.AdditionalNamespaces.Should().Contain("System.Linq");
            export.Model.Dependencies.PackageReferences.Should().NotContain(reference => reference.PackageId == "Awesome.Custom.Package");
            export.Model.Resources.Should().Contain(resource => resource.CreationStatement.Contains("new global::Pulumi.AzureNative.ManagedIdentity.UserAssignedIdentity("));
            export.Model.Resources.Should().Contain(resource => resource.CreationStatement.Contains("global::Pulumi.AzureNative.ServiceBus.GetNamespace.Invoke("));
            export.Model.Resources.Should().Contain(resource => resource.VariableName == "orderServiceMi");
            export.Model.Resources.Should().Contain(resource => resource.VariableName == "serviceBusNamespace");
            export.Model.Resources.Should().Contain(resource => resource.VariableName == "orderApi");
            export.Model.Resources.Should().Contain(resource => resource.VariableName == "orderApiWebApp");

            export.DirectoryPath.Should().Be(ExportProjectDirectory);

            var projectText = File.ReadAllText(export.ProjectFilePath);
            projectText.Should().Contain("Pulumi.AzureNative");
            projectText.Should().Contain("LiveArch.Resources.Azure.csproj");
            projectText.Should().NotContain("LiveArch.Deployment.csproj");

            var testProjectText = File.ReadAllText(export.TestProjectFilePath!);
            testProjectText.Should().Contain("LiveArch.Deployment.Export.Testing.csproj");
            testProjectText.Should().Contain("LiveArch.Order.Deployment.csproj");
            testProjectText.Should().Contain("Microsoft.NET.Test.Sdk");

            var testFileText = File.ReadAllText(export.TestFilePath!);
            testFileText.Should().Contain("ExportedDeploymentTestHost.ExecuteAsync");
            testFileText.Should().Contain("global::LiveArch.Order.Deployment.ExportedDeployment.ProcessAsync(CreateVariables())");
            testFileText.Should().Contain("Assert.NotEmpty(mocks.Resources)");
            testFileText.Should().Contain("private static global::LiveArch.Order.Deployment.LiveArchOrderDeploymentVariables CreateVariables() => new()");

            var deploymentText = File.ReadAllText(export.DeploymentFilePath);
            deploymentText.Should().Contain("LiveArch.Order.Deployment");
            deploymentText.Should().Contain("ProcessAsync");
            deploymentText.Should().Contain("public sealed class LiveArchOrderDeploymentVariables");
            deploymentText.Should().Contain("public static Task ProcessAsync(LiveArchOrderDeploymentVariables vars");
            deploymentText.Should().Contain("NamespaceName = $\"{vars.Env}-sbns\"");
            deploymentText.Should().Contain("VaultName = vars.KeyVaultName");
            deploymentText.Should().Contain("ResourceGroupName = sharedRgReference.Apply(value => value.Name)");
            deploymentText.Should().Contain("ObjectId = orderServiceMi.PrincipalId");
            deploymentText.Should().Contain("PrincipalId = orderWorkerWebApp.Identity.Apply(value => value!.PrincipalId)");
            deploymentText.Should().Contain("new global::Pulumi.AzureNative.ManagedIdentity.UserAssignedIdentity(");
            deploymentText.Should().Contain("global::Pulumi.AzureNative.ServiceBus.GetNamespace.Invoke(");
            deploymentText.Should().Contain("new global::Pulumi.CustomResourceOptions");
            deploymentText.Should().Contain("Push = true");
            deploymentText.Should().Contain("Type = global::Pulumi.AzureNative.Web.ManagedServiceIdentityType.UserAssigned");
            deploymentText.Should().Contain("PrincipalType = global::Pulumi.AzureNative.Authorization.PrincipalType.ServicePrincipal");
            deploymentText.Should().NotContain("ForEachLoop");
            deploymentText.Should().NotContain("ForEachSource");
            deploymentText.Should().Contain("order-env");
        }

        [Fact]
        public async Task ShouldResolveVersionlessAdditionalPackageReferenceFromLoadedAssembly()
        {
            var outputDirectory = CreateTemporaryExportDirectory();
            var exporter = new CSharpPulumiProjectExporter();

            await ProcessDeployment("order-env", exporter);
            var export = exporter.Export("order-env", new CSharpPulumiProjectExporterOptions(
                OutputDirectory: outputDirectory,
                GenerateTestProject: false,
                AdditionalPackageReferences: [new CSharpPackageReference("Pulumi", null)]));

            var packageReference = export.Model.Dependencies.PackageReferences.Single(reference => reference.PackageId == "Pulumi");
            packageReference.Version.Should().NotBeNullOrWhiteSpace();
            export.Model.Diagnostics.Should().NotContain(diagnostic =>
                diagnostic.Message.Contains("Package reference 'Pulumi'", StringComparison.Ordinal));

            var projectText = File.ReadAllText(export.ProjectFilePath);
            projectText.Should().Contain("<PackageReference Include=\"Pulumi\" Version=");
        }

        [Fact]
        public async Task ShouldReportUnresolvedVersionlessAdditionalPackageReference()
        {
            var outputDirectory = CreateTemporaryExportDirectory();
            var exporter = new CSharpPulumiProjectExporter();

            await ProcessDeployment("order-env", exporter);
            var export = exporter.Export("order-env", new CSharpPulumiProjectExporterOptions(
                OutputDirectory: outputDirectory,
                GenerateTestProject: false,
                AdditionalPackageReferences: [new CSharpPackageReference("Contoso.Unresolved.Package", null)]));

            export.Model.Dependencies.PackageReferences.Should().Contain(reference =>
                reference.PackageId == "Contoso.Unresolved.Package" && reference.Version == null);
            export.Model.Diagnostics.Should().ContainSingle(diagnostic =>
                diagnostic.Severity == CSharpExportDiagnosticSeverity.Error &&
                diagnostic.Message.Contains("Contoso.Unresolved.Package", StringComparison.Ordinal));

            var projectText = File.ReadAllText(export.ProjectFilePath);
            projectText.Should().Contain("PackageReference 'Contoso.Unresolved.Package' needs an explicit Version");
            projectText.Should().Contain("<PackageReference Include=\"Contoso.Unresolved.Package\" />");
        }

        private static string CreateTemporaryExportDirectory()
        {
            return Path.Combine(Path.GetTempPath(), "LiveArch.CSharpPulumiProjectExporterTests", Guid.NewGuid().ToString("N"));
        }
    }
}
