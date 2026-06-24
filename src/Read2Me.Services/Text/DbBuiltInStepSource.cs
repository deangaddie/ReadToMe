using Microsoft.EntityFrameworkCore;
using Read2Me.AppData;

namespace Read2Me.Services.Text;

public sealed class DbBuiltInStepSource(IDbContextFactory<Read2MeDbContext> dbFactory) : IBuiltInStepSource
{
    public ITextProcessingStep? Resolve(string stepId, int paragraphTtsServiceConfigId)
    {
        if (stepId != "to-sentence-case")
            return null;

        using var db = dbFactory.CreateDbContext();
        var row = db.ToSentenceCaseConfigs
            .FirstOrDefault(r => r.ParagraphTtsServiceConfigId == paragraphTtsServiceConfigId);

        return row is null
            ? null
            : new ToSentenceCaseStep(row.ParagraphEnabled, row.WordEnabled, row.WordMinLength);
    }
}
