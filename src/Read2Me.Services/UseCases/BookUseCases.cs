using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Read2Me.Core.Models;

namespace Read2Me.Services.UseCases
{
    public class BookUseCases(BookReadingService bookReadingService, IBookCommandHandler commandHandler)
    {
        public virtual async Task<Result> ImportAsync(string folderName, bool reread = false, CancellationToken cancellationToken = default)
        {
            try
            {
                if (reread)
                    await commandHandler.ExecuteAsync(new ClearBookContentCommand(folderName), cancellationToken);
                await bookReadingService.ReadBookAsync(folderName, cancellationToken);
                return Result.Ok();
            }
            catch (FileNotFoundException ex) { return Result.Fail(ex.Message); }
            catch (InvalidOperationException ex) { return Result.Fail(ex.Message); }
            catch (Exception) { return Result.Fail("Failed to import book. Please try again."); }
        }

        public virtual async Task<Result> ImportManuallyAsync(string folderName, ManualReadOptions options, CancellationToken cancellationToken = default)
        {
            try
            {
                var lines = await bookReadingService.FlattenFromFileAsync(folderName, cancellationToken);
                await commandHandler.ExecuteAsync(new ClearBookContentCommand(folderName), cancellationToken);
                await bookReadingService.ReadBookManuallyAsync(folderName, lines, options, cancellationToken);
                return Result.Ok();
            }
            catch (FileNotFoundException ex) { return Result.Fail(ex.Message); }
            catch (InvalidOperationException ex) { return Result.Fail(ex.Message); }
            catch (Exception) { return Result.Fail("Failed to manually reread book. Please try again."); }
        }
    }
}
