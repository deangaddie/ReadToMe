using Read2Me.Services.Audio.Assembly;
using Xunit;

namespace Read2Me.Tests.Services.Audio
{
    public class AssemblyOutputsTests : IDisposable
    {
        private readonly string _projectDir;

        public AssemblyOutputsTests()
        {
            _projectDir = Path.Combine(Path.GetTempPath(), "R2mAssemblyOutputs_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_projectDir);
        }

        public void Dispose()
        {
            try { if (Directory.Exists(_projectDir)) Directory.Delete(_projectDir, recursive: true); } catch { }
        }

        private string Seed(string fileName, int bytes = 4)
        {
            var dir = AssemblyOutputs.DirectoryOf(_projectDir);
            Directory.CreateDirectory(dir);
            var path = Path.Combine(dir, fileName);
            File.WriteAllBytes(path, new byte[bytes]);
            return path;
        }

        [Fact]
        public void FileName_FullBuild_IsTheSanitizedTitle()
        {
            Assert.Equal("Who_ Me_.m4b", AssemblyOutputs.FileName("Who? Me:", partial: false, new DateTime(2026, 9, 19)));
        }

        [Fact]
        public void FileName_PartialBuild_CarriesTheDatedMarker()
        {
            var name = AssemblyOutputs.FileName("Dune", partial: true, new DateTime(2026, 9, 19));

            Assert.Equal("Dune_partial_20260919.m4b", name);
            Assert.True(AssemblyOutputs.IsPartial(name));
            Assert.False(AssemblyOutputs.IsPartial("Dune.m4b"));
        }

        [Fact]
        public void List_MissingOutputFolder_IsEmpty()
        {
            Assert.Empty(AssemblyOutputs.List(_projectDir));
        }

        [Fact]
        public void List_ReturnsM4bsNewestFirst_AndSkipsEverythingElse()
        {
            File.SetLastWriteTimeUtc(Seed("Dune.m4b", bytes: 10), new DateTime(2026, 9, 1, 0, 0, 0, DateTimeKind.Utc));
            File.SetLastWriteTimeUtc(Seed("Dune_partial_20260919.m4b", bytes: 3), new DateTime(2026, 9, 19, 0, 0, 0, DateTimeKind.Utc));
            Seed("Dune.m4b.tmp");
            Seed("notes.txt");

            var outputs = AssemblyOutputs.List(_projectDir);

            Assert.Equal(new[] { "Dune_partial_20260919.m4b", "Dune.m4b" }, outputs.Select(o => o.FileName));
            Assert.True(outputs[0].IsPartial);
            Assert.False(outputs[1].IsPartial);
            Assert.Equal(10, outputs[1].SizeBytes);
            Assert.Equal(new DateTime(2026, 9, 1, 0, 0, 0, DateTimeKind.Utc), outputs[1].CreatedAt.UtcDateTime);
        }

        [Fact]
        public void TryResolve_ExistingOutput_GivesItsPath()
        {
            var seeded = Seed("Dune.m4b");

            Assert.True(AssemblyOutputs.TryResolve(_projectDir, "Dune.m4b", out var path));
            Assert.Equal(seeded, path);
        }

        [Theory]
        [InlineData("../Dune.m4b")]
        [InlineData("..\\Dune.m4b")]
        [InlineData("sub/Dune.m4b")]
        [InlineData("Dune.m4b.tmp")]
        [InlineData("notes.txt")]
        [InlineData("missing.m4b")]
        [InlineData("")]
        public void TryResolve_AnythingElse_IsRefused(string fileName)
        {
            Seed("Dune.m4b");
            Seed("Dune.m4b.tmp");
            Seed("notes.txt");
            File.WriteAllBytes(Path.Combine(_projectDir, "Dune.m4b"), new byte[1]);

            Assert.False(AssemblyOutputs.TryResolve(_projectDir, fileName, out _));
        }
    }
}
