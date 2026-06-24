using Microsoft.EntityFrameworkCore;
using Read2Me.AppData;

namespace Read2Me.Services.Text;

public sealed class TextSubstitutionStepImpl(string from, string to) : ITextProcessingStep
{
    public string Process(string text) => text.Replace(from, to, StringComparison.Ordinal);
}

public sealed class DbTextSubstitutionStepSource(IDbContextFactory<Read2MeDbContext> dbFactory) : ITextSubstitutionStepSource
{
    public ITextProcessingStep? Resolve(string stepId)
    {
        using var db = dbFactory.CreateDbContext();
        var row = db.TextSubstitutionSteps.FirstOrDefault(s => s.Id == stepId);
        return row is null ? null : new TextSubstitutionStepImpl(row.FromText, row.ToText);
    }
}
