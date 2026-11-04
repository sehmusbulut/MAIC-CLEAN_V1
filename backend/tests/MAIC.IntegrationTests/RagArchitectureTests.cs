using System.Reflection;
using MAIC.Application.AI;
using Xunit;

namespace MAIC.IntegrationTests;

/// <summary>
/// Retrieval must go through <see cref="KnowledgeVectorIndex"/>, which enforces workspace/scope filters and validates
/// provider output. No other production type may depend on the raw ports (implementations and decorators of the port
/// itself are allowed).
/// </summary>
public sealed class RagArchitectureTests
{
    private static readonly Type[] RawPorts = [typeof(IKnowledgeVectorStore), typeof(IEmbeddingGenerator)];

    [Fact]
    public void Only_the_knowledge_vector_index_depends_on_the_raw_embedding_and_vector_ports()
    {
        var assemblies = new[] { "MAIC.Domain", "MAIC.Application", "MAIC.Infrastructure", "MAIC.API", "MAIC.Worker" }.Select(Assembly.Load);
        var offenders = assemblies.SelectMany(a => a.GetTypes())
            .Where(type => !IsAllowed(type))
            .Where(type => DependsOnRawPort(type))
            .Select(type => type.FullName)
            .ToArray();
        Assert.Empty(offenders);
    }

    /// <summary>The index, implementations/decorators of a port, and types nested in them (async state machines, lambdas).</summary>
    private static bool IsAllowed(Type type)
    {
        for (Type? current = type; current is not null; current = current.DeclaringType)
            if (current == typeof(KnowledgeVectorIndex) || RawPorts.Any(port => port.IsAssignableFrom(current))) return true;
        return false;
    }

    private static bool DependsOnRawPort(Type type)
    {
        const BindingFlags all = BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly;
        var members = type.GetConstructors(all).SelectMany(c => c.GetParameters().Select(p => p.ParameterType))
            .Concat(type.GetFields(all).Select(f => f.FieldType))
            .Concat(type.GetProperties(all).Select(p => p.PropertyType))
            .Concat(type.GetMethods(all).SelectMany(m => m.GetParameters().Select(p => p.ParameterType).Append(m.ReturnType)));
        return members.Any(member => RawPorts.Any(port => port.IsAssignableFrom(member) || member.IsGenericType
            && member.GetGenericArguments().Any(argument => port.IsAssignableFrom(argument))));
    }
}
