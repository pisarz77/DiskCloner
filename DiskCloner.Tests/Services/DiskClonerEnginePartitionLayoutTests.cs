using DiskCloner.Core.Services;
using Xunit;

namespace DiskCloner.Tests.Services;

public class DiskClonerEnginePartitionLayoutTests
{
    [Fact]
    public void GetDiskPartSizeMegabytes_RoundsUpToAvoidUndersizedTarget()
    {
        // 110,356,327,936 bytes was failing before because floor(MB) undersized target by ~1 MB.
        const long sourceSizeBytes = 110_356_327_936;

        var sizeMb = DiskClonerEngine.GetDiskPartSizeMegabytes(sourceSizeBytes);

        Assert.Equal(105_244, sizeMb);
        Assert.True(sizeMb * 1024L * 1024L >= sourceSizeBytes);
    }

    [Fact]
    public void ParseTargetPartitionLayoutJson_UsesExactBytesAndMapsBasicToPrimary()
    {
        var json =
            "[{\"PartitionNumber\":1,\"Type\":\"System\",\"Offset\":1048576,\"Size\":104857600}," +
            "{\"PartitionNumber\":3,\"Type\":\"Basic\",\"Offset\":122683392,\"Size\":110356332544}]";

        var partitions = DiskClonerEngine.ParseTargetPartitionLayoutJson(json);

        Assert.Equal(2, partitions.Count);
        Assert.Equal((1, "System", 104_857_600L, 1_048_576L), partitions[0]);
        Assert.Equal((3, "Primary", 110_356_332_544L, 122_683_392L), partitions[1]);
    }
}
