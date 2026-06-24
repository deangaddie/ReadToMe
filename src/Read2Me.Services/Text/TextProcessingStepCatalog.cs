using Microsoft.EntityFrameworkCore;
using Read2Me.AppData;

namespace Read2Me.Services.Text;

public class TextProcessingStepCatalog(
    IEnumerable<TextProcessingStepDescriptor> builtIns,
    IDbContextFactory<Read2MeDbContext> dbFactory) : ITextProcessingStepCatalog
{
    public IEnumerable<TextProcessingStepDescriptor> GetAll(int paragraphTtsServiceConfigId)
    {
        foreach (var d in builtIns)
            yield return d;

        if (paragraphTtsServiceConfigId == 0)
            yield break;

        using var db = dbFactory.CreateDbContext();
        var steps = db.TextSubstitutionSteps
            .Where(s => s.ParagraphTtsServiceConfigId == paragraphTtsServiceConfigId)
            .OrderBy(s => s.Order)
            .ToList();

        foreach (var s in steps)
            yield return new TextProcessingStepDescriptor(
                s.Id,
                $"Replace \"{s.FromText}\" → \"{s.ToText}\"",
                $"Substitution: replace \"{s.FromText}\" with \"{s.ToText}\"");
    }
}
