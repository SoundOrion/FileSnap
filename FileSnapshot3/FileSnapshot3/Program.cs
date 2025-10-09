using System.Collections.Immutable;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using FileSnapshot3;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;


public class Program
{
    public static async Task Main(string[] args)
    {
        using var host = Host.CreateDefaultBuilder(args)
            .ConfigureLogging(b => b.SetMinimumLevel(LogLevel.Information))
            .ConfigureServices(services =>
            {
                services.AddSingleton<IFileIndex, InMemoryFileIndex>();
                services.AddHostedService<SnapshotWorker>(); // 1つ目: スナップショット更新
                services.AddHostedService<TcpServerWorker>(); // 2つ目: TCPサーバ
            })
            .Build();

        await host.RunAsync();
    }
}
