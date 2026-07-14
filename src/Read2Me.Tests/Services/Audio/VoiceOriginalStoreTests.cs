using Microsoft.Extensions.Logging.Abstractions;
using Read2Me.Core.Models;
using Read2Me.Services.Audio;
using Read2Me.Tests.Fakes;
using Xunit;

namespace Read2Me.Tests.Services.Audio
{
    public class VoiceOriginalStoreTests
    {
        private const string Root = "C:\\fake-workspace";
        private static readonly ProjectFolderId Folder = new("book-a");

        private readonly FakeFileSystem _fs = new(Root);
        private readonly VoiceOriginalStore _store;
        private readonly Guid _charId = Guid.NewGuid();
        private readonly Guid _voiceId = Guid.NewGuid();

        public VoiceOriginalStoreTests() => _store = new VoiceOriginalStore(_fs);

        private string LivePath(string name = "my-voice") =>
            Path.Combine(Root, "book-a", "voices", _charId.ToString(), $"{_voiceId}-{name}.wav");

        private string OriginalPath() =>
            Path.Combine(Root, "book-a", "voices", _charId.ToString(), $"{_voiceId}.orig.wav");

        private string LiveRelative(string name = "my-voice") =>
            $"voices/{_charId}/{_voiceId}-{name}.wav";

        [Fact]
        public void RelativePath_keys_on_the_voice_id_alone()
        {
            // The live WAV's name is derived from the voice's *name*, so a rename + re-upload moves it.
            // Keying the original on the id alone is what stops that orphaning it.
            Assert.Equal($"voices/{_charId}/{_voiceId}.orig.wav", _store.RelativePath(_charId, _voiceId));
        }

        [Fact]
        public async Task Capture_copies_the_live_wav_byte_for_byte()
        {
            _fs.SeedFile(LivePath(), [1, 2, 3]);

            await _store.CaptureIfAbsentAsync(Folder, _charId, _voiceId, LiveRelative());

            Assert.True(_store.Exists(Folder, _charId, _voiceId));
            Assert.Equal([1, 2, 3], await _store.TryReadAsync(Folder, _charId, _voiceId));
        }

        [Fact]
        public async Task Second_capture_does_not_overwrite_the_first()
        {
            // The second Apply's "live" WAV is already edited audio. Overwriting here would make a
            // re-edit stack filters on filters, and Restore would restore the wrong bytes.
            _fs.SeedFile(LivePath(), [1, 2, 3]);
            await _store.CaptureIfAbsentAsync(Folder, _charId, _voiceId, LiveRelative());

            _fs.SeedFile(LivePath(), [9, 9, 9]);
            await _store.CaptureIfAbsentAsync(Folder, _charId, _voiceId, LiveRelative());

            Assert.Equal([1, 2, 3], await _store.TryReadAsync(Folder, _charId, _voiceId));
        }

        [Fact]
        public async Task Capture_survives_a_rename_because_it_is_keyed_on_the_id()
        {
            _fs.SeedFile(LivePath("old-name"), [1, 2, 3]);
            await _store.CaptureIfAbsentAsync(Folder, _charId, _voiceId, LiveRelative("old-name"));

            // The voice is renamed; the live WAV would move, the original does not.
            Assert.True(_fs.FileExists(OriginalPath()));
            Assert.True(_store.Exists(Folder, _charId, _voiceId));
        }

        [Fact]
        public async Task Capture_with_no_live_audio_stores_nothing()
        {
            await _store.CaptureIfAbsentAsync(Folder, _charId, _voiceId, LiveRelative());

            Assert.False(_store.Exists(Folder, _charId, _voiceId));
        }

        [Fact]
        public async Task TryRead_is_null_when_the_voice_was_never_edited()
        {
            Assert.Null(await _store.TryReadAsync(Folder, _charId, _voiceId));
        }

        [Fact]
        public async Task Delete_clears_the_invariant()
        {
            _fs.SeedFile(LivePath(), [1, 2, 3]);
            await _store.CaptureIfAbsentAsync(Folder, _charId, _voiceId, LiveRelative());

            _store.Delete(Folder, _charId, _voiceId);

            Assert.False(_store.Exists(Folder, _charId, _voiceId));
        }

        [Fact]
        public void Delete_with_nothing_stored_is_a_no_op()
        {
            _store.Delete(Folder, _charId, _voiceId);
            Assert.False(_store.Exists(Folder, _charId, _voiceId));
        }
    }

    public class VoiceAudioEditorTests
    {
        private const string Root = "C:\\fake-workspace";
        private static readonly ProjectFolderId Folder = new("book-a");

        private readonly FakeFileSystem _fs = new(Root);
        private readonly VoiceOriginalStore _originals;
        private readonly VoiceAudioEditor _editor;
        private readonly Guid _charId = Guid.NewGuid();
        private readonly Guid _voiceId = Guid.NewGuid();

        public VoiceAudioEditorTests()
        {
            _originals = new VoiceOriginalStore(_fs);
            _editor = new VoiceAudioEditor(_originals, _fs, NullLogger<VoiceAudioEditor>.Instance);
        }

        private VoiceAudioRef Voice => new(Folder, _charId, _voiceId, $"voices/{_charId}/{_voiceId}-my-voice.wav");

        private string LivePath => Path.Combine(
            Root, "book-a", "voices", _charId.ToString(), $"{_voiceId}-my-voice.wav");

        [Fact]
        public async Task Apply_captures_the_original_then_writes_over_the_existing_path()
        {
            _fs.SeedFile(LivePath, [1, 2, 3]);

            await _editor.ApplyAsync(Voice, [7, 7]);

            // Same path as before: Apply never re-derives the name (a rename would orphan the file).
            Assert.Equal([7, 7], _fs.GetFileContent(LivePath));
            Assert.Equal([1, 2, 3], await _originals.TryReadAsync(Folder, _charId, _voiceId));
        }

        [Fact]
        public async Task Second_apply_keeps_the_first_original_and_is_idempotent()
        {
            _fs.SeedFile(LivePath, [1, 2, 3]);

            await _editor.ApplyAsync(Voice, [7, 7]);
            await _editor.ApplyAsync(Voice, [7, 7]);

            Assert.Equal([1, 2, 3], await _originals.TryReadAsync(Folder, _charId, _voiceId));
            Assert.Equal([7, 7], _fs.GetFileContent(LivePath));
        }

        [Fact]
        public async Task Restore_copies_the_original_back_then_deletes_it()
        {
            _fs.SeedFile(LivePath, [1, 2, 3]);
            await _editor.ApplyAsync(Voice, [7, 7]);

            await _editor.RestoreOriginalAsync(Voice);

            Assert.Equal([1, 2, 3], _fs.GetFileContent(LivePath));
            // The delete is what keeps the invariant exact: a surviving copy leaves the chip lying.
            Assert.False(_originals.Exists(Folder, _charId, _voiceId));
        }

        [Fact]
        public async Task Restore_with_no_original_is_a_no_op_not_a_throw()
        {
            _fs.SeedFile(LivePath, [1, 2, 3]);

            await _editor.RestoreOriginalAsync(Voice);

            Assert.Equal([1, 2, 3], _fs.GetFileContent(LivePath));
        }
    }
}
