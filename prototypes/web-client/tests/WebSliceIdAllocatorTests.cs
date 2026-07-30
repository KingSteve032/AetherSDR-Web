using AetherSDR.Web.Radio;

namespace AetherSDR.Web.Tests;

public sealed class WebSliceIdAllocatorTests
{
    [Fact]
    public void FirstOwnedRadioSliceIsAlwaysWebSliceA()
    {
        WebSliceIdAllocator allocator = new();

        Assert.Equal("A", allocator.GetOrCreate(1));
        Assert.Equal("B", allocator.GetOrCreate(4));
        Assert.Equal("A", allocator.GetOrCreate(1));
    }

    [Fact]
    public void ReleasedLabelsCanBeReusedWithoutRenamingSurvivors()
    {
        WebSliceIdAllocator allocator = new();
        Assert.Equal("A", allocator.GetOrCreate(1));
        Assert.Equal("B", allocator.GetOrCreate(2));

        allocator.Release(1);

        Assert.Equal("B", allocator.GetOrCreate(2));
        Assert.Equal("A", allocator.GetOrCreate(7));
    }

    [Fact]
    public void RadioActiveSliceBecomesThePrimaryWebSlice()
    {
        WebSliceIdAllocator allocator = new();
        Assert.Equal("A", allocator.GetOrCreate(0));

        Assert.Equal("A", allocator.GetOrCreate(1, makePrimary: true));
        Assert.Equal("B", allocator.GetOrCreate(0));
    }

    [Fact]
    public void ConnectedSessionNeverRenamesSlicesWhenRadioActiveChanges()
    {
        WebSliceIdAllocator allocator = new();
        Assert.Equal("A", allocator.GetOrCreate(0));
        Assert.Equal("B", allocator.GetOrCreate(1));
        allocator.Freeze();

        Assert.Equal("B", allocator.GetOrCreate(1, makePrimary: true));
        Assert.Equal("A", allocator.GetOrCreate(0));
        Assert.Equal("B", allocator.GetOrCreate(1));
    }

    [Fact]
    public void ResetAllowsTheNextSessionToChooseItsInitialPrimarySlice()
    {
        WebSliceIdAllocator allocator = new();
        Assert.Equal("A", allocator.GetOrCreate(0));
        allocator.Freeze();
        allocator.Reset();

        Assert.Equal("A", allocator.GetOrCreate(7, makePrimary: true));
    }
}
