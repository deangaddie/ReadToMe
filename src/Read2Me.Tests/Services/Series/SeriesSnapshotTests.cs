using Read2Me.Data.Enums;
using Read2Me.Services.Mutations;
using Read2Me.Services.Series;
using Xunit;
using Link = Read2Me.Services.Series.SeriesLinkRules.Link;

namespace Read2Me.Tests.Services.Series
{
    /// <summary>
    /// The Series snapshot's registry layer (#234, #244), built by hand: no host, no disk, no lock.
    /// "Sherlock Holmes" holds "A Study in Scarlet" (Book 1), "The Sign of the Four" (Book 2) and
    /// "The Hound of the Baskervilles" (Book 3). Dr. Watson is a Series character linked in the first
    /// two; the Hound's own Dr. Watson is unlinked, so it is a suggested cell; Holmes is unlinked in
    /// the first two, so the two of them are a suggested row.
    /// </summary>
    public class SeriesSnapshotTests
    {
        private static readonly Guid SeriesId = Guid.NewGuid();
        private static readonly Guid Watson = Guid.NewGuid(); // the Series character

        private static readonly Guid Study = Guid.NewGuid();
        private static readonly Guid Sign = Guid.NewGuid();
        private static readonly Guid Hound = Guid.NewGuid();
        private static readonly Guid Gone = Guid.NewGuid(); // a member whose project was deleted outside the app

        private static readonly Guid WatsonInStudy = Guid.NewGuid();
        private static readonly Guid WatsonInSign = Guid.NewGuid();
        private static readonly Guid WatsonInHound = Guid.NewGuid();
        private static readonly Guid HolmesInStudy = Guid.NewGuid();
        private static readonly Guid HolmesInSign = Guid.NewGuid();
        private static readonly Guid LestradeInSign = Guid.NewGuid();
        private static readonly Guid DeletedInSign = Guid.NewGuid(); // a Character deleted outside the app

        private const string StudyFolder = "a-study-in-scarlet";
        private const string SignFolder = "the-sign-of-the-four";
        private const string HoundFolder = "the-hound-of-the-baskervilles";

        private static readonly SeriesProjectIndex Index = new(
        [
            new(StudyFolder, Study, "A Study in Scarlet"),
            new(SignFolder, Sign, "The Sign of the Four"),
            new(HoundFolder, Hound, "The Hound of the Baskervilles"),
        ], complete: true);

        private static readonly Dictionary<Guid, decimal> Members = new() { [Hound] = 3m, [Study] = 1m, [Sign] = 2.0m };

        private static readonly Link[] Links = [new(Watson, Study, WatsonInStudy), new(Watson, Sign, WatsonInSign)];

        private static Dictionary<Guid, SeriesBookCast> Casts() => new()
        {
            [Study] = Cast(("Dr. Watson", WatsonInStudy), ("Sherlock Holmes", HolmesInStudy)),
            [Sign] = Cast(("Dr. Watson", WatsonInSign), ("Sherlock Holmes", HolmesInSign), ("Inspector Lestrade", LestradeInSign)),
            [Hound] = Cast(("Dr. Watson", WatsonInHound)),
        };

        // ── the series ──────────────────────────────────────────────────────────

        [Fact]
        public void The_view_lists_member_books_in_position_order_with_normalized_positions()
        {
            var view = Snapshot().View();

            Assert.Equal("Sherlock Holmes", view.Name);
            Assert.Equal(
                [(StudyFolder, "A Study in Scarlet", 1m), (SignFolder, "The Sign of the Four", 2m), (HoundFolder, "The Hound of the Baskervilles", 3m)],
                view.Books.Select(b => (b.Folder, b.Title, b.Position)));
            Assert.Equal("2", view.Books[1].Position.ToString(System.Globalization.CultureInfo.InvariantCulture));
        }

        [Fact]
        public void The_poster_is_the_uploaded_one_else_the_lowest_positioned_books_cover_else_none()
        {
            var uploaded = new SeriesPoster(SeriesPosterSource.Uploaded, "poster.png", 7, null);
            var cover = new SeriesPoster(SeriesPosterSource.Cover, "cover.jpg", 9, null);
            SeriesProjectIndex WithCover(Guid project) => new(
                [.. new[] { Study, Sign, Hound }.Select(id => Index.ById(id)! with { Cover = id == project ? cover : null })], complete: true);

            Assert.Equal(uploaded, new SeriesSnapshot(Record() with { UploadedPoster = uploaded }, WithCover(Study)).View().Poster);
            Assert.Equal(cover with { CoverPosition = 1m }, new SeriesSnapshot(Record(), WithCover(Study)).View().Poster);
            // Only the lowest-positioned book's cover counts, not the first book with one.
            Assert.Equal(SeriesPoster.None, new SeriesSnapshot(Record(), WithCover(Sign)).View().Poster);
        }

        [Fact]
        public void The_workspace_lists_series_by_name_and_maps_each_member_folder_to_its_series()
        {
            var other = new SeriesRecord(Guid.NewGuid(), "Father Brown", new Dictionary<Guid, decimal>(), [], []);
            var workspace = new SeriesWorkspaceSnapshot([Snapshot(), new SeriesSnapshot(other, Index)], unreadable: 0);

            Assert.Equal(["Father Brown", "Sherlock Holmes"], workspace.List().Select(s => s.Name));
            var memberships = workspace.Memberships();
            Assert.Equal([StudyFolder, HoundFolder, SignFolder], memberships.Keys.Order(StringComparer.Ordinal));
            Assert.Equal(new ProjectSeries(SeriesId, "Sherlock Holmes", 2m), memberships[SignFolder.ToUpperInvariant()]);
            Assert.Equal(new ProjectSeries(SeriesId, "Sherlock Holmes", 3m), workspace.MembershipOf(Hound));
            Assert.Null(workspace.MembershipOf(Guid.NewGuid()));
        }

        [Fact]
        public void One_projects_membership_needs_only_that_project_in_the_index()
        {
            var onlyHound = new SeriesProjectIndex([Index.ById(Hound)!], complete: false);
            var workspace = new SeriesWorkspaceSnapshot([new SeriesSnapshot(Record(), onlyHound)], unreadable: 0);

            Assert.Equal(new ProjectSeries(SeriesId, "Sherlock Holmes", 3m), workspace.MembershipOf(Hound));
            // The others are unindexed, not gone: still members.
            Assert.Equal(new ProjectSeries(SeriesId, "Sherlock Holmes", 1m), workspace.MembershipOf(Study));
        }

        // ── peers and the Cast page's links ─────────────────────────────────────

        [Fact]
        public void Peers_are_the_other_books_linked_to_the_Series_character_in_position_order()
        {
            var peers = Snapshot(Links.Append(new Link(Watson, Hound, WatsonInHound)).ToArray()).Peers(Hound, WatsonInHound)!;

            Assert.Equal(Watson, peers.SeriesCharacterId);
            Assert.Equal(new SeriesPeer(HoundFolder, "The Hound of the Baskervilles", 3m, WatsonInHound), peers.Self);
            Assert.Equal([(StudyFolder, WatsonInStudy), (SignFolder, WatsonInSign)], peers.Others.Select(p => (p.Folder, p.CharacterId)));
        }

        [Fact]
        public void An_unlinked_Character_or_a_book_in_no_series_has_no_peers()
        {
            Assert.Null(Snapshot().Peers(Hound, WatsonInHound));
            Assert.Null(Snapshot().Peers(Guid.NewGuid(), WatsonInStudy));
        }

        [Fact]
        public void LinksOf_gives_each_linked_Character_its_series_book_count_and_whether_it_differs()
        {
            // A tie goes to the lowest position, so the Study holds the Series copy and the Sign differs.
            var snapshot = Voiced(Snapshot(casts: Casts()), Held(StudyFolder, "v1"), Held(SignFolder, "v2"));

            var sign = snapshot.LinksOf(Sign);
            var study = snapshot.LinksOf(Study);

            Assert.Equal(new CharacterSeriesLink(SeriesId, Watson, "Sherlock Holmes", 2, Differs: true), sign[WatsonInSign]);
            Assert.False(study[WatsonInStudy].Differs);
            Assert.Empty(snapshot.LinksOf(Hound));
        }

        // ── the cast board and Link suggestions ─────────────────────────────────

        [Fact]
        public void The_cast_board_has_a_row_per_Series_character_with_a_cell_per_book()
        {
            var cast = Voiced(Snapshot(casts: Casts()), Held(StudyFolder, "v1"), Held(SignFolder, "v2")).Cast();

            Assert.Equal([StudyFolder, SignFolder, HoundFolder], cast.Books.Select(b => b.Folder));
            var row = Assert.Single(cast.Rows);
            Assert.Equal((Watson, "Dr. Watson", false), (row.SeriesCharacterId, row.Label, row.IsNarrator));
            Assert.Equal(
                [(StudyFolder, WatsonInStudy, false), (SignFolder, WatsonInSign, true), (HoundFolder, (Guid?)null, false)],
                row.Cells.Select(c => (c.Folder, c.Linked?.CharacterId, c.Differs)));
            Assert.Equal(["Sherlock Holmes"], cast.Unlinked[StudyFolder].Select(c => c.Name));
            Assert.Equal(["Sherlock Holmes", "Inspector Lestrade"], cast.Unlinked[SignFolder].Select(c => c.Name));
            Assert.Equal(["Dr. Watson"], cast.Unlinked[HoundFolder].Select(c => c.Name));
        }

        [Fact]
        public void Link_suggestions_offer_the_unlinked_namesake_as_a_cell_and_the_unlinked_pair_as_a_row()
        {
            var cast = Snapshot(casts: Casts()).Cast();

            Assert.Equal(WatsonInHound, cast.Rows[0].Cells[2].Suggested?.CharacterId);
            var suggested = Assert.Single(cast.SuggestedRows);
            Assert.Equal("Sherlock Holmes", suggested.Label);
            Assert.Equal([(StudyFolder, HolmesInStudy), (SignFolder, HolmesInSign)], suggested.Members.Select(m => (m.Folder, m.CharacterId)));
            Assert.Equal(2, cast.SuggestionCount);
        }

        [Fact]
        public void Not_the_same_hides_the_suggestion_it_names()
        {
            var notTheSame = new SeriesNotTheSame[]
            {
                new(Guid.NewGuid(), Hound, WatsonInHound, SeriesCharacterId: Watson),
                new(Guid.NewGuid(), Study, HolmesInStudy, OtherProjectId: Sign, OtherCharacterId: HolmesInSign),
            };

            var cast = Snapshot(notTheSame: notTheSame, casts: Casts()).Cast();

            Assert.Null(cast.Rows[0].Cells[2].Suggested);
            Assert.Empty(cast.SuggestedRows);
            Assert.Equal(0, cast.SuggestionCount);
        }

        [Fact]
        public void A_book_whose_narrator_link_could_not_be_read_takes_no_suggestion()
        {
            var casts = Casts();
            casts[Hound] = casts[Hound] with { NarratorLinked = null };

            var cast = Snapshot(casts: casts).Cast();

            Assert.Null(cast.Rows[0].Cells[2].Suggested);
            Assert.Equal(["Dr. Watson"], cast.Unlinked[HoundFolder].Select(c => c.Name));
        }

        // ── link, resolve and share-and-link checks and previews ────────────────

        [Fact]
        public void A_link_is_checked_as_the_write_would_check_it()
        {
            var snapshot = Snapshot(casts: Casts());

            Assert.Equal(HoundFolder, snapshot.CheckLink(Watson, HoundFolder, WatsonInHound).Value?.Folder);
            Assert.Equal(SeriesRefusal.UnknownSeriesCharacter, snapshot.CheckLink(Guid.NewGuid(), HoundFolder, WatsonInHound).Refusal);
            Assert.Equal(SeriesRefusal.UnknownCharacter("The Hound of the Baskervilles"), snapshot.CheckLink(Watson, HoundFolder, Guid.NewGuid()).Refusal);
            Assert.Equal(SeriesRefusal.UnknownFolder("the-valley-of-fear"), snapshot.CheckLink(Watson, "the-valley-of-fear", WatsonInHound).Refusal);
            Assert.Equal(
                SeriesRefusal.BookAlreadyLinked("The Sign of the Four", "Dr. Watson", "Dr. Watson"),
                snapshot.CheckLink(Watson, SignFolder, LestradeInSign).Refusal);
            Assert.Equal(
                SeriesRefusal.CharacterAlreadyLinked("Dr. Watson", "A Study in Scarlet", "Dr. Watson"),
                snapshot.CheckLink(Watson, StudyFolder, WatsonInStudy).Refusal);
        }

        [Fact]
        public void The_link_preview_names_what_the_joining_book_gains_and_what_the_linked_books_gain()
        {
            var armyDoctor = Voice("Army doctor");
            var narrator = Voice("Narrating");
            var houndOwn = Voice("Moor walk");
            var merge = SeriesVoiceMerge.Make(
                [Source(HoundFolder, WatsonInHound, houndOwn)],
                [
                    new SeriesVoiceMerge.BookVoices<SeriesVoiceSource>(StudyFolder, [Source(StudyFolder, WatsonInStudy, armyDoctor), Source(StudyFolder, WatsonInStudy, narrator)]),
                    new SeriesVoiceMerge.BookVoices<SeriesVoiceSource>(SignFolder, [Source(SignFolder, WatsonInSign, armyDoctor)]),
                ],
                v => v.Voice.Id);

            var preview = SeriesSnapshot.LinkPreview(merge);

            Assert.Equal(["Army doctor", "Narrating"], preview.BookGains);
            Assert.Equal(["Moor walk"], preview.SeriesGains);
        }

        [Fact]
        public void A_push_replaces_every_other_linked_book_and_a_take_copies_the_series_copy_in()
        {
            var snapshot = Voiced(Snapshot(casts: Casts()), Held(StudyFolder, "v1"), Held(SignFolder, "v2"));

            var push = snapshot.PlanResolve(Watson, SignFolder, push: true).Value!;
            var take = snapshot.PlanResolve(Watson, SignFolder, push: false).Value!;

            Assert.Equal((SignFolder, SeriesRegistry.PushLabel), (push.Source.Folder, push.Step));
            Assert.Equal([StudyFolder], push.Targets.Select(t => t.Folder));
            Assert.Equal((StudyFolder, SeriesRegistry.TakeLabel), (take.Source.Folder, take.Step));
            Assert.Equal([SignFolder], take.Targets.Select(t => t.Folder));
            Assert.Equal(SeriesRefusal.DoesNotDiffer("A Study in Scarlet", "Dr. Watson"), snapshot.PlanResolve(Watson, StudyFolder, push: true).Refusal);
            Assert.Equal(SeriesRefusal.NotLinked("The Hound of the Baskervilles", "Dr. Watson"), snapshot.PlanResolve(Watson, HoundFolder, push: true).Refusal);
        }

        [Fact]
        public void A_suggested_rows_members_are_checked_and_put_in_position_order()
        {
            var snapshot = Snapshot(casts: Casts());

            var picked = snapshot.UnlinkedMembers([new(SignFolder, HolmesInSign), new(StudyFolder, HolmesInStudy)]).Value!;

            Assert.Equal([HolmesInStudy, HolmesInSign], picked.Select(p => p.Character.CharacterId));
            Assert.Equal(SeriesRefusal.TooFewMembers, snapshot.UnlinkedMembers([new(StudyFolder, HolmesInStudy)]).Refusal);
            Assert.Equal(
                SeriesRefusal.BookTwice("A Study in Scarlet"),
                snapshot.UnlinkedMembers([new(StudyFolder, HolmesInStudy), new(StudyFolder, HolmesInStudy)]).Refusal);
            Assert.Equal(
                SeriesRefusal.CharacterAlreadyLinked("Dr. Watson", "A Study in Scarlet", "Dr. Watson"),
                snapshot.UnlinkedMembers([new(StudyFolder, WatsonInStudy), new(SignFolder, HolmesInSign)]).Refusal);
        }

        [Fact]
        public void The_share_and_link_preview_names_what_each_book_gains()
        {
            var snapshot = Snapshot(casts: Casts());
            var deduction = Voice("Deduction");
            var violin = Voice("Violin");

            var gains = SeriesSnapshot.ShareAndLinkPreview(
            [
                (snapshot.MemberOf(StudyFolder)!, [deduction]),
                (snapshot.MemberOf(SignFolder)!, [deduction, violin]),
            ]);

            Assert.Equal([StudyFolder, SignFolder], gains.Select(g => g.Folder));
            Assert.Equal(["Violin"], gains[0].Gains);
            Assert.Empty(gains[1].Gains);
        }

        // ── stale rows: filtered out of every view, never deleted here ──────────

        [Fact]
        public void A_member_whose_project_is_gone_is_left_out_of_every_view_with_its_links()
        {
            var members = new Dictionary<Guid, decimal>(Members) { [Gone] = 0.5m };
            var links = Links.Append(new Link(Watson, Gone, Guid.NewGuid())).ToArray();
            var snapshot = Snapshot(links, members: members, casts: Casts());
            var workspace = new SeriesWorkspaceSnapshot([snapshot], unreadable: 0);

            Assert.Equal([Gone], snapshot.StaleMembers);
            Assert.DoesNotContain(snapshot.Books, b => b.ProjectId == Gone);
            Assert.Equal(3, Assert.Single(workspace.List()).Books.Count);
            Assert.Null(workspace.MembershipOf(Gone));
            Assert.Equal([StudyFolder, SignFolder, HoundFolder], snapshot.Cast().Books.Select(b => b.Folder));
            Assert.Equal([SignFolder], snapshot.Peers(Study, WatsonInStudy)!.Others.Select(p => p.Folder));
            Assert.Equal(2, snapshot.LinksOf(Study)[WatsonInStudy].BookCount);
            Assert.Single(snapshot.StaleLinks);
        }

        [Fact]
        public void A_member_missing_from_an_incomplete_index_is_unreadable_not_gone()
        {
            var index = new SeriesProjectIndex([Index.ById(Study)!, Index.ById(Hound)!], complete: false);

            var snapshot = new SeriesSnapshot(Record(), index, Casts());

            Assert.Empty(snapshot.StaleMembers);
            Assert.Equal([StudyFolder, HoundFolder], snapshot.Books.Select(b => b.Folder));
            // Its link is kept for a later read.
            Assert.Contains(snapshot.Links, l => l.ProjectId == Sign);
            Assert.Empty(snapshot.StaleLinks);
        }

        [Fact]
        public void A_link_whose_Character_is_gone_is_left_out_of_every_view_and_its_Series_character_with_it()
        {
            var gone = Guid.NewGuid();
            var links = Links.Append(new Link(gone, Sign, DeletedInSign)).ToArray();
            var snapshot = Snapshot(links, casts: Casts());

            Assert.Equal([new Link(gone, Sign, DeletedInSign)], snapshot.StaleLinks);
            Assert.False(snapshot.IsSeriesCharacter(gone));
            Assert.Single(snapshot.Cast().Rows);
            Assert.DoesNotContain(DeletedInSign, snapshot.LinksOf(Sign).Keys);
            Assert.Null(snapshot.Peers(Sign, DeletedInSign));
        }

        [Fact]
        public void A_link_in_a_book_whose_Characters_were_not_read_is_kept()
        {
            var casts = Casts();
            casts.Remove(Sign);

            var snapshot = Snapshot(casts: casts);

            Assert.Empty(snapshot.StaleLinks);
            Assert.NotNull(snapshot.LinkOf(Watson, Sign));
            // Its book's Character can't be shown, so it takes no part in the series copy.
            Assert.Equal([StudyFolder], snapshot.LinkedPeers(Watson).Select(p => p.Folder));
        }

        [Fact]
        public void A_Not_the_same_whose_Character_is_gone_or_now_linked_is_left_out()
        {
            var linkedNow = new SeriesNotTheSame(Guid.NewGuid(), Study, WatsonInStudy, OtherProjectId: Sign, OtherCharacterId: LestradeInSign);
            var deleted = new SeriesNotTheSame(Guid.NewGuid(), Sign, DeletedInSign, SeriesCharacterId: Watson);
            var gonePair = new SeriesNotTheSame(Guid.NewGuid(), Study, HolmesInStudy, OtherProjectId: Gone, OtherCharacterId: Guid.NewGuid());
            var kept = new SeriesNotTheSame(Guid.NewGuid(), Hound, WatsonInHound, SeriesCharacterId: Watson);
            var members = new Dictionary<Guid, decimal>(Members) { [Gone] = 4m };

            var snapshot = Snapshot(members: members, notTheSame: [linkedNow, deleted, gonePair, kept], casts: Casts());

            Assert.Equal([kept], snapshot.NotTheSame);
            Assert.Equal([linkedNow, deleted, gonePair], snapshot.StaleNotTheSame);
            // The stale pair no longer hides the Holmes row; the kept cell still hides the Hound's Watson.
            var cast = snapshot.Cast();
            Assert.Single(cast.SuggestedRows);
            Assert.Null(cast.Rows[0].Cells[2].Suggested);
        }

        [Fact]
        public void A_snapshot_with_nothing_stale_has_no_stale_rows()
        {
            Assert.False(Snapshot(casts: Casts()).HasStaleRows);
        }

        // ── the Voices layer: Series copy, Differs from series, resolving, deletes ──

        // All three books link Dr. Watson here. The Study holds "Army doctor" and "Narrating"; the
        // Sign and the Hound hold "Army doctor" alone, so theirs is the Series copy (two books to one),
        // read from the Sign, and the Study differs from series.
        private static readonly Guid ArmyDoctor = Guid.NewGuid();
        private static readonly Guid Narrating = Guid.NewGuid();

        private static SeriesSnapshot AllThreeLinked(IReadOnlyList<Guid>? studyRules = null, bool withHound = true)
        {
            var snapshot = Snapshot([.. Links, new Link(Watson, Hound, WatsonInHound)], casts: Casts());
            var held = new List<SeriesHeldVoices>
            {
                Held(StudyFolder, "v1", [VoiceRow(ArmyDoctor, "Army doctor"), VoiceRow(Narrating, "Narrating")], studyRules, ArmyDoctor),
                Held(SignFolder, "v2", [VoiceRow(ArmyDoctor, "Army doctor")], defaultVoice: ArmyDoctor),
            };
            if (withHound) held.Add(Held(HoundFolder, "v2", [VoiceRow(ArmyDoctor, "Army doctor")], defaultVoice: ArmyDoctor));
            return Voiced(snapshot, [.. held]);
        }

        [Fact]
        public void The_Series_copy_is_the_version_most_linked_books_hold_and_the_others_differ_from_series()
        {
            var snapshot = AllThreeLinked();

            var copy = snapshot.SeriesCopy(Watson)!;

            Assert.Equal(SignFolder, copy.SeriesCopyFolder);
            Assert.True(snapshot.Differs(Watson, StudyFolder));
            Assert.False(snapshot.Differs(Watson, SignFolder));
            Assert.False(snapshot.Differs(Watson, HoundFolder));
            Assert.Equal([true, false, false], snapshot.Cast().Rows[0].Cells.Select(c => c.Differs));
            Assert.True(snapshot.LinksOf(Study)[WatsonInStudy].Differs);
        }

        [Fact]
        public void A_Series_character_whose_Voices_layer_was_not_loaded_differs_nowhere()
        {
            var snapshot = Snapshot(casts: Casts());

            Assert.Null(snapshot.SeriesCopy(Watson));
            Assert.False(snapshot.Differs(Watson, SignFolder));
            Assert.All(snapshot.Cast().Rows[0].Cells, c => Assert.False(c.Differs));
            Assert.Equal(SeriesRefusal.DoesNotDiffer("The Sign of the Four", "Dr. Watson"), snapshot.PlanResolve(Watson, SignFolder, push: true).Refusal);
            Assert.Equal(SeriesRefusal.UnknownSeriesCharacter, snapshot.Character(Watson).Refusal);
        }

        [Fact]
        public void A_take_into_a_differing_book_names_the_Voices_and_positional_rules_it_loses()
        {
            var snapshot = AllThreeLinked(studyRules: [Narrating, Narrating, ArmyDoctor]);

            var take = snapshot.PlanResolve(Watson, StudyFolder, push: false).Value!;

            Assert.Equal(SignFolder, take.Source.Folder);
            var loss = Assert.Single(snapshot.Losses(Watson, take.Source, take.Targets));
            Assert.Equal((StudyFolder, "A Study in Scarlet", 2), (loss.Folder, loss.Title, loss.RuleCount));
            Assert.Equal(["Narrating"], loss.Voices);
            Assert.Equal([ArmyDoctor], snapshot.VoicesOf(Watson, take.Source).Select(v => v.Voice.Id));
            // Pushing the Study's set takes nothing away from the others.
            var push = snapshot.PlanResolve(Watson, StudyFolder, push: true).Value!;
            Assert.Empty(snapshot.Losses(Watson, push.Source, push.Targets));
        }

        [Fact]
        public void An_edit_in_the_Series_copy_brings_back_in_line_the_differing_books_that_lose_no_positional_rules()
        {
            var fromSign = AllThreeLinked().Peers(Sign, WatsonInSign)!;
            var fromStudy = AllThreeLinked().Peers(Study, WatsonInStudy)!;

            Assert.Equal([StudyFolder], AllThreeLinked().DifferingPeers(fromSign));
            // The Study would lose its rule on "Narrating", and nothing asks first: it is left differing.
            Assert.Empty(AllThreeLinked(studyRules: [Narrating]).DifferingPeers(fromSign));
            // An edit in a differing book resolves nothing silently.
            Assert.Empty(AllThreeLinked().DifferingPeers(fromStudy));
        }

        [Fact]
        public void Deleting_a_shared_Voice_is_blocked_by_other_books_positional_rules_then_by_their_last_Voice()
        {
            var peers = AllThreeLinked().Peers(Sign, WatsonInSign)!;

            var ruled = AllThreeLinked(studyRules: [ArmyDoctor, ArmyDoctor]).VoiceDeleteBlock(peers, ArmyDoctor)!;
            var last = AllThreeLinked().VoiceDeleteBlock(peers, ArmyDoctor)!;

            Assert.Equal(SeriesVoiceDeleteReason.PositionalRules, ruled.Reason);
            Assert.Equal([new SeriesVoiceDeleteBook(StudyFolder, "A Study in Scarlet", 2)], ruled.Books);
            Assert.Equal(SeriesVoiceDeleteReason.LastVoice, last.Reason);
            Assert.Equal([new SeriesVoiceDeleteBook(HoundFolder, "The Hound of the Baskervilles", 0)], last.Books);
            // Deleting "Narrating" in the Sign: no other book's rule uses it, and none holds it alone.
            Assert.Null(AllThreeLinked().VoiceDeleteBlock(peers, Narrating));
            // A book whose Voices weren't read doesn't count here; the commit hook fails the delete on it.
            var houndUnread = AllThreeLinked(withHound: false);
            Assert.Null(houndUnread.VoiceDeleteBlock(peers, ArmyDoctor));
            Assert.False(houndUnread.VoicesRead(Watson, peers.Others.Single(p => p.Folder == HoundFolder)));
            Assert.True(houndUnread.VoicesRead(Watson, peers.Others.Single(p => p.Folder == StudyFolder)));
        }

        [Fact]
        public void The_series_Voice_editor_shows_the_Series_copy_each_linked_book_and_the_refused_deletes()
        {
            var view = AllThreeLinked().Character(Watson).Value!;

            Assert.Equal((SignFolder, WatsonInSign), (view.SeriesCopyFolder, view.SeriesCopyCharacterId));
            Assert.Equal([ArmyDoctor], view.Voices.Select(v => v.Id));
            Assert.Equal(
                [
                    new SeriesCharacterBook(StudyFolder, "A Study in Scarlet", 1m, WatsonInStudy, "Dr. Watson", ArmyDoctor, Differs: true),
                    new SeriesCharacterBook(SignFolder, "The Sign of the Four", 2m, WatsonInSign, "Dr. Watson", ArmyDoctor, Differs: false),
                    new SeriesCharacterBook(HoundFolder, "The Hound of the Baskervilles", 3m, WatsonInHound, "Dr. Watson", ArmyDoctor, Differs: false),
                ],
                view.Books);
            // Judged as a delete in the Sign: "Army doctor" is the Hound's last Voice.
            Assert.Equal(SeriesVoiceDeleteReason.LastVoice, view.DeleteBlocks[ArmyDoctor].Reason);
            Assert.Equal([HoundFolder], view.DeleteBlocks[ArmyDoctor].Books.Select(b => b.Folder));
        }

        // ── arrange ─────────────────────────────────────────────────────────────

        private static SeriesRecord Record(
            IReadOnlyList<Link>? links = null, IReadOnlyDictionary<Guid, decimal>? members = null, IReadOnlyList<SeriesNotTheSame>? notTheSame = null) =>
            new(SeriesId, "Sherlock Holmes", members ?? Members, links ?? Links, notTheSame ?? []);

        private static SeriesSnapshot Snapshot(
            IReadOnlyList<Link>? links = null,
            IReadOnlyDictionary<Guid, decimal>? members = null,
            IReadOnlyList<SeriesNotTheSame>? notTheSame = null,
            IReadOnlyDictionary<Guid, SeriesBookCast>? casts = null) =>
            new(Record(links, members, notTheSame), Index, casts);

        private static SeriesSnapshot Voiced(SeriesSnapshot snapshot, params SeriesHeldVoices[] held) =>
            snapshot.WithVoices(new Dictionary<Guid, IReadOnlyList<SeriesHeldVoices>> { [Watson] = held });

        /// <summary>A book's Voices of Dr. Watson as the Voices layer holds them; <paramref name="version"/> stands in for their hashed state.</summary>
        private static SeriesHeldVoices Held(
            string folder, string version, IReadOnlyList<Read2Me.Data.Entities.Voice>? voices = null,
            IReadOnlyList<Guid>? positionalRules = null, Guid? defaultVoice = null)
        {
            var book = Index.ByFolder(folder)!;
            var characterId = new Dictionary<Guid, Guid> { [Study] = WatsonInStudy, [Sign] = WatsonInSign, [Hound] = WatsonInHound }[book.ProjectId];
            var peer = new SeriesPeer(folder, book.Title, SeriesPositions.Normalize(Members[book.ProjectId]), characterId);
            return new SeriesHeldVoices(peer, voices ?? [], version, positionalRules ?? [], defaultVoice);
        }

        private static Read2Me.Data.Entities.Voice VoiceRow(Guid id, string name) =>
            new() { Id = id, Name = name, Source = VoiceSource.Uploaded };

        private static SeriesBookCast Cast(params (string Name, Guid Id)[] characters) =>
            new(characters.Select(c => new SeriesCastCharacter(c.Id, c.Name, false, [])).ToList(), NarratorLinked: false);

        private static SeriesVoiceCopy Voice(string name) =>
            new(Guid.NewGuid(), name, null, VoiceSource.Uploaded, null, null, null, null, null, new DateTime(2026, 11, 7, 0, 0, 0, DateTimeKind.Utc));

        private static SeriesVoiceSource Source(string folder, Guid characterId, SeriesVoiceCopy voice) =>
            new(new Read2Me.Core.Models.ProjectFolderId(folder), characterId, voice);
    }
}
