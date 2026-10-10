// Copyright (c) Alexandre Mutel. All rights reserved.
// Licensed under the BSD-Clause 2 license.
// See license.txt file in the project root for full license information.

using System.Text;
using XenoAtom.Terminal;
using XenoAtom.Terminal.Backends;
using XenoAtom.Terminal.UI.Geometry;
using XenoAtom.Terminal.UI.Rendering;

namespace XenoAtom.Terminal.UI.Tests;

[TestClass]
public sealed class CellBufferDiffRendererTests
{
    [TestMethod]
    public void RenderFullscreen_RestoresCursor_EvenWhenCursorDidNotMove()
    {
        var backend = new InMemoryTerminalBackend(new TerminalSize(6, 1));
        using var session = Terminal.Open(backend, new TerminalOptions { ImplicitStartInput = true }, force: true);
        using var renderer = new CellBufferDiffRenderer();

        var buffer = new CellBuffer(6, 1);
        buffer.Clear(Style.None);
        buffer.SetCell(0, 0, new Rune('A'), Style.None);

        renderer.RenderFullscreen(session.Instance, buffer, wantsCursor: true, cursorX: 0, cursorY: 0);
        var lengthAfterFirst = backend.GetOutText().Length;

        buffer.SetCell(1, 0, new Rune('B'), Style.None);
        renderer.RenderFullscreen(session.Instance, buffer, wantsCursor: true, cursorX: 0, cursorY: 0);

        var delta = backend.GetOutText().Substring(lengthAfterFirst);
        StringAssert.Contains(delta, "\x1b[?25h");
    }

    /// <summary>
    /// An invalidated region must be rewritten even when the buffer is byte-identical to the last
    /// emitted frame. This is the repair path for a terminal that overwrote cells behind the
    /// renderer's back (for example an IME composition overlay restoring cells without colors).
    /// </summary>
    [TestMethod]
    public void InvalidateRegion_RewritesRegion_WhenBufferIsUnchanged()
    {
        var backend = new InMemoryTerminalBackend(new TerminalSize(6, 2));
        using var session = Terminal.Open(backend, new TerminalOptions { ImplicitStartInput = true }, force: true);
        using var renderer = new CellBufferDiffRenderer();

        var buffer = new CellBuffer(6, 2);
        buffer.Clear(Style.None.WithBackground(Colors.Red));
        buffer.SetCell(0, 0, new Rune('A'), Style.None);
        buffer.SetCell(5, 1, new Rune('B'), Style.None);

        renderer.RenderFullscreen(session.Instance, buffer, wantsCursor: false, cursorX: 0, cursorY: 0);
        var afterFirst = backend.GetOutText().Length;

        // Nothing changed: a plain diff would emit nothing at all.
        renderer.RenderFullscreen(session.Instance, buffer, wantsCursor: false, cursorX: 0, cursorY: 0);
        var afterUnchanged = backend.GetOutText().Length;
        Assert.AreEqual(afterFirst, afterUnchanged, "an unchanged frame must not emit anything");

        renderer.InvalidateRegion(new Rectangle(2, 1, 2, 1));
        renderer.RenderFullscreen(session.Instance, buffer, wantsCursor: false, cursorX: 0, cursorY: 0);
        var delta = backend.GetOutText().Substring(afterUnchanged);

        // The region is rewritten (row 2, columns 3-4 in 1-based ANSI coordinates) and nothing else.
        StringAssert.Contains(delta, "\x1b[2;3H");
        Assert.IsFalse(delta.Contains("\x1b[1;"), $"cells outside the region must not be rewritten: {delta}");
        Assert.IsFalse(delta.Contains("\x1b[2;1H"), $"cells left of the region must not be rewritten: {delta}");
    }

    /// <summary>
    /// A region that was never invalidated must keep behaving like a plain diff: only real changes
    /// are emitted. Guards against the forced-region check leaking into normal frames.
    /// </summary>
    [TestMethod]
    public void InvalidateRegion_IsConsumed_ByTheRenderThatAppliesIt()
    {
        var backend = new InMemoryTerminalBackend(new TerminalSize(4, 1));
        using var session = Terminal.Open(backend, new TerminalOptions { ImplicitStartInput = true }, force: true);
        using var renderer = new CellBufferDiffRenderer();

        var buffer = new CellBuffer(4, 1);
        buffer.Clear(Style.None);

        renderer.RenderFullscreen(session.Instance, buffer, wantsCursor: false, cursorX: 0, cursorY: 0);
        var afterFirst = backend.GetOutText().Length;

        renderer.InvalidateRegion(new Rectangle(0, 0, 4, 1));
        renderer.RenderFullscreen(session.Instance, buffer, wantsCursor: false, cursorX: 0, cursorY: 0);
        var afterInvalidated = backend.GetOutText().Length;
        Assert.AreNotEqual(afterFirst, afterInvalidated, "the invalidated region should have been rewritten");

        // The next frame must be silent again: the region was consumed.
        renderer.RenderFullscreen(session.Instance, buffer, wantsCursor: false, cursorX: 0, cursorY: 0);
        Assert.AreEqual(afterInvalidated, backend.GetOutText().Length, "an invalidated region must not repeat every frame");
    }

    /// <summary>
    /// A wide glyph straddling the left edge of an invalidated region must be rewritten from its lead
    /// cell, otherwise the terminal is left with a half-wide character.
    /// </summary>
    [TestMethod]
    public void InvalidateRegion_ExpandsLeft_ToIncludeWideGlyphLead()
    {
        var backend = new InMemoryTerminalBackend(new TerminalSize(6, 1));
        using var session = Terminal.Open(backend, new TerminalOptions { ImplicitStartInput = true }, force: true);
        using var renderer = new CellBufferDiffRenderer();

        var buffer = new CellBuffer(6, 1);
        buffer.Clear(Style.None);
        // Column 1 is a wide glyph lead, column 2 its continuation.
        buffer.SetCell(1, 0, new Rune('中'), Style.None);

        renderer.RenderFullscreen(session.Instance, buffer, wantsCursor: false, cursorX: 0, cursorY: 0);
        var afterFirst = backend.GetOutText().Length;

        // Invalidate starting at the continuation cell only.
        renderer.InvalidateRegion(new Rectangle(2, 0, 2, 1));
        renderer.RenderFullscreen(session.Instance, buffer, wantsCursor: false, cursorX: 0, cursorY: 0);
        var delta = backend.GetOutText().Substring(afterFirst);

        // The run must start at column 2 (1-based), i.e. the lead cell of the wide glyph.
        StringAssert.Contains(delta, "\x1b[1;2H");
        Assert.IsFalse(delta.Contains("\x1b[1;3H"), $"the run must not start on the continuation cell: {delta}");
    }

    /// <summary>
    /// Multiple invalidated regions are unioned into their bounding box, and degenerate regions are
    /// ignored. Pinned because the bounding box is wider than the individual regions: two small
    /// regions on different rows still rewrite the whole rectangle between them.
    /// </summary>
    [TestMethod]
    public void InvalidateRegion_UnionsRegionsIntoBoundingBox_AndIgnoresEmptyOnes()
    {
        var backend = new InMemoryTerminalBackend(new TerminalSize(8, 2));
        using var session = Terminal.Open(backend, new TerminalOptions { ImplicitStartInput = true }, force: true);
        using var renderer = new CellBufferDiffRenderer();

        var buffer = new CellBuffer(8, 2);
        buffer.Clear(Style.None);

        renderer.RenderFullscreen(session.Instance, buffer, wantsCursor: false, cursorX: 0, cursorY: 0);
        var afterFirst = backend.GetOutText().Length;

        // Degenerate regions are ignored.
        renderer.InvalidateRegion(new Rectangle(0, 0, 0, 1));
        renderer.InvalidateRegion(new Rectangle(1, 0, 1, 0));
        renderer.InvalidateRegion(new Rectangle(2, 0, 1, 1));
        renderer.InvalidateRegion(new Rectangle(5, 1, 1, 1));
        renderer.RenderFullscreen(session.Instance, buffer, wantsCursor: false, cursorX: 0, cursorY: 0);
        var delta = backend.GetOutText().Substring(afterFirst);

        // Bounding box is (2,0,4,2): both rows rewrite columns 3..6 (1-based).
        StringAssert.Contains(delta, "\x1b[1;3H");
        StringAssert.Contains(delta, "\x1b[2;3H");
        Assert.IsFalse(delta.Contains("\x1b[1;1H"), $"columns left of the box must not be rewritten: {delta}");
        Assert.IsFalse(delta.Contains("\x1b[1;7H"), $"columns right of the box must not be rewritten: {delta}");
    }
}

