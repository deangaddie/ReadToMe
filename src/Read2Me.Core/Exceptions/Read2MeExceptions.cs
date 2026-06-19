using System;

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
