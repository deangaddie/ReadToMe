using Read2Me.Services.Pronunciations;
using Xunit;

namespace Read2Me.Tests.Services.Pronunciations;

public class PronunciationFormsTests
{
    [Theory]
    [InlineData("  Van \n\t Helsing  ", "Van Helsing")]
    [InlineData(null, "")]
    public void Normalise_TrimsAndCollapsesWhitespace(string? form, string normalised)
    {
        Assert.Equal(normalised, PronunciationForms.Normalise(form));
    }

    [Theory]
    [InlineData("Watson")]
    [InlineData("R2")]
    [InlineData("Dr. Seward")]
    [InlineData("Émile")]
    // A letter number is a word character to the matcher, so it may end a written form too.
    [InlineData("Henry Ⅷ")]
    public void AWrittenForm_StartingAndEndingWithALetterOrDigit_IsValid(string written)
    {
        Assert.Null(PronunciationForms.WrittenProblem(written));
    }

    [Theory]
    [InlineData("")]
    [InlineData("'Watson")]
    [InlineData("Watson!")]
    [InlineData("-")]
    public void AWrittenForm_WithAnotherEdge_IsAProblem(string written)
    {
        Assert.NotNull(PronunciationForms.WrittenProblem(written));
    }
}
