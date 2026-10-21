using System;
using System.Globalization;
using Xunit;

namespace CodeFusion.Fuzz;

/// <summary>
/// Every target on every build: its checked-in corpus, then a fixed number of mutations of it from a seed fixed per
/// target, so a failure here fails the same way on every machine. CODEFUSION_FUZZ_SCALE multiplies the mutations
/// for a longer local run.
/// </summary>
public class ReplayTests(ITestOutputHelper output)
{
    public static TheoryData<string> Names()
    {
        var names = new TheoryData<string>();
        foreach (Target t in Targets.All) names.Add(t.Name);
        return names;
    }

    [Theory]
    [MemberData(nameof(Names))]
    public void TheCorpusAndItsSeededMutationsKeepEveryInvariant(string name)
    {
        Target t = Targets.Get(name);
        var seeds = Targets.Seeds(t);
        Assert.NotEmpty(seeds);
        var findings = new Findings();
        foreach (byte[] seed in seeds) findings.Run(t, seed);
        int seedFindings = findings.Count;

        var mutator = new Mutator(Seed(name), Mutator.LoadDictionary(Targets.DictionaryPath(t)));
        int n = t.Mutations * Scale();
        for (int i = 0; i < n; i++) findings.Run(t, mutator.Next(seeds, t.MaxLength));

        output.WriteLine($"{name}: {seeds.Count} seeds, {n} mutations");
        string observed = Observed.Report(name);
        if (observed.Length > 0) output.WriteLine("observed, not failures:\n" + observed);
        if (findings.Count > 0)
            Assert.Fail($"{findings.Count} kinds of finding ({seedFindings} from the seeds alone):\n{findings.Report(t)}");
    }

    static int Scale() =>
        int.TryParse(Environment.GetEnvironmentVariable("CODEFUSION_FUZZ_SCALE"), NumberStyles.None, CultureInfo.InvariantCulture, out int s) && s > 0 ? s : 1;

    /// <summary>FNV-1a of the name: the same on every run, unlike string.GetHashCode.</summary>
    static int Seed(string name)
    {
        uint h = 2166136261;
        foreach (char c in name) h = (h ^ c) * 16777619;
        return (int)h;
    }
}
