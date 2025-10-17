using System.Runtime.Intrinsics.X86;

public static class ServerTuning
{
    public static int EstimateMaxClients(
        double diskReadMBps = 500,     // ディスク読み込み速度 (MB/s)
        double clientAvgMBps = 2.0,    // クライアント1つあたりの平均送信レート
        double netBandwidthMbps = 1000 // 総ネットワーク帯域 (Mbps)
    )
    {
        int cpuCores = Environment.ProcessorCount;

        // ベース：コア数 × 50
        double baseLoad = cpuCores * 50;

        // ディスク→ネット転送補正
        double ioFactor = diskReadMBps / clientAvgMBps;

        // 帯域上限 (Mbps→MB/s換算)
        double netLimit = netBandwidthMbps / (clientAvgMBps * 8);

        // 安全係数
        double safety = 0.5;

        double est = baseLoad * ioFactor * safety;
        return (int)Math.Max(10, Math.Min(est, netLimit));
    }
}

//| 環境例 | 自動上限 |
//| ----------------------------- | ------------ |
//| 8コア / SSD 500MB / s / 1Gbps | 約 1000 接続 |
//| 4コア / HDD 150MB / s / 1Gbps | 約 200～300 接続 |
//| 16コア / NVMe 3500MB / s / 10Gbps | 4000～6000 接続 |
