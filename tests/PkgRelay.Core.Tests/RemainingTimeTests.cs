using PkgRelay.Models;
using PkgRelay.Services;
using Xunit;

namespace PkgRelay.Core.Tests;

public sealed class RemainingTimeTests
{
    [Theory]
    [InlineData("4294967295")]
    [InlineData("\"0xFFFFFFFF\"")]
    [InlineData("-1")]
    [InlineData("2147483647")]
    public void UnknownPs4RemainingTimeIsNotAnEta(string seconds)
    {
        using var document = RpiJson.Parse("{\"rest_sec_total\":" + seconds + "}");
        Assert.Equal(-1, RpiProgress.FromJson(document.RootElement).RestSecondsTotal);
    }

    [Fact]
    public void ValidRemainingTimeIsPreserved()
    {
        using var document = RpiJson.Parse("{\"rest_sec_total\":120}");
        Assert.Equal(120, RpiProgress.FromJson(document.RootElement).RestSecondsTotal);
    }

    [Fact]
    public void FailedJobsDoNotShowStaleTransferDetails()
    {
        var job = new InstallJob { Status = InstallStatus.Installing, EtaSeconds = 120, TransferRateBytesPerSecond = 1024 };
        Assert.NotEmpty(job.TransferDetailsText);
        job.Status = InstallStatus.Failed;
        Assert.Empty(job.TransferDetailsText);
    }

    [Fact]
    public void PreviouslyPersistedUnknownEtaIsNormalized()
    {
        var job = new InstallJob { EtaSeconds = int.MaxValue };
        Assert.Null(job.EtaSeconds);
    }
}
