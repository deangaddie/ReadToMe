using System.Text.Json;
using Read2Me.Services.AiServers;
using Xunit;

namespace Read2Me.Tests.Services.AiServers
{
    /// <summary>
    /// <see cref="ModelListing.Parse"/> over both <c>GET /v1/models</c> variants: llama's
    /// <c>status.value</c> and audio.cpp's <c>loaded</c> flag. Pure, no HTTP.
    /// </summary>
    public class ModelListingTests
    {
        private static ModelListing Parse(string json)
        {
            using var doc = JsonDocument.Parse(json);
            return ModelListing.Parse(doc.RootElement);
        }

        [Theory]
        [InlineData("loaded", true)]
        [InlineData("unloaded", false)]
        [InlineData("loading", false)]
        public void Llama_status_value_says_whether_a_model_is_loaded(string value, bool loaded)
        {
            var listing = Parse($$$"""{"data":[{"id":"qwen-28b","status":{"value":"{{{value}}}"}}]}""");

            Assert.Equal([new ListedModel("qwen-28b", loaded)], listing.Models);
        }

        [Theory]
        [InlineData("""{"id":"breeze","loaded":true}""", true)]
        [InlineData("""{"id":"breeze","loaded":false}""", false)]
        [InlineData("""{"id":"breeze"}""", false)]
        public void AudioCpp_loaded_flag_says_whether_a_model_is_loaded(string item, bool loaded)
        {
            var listing = Parse($$$"""{"data":[{{{item}}}]}""");

            Assert.Equal([new ListedModel("breeze", loaded)], listing.Models);
        }

        [Fact]
        public void Models_keep_the_server_order()
        {
            var listing = Parse("""{"data":[{"id":"c"},{"id":"a","loaded":true},{"id":"b"}]}""");

            Assert.Equal(["c", "a", "b"], listing.Models.Select(m => m.Id));
        }

        [Fact]
        public void Find_answers_the_listed_model_or_null_when_it_is_not_listed()
        {
            var listing = Parse("""{"data":[{"id":"breeze","loaded":false}]}""");

            Assert.Equal(new ListedModel("breeze", false), listing.Find("breeze"));
            Assert.Null(listing.Find("voxcpm2"));
        }

        [Fact]
        public void IsLoaded_is_false_when_the_model_is_not_listed_or_not_loaded()
        {
            var listing = Parse(
                """{"data":[{"id":"qwen-28b","status":{"value":"loaded"}},{"id":"gemma-12b","status":{"value":"unloaded"}}]}""");

            Assert.True(listing.IsLoaded("qwen-28b"));
            Assert.False(listing.IsLoaded("gemma-12b"));
            Assert.False(listing.IsLoaded("absent"));
            Assert.False(listing.IsLoaded(null));
        }

        [Fact]
        public void LoadedModel_is_the_first_loaded_id()
        {
            var listing = Parse(
                """{"data":[{"id":"a","status":{"value":"unloaded"}},{"id":"b","status":{"value":"loaded"}},{"id":"c","loaded":true}]}""");

            Assert.Equal("b", listing.LoadedModel);
        }

        [Fact]
        public void LoadedModel_is_null_when_none_is_loaded()
        {
            var listing = Parse("""{"data":[{"id":"a","status":{"value":"loading"}},{"id":"b","loaded":false}]}""");

            Assert.Null(listing.LoadedModel);
        }

        [Theory]
        [InlineData("""{"data":[{"id":""},{"id":"  "},{"id":"kept"}]}""")]
        [InlineData("""{"data":[42,"text",null,[],{"id":"kept"}]}""")]
        [InlineData("""{"data":[{"name":"no id"},{"id":7},{"id":null},{"id":"kept"}]}""")]
        public void Blank_ids_and_malformed_items_are_skipped(string json)
        {
            var listing = Parse(json);

            Assert.Equal([new ListedModel("kept", false)], listing.Models);
        }

        [Theory]
        [InlineData("""{"object":"list"}""")]
        [InlineData("""{"data":{"id":"a"}}""")]
        [InlineData("""[{"id":"a"}]""")]
        [InlineData("\"text\"")]
        [InlineData("null")]
        public void A_missing_data_array_or_a_root_that_is_not_an_object_gives_an_empty_listing(string json)
        {
            var listing = Parse(json);

            Assert.Empty(listing.Models);
            Assert.Null(listing.LoadedModel);
        }
    }
}
