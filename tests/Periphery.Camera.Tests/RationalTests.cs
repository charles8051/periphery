namespace Periphery.Camera.Tests;

public sealed class RationalTests
{
    [Fact]
    public void Constructor_WholeNumber_HasDenominatorOne()
    {
        var r = new Rational(30);
        Assert.Equal(30, r.Numerator);
        Assert.Equal(1, r.Denominator);
    }

    [Fact]
    public void Constructor_ZeroDenominator_Throws()
    {
        Assert.Throws<ArgumentException>(() => new Rational(1, 0));
    }

    [Fact]
    public void Constructor_NegativeDenominator_Normalizes()
    {
        var r = new Rational(30, -1);
        Assert.Equal(-30, r.Numerator);
        Assert.Equal(1, r.Denominator);
    }

    [Fact]
    public void ToDouble_ReturnsCorrectValue()
    {
        var r = new Rational(30000, 1001);
        Assert.Equal(30000.0 / 1001.0, r.ToDouble(), precision: 10);
    }

    [Fact]
    public void ImplicitConversion_ToDouble()
    {
        Rational r = new(30);
        double d = r;
        Assert.Equal(30.0, d);
    }

    [Fact]
    public void ImplicitConversion_FromInt()
    {
        Rational r = 30;
        Assert.Equal(30, r.Numerator);
        Assert.Equal(1, r.Denominator);
    }

    [Fact]
    public void CompareTo_OrdersCorrectly()
    {
        var r15 = new Rational(15);
        var r30 = new Rational(30);
        var r2997 = new Rational(30000, 1001);

        Assert.True(r15 < r30);
        Assert.True(r2997 < r30);
        Assert.True(r15 < r2997);
        Assert.True(r30 > r15);
        Assert.True(r30 >= new Rational(30));
        Assert.True(r15 <= new Rational(15));
    }

    [Fact]
    public void ToString_WholeNumber_OmitsDenominator()
    {
        var r = new Rational(30);
        Assert.Equal("30", r.ToString());
    }

    [Fact]
    public void ToString_Fraction_ShowsBoth()
    {
        var r = new Rational(30000, 1001);
        Assert.Equal("30000/1001", r.ToString());
    }

    [Fact]
    public void Equality_SameValue()
    {
        var a = new Rational(30, 1);
        var b = new Rational(30, 1);
        Assert.Equal(a, b);
    }

    [Fact]
    public void Equality_DifferentRepresentation_NotEqual()
    {
        // Rational does not reduce: 60/2 != 30/1 as record structs
        var a = new Rational(60, 2);
        var b = new Rational(30, 1);
        Assert.NotEqual(a, b);
        // But they compare equal
        Assert.Equal(0, a.CompareTo(b));
    }
}
