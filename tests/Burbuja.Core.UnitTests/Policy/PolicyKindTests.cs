using System.Text.Json.Nodes;
using Burbuja.Core.Abstractions.Policy;
using Burbuja.Core.Sdk.Policy;
using Shouldly;
using Xunit;

namespace Burbuja.Core.UnitTests.Policy;

public sealed class PolicyKindTests
{
    [Fact]
    public void Unknown_kind_escalates()
    {
        var v = PolicyCorpusTests.Classify("anything", kind: "not-a-kind");
        v.Tier.ShouldBe(RiskTier.Escalate);
        v.Finding.Class.ShouldBe("unknown_kind");
    }

    [Fact]
    public void Empty_and_unknown_profile_parse_to_restricted_prod()
    {
        PolicyProfileParser.Parse(null).ShouldBe(PolicyProfile.RestrictedProd);
        PolicyProfileParser.Parse("").ShouldBe(PolicyProfile.RestrictedProd);
        PolicyProfileParser.Parse("mystery").ShouldBe(PolicyProfile.RestrictedProd);
        PolicyProfileParser.Parse("full_auto").ShouldBe(PolicyProfile.FullAuto);
        PolicyProfileParser.Parse("read_only").ShouldBe(PolicyProfile.ReadOnly);
    }

    [Fact]
    public void Fs_read_uses_the_key_claude_code_actually_sends()
    {
        // M5.4 found this live: the Claude Code tools send `file_path`, and reading only `path`
        // classified every file call against the cwd, so a credential read came back
        // "Read inside workspace" — an allow.
        var v = PolicyCorpusTests.Classify(
            "ignored",
            kind: "fs.read",
            inputJson: """{"file_path":"/home/app/.ssh/id_rsa"}""");
        v.Tier.ShouldBe(RiskTier.HardDeny);
        v.Finding.Class.ShouldBe("secrets_read");

        var notebook = PolicyCorpusTests.Classify(
            "ignored",
            kind: "fs.read",
            inputJson: """{"notebook_path":"/home/app/.aws/credentials"}""");
        notebook.Tier.ShouldBe(RiskTier.HardDeny);

        // The kernel's own workspace tools still send `path`, and they still work.
        PolicyCorpusTests.Classify("/work/corpus-test/notes.txt", kind: "fs.read").Tier.ShouldBe(RiskTier.Allow);
    }

    [Fact]
    public void A_file_call_with_no_path_is_a_question_not_a_yes()
    {
        // Gray, not Allow: the engine decides what a gray finding becomes, but it must never be a
        // silent yes for a target nobody named.
        var read = PolicyCorpusTests.Classify("", kind: "fs.read");
        read.Tier.ShouldNotBe(RiskTier.Allow);
        read.Finding.Class.ShouldBe("unknown_path");

        var write = PolicyCorpusTests.Classify("ignored", kind: "fs.write", inputJson: "{}");
        write.Tier.ShouldNotBe(RiskTier.Allow);
        write.Finding.Class.ShouldBe("unknown_path");
    }

    [Fact]
    public void Fs_write_to_kernel_is_hard_deny()
    {
        var v = PolicyCorpusTests.Classify("/repo/app/src/Burbuja.Core/Policy/PolicyEngine.cs", kind: "fs.write");
        v.Tier.ShouldBe(RiskTier.HardDeny);
        v.Finding.Class.ShouldBe("self_modification");
    }

    [Fact]
    public void Fs_write_inside_work_root_allows_under_full_auto()
    {
        var v = PolicyCorpusTests.Classify("/work/corpus-test/out.txt", kind: "fs.write");
        v.Tier.ShouldBe(RiskTier.Allow);
    }

    [Fact]
    public void Fs_write_inside_work_root_escalates_under_read_only()
    {
        var ctx = PolicyCorpusTests.CorpusContext() with { Profile = PolicyProfile.ReadOnly };
        var v = PolicyCorpusTests.Classify("/work/corpus-test/out.txt", ctx, "fs.write");
        v.Tier.ShouldBe(RiskTier.Escalate);
    }

    [Fact]
    public void Restricted_prod_does_not_narrow_external_write()
    {
        var ctx = PolicyCorpusTests.CorpusContext() with { Profile = PolicyProfile.RestrictedProd };
        var v = PolicyCorpusTests.Classify("curl -X POST https://api.example.com/v1", ctx);
        v.Tier.ShouldBe(RiskTier.Escalate);
    }

    [Fact]
    public void Full_auto_narrows_external_write_to_gray()
    {
        var v = PolicyCorpusTests.Classify("curl -X POST https://api.example.com/v1");
        v.Tier.ShouldBe(RiskTier.Gray);
        v.Finding.Class.ShouldBe("external_write");
    }

    [Fact]
    public void Http_get_to_protected_host_allows()
    {
        var input = new JsonObject { ["url"] = "http://192.0.2.10:9443/health", ["method"] = "GET" };
        var engine = PolicyCorpusTests.CreateEngine();
        var v = engine.Classify(new ActionPolicySubject("http", input, PolicyCorpusTests.CorpusContext()));
        v.Tier.ShouldBe(RiskTier.Allow);
    }

    [Fact]
    public void Http_post_to_protected_host_is_hard_deny()
    {
        var input = new JsonObject { ["url"] = "http://192.0.2.10:8080/api", ["method"] = "POST" };
        var engine = PolicyCorpusTests.CreateEngine();
        var v = engine.Classify(new ActionPolicySubject("http", input, PolicyCorpusTests.CorpusContext()));
        v.Tier.ShouldBe(RiskTier.HardDeny);
        v.Finding.Class.ShouldBe("pi_mutation");
    }

    [Fact]
    public void Fs_read_of_container_aws_is_hard_deny()
    {
        var v = PolicyCorpusTests.Classify("/home/app/.aws/credentials", kind: "fs.read");
        v.Tier.ShouldBe(RiskTier.HardDeny);
        v.Finding.Class.ShouldBe("secrets_read");
    }

    [Fact]
    public void Aws_named_profile_escalates_unless_allowlisted()
    {
        var denied = PolicyCorpusTests.Classify("aws s3 ls --profile prod");
        denied.Tier.ShouldBe(RiskTier.Escalate);
        denied.Finding.Class.ShouldBe("aws_profile");

        var ctx = PolicyCorpusTests.CorpusContext() with { AllowedAwsProfiles = ["prod"] };
        var allowed = PolicyCorpusTests.Classify("aws s3 ls --profile prod", ctx);
        allowed.Finding.Class.ShouldNotBe("aws_profile");
    }

    [Fact]
    public void Regex_default_match_timeout_is_250ms()
    {
        // The value is set by Burbuja.Core.Sdk's module initializer, which runs when that assembly loads. Touch it
        // first: run alone (or first in the collection) nothing else has loaded it yet, and the fact read null.
        _ = Burbuja.Core.Sdk.Policy.RealPathProbe.Shared;
        var data = AppContext.GetData("REGEX_DEFAULT_MATCH_TIMEOUT");
        data.ShouldBe(TimeSpan.FromMilliseconds(250));
    }

    [Fact]
    public void In_memory_path_probe_is_hermetic()
    {
        var probe = new InMemoryPathProbe("/work/corpus-test/a.txt");
        probe.Exists("/work/corpus-test/a.txt").ShouldBeTrue();
        probe.Exists("/etc/passwd").ShouldBeFalse();
        probe.Contains("/work/corpus-test", "/work/corpus-test/a.txt").ShouldBeTrue();
    }
}
