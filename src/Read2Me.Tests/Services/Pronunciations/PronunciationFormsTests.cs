using Read2Me.Services.Pronunciations;
using Xunit;

namespace Read2Me.Tests.Services.Pronunciations;

public class PronunciationFormsTests
{
    [Theory]
    [InlineData("  Minas \n\t Tirith  ", "Minas Tirith")]
    [InlineData(null, "")]
    public void Normalise_TrimsAndCollapsesWhitespace(string? form, string normalised)
    {
        Assert.Equal(normalised, PronunciationForms.Normalise(form));
    }

    [Theory]
    [InlineData("Kulgan")]
    [InlineData("R2")]
    [InlineData("Dr. No")]
    [InlineData("Éowyn")]
    // A letter number is a word character to the matcher, so it may end a written form too.
    [InlineData("Henry Ⅷ")]
    public void AWrittenForm_StartingAndEndingWithALetterOrDigit_IsValid(string written)
    {
        Assert.Null(PronunciationForms.WrittenProblem(written));
    }

    [Theory]
    [InlineData("")]
    [InlineData("'Kulgan")]
    [InlineData("Kulgan!")]
    [InlineData("-")]
    public void AWrittenForm_WithAnotherEdge_IsAProblem(string written)
    {
        Assert.NotNull(PronunciationForms.WrittenProblem(written));
    }
}
