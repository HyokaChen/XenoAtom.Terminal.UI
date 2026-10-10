// Copyright (c) Alexandre Mutel. All rights reserved.
// Licensed under the BSD-Clause 2 license.
// See license.txt file in the project root for full license information.

using XenoAtom.Terminal.UI.Controls;
using XenoAtom.Terminal.UI.Geometry;
using XenoAtom.Terminal.UI.Styling;

namespace XenoAtom.Terminal.UI.Tests;

/// <summary>
/// Tests for <see cref="TerminalApp.RequestRegionRender"/>, the targeted counterpart to
/// <see cref="TerminalApp.RequestFullRender"/>. The motivating scenario is a terminal that
/// overwrites cells behind the renderer's back (for example an IME composition overlay that
/// restores cells without their colors): the application buffer is correct, so a diff-only
/// render emits nothing, and only rewriting the damaged region can repair it.
/// </summary>
[TestClass]
public sealed class TerminalAppRegionRenderTests
{
    [TestMethod]
    public void RequestRegionRender_RewritesOnlyTheRegion()
    {
        using var driver = new TerminalAppTestDriver(new TextBlock { Text = "hello" }, size: new TerminalSize(20, 5));
        driver.TickUntil(() => driver.App.Root.Bounds.Width > 0);

        var backend = driver.Backend;
        var before = backend.GetOutText().Length;

        // Nothing changed: settle the loop so a plain diff emits nothing.
        driver.Tick(5);
        var settled = backend.GetOutText().Length;
        Assert.AreEqual(before, settled, "an unchanged frame should not emit anything");

        // Damage a region outside of any content (the right half of the screen).
        driver.App.RequestRegionRender(new Rectangle(10, 0, 10, 5));
        driver.Tick(5);

        var delta = backend.GetOutText().Substring(settled);

        // The region is rewritten, and the left half is untouched.
        StringAssert.Contains(delta, "\x1b[1;11H");
        Assert.IsFalse(delta.Contains("\x1b[1;1H"), $"cells outside the region must not be rewritten: {delta}");
    }

    [TestMethod]
    public void RequestRegionRender_DoesNotEraseTheWholeScreen()
    {
        using var driver = new TerminalAppTestDriver(new TextBlock { Text = "hello" }, size: new TerminalSize(20, 5));
        driver.TickUntil(() => driver.App.Root.Bounds.Width > 0);

        driver.Tick(5);
        var settled = driver.Backend.GetOutText().Length;

        driver.App.RequestRegionRender(new Rectangle(0, 0, 4, 1));
        driver.Tick(5);

        var delta = driver.Backend.GetOutText().Substring(settled);

        // A full repaint starts with a clear-screen (ED 2); a region render must not.
        Assert.IsFalse(delta.Contains("\x1b[2J"), $"a region render must not clear the screen: {delta}");
    }

    [TestMethod]
    public void RequestRegionRender_IsIgnored_ForEmptyRegion()
    {
        using var driver = new TerminalAppTestDriver(new TextBlock { Text = "hello" }, size: new TerminalSize(20, 5));
        driver.TickUntil(() => driver.App.Root.Bounds.Width > 0);

        driver.Tick(5);
        var settled = driver.Backend.GetOutText().Length;

        driver.App.RequestRegionRender(new Rectangle(5, 5, 0, 0));
        driver.Tick(5);

        Assert.AreEqual(settled, driver.Backend.GetOutText().Length, "an empty region must be a no-op");
    }
}
