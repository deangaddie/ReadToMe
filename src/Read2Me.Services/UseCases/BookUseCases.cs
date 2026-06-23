using Microsoft.EntityFrameworkCore;
using Read2Me.Core.Models;

namespace Read2Me.Services.UseCases
{
    public class BookUseCases(BookReadingService bookReadingService, IBookCommandHandler commandHandler, ProjectDbSession session, IProjectWriter projectWriter)
    {
        public virtual async Task<Result> ImportAsync(string folderName, bool reread = false, CancellationToken cancellationToken = default)
        {
            try
            {
                if (reread)
                    await commandHandler.ExecuteAsync(new ClearBookContentCommand(folderName), cancellationToken);
                var coverImage = await bookReadingService.ReadBookAsync(folderName, cancellationToken);
                session.Evict(folderName);
                await TrySaveCoverImageAsync(folderName, coverImage, cancellationToken);
                return Result.Ok();
            }
            catch (FileNotFoundException ex) { return Result.Fail(ex.Message); }
            catch (InvalidOperationException ex) { return Result.Fail(ex.Message); }
            catch (Exception) { return Result.Fail("Failed to import book. Please try again."); }
        }

        private async Task TrySaveCoverImageAsync(string folderName, byte[]? coverImage, CancellationToken cancellationToken)
        {
            if (coverImage is null || coverImage.Length == 0)
                return;

            try
            {
                var db = await session.OpenAsync(folderName);
                var entity = await db.Projects.SingleOrDefaultAsync(cancellationToken);
                if (entity?.CoverImage != null)
                    return;

                var ext = DetectImageExtension(coverImage);
                if (ext is null)
                    return;

                await projectWriter.SaveCoverImageAsync(folderName, "cover" + ext, new MemoryStream(coverImage));
            }
            catch
            {
                // cover extraction is best-effort; never fail the import
            }
        }

        private static string? DetectImageExtension(byte[] bytes)
        {
            if (bytes.Length >= 3 && bytes[0] == 0xFF && bytes[1] == 0xD8 && bytes[2] == 0xFF)
                return ".jpg";
            if (bytes.Length >= 4 && bytes[0] == 0x89 && bytes[1] == 0x50 && bytes[2] == 0x4E && bytes[3] == 0x47)
                return ".png";
            return null;
        }

        public virtual async Task<Result> ImportManuallyAsync(string folderName, ManualReadOptions options, CancellationToken cancellationToken = default)
        {
            try
            {
                var lines = await bookReadingService.FlattenFromFileAsync(folderName, cancellationToken);
                await commandHandler.ExecuteAsync(new ClearBookContentCommand(folderName), cancellationToken);
                await bookReadingService.ReadBookManuallyAsync(folderName, lines, options, cancellationToken);
                session.Evict(folderName);
                return Result.Ok();
            }
            catch (FileNotFoundException ex) { return Result.Fail(ex.Message); }
            catch (InvalidOperationException ex) { return Result.Fail(ex.Message); }
            catch (Exception) { return Result.Fail("Failed to manually reread book. Please try again."); }
        }
    }
}
