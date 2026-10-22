using Read2Me.Services.History;
using Xunit;

namespace Read2Me.Tests.Services.History;

/// <summary>
/// A step's file list: each state file's path to the hash of its content, case-insensitive like
/// the file system, with a chunk's changes (<see cref="FileChanges"/>) applied to it and stored
/// as JSON beside a snapshot.
/// </summary>
public class FileListTests
{
    [Fact]
    public void Paths_AreCaseInsensitive()
    {
        var list = new FileList();
        list.Set("voices/Alice/Calm.wav", "aaa");

        Assert.Equal("aaa", list.HashOf("VOICES/alice/calm.WAV"));
        list.Set("voices/alice/calm.wav", "bbb");
        Assert.Equal(["voices/Alice/Calm.wav"], list.Paths);
        Assert.Equal("bbb", list.HashOf("voices/Alice/Calm.wav"));
    }

    [Fact]
    public void Apply_AddsReplacesAndRemoves()
    {
        var list = new FileList();
        list.Set("cover.jpg", "c1");
        list.Set("book.epub", "b1");
        list.Set("voices/a.wav", "a1");
        var changes = new FileChanges();
        changes.Set("COVER.jpg", "c2");
        changes.Set("Book.epub", null);
        changes.Set("voices/b.wav", "b2");

        list.Apply(changes);

        Assert.Equal("c2", list.HashOf("cover.jpg"));
        Assert.Null(list.HashOf("book.epub"));
        Assert.False(list.Contains("book.epub"));
        Assert.Equal("a1", list.HashOf("voices/a.wav"));
        Assert.Equal("b2", list.HashOf("voices/b.wav"));
        Assert.Equal(3, list.Paths.Count());
    }

    [Fact]
    public void Serialise_ReadsBackTheSameListAndStaysCaseInsensitive()
    {
        var list = new FileList();
        list.Set("cover.jpg", "c1");
        list.Set("voices/a.wav", "a1");

        var read = FileList.Read(list.Serialise());

        Assert.Equal(["cover.jpg", "voices/a.wav"], read.Paths.Order());
        Assert.Equal("c1", read.HashOf("COVER.JPG"));
        Assert.Equal("a1", read.HashOf("voices/A.wav"));
    }

    [Fact]
    public void Read_TheStoredJsonShape()
    {
        // The form snapshots and chunks already hold: a JSON object of path to hash.
        var read = FileList.Read("""{"cover.jpg":"c1","voices/a.wav":"a1"}""");

        Assert.Equal("c1", read.HashOf("cover.jpg"));
        Assert.Equal("a1", read.HashOf("voices/a.wav"));
    }

    [Fact]
    public void FileChanges_RoundTripGoneFilesAsNull()
    {
        var changes = new FileChanges();
        changes.Set("cover.jpg", "c2");
        changes.Set("book.epub", null);

        var read = FileChanges.Read(changes.Serialise());

        Assert.Equal(2, read.Count);
        var list = new FileList();
        list.Set("BOOK.epub", "b1");
        list.Apply(read);
        Assert.Equal(["cover.jpg"], list.Paths);
    }
}
