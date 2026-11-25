using System;
using System.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Frame = MCPanel.SvnProductTransferService.SvnFailureFrame;

namespace MCPanel.Tests;

public sealed partial class ReliabilityTests
{
    private const string SupplierRepository = "https://update.example.test:9443/udp/online_store_project";

    [TestMethod]
    public void ConnectionClosedMidCheckoutIsRetriedEvenAsAPlainSvnException()
    {
        // The chain from a real failure: a large product's checkout ran for ~50
        // minutes, then the server dropped the connection. Both levels are
        // plain SvnException, which used to stop after the first attempt.
        var frames = new[]
        {
            new Frame("SvnException", "RepositoryAccess", 170013,
                $"Unable to connect to a repository at URL '{SupplierRepository}'"),
            new Frame("SvnException", "RepositoryAccessDav", 175002,
                "Error running context: The server unexpectedly closed the connection.")
        };
        Assert.IsTrue(SvnProductTransferService.IsRetryableSvnFailure(frames));

        // Recognised by message alone too, whatever category SharpSvn reports.
        var messagesOnly = frames.Select(frame => frame with { Category = string.Empty, ErrorCode = 0 }).ToArray();
        Assert.IsTrue(SvnProductTransferService.IsRetryableSvnFailure(messagesOnly));
    }

    [DataTestMethod]
    [DataRow("SvnRepositoryIOException", "", 0, "Could not read chunk size")]
    [DataRow("SvnException", "", 175012, "Connection timed out")]
    [DataRow("SvnException", "", 0, "An existing connection was forcibly closed; connection reset by peer")]
    [DataRow("SvnException", "", 0, "Unexpected HTTP status 503 'Service Unavailable'")]
    [DataRow("SvnException", "RepositoryAccessSerf", 0, "Serf stream bucket error")]
    public void DroppedOrTimedOutConnectionsAreRetried(string type, string category, int code, string message) =>
        Assert.IsTrue(SvnProductTransferService.IsRetryableSvnFailure(new[] { new Frame(type, category, code, message) }));

    [DataTestMethod]
    [DataRow("Authentication failed for user")]
    [DataRow("Unexpected HTTP status 403 'Forbidden'")]
    [DataRow("Unexpected HTTP status 404 'Not Found'")]
    [DataRow("Server SSL certificate verification failed: certificate is not trusted")]
    [DataRow("There is not enough space on the disk.")]
    public void PermanentFailuresStopEvenOnARepositoryConnectionError(string inner)
    {
        var frames = new[]
        {
            new Frame("SvnRepositoryIOException", "RepositoryAccess", 170013, "Unable to connect to a repository"),
            new Frame("SvnException", "", 0, inner)
        };
        Assert.IsFalse(SvnProductTransferService.IsRetryableSvnFailure(frames),
            "Retrying cannot fix credentials, missing paths, certificates or a full disk.");
    }

    [TestMethod]
    public void LocalWorkingCopyErrorsAreNotRetried() =>
        Assert.IsFalse(SvnProductTransferService.IsRetryableSvnFailure(new[]
        {
            new Frame("SvnException", "WorkingCopy", 155004, "Working copy is locked")
        }));

    [TestMethod]
    public void LargeTransfersGetFiveAttemptsWithGrowingWaits()
    {
        Assert.AreEqual(5, SvnProductTransferService.MaxSvnTransferAttempts);
        CollectionAssert.AreEqual(
            new[] { 5d, 10d, 20d, 30d },
            Enumerable.Range(1, 4).Select(attempt => SvnProductTransferService.RetryDelay(attempt).TotalSeconds).ToArray());
    }

    [TestMethod]
    public void FailureChainKeepsSvnCategoryCodeAndMessage()
    {
        var chain = SvnProductTransferService.DescribeFailureChain(
            new InvalidOperationException("outer", new TimeoutException("inner timed out")));
        Assert.AreEqual(2, chain.Count);
        Assert.AreEqual("InvalidOperationException", chain[0].TypeName);
        Assert.AreEqual("inner timed out", chain[1].Message);
        Assert.AreEqual(0, chain[1].ErrorCode, "Non-SVN exceptions carry no SVN code.");
    }
}
