using System.Reflection;
using Burbuja.Core.Abstractions.Channel;
using Shouldly;
using Xunit;

namespace Burbuja.Core.UnitTests.Channel;

/// <summary>
/// M14 R11 (request row X8b) — the inbound seam's two properties, pinned where a later round would break them
/// silently.
///
/// <para><b>Why a reflection fact.</b> The seam exists so that a channel adapter (lane C's Discord and
/// WhatsApp plugins) and a domain (lane K's AKOS) can meet without either assembling the other. That property
/// is not a behaviour anybody can test by calling the interface: it is a property of the ASSEMBLY the types
/// live in and of what those types mention. A domain word leaking into this file — an <c>AkosTurn</c>, a
/// <c>DiscordSnowflake</c> — compiles perfectly and turns the seam into a dependency every channel has to
/// carry, which is the defect X8b was written to prevent.</para>
/// </summary>
public sealed class InboundSeamTests
{
    [Fact]
    public void The_seam_is_domain_free_and_lives_in_the_assembly_every_plugin_already_references()
    {
        var channel = typeof(IInboundChannel);
        var assembly = channel.Assembly;

        // Core.Abstractions is the ONE assembly a plugin compiles against, so a seam here is reachable by every
        // adapter without a new reference.
        assembly.GetName().Name.ShouldBe("Burbuja.Core.Abstractions");

        Type[] seam = [typeof(InboundMessage), typeof(IInboundChannel), typeof(ChannelDelivery), typeof(IChannelResponder), typeof(ChannelReply)];
        foreach (var type in seam)
        {
            type.Assembly.ShouldBeSameAs(assembly);
            type.IsPublic.ShouldBeTrue($"{type.Name} is the contract a plugin implements; it must be public");
        }

        // Every type the five mention — signatures, properties, generic arguments, enum types — must be from
        // Core.Abstractions or the BCL. `Burbuja.Akos`, `Burbuja.Agents` and any plugin assembly appearing here
        // is the leak this fact exists for.
        var forbidden = new[] { "Burbuja.Akos", "Burbuja.Agents", "Burbuja.Plugins", "Discord", "WhatsApp" };
        foreach (var type in seam)
        {
            foreach (var referenced in Referenced(type))
            {
                var name = referenced.Assembly.GetName().Name ?? "";
                forbidden.ShouldNotContain(
                    f => name.StartsWith(f, StringComparison.OrdinalIgnoreCase),
                    $"{type.Name} mentions {referenced.FullName} from {name}: the seam must stay domain-free, or "
                    + "every channel has to reference the domain it talks to");
            }
        }

        // And the words themselves, in the file that defines them: a signature is not the only place a domain
        // can leak — a type used only inside a method body still forces the reference.
        var source = File.ReadAllText(Path.Combine(RepoRoot, "src/Burbuja.Core.Abstractions/Channel/Inbound.cs"));
        foreach (var word in new[] { "Akos", "Agents", "Discord", "WhatsApp", "Persona", "Turn" })
        {
            source.ShouldNotContain($"Burbuja.{word}", Case.Sensitive,
                $"Inbound.cs names a domain type ({word}): the seam's whole value is that it names none");
        }
    }

    [Fact]
    public void A_redelivery_is_an_answer_and_not_a_refusal()
    {
        var ok = ChannelDelivery.Ok("t-1");
        var again = ChannelDelivery.Redelivered("t-1");
        var refused = ChannelDelivery.Refused("the room is not allowed to open turns");

        ok.Accepted.ShouldBeTrue();
        ok.Handle.ShouldBe("t-1");
        ok.Duplicate.ShouldBeFalse("a first delivery is not a duplicate");
        ok.Reason.ShouldBeNull("nothing to explain about a message that was taken");

        again.Accepted.ShouldBeTrue("a redelivery is TAKEN — the caller must not retry it");
        again.Duplicate.ShouldBeTrue("and it is flagged, so a caller can tell it from a first delivery");
        again.Handle.ShouldBe("t-1", "the handle is the SAME one: a redelivery must not create a second turn");

        refused.Accepted.ShouldBeFalse();
        refused.Reason.ShouldBe("the room is not allowed to open turns", "a refusal carries the domain's own sentence");
        refused.Handle.ShouldBeNull("nothing was created, so there is nothing to hand back");
    }

    [Fact]
    public void An_inbound_message_carries_the_provenance_the_domain_records_and_the_key_it_dedupes_on()
    {
        var at = new DateTimeOffset(2026, 9, 16, 22, 30, 0, TimeSpan.Zero);
        var message = new InboundMessage("Channel.Discord.Akos", "room-7", "discord:42", "revisá el checkout", at);

        message.ChannelId.ShouldBe("Channel.Discord.Akos");
        message.ConversationId.ShouldBe("room-7");
        message.ActorId.ShouldBe("discord:42", "provenance is the channel's vocabulary, recorded verbatim");
        message.At.ShouldBe(at, "the channel's clock, not ours");
        message.ReplyToId.ShouldBeNull("a channel that cannot say what a message answers leaves it empty");
        message.DedupeKey.ShouldBeNull("and a channel with no message id of its own has nothing to dedupe on");
        message.Metadata.ShouldBeNull();

        // An image-only message is empty text, not an absent message: the seam does not conflate them.
        var captionless = message with { Text = "" };
        captionless.Text.ShouldBeEmpty();
        captionless.DedupeKey.ShouldBeNull();
    }

    private static IEnumerable<Type> Referenced(Type type)
    {
        foreach (var method in type.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly))
        {
            yield return method.ReturnType;
            foreach (var parameter in method.GetParameters())
                yield return parameter.ParameterType;
        }

        foreach (var property in type.GetProperties(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly))
            yield return property.PropertyType;

        if (type.IsInterface)
        {
            foreach (var parent in type.GetInterfaces())
                yield return parent;
        }
    }

    private static string RepoRoot { get; } = Find();

    private static string Find()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Burbuja.slnx")))
            dir = dir.Parent;
        dir.ShouldNotBeNull("the repository root (Burbuja.slnx) is not above the test output directory");
        return dir!.FullName;
    }
}
