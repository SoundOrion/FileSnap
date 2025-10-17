using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using System.Text;

public static class Program
{
    public static async Task Main(string[] args)
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
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