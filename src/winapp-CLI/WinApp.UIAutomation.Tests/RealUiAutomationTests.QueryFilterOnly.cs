// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using Microsoft.Windows.SDK.BuildTools.WinApp.UIAutomation.TestSupport;

namespace Microsoft.Windows.SDK.BuildTools.WinApp.UIAutomation.Tests;

public partial class RealUiAutomationTests
{
    [TestMethod]
    public async Task QueryFilterOnly_ClassNameAlone_MatchesEveryElementOfThatClass()
    {
        using var fx = new UiaTestFixture();
        var svc = NewService();
        var target = SessionFor(fx);
        var slider = await ResolveAsync(svc, target, "trkSlider");
        var button = await ResolveAsync(svc, target, "btnInvoke");

        var unique = new UiSelector { ClassName = slider.ClassName!.ToUpperInvariant() };
        var sliderMatches = await svc.SearchAsync(target, unique, 50, CancellationToken.None);
        Assert.HasCount(1, sliderMatches);
        Assert.AreEqual("trkSlider", sliderMatches[0].AutomationId);
        Assert.AreEqual("trkSlider",
            (await svc.FindSingleElementAsync(target, unique, requireUnique: true, CancellationToken.None))?.AutomationId);

        var shared = new UiSelector { ClassName = button.ClassName };
        var buttonMatches = await svc.SearchAsync(target, shared, 50, CancellationToken.None);
        Assert.IsTrue(buttonMatches.Length > 1, "The fixture has several buttons with the same class.");
        Assert.IsTrue(buttonMatches.All(m => string.Equals(m.ClassName, button.ClassName, StringComparison.OrdinalIgnoreCase)));
        Assert.IsTrue(buttonMatches.Any(m => m.AutomationId == "btnInvoke"));
        await Assert.ThrowsAsync<UiAmbiguousSelectorException>(() =>
            svc.FindSingleElementAsync(target, shared, requireUnique: true, CancellationToken.None));
        await Assert.ThrowsAsync<UiAmbiguousSelectorException>(() =>
            svc.FindSingleElementAsync(target, shared, CancellationToken.None));

        Assert.IsEmpty(await svc.SearchAsync(target, new UiSelector { ClassName = "*" }, 50, CancellationToken.None));
    }

    [TestMethod]
    public async Task QueryFilterOnly_TypeAloneUnderRoot_MatchesOnlyThatSubtree()
    {
        using var fx = new UiaTestFixture();
        var svc = NewService();
        var target = SessionFor(fx);
        await ResolveAsync(svc, target, "lblInsideInvoke");

        var query = new UiSelector { Root = new UiSelector { Query = "pnlInsideInvoke" }, ControlType = "TextBlock" };
        var matches = await svc.SearchAsync(target, query, 50, CancellationToken.None);
        Assert.HasCount(1, matches);
        Assert.AreEqual("lblInsideInvoke", matches[0].AutomationId);
        Assert.AreEqual("lblInsideInvoke",
            (await svc.FindSingleElementAsync(target, query, requireUnique: true, CancellationToken.None))?.AutomationId);
        Assert.IsEmpty(await svc.SearchAsync(target, query with { ControlType = "Button" }, 50, CancellationToken.None));
    }
}
