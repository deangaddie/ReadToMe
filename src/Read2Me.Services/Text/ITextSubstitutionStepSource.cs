namespace Read2Me.Services.Text;

public interface ITextSubstitutionStepSource
{
    ITextProcessingStep? Resolve(string stepId);
}
