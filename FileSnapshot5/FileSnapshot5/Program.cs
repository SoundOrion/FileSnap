using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using System.Threading.Channels;

public static class Program
{
    public static async Task Main(string[] args)
    {
        using var host = Host.CreateDefaultBuilder(args)
            .ConfigureServices(s =>
            {
                s.AddSingleton<IFileIndex, InMemoryFileIndex>();

                s.AddSingleton(sp => Channel.CreateBounded<SyncJob>(new BoundedChannelOptions(1024)
                {
                    FullMode = BoundedChannelFullMode.Wait,
                    SingleReader = false,
                    SingleWriter = false
                }));
                s.AddSingleton<ChannelWriter<SyncJob>>(sp => sp.GetRequiredService<Channel<SyncJob>>().Writer);
                s.AddSingleton<ChannelReader<SyncJob>>(sp => sp.GetRequiredService<Channel<SyncJob>>().Reader);

                s.AddHostedService<SnapshotWorker>(); // #1 スナップショット
                s.AddHostedService<AcceptService>();   // #2 受信→enqueue
                s.AddHostedService<ProcessService>();  // #3 デキュー→比較→送信
            })
            .Build();

        await host.RunAsync();
    }
}
