using System;
using System.Reflection;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace MCPanel.Tests;

public sealed partial class ReliabilityTests
{
    [TestMethod]
    public void RuntimeMonitoringUsesPageAwareLowFrequencyCadence()
    {
        var type = typeof(MainWindow);
        var flags = BindingFlags.NonPublic | BindingFlags.Static;
        var environmentValue = type
            .GetField("EnvironmentRuntimeRefreshInterval", flags)?
            .GetValue(null);
        var backgroundValue = type
            .GetField("BackgroundRuntimeRefreshInterval", flags)?
            .GetValue(null);

        Assert.IsInstanceOfType(environmentValue, typeof(TimeSpan));
        Assert.IsInstanceOfType(backgroundValue, typeof(TimeSpan));
        var environment = (TimeSpan)environmentValue!;
        var background = (TimeSpan)backgroundValue!;

        Assert.IsTrue(environment >= TimeSpan.FromSeconds(5),
            "Environment runtime discovery must not return to high-frequency polling.");
        Assert.IsTrue(background >= TimeSpan.FromSeconds(15),
            "Background pages must use a substantially slower runtime discovery cadence.");
        Assert.IsTrue(background > environment,
            "The Environment page may refresh faster than unrelated pages, not the other way around.");
    }
}
