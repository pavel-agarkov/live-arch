using LiveArch.Deployment.Observers;
using Pulumi;
using Structurizr;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;

namespace LiveArch.Deployment.Export.CSharp
{
    public sealed class CSharpPulumiProjectExporter : IStructurizrDeploymentObserver
    {
        private readonly List<ObservedResource> createdResources = [];
        private readonly List<ObservedResource> referencedResources = [];

        public void OnResourceCreated(ModelItem node, StructurizrDeploymentProcessor.ResourceScope scope, object resource, IReadOnlyCollection<Resource> dependsOn)
        {
            createdResources.Add(new ObservedResource("Created", node, scope.Id, resource, dependsOn));
        }

        public void OnResourceReferenced(ModelItem node, StructurizrDeploymentProcessor.ResourceScope scope, object resource, IReadOnlyCollection<Resource> dependsOn)
        {
            referencedResources.Add(new ObservedResource("Referenced", node, scope.Id, resource, dependsOn));
        }

        public CSharpPulumiProjectExport Export(string deployment, CSharpPulumiProjectExporterOptions? options = null)
        {
            options ??= new CSharpPulumiProjectExporterOptions();

            var allResources = createdResources.Concat(referencedResources).ToList();
            var keyByResource = allResources
                .GroupBy(resource => resource.Resource, ReferenceEqualityComparer.Instance)
                .ToDictionary(group => group.Key, group => CreateResourceKey(group.First()), ReferenceEqualityComparer.Instance);

            var projectName = string.IsNullOrWhiteSpace(options.ProjectName)
                ? CreateDefaultProjectName(deployment)
                : options.ProjectName.Trim();

            var model = new CSharpPulumiProjectModel(
                deployment,
                projectName,
                options.RootNamespace,
                options.AdditionalNamespaces,
                ResolvePackageReferences(allResources, options.AdditionalPackageReferences),
                ResolveProjectReferences(allResources),
                createdResources.Count,
                referencedResources.Count,
                [.. allResources.Select(resource => new CSharpPulumiResourceModel(
                    CreateResourceKey(resource),
                    resource.Kind,
                    resource.Node.ToString(),
                    resource.ScopeId,
                    resource.Resource.GetType(),
                    [.. resource.DependsOn
                        .Select(dependency => keyByResource.TryGetValue(dependency, out var key) ? key : dependency.GetResourceName())
                        .Distinct(StringComparer.Ordinal)]))]);

            return CSharpPulumiProjectWriter.Export(model);
        }

        private static string CreateDefaultProjectName(string deployment)
        {
            var safeDeploymentName = Regex.Replace(deployment, "[^a-zA-Z0-9_-]", "-").Trim('-');
            return string.IsNullOrWhiteSpace(safeDeploymentName)
                ? "Generated.Pulumi.Project"
                : $"Generated.{safeDeploymentName}.Pulumi";
        }

        private static string CreateResourceKey(ObservedResource resource)
        {
            return $"{resource.Kind}:{resource.ScopeId}:{resource.Node}";
        }

        private static IReadOnlyCollection<CSharpPackageReference> ResolvePackageReferences(
            IReadOnlyCollection<ObservedResource> resources,
            IReadOnlyCollection<CSharpPackageReference> additionalPackageReferences)
        {
            var packageReferences = new Dictionary<string, CSharpPackageReference>(StringComparer.OrdinalIgnoreCase)
            {
                ["Pulumi"] = new CSharpPackageReference("Pulumi", "3.106.2")
            };

            foreach (var resource in resources)
            {
                foreach (var packageReference in KnownPackageRegistry.Resolve(resource.Resource.GetType()))
                {
                    packageReferences[packageReference.PackageId] = packageReference;
                }
            }

            foreach (var packageReference in additionalPackageReferences)
            {
                packageReferences[packageReference.PackageId] = packageReference;
            }

            return [.. packageReferences.Values.OrderBy(reference => reference.PackageId, StringComparer.OrdinalIgnoreCase)];
        }

        private static IReadOnlyCollection<CSharpProjectReference> ResolveProjectReferences(IReadOnlyCollection<ObservedResource> resources)
        {
            var projectReferences = new Dictionary<string, CSharpProjectReference>(StringComparer.OrdinalIgnoreCase);
            foreach (var resource in resources)
            {
                foreach (var projectReference in KnownPackageRegistry.ResolveProjectReferences(resource.Resource.GetType()))
                {
                    projectReferences[projectReference.ProjectPath] = projectReference;
                }
            }

            return [.. projectReferences.Values.OrderBy(reference => reference.ProjectPath, StringComparer.OrdinalIgnoreCase)];
        }

        private sealed record ObservedResource(string Kind, ModelItem Node, int ScopeId, object Resource, IReadOnlyCollection<Resource> DependsOn);
    }

    public static class CSharpPulumiProjectWriter
    {
        public static CSharpPulumiProjectExport Export(CSharpPulumiProjectModel model)
        {
            var directory = CreateOutputDirectory(model.Deployment);
            Directory.CreateDirectory(directory);

            var projectFilePath = Path.Combine(directory, $"{model.ProjectName}.csproj");
            var deploymentFilePath = Path.Combine(directory, "ExportedDeployment.cs");

            File.WriteAllText(projectFilePath, CreateProjectFile(directory, model), Encoding.UTF8);
            File.WriteAllText(deploymentFilePath, CreateDeploymentFile(model), Encoding.UTF8);

            return new CSharpPulumiProjectExport(directory, projectFilePath, deploymentFilePath, model);
        }

        private static string CreateOutputDirectory(string deployment)
        {
            var safeDeploymentName = Regex.Replace(deployment, "[^a-zA-Z0-9_-]", "-");
            return Path.Combine(AppContext.BaseDirectory, "TestResults", "GeneratedProjects", $"{safeDeploymentName}-{Guid.NewGuid():N}");
        }

        private static string CreateProjectFile(string outputDirectory, CSharpPulumiProjectModel model)
        {
            var builder = new StringBuilder();
            builder.AppendLine("<Project Sdk=\"Microsoft.NET.Sdk\">");
            builder.AppendLine();
            builder.AppendLine("  <PropertyGroup>");
            builder.AppendLine("    <TargetFramework>net10.0</TargetFramework>");
            builder.AppendLine("    <ImplicitUsings>enable</ImplicitUsings>");
            builder.AppendLine("    <Nullable>enable</Nullable>");
            builder.AppendLine("  </PropertyGroup>");
            builder.AppendLine();

            if (model.PackageReferences.Count > 0)
            {
                builder.AppendLine("  <ItemGroup>");
                foreach (var packageReference in model.PackageReferences)
                {
                    builder.AppendLine($"    <PackageReference Include=\"{packageReference.PackageId}\" Version=\"{packageReference.Version}\" />");
                }
                builder.AppendLine("  </ItemGroup>");
                builder.AppendLine();
            }

            if (model.ProjectReferences.Count > 0)
            {
                builder.AppendLine("  <ItemGroup>");
                foreach (var projectReference in model.ProjectReferences)
                {
                    var fullProjectPath = Path.GetFullPath(projectReference.ProjectPath, AppContext.BaseDirectory);
                    var relativePath = Path.GetRelativePath(outputDirectory, fullProjectPath);
                    builder.AppendLine($"    <ProjectReference Include=\"{relativePath.Replace("\\", "\\\\")}\" />");
                }
                builder.AppendLine("  </ItemGroup>");
                builder.AppendLine();
            }

            builder.AppendLine("</Project>");
            return builder.ToString();
        }

        private static string CreateDeploymentFile(CSharpPulumiProjectModel model)
        {
            var builder = new StringBuilder();
            builder.AppendLine("using System.Collections.Generic;");
            builder.AppendLine("using System.Threading;");
            builder.AppendLine("using System.Threading.Tasks;");
            foreach (var namespaceName in model.AdditionalNamespaces.OrderBy(x => x, StringComparer.Ordinal))
            {
                builder.AppendLine($"using {namespaceName};");
            }
            builder.AppendLine();
            builder.AppendLine($"namespace {model.RootNamespace};");
            builder.AppendLine();
            builder.AppendLine("public static class ExportedDeployment");
            builder.AppendLine("{");
            builder.AppendLine("    public static Task ProcessAsync(CancellationToken cancellationToken = default)");
            builder.AppendLine("    {");
            builder.AppendLine("        return Task.CompletedTask;");
            builder.AppendLine("    }");
            builder.AppendLine();
            builder.AppendLine("    public static IReadOnlyCollection<ExportedResourceDescriptor> DescribeResources() =>");
            builder.AppendLine("    [");
            foreach (var resource in model.Resources)
            {
                builder.AppendLine("        new ExportedResourceDescriptor(");
                builder.AppendLine($"            {ToCSharpString(resource.Key)},");
                builder.AppendLine($"            {ToCSharpString(resource.Kind)},");
                builder.AppendLine($"            {ToCSharpString(resource.Node)},");
                builder.AppendLine($"            {resource.ScopeId},");
                builder.AppendLine($"            typeof(global::{resource.ResourceType.FullName!.Replace('+', '.')}),");
                builder.Append("            [");
                builder.Append(string.Join(", ", resource.DependsOn.Select(ToCSharpString)));
                builder.AppendLine("]),");
            }
            builder.AppendLine("    ];");
            builder.AppendLine("}");
            builder.AppendLine();
            builder.AppendLine("public sealed record ExportedResourceDescriptor(");
            builder.AppendLine("    string Key,");
            builder.AppendLine("    string Kind,");
            builder.AppendLine("    string Node,");
            builder.AppendLine("    int ScopeId,");
            builder.AppendLine("    global::System.Type ResourceType,");
            builder.AppendLine("    IReadOnlyCollection<string> DependsOn);");

            return builder.ToString();
        }

        private static string ToCSharpString(string value)
        {
            return $"\"{value.Replace("\\", "\\\\").Replace("\"", "\\\"")}\"";
        }
    }

    public sealed record CSharpPulumiProjectExporterOptions(
        string? ProjectName = null,
        string RootNamespace = "Generated.Pulumi",
        IReadOnlyCollection<string>? AdditionalNamespaces = null,
        IReadOnlyCollection<CSharpPackageReference>? AdditionalPackageReferences = null)
    {
        public IReadOnlyCollection<string> AdditionalNamespaces { get; init; } = AdditionalNamespaces ?? [];
        public IReadOnlyCollection<CSharpPackageReference> AdditionalPackageReferences { get; init; } = AdditionalPackageReferences ?? [];
    }

    public sealed record CSharpPulumiProjectExport(
        string DirectoryPath,
        string ProjectFilePath,
        string DeploymentFilePath,
        CSharpPulumiProjectModel Model);

    public sealed record CSharpPulumiProjectModel(
        string Deployment,
        string ProjectName,
        string RootNamespace,
        IReadOnlyCollection<string> AdditionalNamespaces,
        IReadOnlyCollection<CSharpPackageReference> PackageReferences,
        IReadOnlyCollection<CSharpProjectReference> ProjectReferences,
        int CreatedCount,
        int ReferencedCount,
        IReadOnlyCollection<CSharpPulumiResourceModel> Resources);

    public sealed record CSharpPulumiResourceModel(
        string Key,
        string Kind,
        string Node,
        int ScopeId,
        Type ResourceType,
        IReadOnlyCollection<string> DependsOn);

    public sealed record CSharpPackageReference(string PackageId, string Version);

    public sealed record CSharpProjectReference(string ProjectPath);

    internal static class KnownPackageRegistry
    {
        public static IReadOnlyCollection<CSharpPackageReference> Resolve(Type type)
        {
            var fullName = type.FullName ?? type.Name;
            var packages = new List<CSharpPackageReference>();

            if (fullName.StartsWith("Pulumi.AzureNative.", StringComparison.Ordinal))
            {
                packages.Add(new CSharpPackageReference("Pulumi.AzureNative", "3.18.0"));
            }

            if (fullName.StartsWith("Pulumi.DockerBuild.", StringComparison.Ordinal))
            {
                packages.Add(new CSharpPackageReference("Pulumi.DockerBuild", "0.0.16"));
            }

            return packages;
        }

        public static IReadOnlyCollection<CSharpProjectReference> ResolveProjectReferences(Type type)
        {
            var assemblyName = type.Assembly.GetName().Name;
            return assemblyName switch
            {
                "LiveArch.Resources.Azure" => [new CSharpProjectReference("..\\..\\..\\..\\LiveArch.Resources.Azure\\LiveArch.Resources.Azure.csproj")],
                _ => []
            };
        }
    }
}
