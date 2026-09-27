namespace Burbuja.Core.Abstractions.Host;

/// <summary>
/// M14 R10 (request row X2) — a seam for the parts of a seed the kernel does not own.
///
/// <para><b>The problem it solves.</b> <c>KernelSeedHostedService</c> seeds four resources the KERNEL owns
/// (the subscription, a resource group, a workspace, an environment) and stops there. Everything a demo
/// actually wants to look at — a turn, a memory, a source, an amendment — belongs to a plugin or to a lane,
/// and before this seam the only way to get it was for the Host to reference that lane's assembly, which is
/// how a kernel becomes a pile of imports.</para>
///
/// <para><b>Who runs, in what order, and what a failure means.</b> Contributors run AFTER the kernel's own
/// four rows, in DI registration order, only when the seed is on (<c>Kernel:Seed=true</c> and a control-plane
/// or all role) — the same switch that decides the rest of the seed, because a host that was not asked to
/// seed must not grow rows because a plugin shipped a contributor. A contributor that THROWS fails the boot
/// and names itself: a half-seeded host that carried on quietly is the state this kernel refuses everywhere
/// else (<em>ausente ≠ vacío</em> — "nothing was seeded" and "the seed broke halfway" are different answers
/// and the second one must be readable). A contributor that has nothing to write should say so in its own
/// log line and return; it must not throw to mean "skip".</para>
/// </summary>
public interface IKernelSeedContributor
{
    /// <summary>What to call this contributor in the boot log and in the failure it can cause — the plugin
    /// id for a plugin's contributor, so an operator can go and read the thing that broke.</summary>
    string Name { get; }

    /// <summary>Write this contributor's rows. Called once, on a scope of its own, after the kernel's own
    /// seed has landed.</summary>
    ValueTask SeedAsync(CancellationToken ct);
}
