using FractionalIndexing;
using Read2Me.Data.Entities;
using Read2Me.Services.Books;
using Read2Me.Services.Mutations;
using Xunit;

namespace Read2Me.Tests.Services.Books
{
    /// <summary>
    /// PlanDeleteVolume — a book always keeps one Volume, so there is always a node to insert from.
    /// Deleting one of several removes it (its subtree goes by cascade); the last one is refused.
    /// </summary>
    public class BookHierarchyDeleteVolumeTests
    {
        private static string Key(string? after = null) => OrderKeyGenerator.GenerateKeyBetween(after, null);

        private static (BookHierarchy H, Volume First, Volume Second) MakeTwoVolumes()
        {
            var first = new Volume { Id = Guid.NewGuid(), Order = Key(), Title = "One" };
            var second = new Volume { Id = Guid.NewGuid(), Order = Key(first.Order), Title = "Two" };
            var part = new Part { Id = Guid.NewGuid(), VolumeId = first.Id, Order = Key() };
            return (new BookHierarchy { Volumes = [first, second], Parts = { [first.Id] = [part] } }, first, second);
        }

        [Fact]
        public void OneOfSeveral_IsPlannedAsItsOwnDeletion_CreatingNothing()
        {
            var (h, first, _) = MakeTwoVolumes();

            var planned = Assert.IsType<HierarchyPlan.Planned>(h.PlanDeleteVolume(first.Id));

            Assert.Same(first, Assert.Single(planned.Mutation.ToDelete));
            Assert.Empty(planned.Mutation.ToAdd);
            Assert.Empty(planned.Mutation.ToUpdate);
            Assert.Null(planned.Created);
        }

        [Fact]
        public void TheLastVolume_IsRefusedAsValidation()
        {
            var (h, first, second) = MakeTwoVolumes();
            h.Volumes.Remove(second);

            var refused = Assert.IsType<HierarchyPlan.Refused>(h.PlanDeleteVolume(first.Id));

            Assert.Equal(BookMutationRejection.Validation, refused.Rejection);
            Assert.Equal("A book must keep at least one volume.", refused.Reason);
        }

        [Fact]
        public void AnUnknownVolume_IsRefusedAsNotFound()
        {
            var (h, _, _) = MakeTwoVolumes();

            var refused = Assert.IsType<HierarchyPlan.Refused>(h.PlanDeleteVolume(Guid.NewGuid()));

            Assert.Equal(BookMutationRejection.NotFound, refused.Rejection);
        }
    }
}
