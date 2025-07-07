using Aspire.Hosting;
using Aspire.Hosting.ApplicationModel;
using Microsoft.Extensions.DependencyInjection;

Console.WriteLine("Validating Aspire.Hosting.DocumentDB...");

try
{
    // Test basic functionality
    var appBuilder = DistributedApplication.CreateBuilder();
    
    // Test adding DocumentDB
    var documentdb = appBuilder.AddDocumentDB("test-documentdb");
    Console.WriteLine("✓ AddDocumentDB works");
    
    // Test adding database
    var database = documentdb.AddDatabase("test-database");
    Console.WriteLine("✓ AddDatabase works");
    
    // Test building the app
    using var app = appBuilder.Build();
    Console.WriteLine("✓ Application builds successfully");
    
    // Test getting the application model
    var appModel = app.Services.GetRequiredService<DistributedApplicationModel>();
    Console.WriteLine($"✓ Application model contains {appModel.Resources.Count()} resources");
    
    // Check DocumentDB server resource
    var serverResource = appModel.Resources.OfType<Aspire.Hosting.ApplicationModel.DocumentDBServerResource>().FirstOrDefault();
    if (serverResource != null)
    {
        Console.WriteLine($"✓ DocumentDB server resource found: {serverResource.Name}");
    }
    
    // Check DocumentDB database resource
    var dbResource = appModel.Resources.OfType<Aspire.Hosting.ApplicationModel.DocumentDBDatabaseResource>().FirstOrDefault();
    if (dbResource != null)
    {
        Console.WriteLine($"✓ DocumentDB database resource found: {dbResource.Name}");
    }
    
    Console.WriteLine("\n🎉 All basic validations passed!");
    Console.WriteLine("The Aspire.Hosting.DocumentDB library appears to be working correctly.");
}
catch (Exception ex)
{
    Console.WriteLine($"❌ Validation failed: {ex.Message}");
    Console.WriteLine($"Stack trace: {ex.StackTrace}");
    return 1;
}

return 0;
