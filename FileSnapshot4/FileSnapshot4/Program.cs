// ====== Host 起動（Channelをシングルトン共有：Writer/Readerを個別DI） ======
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
                // 共有スナップショット
                s.AddSingleton<IFileIndex, InMemoryFileIndex>();

                // Channel をシングルトン化し、Writer/Reader を個別登録
                s.AddSingleton(sp => Channel.CreateBounded<SyncJob>(new BoundedChannelOptions(1024)
                {
                    FullMode = BoundedChannelFullMode.Wait,
                    SingleReader = false,
                    SingleWriter = false
                }));
                s.AddSingleton<ChannelWriter<SyncJob>>(sp => sp.GetRequiredService<Channel<SyncJob>>().Writer);
                s.AddSingleton<ChannelReader<SyncJob>>(sp => sp.GetRequiredService<Channel<SyncJob>>().Reader);

                // Hosted services
                s.AddHostedService<SnapshotWorker>();
                s.AddHostedService<AcceptService>();   // producer
                s.AddHostedService<ProcessService>();  // consumer(s)
            })
            .Build();

        await host.RunAsync();
    }
}