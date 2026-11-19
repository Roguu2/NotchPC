using NotchPC.Core.Layout;

namespace NotchPC.Tests;

public sealed class ListOrderTests
{
    private static readonly string[] Abcd = ["a", "b", "c", "d"];

    [Fact]
    public void Move_before_and_after()
    {
        Assert.Equal(["c", "a", "b", "d"], ListOrder.Move(Abcd, "c", "a", after: false));
        Assert.Equal(["b", "c", "a", "d"], ListOrder.Move(Abcd, "a", "c", after: true));
        Assert.Equal(["a", "b", "d", "c"], ListOrder.Move(Abcd, "c", "d", after: true));
    }

    [Fact]
    public void Unknown_or_same_item_changes_nothing()
    {
        Assert.Equal(Abcd, ListOrder.Move(Abcd, "x", "a", after: false));
        Assert.Equal(Abcd, ListOrder.Move(Abcd, "a", "x", after: false));
        Assert.Equal(Abcd, ListOrder.Move(Abcd, "b", "b", after: true));
    }

    [Fact]
    public void Move_by_key_keeps_the_objects()
    {
        var items = new[] { (Id: "a", On: true), (Id: "b", On: false), (Id: "c", On: true) };
        var moved = ListOrder.MoveBy(items, x => x.Id, "c", "a", after: false);
        Assert.Equal(["c", "a", "b"], moved.Select(x => x.Id));
        Assert.False(moved[2].On);
    }
}
