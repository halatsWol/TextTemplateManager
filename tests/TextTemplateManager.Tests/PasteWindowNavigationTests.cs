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
}
