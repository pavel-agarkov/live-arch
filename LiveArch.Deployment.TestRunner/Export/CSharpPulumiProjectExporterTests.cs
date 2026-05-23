using LiveArch.Deployment.Export.CSharp;

namespace LiveArch.Deployment.TestRunner.Export
{
    public class CSharpPulumiProjectExporterTests : DeploymentTestBase
    {
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
        public async Task ShouldExportCSharpPulumiProjectLibraryToTestResults()
        {
            var exporter = new CSharpPulumiProjectExporter();

            var ws = await ProcessDeployment("order-env", exporter);
            var export = exporter.Export("order-env", new CSharpPulumiProjectExporterOptions(
                ProjectName: "Order.Generated.Pulumi",
                RootNamespace: "Generated.Order.Pulumi",
                AdditionalNamespaces: ["System.Linq"],
                AdditionalPackageReferences: [new CSharpPackageReference("Awesome.Custom.Package", "1.2.3")]));

            Directory.Exists(export.DirectoryPath).Should().BeTrue();
            File.Exists(export.ProjectFilePath).Should().BeTrue();
            File.Exists(export.DeploymentFilePath).Should().BeTrue();

            export.Model.CreatedCount.Should().Be(ws.CreatedResources.Count);
            export.Model.ReferencedCount.Should().Be(ws.ReferencedResources.Count);
            export.Model.Resources.Should().NotBeEmpty();
            export.Model.Resources.Should().Contain(resource => resource.Kind == "Created");
            export.Model.Resources.Should().Contain(resource => resource.Kind == "Referenced");
            export.Model.ProjectName.Should().Be("Order.Generated.Pulumi");
            export.Model.AdditionalNamespaces.Should().Contain("System.Linq");
            export.Model.PackageReferences.Should().Contain(reference => reference.PackageId == "Awesome.Custom.Package");
            export.Model.Resources.Should().Contain(resource => resource.CreationStatement.Contains("new global::Pulumi.AzureNative.ManagedIdentity.UserAssignedIdentity("));
            export.Model.Resources.Should().Contain(resource => resource.CreationStatement.Contains("global::Pulumi.AzureNative.ServiceBus.GetNamespace.Invoke("));
            export.Model.Resources.Should().Contain(resource => resource.VariableName == "orderServiceMi");
            export.Model.Resources.Should().Contain(resource => resource.VariableName == "serviceBusNamespace");

            Path.GetFileName(export.DirectoryPath).Should().MatchRegex(@"^order-env-\d{8}-\d{6}-\d{3}(-\d+)?$");

            var projectText = File.ReadAllText(export.ProjectFilePath);
            projectText.Should().Contain("Awesome.Custom.Package");
            projectText.Should().Contain("Pulumi.AzureNative");

            var deploymentText = File.ReadAllText(export.DeploymentFilePath);
            deploymentText.Should().Contain("Generated.Order.Pulumi");
            deploymentText.Should().Contain("ProcessAsync");
            deploymentText.Should().Contain("new global::Pulumi.AzureNative.ManagedIdentity.UserAssignedIdentity(");
            deploymentText.Should().Contain("global::Pulumi.AzureNative.ServiceBus.GetNamespace.Invoke(");
            deploymentText.Should().Contain("new global::Pulumi.CustomResourceOptions");
            deploymentText.Should().Contain("/* saList.value | transformers: SplitTransformer");
            deploymentText.Should().Contain("order-env");
        }
    }
}
