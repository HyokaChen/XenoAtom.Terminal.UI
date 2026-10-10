// Copyright (c) Alexandre Mutel. All rights reserved.
// Licensed under the BSD-Clause 2 license.
// See license.txt file in the project root for full license information.

using XenoAtom.Terminal.UI.Geometry;
using XenoAtom.Terminal.UI.Rendering;

namespace XenoAtom.Terminal.UI.Tests;

/// <summary>
/// Tests for <see cref="TerminalApp.ExpandDirtyRectAroundWideGlyphs"/>, the alignment that keeps
/// <see cref="CellBuffer.ClearCurrentClip"/> from clearing a cell it will never repaint.
/// </summary>
[TestClass]
public sealed class DirtyRectWideGlyphTests
{
    /// <summary>
    /// A wide glyph sitting on the left edge of the dirty rect: the rect starts on the continuation
    /// cell, so the lead one column to the left is outside. Without the alignment that lead is
    /// cleared (as the continuation halo) and never repainted, leaving a permanent 1-cell block of
    /// the base background.
    /// </summary>
    [TestMethod]
    public void LeftEdge_OnContinuation_ExpandsToIncludeLead()
    {
        var buffer = new CellBuffer(10, 1);
        buffer.Clear(Style.None);
        // Column 2 is a wide lead, column 3 its continuation.
        buffer.SetCell(2, 0, new Rune('中'), Style.None);

        var rect = new Rectangle(3, 0, 2, 1);
        var aligned = TerminalApp.ExpandDirtyRectAroundWideGlyphs(buffer, rect);

        Assert.AreEqual(2, aligned.X, "the lead of the wide glyph must be inside the rect");
        Assert.AreEqual(3, aligned.Width, "the rect must still cover its original columns");
    }

    /// <summary>
    /// The mirror case: the rect ends just before a continuation cell, so the wide lead is the last
    /// column inside and its continuation is outside.
    /// </summary>
    [TestMethod]
    public void RightEdge_BeforeContinuation_ExpandsToIncludeContinuation()
    {
        var buffer = new CellBuffer(10, 1);
        buffer.Clear(Style.None);
        // Column 3 is a wide lead, column 4 its continuation.
        buffer.SetCell(3, 0, new Rune('中'), Style.None);

        var rect = new Rectangle(1, 0, 3, 1); // covers columns 1..3, right edge exclusive at 4
        var aligned = TerminalApp.ExpandDirtyRectAroundWideGlyphs(buffer, rect);

        Assert.AreEqual(4, aligned.Width, "the continuation of the wide glyph must be inside the rect");
    }

    /// <summary>
    /// The alignment must be inert when the boundary does not cut a glyph, so the common case keeps
    /// its exact rectangle and does not pay for a wider repaint.
    /// </summary>
    [TestMethod]
    public void NarrowGlyphBoundary_LeavesRectangleUnchanged()
    {
        var buffer = new CellBuffer(10, 1);
        buffer.Clear(Style.None);
        buffer.SetCell(0, 0, new Rune('a'), Style.None);
        buffer.SetCell(4, 0, new Rune('b'), Style.None);

        var rect = new Rectangle(2, 0, 2, 1);
        var aligned = TerminalApp.ExpandDirtyRectAroundWideGlyphs(buffer, rect);

        Assert.AreEqual(rect, aligned);
    }

    /// <summary>
    /// The alignment is per row: a glyph straddling the boundary on any row of the rect forces the
    /// expansion, because the rect covers all of them.
    /// </summary>
    [TestMethod]
    public void StraddlingGlyph_OnAnyRow_Expands()
    {
        var buffer = new CellBuffer(10, 3);
        buffer.Clear(Style.None);
        // Row 2 (middle) has a glyph straddling the left edge at column 3.
        buffer.SetCell(2, 1, new Rune('中'), Style.None);

        var rect = new Rectangle(3, 0, 2, 3);
        var aligned = TerminalApp.ExpandDirtyRectAroundWideGlyphs(buffer, rect);

        Assert.AreEqual(2, aligned.X);
        Assert.AreEqual(3, aligned.Width);
    }

    /// <summary>
    /// A degenerate rectangle is returned as-is rather than being expanded into something invalid.
    /// </summary>
    [TestMethod]
    public void EmptyRectangle_IsReturnedUnchanged()
    {
        var buffer = new CellBuffer(10, 1);
        buffer.Clear(Style.None);
        buffer.SetCell(2, 0, new Rune('中'), Style.None);

        var rect = new Rectangle(3, 0, 0, 0);
        var aligned = TerminalApp.ExpandDirtyRectAroundWideGlyphs(buffer, rect);

        Assert.AreEqual(rect, aligned);
    }

    /// <summary>
    /// An edge already at the screen border must not be pushed out of the buffer.
    /// </summary>
    [TestMethod]
    public void EdgeAtScreenBorder_IsNotExpandedPastBuffer()
    {
        var buffer = new CellBuffer(4, 1);
        buffer.Clear(Style.None);
        // Wide glyph occupying the last two columns; the continuation is the final column.
        buffer.SetCell(2, 0, new Rune('中'), Style.None);

        var aligned = TerminalApp.ExpandDirtyRectAroundWideGlyphs(buffer, new Rectangle(3, 0, 1, 1));

        Assert.AreEqual(2, aligned.X);
        Assert.IsTrue(aligned.Right <= buffer.Width, $"rect must stay inside the buffer, got {aligned}");
    }

    /// <summary>
    /// End-to-end: hovering a control sitting right next to a wide glyph used to destroy the glyph.
    /// <para>
    /// The hover changes only the button style, so the frame goes through <c>SceneRenderMode.Dirty</c>.
    /// The dirty rect (button bounds expanded by one cell) starts on the continuation cell of the
    /// glyph, so <see cref="CellBuffer.ClearCurrentClip"/> cleared the lead one column to the left —
    /// outside the clip, therefore never repainted. The glyph came back as two blanks carrying the
    /// base background: a permanent black block on a dark theme, white on a light one.
    /// </para>
    /// <para>
    /// The observable signature is the emitted frame reaching back to the glyph column and writing
    /// blanks there. With the alignment the rect starts at the button, so nothing is written over
    /// the glyph and the frame is narrower.
    /// </para>
    /// </summary>
    [TestMethod]
    public void HoverNextToWideGlyph_DoesNotClobberGlyph()
    {
        var glyph = new TextBlock { Text = "中" };
        var button = new Button("x");
        var root = new HStack { Children = { glyph, button } }.Spacing(0);

        using var driver = new TerminalAppTestDriver(root, size: new TerminalSize(20, 3));
        driver.TickUntil(() => driver.App.Root.Bounds.Width > 0);

        var before = driver.Backend.GetOutText().Length;

        // Hover the button: style-only change, layout untouched.
        driver.Backend.PushEvent(new TerminalMouseEvent
        {
            Kind = TerminalMouseKind.Move,
            Button = TerminalMouseButton.None,
            X = button.Bounds.X + 1,
            Y = button.Bounds.Y,
        });
        driver.TickUntil(() => button.IsHovered);

        var delta = driver.Backend.GetOutText().Substring(before);

        // The glyph occupies the columns before the button; nothing may be written there.
        var glyphColumn = glyph.Bounds.X + 1; // 1-based ANSI column
        Assert.IsFalse(
            delta.Contains($"\u001b[1;{glyphColumn}H"),
            $"the frame reached back to column {glyphColumn} and overwrote the wide glyph with blanks. "
            + $"delta={delta}");

        // And the button column itself must still be written, i.e. the hover repaint did happen.
        var buttonColumn = button.Bounds.X + 1;
        StringAssert.Contains(delta, $"\u001b[1;{buttonColumn}H",
            $"the hovered control must still be repainted. delta={delta}");
    }
}
