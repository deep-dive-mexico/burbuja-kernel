using Burbuja.Core.Arm;
using Burbuja.Core.Process;
using Shouldly;
using Xunit;

namespace Burbuja.Core.UnitTests.Arm;

/// <summary>
/// C2 (N3/N5): the skip-token codec, the `$filter` parser and the process-environment policy are
/// public SDK contracts with a stable wire shape, so out-of-tree stores and tools can share them.
/// </summary>
public sealed class SdkContractTests
{
    [Fact]
    public void SkipToken_format_is_stable()
    {
        var created = new DateTimeOffset(2026, 9, 1, 0, 0, 0, TimeSpan.Zero);
        var token = ArmSkipTokenCodec.Encode(created, "abc");
        token.ShouldBe("NjM5MjM4MTc2MDAwMDAwMDAwCmFiYw");

        ArmSkipTokenCodec.TryDecode(token, out var decodedAt, out var decodedId).ShouldBeTrue();
        decodedAt.ShouldBe(created);
        decodedId.ShouldBe("abc");
    }

    [Fact]
    public void Malformed_skip_token_does_not_throw()
    {
        ArmSkipTokenCodec.TryDecode("not-a-token", out _, out _).ShouldBeFalse();
    }

    [Theory]
    [InlineData("name eq 'web'", "web", null, null)]
    [InlineData("name startswith 'we'", null, "we", null)]
    [InlineData("properties/spec.kind eq 'process'", null, null, "spec.kind")]
    public void Filter_clauses_parse(string raw, string? nameEq, string? nameStarts, string? propKey)
    {
        ArmFilterParser.TryParse(raw, out var filter, out var error).ShouldBeTrue();
        error.ShouldBeNull();
        filter.ShouldNotBeNull();
        filter!.NameEq.ShouldBe(nameEq);
        filter.NameStartsWith.ShouldBe(nameStarts);
        filter.PropertyKey.ShouldBe(propKey);
    }

    [Fact]
    public void Filter_tag_clause_parses_key_and_value()
    {
        ArmFilterParser.TryParse("tags/env eq 'prod'", out var filter, out _).ShouldBeTrue();
        filter!.TagKey.ShouldBe("env");
        filter.TagEq.ShouldBe("prod");
    }

    [Fact]
    public void Unsupported_filter_clause_is_rejected()
    {
        ArmFilterParser.TryParse("location eq 'west'", out _, out var error).ShouldBeFalse();
        error.ShouldNotBeNull();
    }

    [Fact]
    public void Environment_policy_lives_in_the_sdk_assembly()
    {
        typeof(ProcessEnvironmentPolicy).Assembly.GetName().Name.ShouldBe("Burbuja.Core.Sdk");
        ProcessEnvironmentPolicy.IsDenied("BURBUJA_API_KEY").ShouldBeTrue();
        ProcessEnvironmentPolicy.IsDenied("PATH").ShouldBeFalse();
    }

    [Fact]
    public void Execution_grant_authority_stays_in_the_kernel()
    {
        // The ExecutionGrant constructor is internal to Burbuja.Core; only the kernel may mint.
        typeof(ExecutionGrantAuthority).Assembly.GetName().Name.ShouldBe("Burbuja.Core");
    }
}
