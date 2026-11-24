using System;
using System.Windows;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace MCPanel.Tests;

public sealed partial class ReliabilityTests
{
    private const double Phi = 1.6180339887498949;

    /// <summary>
    /// Work areas in DIPs (screen minus a 40-48 px taskbar, divided by the
    /// display scale) for common setups.
    /// </summary>
    [DataTestMethod]
    [DataRow(1920d, 1040d, DisplayName = "1080p at 100%")]
    [DataRow(1536d, 824d, DisplayName = "1080p at 125%")]
    [DataRow(2560d, 1400d, DisplayName = "1440p at 100%")]
    [DataRow(2048d, 1112d, DisplayName = "1440p at 125%")]
    [DataRow(2560d, 1392d, DisplayName = "4K at 150%")]
    [DataRow(1920d, 1032d, DisplayName = "4K at 200%")]
    [DataRow(1680d, 1010d, DisplayName = "1680x1050 at 100%")]
    public void DefaultWindowIsAGoldenRectangleCoveringAGoldenShareOfTheScreen(double workWidth, double workHeight)
    {
        var workArea = new Rect(0, 0, workWidth, workHeight);
        var bounds = ResponsiveWindowSizing.GoldenBounds(workArea);

        Assert.AreEqual(Phi, bounds.Width / bounds.Height, 0.01d, "The window must be a φ:1 golden rectangle.");
        Assert.AreEqual(1d / Phi, bounds.Width * bounds.Height / (workWidth * workHeight), 0.01d,
            "The window must cover 1/φ ≈ 61.8% of the work area on every common monitor.");
        AssertGoldenPlacement(workArea, bounds);
    }

    [TestMethod]
    public void DefaultWindowKeepsTheSameScreenShareAcrossResolutions()
    {
        var full = ResponsiveWindowSizing.GoldenBounds(new Rect(0, 0, 1920, 1040));
        var quad = ResponsiveWindowSizing.GoldenBounds(new Rect(0, 0, 2560, 1400));
        Assert.AreEqual(full.Width / 1920d, quad.Width / 2560d, 0.01d, "Width share must not depend on resolution.");
        Assert.AreEqual(full.Height / 1040d, quad.Height / 1400d, 0.01d, "Height share must not depend on resolution.");
    }

    [DataTestMethod]
    [DataRow(1366d, 728d, true, DisplayName = "1366x768 laptop")]
    [DataRow(1280d, 680d, true, DisplayName = "1280x720")]
    [DataRow(1093d, 547d, false, DisplayName = "1366x768 at 125%")]
    public void SmallScreensGetAComfortableGoldenWindowThatStillFits(double workWidth, double workHeight, bool golden)
    {
        var workArea = new Rect(0, 0, workWidth, workHeight);
        var bounds = ResponsiveWindowSizing.GoldenBounds(workArea);

        if (golden)
        {
            Assert.AreEqual(Phi, bounds.Width / bounds.Height, 0.01d, "Small but not tiny screens still get a golden rectangle.");
        }

        Assert.IsTrue(bounds.Width >= Math.Min(ResponsiveWindowSizing.ComfortableMinimumWidth, workWidth * ResponsiveWindowSizing.MaximumWorkAreaFill) - 1d,
            $"Small screens must get a usable window, got {bounds.Width}x{bounds.Height}.");
        AssertGoldenPlacement(workArea, bounds);
    }

    [DataTestMethod]
    [DataRow(3440d, 1400d, DisplayName = "Ultrawide 3440x1440")]
    [DataRow(1080d, 1880d, DisplayName = "Portrait 1080x1920")]
    [DataRow(5120d, 1400d, DisplayName = "Super ultrawide 5120x1440")]
    public void UnusualScreensKeepTheGoldenAspectInsideTheWorkArea(double workWidth, double workHeight)
    {
        var workArea = new Rect(0, 0, workWidth, workHeight);
        var bounds = ResponsiveWindowSizing.GoldenBounds(workArea);

        Assert.AreEqual(Phi, bounds.Width / bounds.Height, 0.01d);
        AssertGoldenPlacement(workArea, bounds);
    }

    [TestMethod]
    public void GoldenWindowFollowsAWorkAreaThatDoesNotStartAtTheOrigin()
    {
        // A secondary monitor left of the primary one, with a top taskbar.
        var workArea = new Rect(-1920, 40, 1920, 1040);
        var bounds = ResponsiveWindowSizing.GoldenBounds(workArea);
        AssertGoldenPlacement(workArea, bounds);
    }

    private static void AssertGoldenPlacement(Rect workArea, Rect bounds)
    {
        Assert.IsTrue(bounds.Left >= workArea.Left && bounds.Top >= workArea.Top &&
                      bounds.Right <= workArea.Right && bounds.Bottom <= workArea.Bottom,
            $"Window {bounds} must stay inside the work area {workArea}.");
        Assert.IsTrue(bounds.Width <= workArea.Width * ResponsiveWindowSizing.MaximumWorkAreaFill + 1d &&
                      bounds.Height <= workArea.Height * ResponsiveWindowSizing.MaximumWorkAreaFill + 1d,
            "The window must leave a margin around it.");
        Assert.AreEqual(bounds.Left - workArea.Left, workArea.Right - bounds.Right, 1d, "The window must be centred horizontally.");
        var above = bounds.Top - workArea.Top;
        var below = workArea.Bottom - bounds.Bottom;
        Assert.AreEqual((above + below) / (Phi * Phi), above, 1d,
            "The window must sit on the upper golden section (38.2% above, 61.8% below), to the pixel.");
    }
}
