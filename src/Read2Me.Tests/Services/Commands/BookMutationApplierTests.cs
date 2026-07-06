using Microsoft.EntityFrameworkCore;
using Read2Me.Data.Entities;
using Read2Me.Services.Books;
using Read2Me.Services.Commands;
using Read2Me.Tests.Infrastructure;
using Xunit;

namespace Read2Me.Tests.Services.Commands
{
    public class BookMutationApplierTests : ProjectDbTestBase
    {
        [Fact]
        public async Task Add_lands_each_entity_in_its_own_table()
        {
            await using var db = await OpenDbAsync();

            var volume = new Volume { Id = Guid.NewGuid(), Order = "a" };
            var part = new Part { Id = Guid.NewGuid(), VolumeId = volume.Id, Order = "a" };
            var chapter = new Chapter { Id = Guid.NewGuid(), PartId = part.Id, Order = "a" };
            var paragraph = new Paragraph { Id = Guid.NewGuid(), ChapterId = chapter.Id, Order = "a" };
            var item = new ParagraphItem { Id = Guid.NewGuid(), ParagraphId = paragraph.Id, Order = "a" };

            var mutation = new HierarchyMutation(
                ToAdd: [volume, part, chapter, paragraph, item],
                ToDelete: [],
                ToUpdate: []);

            await BookMutationApplier.ApplyMutationAsync(db, mutation);

            await using var verify = await OpenDbAsync();
            Assert.True(await verify.Volumes.AnyAsync(v => v.Id == volume.Id));
            Assert.True(await verify.Parts.AnyAsync(p => p.Id == part.Id));
            Assert.True(await verify.Chapters.AnyAsync(c => c.Id == chapter.Id));
            Assert.True(await verify.Paragraphs.AnyAsync(p => p.Id == paragraph.Id));
            Assert.True(await verify.ParagraphItems.AnyAsync(i => i.Id == item.Id));
        }

        [Fact]
        public async Task Delete_removes_the_entity_from_its_table()
        {
            await using var db = await OpenDbAsync();
            var volume = new Volume { Id = Guid.NewGuid(), Order = "a" };
            db.Volumes.Add(volume);
            await db.SaveChangesAsync();

            var mutation = new HierarchyMutation(
                ToAdd: [],
                ToDelete: [volume],
                ToUpdate: []);

            await BookMutationApplier.ApplyMutationAsync(db, mutation);

            await using var verify = await OpenDbAsync();
            Assert.False(await verify.Volumes.AnyAsync(v => v.Id == volume.Id));
        }

        [Fact]
        public async Task Update_persists_changes_via_modified_state()
        {
            await using var seed = await OpenDbAsync();
            var volume = new Volume { Id = Guid.NewGuid(), Title = "before", Order = "a" };
            seed.Volumes.Add(volume);
            await seed.SaveChangesAsync();

            await using var db = await OpenDbAsync();
            var tracked = await db.Volumes.SingleAsync(v => v.Id == volume.Id);
            tracked.Title = "after";

            var mutation = new HierarchyMutation(
                ToAdd: [],
                ToDelete: [],
                ToUpdate: [tracked]);

            await BookMutationApplier.ApplyMutationAsync(db, mutation);

            await using var verify = await OpenDbAsync();
            var reloaded = await verify.Volumes.SingleAsync(v => v.Id == volume.Id);
            Assert.Equal("after", reloaded.Title);
        }

        [Fact]
        public async Task Add_of_an_unhandled_entity_type_throws()
        {
            await using var db = await OpenDbAsync();
            var mutation = new HierarchyMutation(
                ToAdd: [new Rogue()],
                ToDelete: [],
                ToUpdate: []);

            await Assert.ThrowsAsync<NotSupportedException>(
                () => BookMutationApplier.ApplyMutationAsync(db, mutation));
        }

        [Fact]
        public async Task Delete_of_an_unhandled_entity_type_throws()
        {
            await using var db = await OpenDbAsync();
            var mutation = new HierarchyMutation(
                ToAdd: [],
                ToDelete: [new Rogue()],
                ToUpdate: []);

            await Assert.ThrowsAsync<NotSupportedException>(
                () => BookMutationApplier.ApplyMutationAsync(db, mutation));
        }

        /// <summary>An IBookEntity the applier doesn't handle — proves the default arm throws.</summary>
        private sealed record Rogue : IBookEntity;
    }
}
