using Xunit.Abstractions;
using Xunit.Sdk;

namespace Tedd.DotNetLab.Tests;

public sealed class CaseTraitDiscoverer : ITraitDiscoverer
{
    public IEnumerable<KeyValuePair<string,string>> GetTraits(IAttributeInfo attribute)
    {
        var values=attribute.GetConstructorArguments().Cast<string>().ToArray();
        return Tags(values[0],values[1],values[2],values[3]);
    }
    public static IEnumerable<KeyValuePair<string,string>> Tags(string cases,string requirements,string milestone,string level)
    {
        foreach(string value in cases.Split(',',StringSplitOptions.RemoveEmptyEntries|StringSplitOptions.TrimEntries).Distinct())
            yield return new("Case",value);
        foreach(string value in requirements.Split(',',StringSplitOptions.RemoveEmptyEntries|StringSplitOptions.TrimEntries).Distinct())
            yield return new("Requirement",value);
        yield return new("Milestone",milestone); yield return new("Level",level);
    }
}
