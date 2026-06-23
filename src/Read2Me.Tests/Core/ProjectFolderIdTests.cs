using Read2Me.Core.Models;
using Xunit;

namespace Read2Me.Tests.Core
{
    public class ProjectFolderIdTests
    {
        [Fact]
        public void TwoIds_SameValue_AreEqual()
        {
            var a = new ProjectFolderId("my-book");
            var b = new ProjectFolderId("my-book");
            Assert.Equal(a, b);
        }

        [Fact]
        public void TwoIds_DifferentValues_AreNotEqual()
        {
            var a = new ProjectFolderId("book-a");
            var b = new ProjectFolderId("book-b");
            Assert.NotEqual(a, b);
        }

        [Fact]
        public void UsedAsDictionaryKey_FindsByValueEquality()
        {
            var dict = new Dictionary<ProjectFolderId, int>();
            var key = new ProjectFolderId("my-book");
            dict[key] = 42;

            var lookup = new ProjectFolderId("my-book");
            Assert.Equal(42, dict[lookup]);
        }

        [Fact]
        public void ImplicitCast_ToString_Works()
        {
            ProjectFolderId id = new("my-book");
            string s = id;
            Assert.Equal("my-book", s);
        }

        [Fact]
        public void ImplicitCast_FromString_Works()
        {
            ProjectFolderId id = "my-book";
            Assert.Equal("my-book", id.Value);
        }

        [Fact]
        public void ToString_ReturnsValue()
        {
            var id = new ProjectFolderId("my-book");
            Assert.Equal("my-book", id.ToString());
        }

        [Fact]
        public void EmptyString_IsValid()
        {
            var id = new ProjectFolderId("");
            Assert.Equal("", id.Value);
        }

        [Fact]
        public void StringInterpolation_UsesValue()
        {
            var id = new ProjectFolderId("my-book");
            Assert.Equal("folder: my-book", $"folder: {id}");
        }
    }
}
