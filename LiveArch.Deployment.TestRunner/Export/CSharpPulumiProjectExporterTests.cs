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
            deploymentText.Should().Contain("public static Task ProcessAsync(LiveArchOrderDeploymentVariables variables");
            deploymentText.Should().Contain("NamespaceName = $\"{variables.Env}-sbns\"");
            deploymentText.Should().Contain("VaultName = variables.KeyVaultName");
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
