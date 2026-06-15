using Read2Me.App.Shared;
using Read2Me.Data.Enums;
using Xunit;

namespace Read2Me.Tests.App.Dialogs;

public class AddProjectFormTests
{
    [Fact]
    public void IsValid_FalseWhenAllEmpty()
    {
        var form = new AddProjectForm();
        Assert.False(form.IsValid);
    }

    [Fact]
    public void IsValid_TrueWhenAllFieldsSet()
    {
        var form = new AddProjectForm
        {
            Title = "My Book",
            BookTitle = "My Book",
            Author = "Author",
        };
        form.SetFile("book.txt");

        Assert.True(form.IsValid);
    }

    [Theory]
    [InlineData("book.epub", BookFileType.Epub)]
    [InlineData("book.txt", BookFileType.Text)]
    [InlineData("BOOK.TXT", BookFileType.Text)]
    public void SetFile_DetectsKnownExtensions(string fileName, BookFileType expected)
    {
        var form = new AddProjectForm();
        form.SetFile(fileName);

        Assert.Equal(expected, form.DetectedType);
    }

    [Fact]
    public void SetFile_UnknownExtension_NullDetectedType()
    {
        var form = new AddProjectForm();
        form.SetFile("book.pdf");

        Assert.Null(form.DetectedType);
        Assert.False(form.IsValid);
    }

    [Fact]
    public void OnBookTitleBlur_CopiesBookTitleToTitleWhenTitleEmpty()
    {
        var form = new AddProjectForm { BookTitle = "Great Book" };
        form.OnBookTitleBlur();

        Assert.Equal("Great Book", form.Title);
    }

    [Fact]
    public void OnBookTitleBlur_DoesNotOverwriteExistingTitle()
    {
        var form = new AddProjectForm { BookTitle = "New Title", Title = "My Title" };
        form.OnBookTitleBlur();

        Assert.Equal("My Title", form.Title);
    }
}

