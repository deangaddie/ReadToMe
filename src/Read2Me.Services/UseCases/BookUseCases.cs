using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Read2Me.Core.Models;

namespace Read2Me.Services.UseCases
{
    public class BookUseCases(BookReadingService bookReadingService, IProjectWriter projectWriter)
    {
        public async Task<Result> ImportAsync(string folderName, bool reread = false, CancellationToken cancellationToken = default)
        {
            try
            {
                if (reread)
                    await projectWriter.ClearBookContentAsync(folderName);
                await bookReadingService.ReadBookAsync(folderName, cancellationToken);
                return Result.Ok();
            }
            catch (FileNotFoundException ex) { return Result.Fail(ex.Message); }
            catch (InvalidOperationException ex) { return Result.Fail(ex.Message); }
            catch (Exception) { return Result.Fail("Failed to import book. Please try again."); }
        }
    }
}
