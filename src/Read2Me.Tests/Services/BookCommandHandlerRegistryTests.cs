using System;
using System.Linq;
using System.Reflection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Read2Me.Core.Configuration;
using Read2Me.Core.Models;
using Read2Me.Services;
using Read2Me.Services.IO;
using Read2Me.Tests.Infrastructure;
using Xunit;

namespace Read2Me.Tests.Services
{
    public class BookCommandHandlerRegistryTests : ProjectDbTestBase
    {
        [Fact]
        public void EveryBookCommandSubclass_HasRegisteredHandler()
        {
            var fs = new FileSystemService(Options.Create(new WorkspaceOptions { FolderPath = TempDir }));
            var session = new ProjectDbSession(fs, new ProjectDbContextProvider(), NullLogger<ProjectDbSession>.Instance);
            var handler = new BookCommandHandler(session, fs);
            
            // Use reflection to get the private _handlers dictionary
            var handlersField = typeof(BookCommandHandler).GetField("_handlers", BindingFlags.NonPublic | BindingFlags.Instance);
            var handlers = (System.Collections.Generic.Dictionary<Type, Func<BookCommand, CancellationToken, Task<Guid?>>>)handlersField!.GetValue(handler)!;

            var commandTypes = typeof(BookCommand).Assembly.GetTypes()
                .Where(t => t.IsSubclassOf(typeof(BookCommand)) && !t.IsAbstract);

            foreach (var type in commandTypes)
            {
                Assert.True(handlers.ContainsKey(type), $"Command type {type.Name} is not registered in BookCommandHandler.");
            }
        }
    }
}
