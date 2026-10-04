using System.Net;

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

    /// <summary>The provider answered with a non-success status.</summary>
    public LlmProviderException(string message, HttpStatusCode statusCode) : base(message)
    {
        StatusCode = statusCode;
    }

    /// <summary>The status the provider answered with; null when no response arrived.</summary>
    public HttpStatusCode? StatusCode { get; }

    /// <summary>
    /// A 4xx that blames the request (for llama, a prompt over the context size): the service is
    /// up and answering, so a restart cannot fix it and a retry of the same request fails the same
    /// way. 408 and 429 are left out: they say "slow or busy, try again".
    /// </summary>
    public bool IsClientError =>
        StatusCode is { } s
        && (int)s is >= 400 and < 500
        && s is not HttpStatusCode.RequestTimeout and not HttpStatusCode.TooManyRequests;
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
