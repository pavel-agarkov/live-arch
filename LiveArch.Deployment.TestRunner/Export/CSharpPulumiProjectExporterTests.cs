using LiveArch.Deployment.Export.CSharp;

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
                ProjectName: "LiveArch.Order.Deployment",
                RootNamespace: "LiveArch.Order.Deployment",
                OutputDirectory: ExportProjectDirectory,
                AdditionalNamespaces: ["System.Linq"]));

            Directory.Exists(export.DirectoryPath).Should().BeTrue();
            File.Exists(export.ProjectFilePath).Should().BeTrue();
            File.Exists(export.DeploymentFilePath).Should().BeTrue();

            export.Model.CreatedCount.Should().Be(ws.CreatedResources.Count - 2);
            export.Model.ReferencedCount.Should().Be(ws.ReferencedResources.Count);
            export.Model.Resources.Should().NotBeEmpty();
            export.Model.Resources.Should().Contain(resource => resource.Kind == "Created");
            export.Model.Resources.Should().Contain(resource => resource.Kind == "Referenced");
            export.Model.ProjectName.Should().Be("LiveArch.Order.Deployment");
            export.Model.AdditionalNamespaces.Should().Contain("System.Linq");
            export.Model.PackageReferences.Should().NotContain(reference => reference.PackageId == "Awesome.Custom.Package");
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

            var deploymentText = File.ReadAllText(export.DeploymentFilePath);
            deploymentText.Should().Contain("LiveArch.Order.Deployment");
            deploymentText.Should().Contain("ProcessAsync");
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
    }
}
