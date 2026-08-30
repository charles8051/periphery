// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

using System.Globalization;

namespace Periphery.Camera;

/// <summary>
/// An exact rational number, used primarily for frame rates where decimal
/// approximation loses precision (e.g. 30000/1001 ≈ 29.97 fps).
/// </summary>
public readonly record struct Rational : IComparable<Rational>
{
    public int Numerator { get; }
    public int Denominator { get; }

    public Rational(int numerator, int denominator)
    {
        if (denominator == 0) throw new ArgumentException("Denominator cannot be zero.", nameof(denominator));
        if (denominator < 0) { numerator = -numerator; denominator = -denominator; }
        Numerator = numerator;
        Denominator = denominator;
    }

    public Rational(int wholeNumber) : this(wholeNumber, 1) { }

    public double ToDouble() => (double)Numerator / Denominator;

    public int CompareTo(Rational other) => (Numerator * (long)other.Denominator)
        .CompareTo(other.Numerator * (long)Denominator);

    public static bool operator <(Rational left, Rational right) => left.CompareTo(right) < 0;
    public static bool operator >(Rational left, Rational right) => left.CompareTo(right) > 0;
    public static bool operator <=(Rational left, Rational right) => left.CompareTo(right) <= 0;
    public static bool operator >=(Rational left, Rational right) => left.CompareTo(right) >= 0;

    public static implicit operator double(Rational r) => r.ToDouble();
    public static implicit operator Rational(int value) => new(value);

    public override string ToString() =>
        Denominator == 1 ? Numerator.ToString(CultureInfo.InvariantCulture)
                         : $"{Numerator}/{Denominator}";
}
