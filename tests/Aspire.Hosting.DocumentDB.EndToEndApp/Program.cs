using Aspire.Hosting;

namespace Aspire.Hosting.DocumentDB.EndToEndApp;

public class Program
{
    public static async Task Main(string[] args)
    {
        var builder = DistributedApplication.CreateBuilder(args);

        builder.AddDocumentDB("documentdb")
            .AddDatabase("appdb");

        var app = builder.Build();

        await app.RunAsync();
    }
}
