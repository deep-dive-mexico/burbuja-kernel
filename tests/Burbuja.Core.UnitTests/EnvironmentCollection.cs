using Xunit;

namespace Burbuja.Core.UnitTests;

/// <summary>
/// M15 M-R3 (lane request X32) — the one collection for facts that WRITE the process environment.
///
/// <para><c>ConfigAuthTests.BURBUJA_API_KEY_loads_without_throwing_and_seeds_owner</c> went red once in a parallel
/// run and green alone: it sets <c>BURBUJA_API_KEY</c>, builds a configuration that enumerates every variable,
/// and restores it — while <c>ProcessRunnerTests</c> was writing the environment from another thread. The
/// process environment is one dictionary; two writers on it are a race no assertion can be written around, so
/// the classes that write it share this collection and the collection runs alone.</para>
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class EnvironmentCollection
{
    public const string Name = "process environment";
}
