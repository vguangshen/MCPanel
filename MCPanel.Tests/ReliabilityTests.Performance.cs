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
        var environment = (TimeSpan?)type
            .GetField("EnvironmentRuntimeRefreshInterval", flags)?
            .GetValue(null);
        var background = (TimeSpan?)type
            .GetField("BackgroundRuntimeRefreshInterval", flags)?
            .GetValue(null);

        Assert.IsNotNull(environment);
        Assert.IsNotNull(background);
        Assert.IsTrue(environment.Value >= TimeSpan.FromSeconds(5),
            "Environment runtime discovery must not return to high-frequency polling.");
        Assert.IsTrue(background.Value >= TimeSpan.FromSeconds(15),
            "Background pages must use a substantially slower runtime discovery cadence.");
        Assert.IsTrue(background.Value > environment.Value,
            "The Environment page may refresh faster than unrelated pages, not the other way around.");
    }
}
