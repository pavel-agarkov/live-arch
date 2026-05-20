namespace LiveArch.Deployment.Configuration
{
    /// <summary>
    /// Supplies the deployment coordinates required by the processor.
    /// Implement this contract when another application wants to provide deployment selection differently.
    /// </summary>
    public interface IDeploymentCommandOptions
    {
        /// <summary>
        /// Gets the environment name used to filter active deployment nodes.
        /// </summary>
        public string Environment { get; }

        /// <summary>
        /// Gets the Structurizr deployment view key to execute.
        /// </summary>
        public string Deployment { get; }

        /// <summary>
        /// Gets the path to the Structurizr workspace JSON file.
        /// </summary>
        public string WorkspacePath { get; }
    }
}
