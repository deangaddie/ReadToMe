using System;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Read2Me.Core.Configuration;
using Read2Me.Core.Models;
using Read2Me.Data;
using Read2Me.Services;
using Read2Me.Services.Commands;
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
            var services = new ServiceCollection();
            services.AddBookCommandHandlers();
            services.Configure<WorkspaceOptions>(o => o.FolderPath = TempDir);
            services.AddSingleton<IProjectDbContextFactory, ProjectDbContextProvider>();
            var sp = services.BuildServiceProvider();

            var commandTypes = typeof(BookCommand).Assembly.GetTypes()
                .Where(t => t.IsSubclassOf(typeof(BookCommand)) && !t.IsAbstract);

            foreach (var type in commandTypes)
            {
                var handlerType = typeof(ICommandHandler<>).MakeGenericType(type);
                var handler = sp.GetService(handlerType);
                Assert.True(handler != null, $"Command type {type.Name} is not registered in DI.");
            }
        }
    }
}
