namespace Read2Me.Services.Text;

public interface IBuiltInStepSource
{
    ITextProcessingStep? Resolve(string stepId, int paragraphTtsServiceConfigId);
}
