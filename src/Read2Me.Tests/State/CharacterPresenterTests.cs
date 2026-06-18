using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using NSubstitute;
using Read2Me.App.State;
using Read2Me.Core.Audio;
using Read2Me.Core.Models;
using Read2Me.Services;
using Read2Me.Services.Audio.Transcription;
using Read2Me.Services.Audio.VoiceDesign;
using Xunit;

namespace Read2Me.Tests.State
{
    public class CharacterPresenterTests
    {
        private static readonly ProjectFolderId Folder = new("test-book");

        private static CharacterPresenter CreatePresenter(
            IAudioPipeline? audioPipeline = null,
            IBookCommandHandler? commandHandler = null)
        {
            var reader = Substitute.For<IProjectReader>();
            reader.GetCharactersWithAliasesAsync(Folder)
                .Returns(new System.Collections.Generic.List<Read2Me.Data.Entities.Character>());

            var pipeline = audioPipeline ?? Substitute.For<IAudioPipeline>();
            var cmd = commandHandler ?? Substitute.For<IBookCommandHandler>();

            return new CharacterPresenter(
                reader,
                cmd,
                pipeline,
                transcriptionResolver: null!,
                voiceDesignResolver: null!,
                voiceDesignSettings: null!,
                transcriptionSettings: null!,
                voiceDesignPromptService: null!,
                fileSystem: null!);
        }

        [Fact]
        public async Task VoiceAudioUrl_ChangesAfterUpload()
        {
            var voiceId = Guid.NewGuid();
            var charId = Guid.NewGuid();

            var pipeline = Substitute.For<IAudioPipeline>();
            pipeline.StoreAsync(Arg.Any<AudioStoreRequest>(), Arg.Any<CancellationToken>())
                .Returns("voices/test.wav");

            var presenter = CreatePresenter(audioPipeline: pipeline);
            await presenter.LoadAsync(Folder);

            var tokenBefore = presenter.AudioToken(voiceId);
            await presenter.UploadVoiceAudioAsync(
                charId, voiceId, "TestVoice",
                new MemoryStream(new byte[] { 1, 2, 3 }), ".wav");
            var tokenAfter = presenter.AudioToken(voiceId);

            Assert.True(tokenAfter > tokenBefore, "AudioToken must increment after upload so URL changes");
        }

        [Fact]
        public async Task VoiceAudioUrl_TwoSuccessiveUploads_TokenIncreasesTwice()
        {
            var voiceId = Guid.NewGuid();
            var charId = Guid.NewGuid();

            var pipeline = Substitute.For<IAudioPipeline>();
            pipeline.StoreAsync(Arg.Any<AudioStoreRequest>(), Arg.Any<CancellationToken>())
                .Returns("voices/test.wav");

            var presenter = CreatePresenter(audioPipeline: pipeline);
            await presenter.LoadAsync(Folder);

            var t0 = presenter.AudioToken(voiceId);
            await presenter.UploadVoiceAudioAsync(charId, voiceId, "V", new MemoryStream([1]), ".wav");
            var t1 = presenter.AudioToken(voiceId);
            await presenter.UploadVoiceAudioAsync(charId, voiceId, "V", new MemoryStream([2]), ".wav");
            var t2 = presenter.AudioToken(voiceId);

            Assert.True(t1 > t0);
            Assert.True(t2 > t1);
        }
    }
}
