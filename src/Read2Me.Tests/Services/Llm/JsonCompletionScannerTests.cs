using Read2Me.Services.Llm;
using Xunit;

namespace Read2Me.Tests.Services.Llm
{
    public class JsonCompletionScannerTests
    {
        [Fact]
        public void Object_SingleChunk_Completes()
        {
            var s = JsonCompletionScanner.ForObject();
            Assert.True(s.Append("""{ "character": "Alice" }"""));
            Assert.True(s.Completed);
        }

        [Fact]
        public void Array_SingleChunk_Completes()
        {
            var s = JsonCompletionScanner.ForArray();
            Assert.True(s.Append("""[ { "index": 0 } ]"""));
        }

        [Fact]
        public void SplitAcrossChunks_CompletesOnClosingChunk()
        {
            var s = JsonCompletionScanner.ForArray();
            Assert.False(s.Append("[ { \"index\": 0, "));
            Assert.False(s.Append("\"character\": \"Ali"));
            Assert.True(s.Append("ce\" } ]"));
        }

        [Fact]
        public void ProseBeforeOpen_IsSkipped()
        {
            var s = JsonCompletionScanner.ForArray();
            Assert.False(s.Append("Here is the answer you ]wanted[:"));
            // The stray "[:" opened the array — a scanner only sees brackets. That is fine:
            // it opened at "[", so the next "]" closes it.
            Assert.True(s.Append("]"));
        }

        [Fact]
        public void BracketsInsideStrings_AreIgnored()
        {
            var s = JsonCompletionScanner.ForArray();
            Assert.False(s.Append("""[ { "character": "Bob ] the ] builder" }"""));
            Assert.True(s.Append("]"));
        }

        [Fact]
        public void EscapedQuoteInsideString_DoesNotEndString()
        {
            var s = JsonCompletionScanner.ForArray();
            Assert.False(s.Append("""[ { "character": "He said \"] done" }"""));
            Assert.True(s.Append("]"));
        }

        [Fact]
        public void NestedArrays_OnlyTopLevelCloseCompletes()
        {
            var s = JsonCompletionScanner.ForArray();
            Assert.False(s.Append("[ [1, 2], [3"));
            Assert.False(s.Append(", 4]"));
            Assert.True(s.Append(" ]"));
        }

        [Fact]
        public void ObjectScanner_IgnoresSquareBrackets()
        {
            var s = JsonCompletionScanner.ForObject();
            Assert.False(s.Append("""{ "values": [1, 2, 3]"""));
            Assert.True(s.Append("}"));
        }

        [Fact]
        public void AfterCompletion_AppendStaysTrue()
        {
            var s = JsonCompletionScanner.ForObject();
            Assert.True(s.Append("{}"));
            Assert.True(s.Append("more thinking text"));
            Assert.True(s.Completed);
        }

        [Fact]
        public void NoJson_NeverCompletes()
        {
            var s = JsonCompletionScanner.ForObject();
            Assert.False(s.Append("just prose, no json at all"));
            Assert.False(s.Completed);
        }
    }
}
