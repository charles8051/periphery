using System;
using System.IO;

namespace Periphery.Bootloader.Tests;

/// <summary>
/// <see cref="ExceptionSummary.Describe"/> is pure and total: exercise the chain shapes a device
/// stack actually produces. The motivating one is a wrapper whose own message says nothing useful
/// ("Treehopper reconcile failed.") over the inner exception that says everything.
/// </summary>
public class ExceptionSummaryTests
{
    [Fact]
    public void A_lone_exception_is_its_own_message()
        => Assert.Equal("boom", ExceptionSummary.Describe(new InvalidOperationException("boom")));

    [Fact]
    public void An_inner_cause_is_appended_with_its_type()
    {
        var ex = new InvalidOperationException("Treehopper reconcile failed.", new IOException("Access is denied."));
        Assert.Equal("Treehopper reconcile failed. -> IOException: Access is denied.", ExceptionSummary.Describe(ex));
    }

    [Fact]
    public void The_whole_chain_is_rendered_in_order()
    {
        var ex = new InvalidOperationException("outer",
            new InvalidOperationException("middle", new IOException("inner")));

        Assert.Equal("outer -> InvalidOperationException: middle -> IOException: inner", ExceptionSummary.Describe(ex));
    }

    [Fact]
    public void MaxDepth_bounds_how_many_links_are_rendered()
    {
        var ex = new InvalidOperationException("a", new InvalidOperationException("b", new IOException("c")));

        Assert.Equal("a", ExceptionSummary.Describe(ex, maxDepth: 1));
        Assert.Equal("a -> InvalidOperationException: b", ExceptionSummary.Describe(ex, maxDepth: 2));
    }

    [Fact]
    public void A_link_that_only_restates_its_cause_is_skipped()
    {
        // A wrapper that passes the inner message through verbatim adds nothing but noise.
        var ex = new InvalidOperationException("same", new IOException("same"));
        Assert.Equal("same", ExceptionSummary.Describe(ex));
    }

    [Fact]
    public void A_single_fault_aggregate_is_unwrapped_to_the_real_cause()
    {
        var ex = new AggregateException(new IOException("Access is denied."));
        Assert.Equal("Access is denied.", ExceptionSummary.Describe(ex));
    }

    [Fact]
    public void A_multi_fault_aggregate_keeps_its_own_enumerating_message()
    {
        var ex = new AggregateException(new IOException("first"), new IOException("second"));
        Assert.Contains("first", ExceptionSummary.Describe(ex), StringComparison.Ordinal);
        Assert.Contains("second", ExceptionSummary.Describe(ex), StringComparison.Ordinal);
    }

    [Fact]
    public void A_blank_message_falls_back_to_the_type_name()
        => Assert.Equal(nameof(IOException), ExceptionSummary.Describe(new IOException("")));

    [Fact]
    public void Rejects_a_null_exception_and_a_depth_below_one()
    {
        Assert.Throws<ArgumentNullException>(() => ExceptionSummary.Describe(null!));
        Assert.Throws<ArgumentOutOfRangeException>(() => ExceptionSummary.Describe(new IOException("x"), maxDepth: 0));
    }

    [Fact]
    public void FlashResult_Fail_from_an_exception_carries_the_chain()
    {
        var result = FlashResult.Fail(new InvalidOperationException("wrapper", new IOException("cause")));

        Assert.False(result.Success);
        Assert.Equal("wrapper -> IOException: cause", result.Error);
    }
}
