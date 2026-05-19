using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;
using NoteAssistant.KnowledgeGraph.Backend.Services;
using Xunit;

namespace NoteAssistant.KnowledgeGraph.Backend.Tests;

public sealed class AgeDatabaseConnectionFactoryTests
{
    [Fact]
    public void IsConfigured_True_WhenConnectionStringProvided()
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:AgeDatabase"] = "Host=localhost;Database=test;Username=user;Password=pass"
            })
            .Build();

        var options = Options.Create(new DatabaseOptions());
        var factory = new AgeDatabaseConnectionFactory(config, options);

        Assert.True(factory.IsConfigured);
    }

    [Fact]
    public void IsConfigured_True_WhenDatabaseOptionsProvided()
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection().Build();
        var options = Options.Create(new DatabaseOptions
        {
            Host = "localhost",
            Database = "noteassistant",
            Username = "user"
        });

        var factory = new AgeDatabaseConnectionFactory(config, options);

        Assert.True(factory.IsConfigured);
    }
}
