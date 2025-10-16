using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

public static class Program
{
    public static async Task Main(string[] args)
    {
        using var host = Host.CreateDefaultBuilder(args)
            .ConfigureServices(s =>
            {
                s.AddSingleton<IFileIndex, InMemoryFileIndex>();
                s.AddHostedService<SnapshotWorker>();
                s.AddHostedService<SyncServer>();
            })
            .Build();

        await host.RunAsync();
    }
}
