using TextTemplateManager;
using Xunit;

namespace TextTemplateManager.Tests;

/// <summary>Arrow-key movement through the Quick Paste shortcut lists. The Quick Paste window is driven
/// entirely by the keyboard while another application has focus, so walking off either end of a list is
/// something a user hits constantly — it has to come back round rather than stop.</summary>
public class PasteWindowNavigationTests
{
    [Theory]
    [InlineData(0, 1, 5, 1)]
    [InlineData(3, 1, 5, 4)]
    [InlineData(4, -1, 5, 3)]
    [InlineData(1, -1, 5, 0)]
    public void Stepping_within_the_list_moves_one_place(int current, int delta, int count, int expected)
    {
        Assert.Equal(expected, PasteWindow.NextIndex(current, delta, count));
    }

    [Fact]
    public void Down_from_the_last_item_wraps_to_the_first()
    {
        Assert.Equal(0, PasteWindow.NextIndex(current: 4, delta: 1, count: 5));
    }

    [Fact]
    public void Up_from_the_first_item_wraps_to_the_last()
    {
        Assert.Equal(4, PasteWindow.NextIndex(current: 0, delta: -1, count: 5));
    }

    [Fact]
    public void With_nothing_selected_down_starts_at_the_top_and_up_at_the_bottom()
    {
        // Both lists normally open with something selected — the single-key list on its first item, and
        // the multi-key list on the top match as soon as a buffer is typed. The unselected case is the
        // narrow one: arrowing the multi-key list while ALT is held but nothing typed yet, where
        // RefreshMultiKeyList has left it at -1.
        Assert.Equal(0, PasteWindow.NextIndex(current: -1, delta: 1, count: 5));
        Assert.Equal(4, PasteWindow.NextIndex(current: -1, delta: -1, count: 5));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(-1)]
    public void A_single_item_list_stays_on_that_item(int delta)
    {
        Assert.Equal(0, PasteWindow.NextIndex(current: 0, delta: delta, count: 1));
    }

    [Theory]
    [InlineData(0, 1)]
    [InlineData(-1, -1)]
    public void An_empty_list_selects_nothing(int current, int delta)
    {
        // Guards the caller: -1 means "leave the selection alone" rather than selecting index 0.
        Assert.Equal(-1, PasteWindow.NextIndex(current, delta, count: 0));
    }

    [Fact]
    public void Wrapping_holds_for_a_step_larger_than_the_list()
    {
        // Not reachable from the arrow keys today, but the modulo has to stay in range regardless of
        // what a future page-up/down step passes in.
        Assert.Equal(1, PasteWindow.NextIndex(current: 0, delta: 4, count: 3));
        Assert.Equal(2, PasteWindow.NextIndex(current: 0, delta: -4, count: 3));
    }

    [Fact]
    public void Down_from_the_end_of_the_search_box_steps_into_the_tree()
    {
        // Caret collapsed at the end, tree has rows -> hand off to the tree.
        Assert.Equal(PasteWindow.SearchDown.EnterTree,
            PasteWindow.DecideSearchDown(caret: 4, selectionLength: 0, textLength: 4, treeNodeCount: 3));
    }

    [Fact]
    public void Down_from_an_empty_search_box_steps_into_the_tree()
    {
        // An empty box is "at the end" (caret 0 == length 0), so Down goes straight in with no filter.
        Assert.Equal(PasteWindow.SearchDown.EnterTree,
            PasteWindow.DecideSearchDown(caret: 0, selectionLength: 0, textLength: 0, treeNodeCount: 3));
    }

    [Fact]
    public void Down_from_mid_string_only_moves_the_caret_to_the_end()
    {
        Assert.Equal(PasteWindow.SearchDown.MoveCaretToEnd,
            PasteWindow.DecideSearchDown(caret: 2, selectionLength: 0, textLength: 5, treeNodeCount: 3));
    }

    [Fact]
    public void Down_with_text_selected_collapses_to_the_end_rather_than_entering_the_tree()
    {
        // A live selection (even one that reaches the end) collapses first — a second Down then enters.
        Assert.Equal(PasteWindow.SearchDown.MoveCaretToEnd,
            PasteWindow.DecideSearchDown(caret: 0, selectionLength: 5, textLength: 5, treeNodeCount: 3));
    }

    [Fact]
    public void Down_stays_in_the_search_box_when_the_tree_is_empty()
    {
        // At the end but nothing to land on -> stay put (re-pin the caret to the end).
        Assert.Equal(PasteWindow.SearchDown.MoveCaretToEnd,
            PasteWindow.DecideSearchDown(caret: 4, selectionLength: 0, textLength: 4, treeNodeCount: 0));
    }
}
