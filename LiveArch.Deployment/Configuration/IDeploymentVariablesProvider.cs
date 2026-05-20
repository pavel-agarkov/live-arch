namespace LiveArch.Deployment.Configuration
{
    /// <summary>
    /// Supplies root variables used during DSL substitution and conversion.
    /// Implement this contract when another application wants to resolve variables from a different source.
    /// </summary>
    public interface IDeploymentVariablesProvider
    {
        /// <summary>
        /// Gets the variable set available to the active deployment run.
        /// </summary>
        /// <returns>
        /// A read-only dictionary of variable names to values.
        /// Values can be of any type, including complex objects,
        /// and will be used as the root context for variable substitution and value conversion.
        /// For example, entire Pulumi object can be passed for assignment to a resource property,
        /// or a simple string can be passed for substitution into another string.
        /// </returns>
        IReadOnlyDictionary<string, object> GetVariables();
    }
}
