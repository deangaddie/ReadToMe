namespace Read2Me.Core.Exceptions;

public class Read2MeException : Exception
{
    public Read2MeException(string message) : base(message) { }
    public Read2MeException(string message, Exception innerException) : base(message, innerException) { }
}

public class ProjectAlreadyExistsException : Read2MeException
{
    public string ProjectName { get; }
    public ProjectAlreadyExistsException(string projectName) 
        : base($"A project named \"{projectName}\" already exists.")
    {
        ProjectName = projectName;
    }
}

public class ProjectNotFoundException : Read2MeException
{
    public string ProjectId { get; }
    public ProjectNotFoundException(string projectId) 
        : base($"Project \"{projectId}\" not found.")
    {
        ProjectId = projectId;
    }
}

public class DatabaseInconsistentException : Read2MeException
{
    public DatabaseInconsistentException(string message) : base(message) { }
}

public class LlmProviderException : Read2MeException
{
    public LlmProviderException(string message, Exception innerException) : base(message, innerException) { }
}

/// <summary>
/// Thrown when a switchable llama endpoint stays responsive but the target model has not finished
/// loading within the budget. Distinct from <see cref="LlmProviderException"/> so callers wait/retry
/// (the model is still loading) rather than treat the endpoint as dead and escalate to another config.
/// </summary>
public class ModelStillLoadingException : Read2MeException
{
    public string BaseUrl { get; }
    public string Model { get; }
    public TimeSpan Elapsed { get; }
    public TimeSpan Budget { get; }

    public ModelStillLoadingException(string baseUrl, string model, TimeSpan elapsed, TimeSpan budget)
        : base($"Model \"{model}\" is still loading on {baseUrl} after {elapsed.TotalSeconds:0}s (budget {budget.TotalSeconds:0}s).")
    {
        BaseUrl = baseUrl;
        Model = model;
        Elapsed = elapsed;
        Budget = budget;
    }
}
