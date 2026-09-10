namespace Calculator.Tests;

public class MathTests
{
    [Fact]
    public void AddsTwoNumbers()
    {
        Assert.Equal(5, Math.Add(2, 3));
    }

    [Fact]
    public void AddsNegatives()
    {
        Assert.Equal(-2   ,    Math.Add(-1, -1));
    }
}
