namespace Read2Me.Services.Text;

public interface ITextProcessingStepCatalog
{
    IEnumerable<TextProcessingStepDescriptor> GetAll(int paragraphTtsServiceConfigId);
}
