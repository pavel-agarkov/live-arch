using LiveArch.Deployment.Azure.Converters;
using LiveArch.Deployment.Azure.Docker;
using LiveArch.Deployment.Azure.ResourceHierarchy;
using LiveArch.Deployment.Configuration;
using LiveArch.Deployment.Controls;
using LiveArch.Deployment.Converters;
using LiveArch.Deployment.Docker;
using LiveArch.Deployment.Export.CSharp;
using LiveArch.Deployment.ResourceHierarchy;
using LiveArch.Deployment.ResourceTypes;
using LiveArch.Deployment.Transformers;
using Microsoft.Extensions.DependencyInjection;
using Pulumi.AzureNative.AppConfiguration;
using Pulumi.AzureNative.Authorization;
using Pulumi.AzureNative.ManagedIdentity;
using Pulumi.AzureNative.Resources;
using Pulumi.AzureNative.Sql;
using Pulumi.AzureNative.Storage;
using Pulumi.AzureNative.Web;
using Pulumi.AzureNative.Web.Inputs;
using Pulumi.DockerBuild;
using Pulumi.Testing;
using Structurizr;
using ManagedServiceIdentityType = Pulumi.AzureNative.Web.ManagedServiceIdentityType;

namespace LiveArch.Deployment.TestRunner
{
    public class DeploymentTests
    {
        private readonly Mocks testMocks = new();
        private readonly IReadOnlyDictionary<string, object> variables = new Dictionary<string, object>()
        {
            { "ENV", "prod" },
            { "LOCATION", "westeurope" },
            { "KEY_VAULT_NAME", "main_prod_kv" },
            { "RESOURCE_GROUP_NAME", "main_prod_rg" },
            { "APP_CONFIG_NAME", "main_prod_app_config" },
            { "TENANT_ID", "pavel.agarkov" },
            { "VNET_NAME", "main_prod_vnet" },
            { "SQL_SERVER_REGISTRATION_NAME", "main_prod_sql_reg" },
            { "SQL_SERVER_NAME", "main_prod_sql_server" },
            { "SQL_ELASTIC_POOL_NAME", "main_prod_sql_elastic_pool" },
        };
        private readonly ResourceTypesRegistry resourceTypesRegistry;
        private readonly DockerImageReferenceConfigurator dockerImageConfig;
        private readonly ResourceHierarchyRegistry hierarchyRegistry;
        private readonly IConversionEngine conversionEngine;
        private readonly ITransformerRegistry transformerRegistry;

        public DeploymentTests()
        {
            resourceTypesRegistry = new ResourceTypesRegistry(new[]
            {
                new ResourceTypesAssemblyMarker(typeof(Image)),
                new ResourceTypesAssemblyMarker(typeof(ResourceGroup)),
                new ResourceTypesAssemblyMarker(typeof(ForEachLoop)),
                new ResourceTypesAssemblyMarker(typeof(LiveArch.Resources.Azure.ServiceBus.ReadableSubscription)),
            });
            dockerImageConfig = new DockerImageReferenceConfigurator([
                new AzureDockerImageReferenceConfigurator()
                ]);
            hierarchyRegistry = new ResourceHierarchyBuilder([new AzureResourceHierarchy()], resourceTypesRegistry).Registry;

            var services = new ServiceCollection();
            services.AddDefaultTransformers();
            services.AddDefaultValueConverters();
            services.AddAzureValueConverters();
            var serviceProvider = services.BuildServiceProvider();
            conversionEngine = serviceProvider.GetRequiredService<IConversionEngine>();
            transformerRegistry = serviceProvider.GetRequiredService<ITransformerRegistry>();
        }

        [Fact]
        public async Task ShouldCreateAllResourcesForOrderService()
        {
            var ws = await ProcessDeployment("order-env");
            var createdSummary = string.Join(", ", ws.CreatedResources.Values.GroupBy(resource => resource.GetType().Name).OrderBy(group => group.Key).Select(group => $"{group.Key}={group.Count()}"));
            var referencedSummary = string.Join(", ", ws.ReferencedResources.Values.GroupBy(resource => resource.GetType().Name).OrderBy(group => group.Key).Select(group => $"{group.Key}={group.Count()}"));

            ws.CreatedResources.Count.Should().Be(13, "created: {0}; referenced: {1}", createdSummary, referencedSummary);

            ws.ReferencedResources.Count.Should().Be(15);
        }

        [Fact]
        public async Task ShouldCreateAllResourcesForDeliveryService()
        {
            var ws = await ProcessDeployment("delivery-env");

            ws.CreatedResources.Count.Should().Be(8);

            ws.ReferencedResources.Count.Should().Be(11);
        }

        [Fact]
        public async Task ShouldCreateAllSharedResources()
        {
            var ws = await ProcessDeployment("shared-env");

            ws.CreatedResources.Count.Should().Be(4);

            ws.ReferencedResources.Count.Should().Be(0);
        }

        [Fact]
        public async Task ShouldGetAllSharedResourceReferences()
        {
            var ws = await ProcessDeployment("shared-ref-env");

            ws.CreatedResources.GroupBy(x => x.Key.Node).Count().Should().Be(0);

            ws.ReferencedResources.GroupBy(x => x.Key.Node).Count().Should().Be(4);
        }

        [Fact]
        public async Task ShouldCreateAllSandboxResources()
        {
            var ws = await ProcessDeployment("sandbox");

            ws.CreatedResources.Count.Should().Be(3);

            ws.ReferencedResources.Count.Should().Be(1);
        }

        [Theory]
        [InlineData("sa1", 1, 2)]
        [InlineData("sa1, sa2, sa3", 3, 2)]
        [InlineData("sa1,sa2,sa3,sa4", 4, 2)]
        [InlineData("", 0, 2)]
        public async Task ShouldCreateExpectedLoopScopesForConfiguredStorageAccounts(string storageAccounts, int expectedScopeCount, int expectedElementsPerScope)
        {
            testMocks.AddGetResourceMock<GetKeyValueArgs>(typeof(GetKeyValue), _ => new Dictionary<string, object>
            {
                ["value"] = storageAccounts,
            });

            var ws = await ProcessDeployment("order-env");
            var loop = ws.CreatedResources
                .Single(resource => resource.Value is ForEachLoop);
            var parentScope = FindScopeById(ws.RootScope, loop.Key.ScopeId);

            var loopScopes = parentScope.ChildScopes
                .Where(scope => ReferenceEquals(scope.OwnerResource, loop.Value))
                .ToList();

            loopScopes.Count.Should().Be(expectedScopeCount);
            loopScopes.Select(scope => scope.CreatedResources.Count + scope.ReferencedResources.Count)
                .Should().OnlyContain(elementCount => elementCount == expectedElementsPerScope);
        }

        [Theory]
        [InlineData("sa1", 1)]
        [InlineData("sa1, sa2, sa3", 3)]
        [InlineData("", 0)]
        public async Task ShouldRepeatIncomingRelationshipResourcesInsideLoopScopes(string storageAccounts, int expectedRoleAssignments)
        {
            testMocks.AddGetResourceMock<GetKeyValueArgs>(typeof(GetKeyValue), _ => new Dictionary<string, object>
            {
                ["value"] = storageAccounts,
            });

            var ws = await ProcessDeployment("order-env");

            ws.CreatedResources.Values.OfType<RoleAssignment>().Count().Should().Be(expectedRoleAssignments);
        }

        [Fact]
        public async Task ShouldNotifyObserverForCreatedAndReferencedResources()
        {
            var observer = new RecordingStructurizrDeploymentObserver();

            var ws = await ProcessDeployment("order-env", observer);

            observer.CreatedResources.Count.Should().Be(ws.CreatedResources.Count);
            observer.ReferencedResources.Count.Should().Be(ws.ReferencedResources.Count);
            observer.CreatedResources.Any(resource => resource.DependsOn.Count > 0).Should().BeTrue();
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

            var projectText = File.ReadAllText(export.ProjectFilePath);
            projectText.Should().Contain("Awesome.Custom.Package");
            projectText.Should().Contain("Pulumi.AzureNative");

            var deploymentText = File.ReadAllText(export.DeploymentFilePath);
            deploymentText.Should().Contain("Generated.Order.Pulumi");
            deploymentText.Should().Contain("ExportedResourceDescriptor");
            deploymentText.Should().Contain("Pulumi.AzureNative.ManagedIdentity.UserAssignedIdentity");
            deploymentText.Should().Contain("order-env");
        }

        private async Task<StructurizrDeploymentProcessor> ProcessDeployment(string deployment, IStructurizrDeploymentObserver? observer = null)
        {
            var ws = new StructurizrDeploymentProcessor(
                new TestDeploymentCommandOptions("prod", deployment, "workspace.json"),
                new TestDeploymentVariablesProvider(variables),
                new ResourceHierarchyBuilder([new AzureResourceHierarchy()], resourceTypesRegistry),
                resourceTypesRegistry,
                dockerImageConfig,
                conversionEngine,
                transformerRegistry,
                observer ?? new RecordingStructurizrDeploymentObserver());

            await Pulumi.Deployment.TestAsync(testMocks, new TestOptions { IsPreview = false }, async () =>
            {
                await ws.ProcessDeploymentAsync(default);
            });

            return ws;
        }

        private static IEnumerable<StructurizrDeploymentProcessor.ResourceScope> GetDescendantScopes(StructurizrDeploymentProcessor.ResourceScope scope)
        {
            foreach (var childScope in scope.ChildScopes)
            {
                yield return childScope;

                foreach (var nestedScope in GetDescendantScopes(childScope))
                {
                    yield return nestedScope;
                }
            }
        }

        private static StructurizrDeploymentProcessor.ResourceScope FindScopeById(StructurizrDeploymentProcessor.ResourceScope scope, int scopeId)
        {
            return scope.Id == scopeId
                ? scope
                : GetDescendantScopes(scope).Single(childScope => childScope.Id == scopeId);
        }

        private sealed class TestDeploymentCommandOptions(string environment, string deployment, string workspacePath) : IDeploymentCommandOptions
        {
            public string Environment { get; } = environment;
            public string Deployment { get; } = deployment;
            public string WorkspacePath { get; } = workspacePath;
        }

        private sealed class TestDeploymentVariablesProvider(IReadOnlyDictionary<string, object> values) : IDeploymentVariablesProvider
        {
            public IReadOnlyDictionary<string, object> GetVariables() => values;
        }

        private sealed record RegisteredResource(ModelItem Node, int ScopeId, object Resource, IReadOnlyCollection<Pulumi.Resource> DependsOn);

        private sealed class RecordingStructurizrDeploymentObserver : IStructurizrDeploymentObserver
        {
            public List<RegisteredResource> CreatedResources { get; } = [];
            public List<RegisteredResource> ReferencedResources { get; } = [];

            public void OnResourceCreated(ModelItem node, StructurizrDeploymentProcessor.ResourceScope scope, object resource, IReadOnlyCollection<Pulumi.Resource> dependsOn)
            {
                CreatedResources.Add(new RegisteredResource(node, scope.Id, resource, dependsOn));
            }

            public void OnResourceReferenced(ModelItem node, StructurizrDeploymentProcessor.ResourceScope scope, object resource, IReadOnlyCollection<Pulumi.Resource> dependsOn)
            {
                ReferencedResources.Add(new RegisteredResource(node, scope.Id, resource, dependsOn));
            }
        }

        public static async Task TestCases()
        {
            var app = new WebApp("demo-app", new WebAppArgs
            {
                //ResourceGroupName = rg.Name,
                //ServerFarmId = plan.Id,
                Kind = "app,linux",
                Identity = new ManagedServiceIdentityArgs
                {
                    Type = ManagedServiceIdentityType.UserAssigned,
                    //UserAssignedIdentities
                },
                SiteConfig = new SiteConfigArgs
                {
                    LinuxFxVersion = "DOCKER|demoacr.azurecr.io/demoapi:latest",
                    AppSettings =
                    {
                        new NameValuePairArgs { Name = "WEBSITES_PORT", Value = "8080" },
                        //new NameValuePairArgs { Name = "DOCKER_REGISTRY_SERVER_URL", Value = acr.LoginServer },
                        //new NameValuePairArgs { Name = "DOCKER_REGISTRY_SERVER_USERNAME", Value = username },
                        //new NameValuePairArgs { Name = "DOCKER_REGISTRY_SERVER_PASSWORD", Value = password },
                    },
                    ConnectionStrings = {
                        new ConnStringInfoArgs
                        {
                            Name = "my-db-conn-string",
                            ConnectionString = "Server=tcp:myserver.database.windows.net,1433;Initial Catalog=mydb;Persist Security Info=False;User ID=myuser;Password=mypassword;MultipleActiveResultSets=False;Encrypt=True;TrustServerCertificate=False;Connection Timeout=30;",
                            Type = ConnectionStringType.SQLAzure
                        }
                    },
                    Cors = new CorsSettingsArgs
                    {
                        //AllowedOrigins = 
                    }
                }
            });

            //new Pulumi.AzureNative.ServiceBus.GetTopic().Invoke()

            var topic = new Pulumi.AzureNative.ServiceBus.Topic("", new Pulumi.AzureNative.ServiceBus.TopicArgs
            {
                TopicName = ""
            });

            var subs = new Pulumi.AzureNative.ServiceBus.Subscription("", new Pulumi.AzureNative.ServiceBus.SubscriptionArgs
            {
                NamespaceName = "",
                TopicName = topic.Name
            });

            var ra = new RoleAssignment("", new RoleAssignmentArgs
            {
                PrincipalId = app.Identity.Apply(x => x.PrincipalId)
            });

            var db = new Database("", new Pulumi.AzureNative.Sql.DatabaseArgs
            {
                DatabaseName = "",
                ServerName = "",
                ResourceGroupName = ""
            });


            var sa = new Pulumi.AzureNative.Storage.StorageAccount("", new Pulumi.AzureNative.Storage.StorageAccountArgs
            {
                AccountName = "",
                ResourceGroupName = "",
                Sku = new Pulumi.AzureNative.Storage.Inputs.SkuArgs
                {
                    Name = Pulumi.AzureNative.Storage.SkuName.Standard_LRS
                },
                Kind = Pulumi.AzureNative.Storage.Kind.StorageV2,
                AllowBlobPublicAccess = false,
                AccessTier = AccessTier.Cool,
                MinimumTlsVersion = MinimumTlsVersion.TLS1_2
            });


            // 🔐 Managed Identity
            var identity = new UserAssignedIdentity("storage-identity",
                new UserAssignedIdentityArgs
                {
                    ResourceGroupName = "resourceGroup.Name",
                    Location = "resourceGroup.Location"
                });


            // 🔑 Назначаем роль identity на storage account
            var roleAssignment = new RoleAssignment("storage-access",
                new RoleAssignmentArgs
                {
                    PrincipalId = identity.PrincipalId,
                    PrincipalType = Pulumi.AzureNative.Authorization.PrincipalType.ServicePrincipal,
                    RoleDefinitionId = "ba92f5b4-2d11-453d-a403-e96b0029c9fe",
                    Scope = sa.Id
                });

            var sbNs = Pulumi.AzureNative.ServiceBus.GetNamespace.Invoke(new Pulumi.AzureNative.ServiceBus.GetNamespaceInvokeArgs
            {
                NamespaceName = "",
                ResourceGroupName = ""
            });
            var sbTopic = new Pulumi.AzureNative.ServiceBus.Topic("sb-topic", new Pulumi.AzureNative.ServiceBus.TopicArgs
            {
                TopicName = "",
                NamespaceName = sbNs.Apply(x => x.ServiceBusEndpoint),
                ResourceGroupName = ""
            });
            var sbSub = new Pulumi.AzureNative.ServiceBus.Subscription("sb-subscription", new Pulumi.AzureNative.ServiceBus.SubscriptionArgs
            {
                SubscriptionName = "",
                TopicName = sbTopic.Name,
                NamespaceName = sbNs.Apply(x => x.Name),
                ResourceGroupName = ""
            });

        }
    }
}
