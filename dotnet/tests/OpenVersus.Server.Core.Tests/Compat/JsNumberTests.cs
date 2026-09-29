using OpenVersus.Server.Core.Compat;

namespace OpenVersus.Server.Core.Tests.Compat;

/// <summary>Js.ParseInt and Js.SliceBounds against what node gives for the same inputs.</summary>
public sealed class JsNumberTests
{
    [Theory]
    [InlineData("100", 100)]
    [InlineData("  7x", 7)]
    [InlineData("+5", 5)]
    [InlineData("-3", -3)]
    [InlineData("5,9", 5)]
    [InlineData("1e3", 1)]
    [InlineData("2.9", 2)]
    [InlineData("0x1A", 26)]
    [InlineData("-0X10", -16)]
    [InlineData("﻿ 12", 12)]
    [InlineData("99999999999", 99999999999)]
    [InlineData("12345678901234567890", 12345678901234567000)]
    public void ParseIntAsNodeReadsIt(string text, double expected) => Assert.Equal(expected, Js.ParseInt(text));

    [Theory]
    [InlineData("abc")]
    [InlineData("")]
    [InlineData("0x")]
    [InlineData("-")]
    [InlineData(null)]
    public void ParseIntWithNoDigitsIsNaN(string? text) => Assert.True(double.IsNaN(Js.ParseInt(text)));

    [Theory]
    [InlineData("964", 964)]
    [InlineData("  964 ", 964)]
    [InlineData("", 0)]
    [InlineData("0x3C4", 964)]
    [InlineData("0o17", 15)]
    [InlineData("0b101", 5)]
    [InlineData("1e3", 1000)]
    [InlineData("+5", 5)]
    [InlineData(".5", 0.5)]
    [InlineData("5.", 5)]
    [InlineData("1.5e-2", 0.015)]
    [InlineData("\uFEFF7", 7)]
    [InlineData("Infinity", double.PositiveInfinity)]
    [InlineData("-Infinity", double.NegativeInfinity)]
    public void NumberAsNodeReadsAString(string text, double expected) => Assert.Equal(expected, Js.Number(text));

    [Theory]
    [InlineData("-0x10")]
    [InlineData("abc")]
    [InlineData("12abc")]
    [InlineData("1_000")]
    [InlineData(null)]
    public void NumberOfNoNumberIsNaN(string? text) => Assert.True(double.IsNaN(Js.Number(text)));

    [Theory]
    [InlineData(0, 10, 0, 10)]
    [InlineData(-3, double.PositiveInfinity, 7, 10)]
    [InlineData(0, -5, 0, 5)]
    [InlineData(-100, 3, 0, 3)]
    [InlineData(2.9, 5.5, 2, 5)]
    [InlineData(double.NaN, 4, 0, 4)]
    [InlineData(double.NegativeInfinity, 2, 0, 2)]
    [InlineData(5, 1e20, 5, 10)]
    public void SliceOfTen(double start, double end, int from, int to) => Assert.Equal((from, to), Js.SliceBounds(10, start, end));

    [Theory]
    [InlineData(10, 20)]
    [InlineData(3, 1)]
    public void EmptySlices(double start, double end)
    {
        var (from, to) = Js.SliceBounds(10, start, end);
        Assert.Equal(from, to);
    }
}
