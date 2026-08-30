using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Periphery.Camera.Tests.Fakes;
using Xunit;

namespace Periphery.Camera.Tests;

/// <summary>
/// Guards the harness that keeps device tests honest (#276).
/// </summary>
/// <remarks>
/// These assert on the shape of the test assembly itself, because the defect they pin is a
/// <em>scheduling</em> property: a device test that resolved <c>CameraDevice.OpenAsync</c> to the
/// in-memory fake did not fail, it <b>passed</b>. Nothing downstream could notice, so there is no
/// behavioural test to write — only a structural one that fails when the arrangement preventing it
/// is undone.
/// </remarks>
public class RigCollectionConventionTests
{
    private static IEnumerable<Type> IntegrationTestClasses =>
        typeof(RigCollectionConventionTests).Assembly
            .GetTypes()
            // Both placements. xUnit honours [Trait] on the class as well as on the method, so
            // scanning only methods would let a class-level annotation escape the convention
            // entirely -- and escape it silently, which is the same shape as the bug this file
            // exists to prevent (#279 review turn 1).
            .Where(t => HasIntegrationTrait(t.GetCustomAttributesData())
                        || t.GetMethods(BindingFlags.Public | BindingFlags.Instance)
                            .Any(m => HasIntegrationTrait(m.GetCustomAttributesData())))
            .Distinct();

    private static bool HasIntegrationTrait(IEnumerable<CustomAttributeData> attributes) =>
        attributes.Any(a =>
            a.AttributeType.FullName == "Xunit.TraitAttribute"
            && a.ConstructorArguments.Count == 2
            && (string?)a.ConstructorArguments[0].Value == "Category"
            && (string?)a.ConstructorArguments[1].Value == "Integration");

    private static string? CollectionNameOf(Type type) =>
        type.GetCustomAttributesData()
            .Where(a => a.AttributeType.FullName == "Xunit.CollectionAttribute"
                        && a.ConstructorArguments.Count == 1)
            .Select(a => (string?)a.ConstructorArguments[0].Value)
            .FirstOrDefault();

    [Fact]
    public void EveryIntegrationTestClass_IsInTheNonParallelRigCollection()
    {
        // The failure this prevents: LinuxV4l2IntegrationTests carried no [Collection] at all,
        // so xUnit gave it its own parallelizable collection and it ran beside the "Camera"
        // collection whose fixture installs the fake into a process-global static.
        var classes = IntegrationTestClasses.ToList();

        // If this trips, the trait moved or was renamed and the check below is now vacuous —
        // an empty set would otherwise satisfy every assertion in this method.
        Assert.NotEmpty(classes);

        var stray = classes
            .Where(t => CollectionNameOf(t) != RigFixtures.Name)
            .Select(t => $"{t.Name} (collection: {CollectionNameOf(t) ?? "<none>"})")
            .ToList();

        Assert.True(stray.Count == 0,
            "every class with an [Trait(\"Category\", \"Integration\")] test opens real hardware "
            + $"and must be in the \"{RigFixtures.Name}\" collection, so it never runs beside the "
            + "\"Camera\" collection's in-memory backend fixture (#276). Not in it: "
            + string.Join("; ", stray));
    }

    [Fact]
    public void TheAssembly_RunsItsCollectionsSerially()
    {
        // The collection attribute above is opt-in, so it only protects classes someone
        // remembered to annotate. This is the arrangement that holds regardless.
        var behaviour = typeof(RigCollectionConventionTests).Assembly
            .GetCustomAttributesData()
            .FirstOrDefault(a => a.AttributeType.FullName == "Xunit.CollectionBehaviorAttribute");

        Assert.True(behaviour is not null,
            "Periphery.Camera.Tests must keep [assembly: CollectionBehavior(DisableTestParallelization "
            + "= true)] — without it, CameraDevice.BackendFactory (a process-global static) can be "
            + "installed by one collection while another opens a real device (#276).");

        var disabled = behaviour!.NamedArguments
            .Where(a => a.MemberName == "DisableTestParallelization")
            .Select(a => (bool?)a.TypedValue.Value)
            .FirstOrDefault();

        Assert.True(disabled == true,
            "CollectionBehavior is present but DisableTestParallelization is not true, which "
            + "leaves the fake-backend leak in #276 open.");
    }

    [Fact]
    public void RigGuard_FailsWhenTheInMemoryBackendIsInstalled()
    {
        // The tripwire has to actually trip. A guard that silently passes in the condition it
        // exists to catch is worse than none, because it reads as evidence.
        Assert.Null(CameraDevice.BackendFactory);

        TestHelpers.InstallTestBackendFactory();
        try
        {
            Assert.ThrowsAny<Xunit.Sdk.XunitException>(RigGuard.RequireRealBackend);
        }
        finally
        {
            TestHelpers.ClearBackendFactory();
        }

        RigGuard.RequireRealBackend();
    }

    private static IEnumerable<Type> PhysicalFixtureClasses =>
        typeof(RigCollectionConventionTests).Assembly
            .GetTypes()
            .Where(t => HasTrait(t.GetCustomAttributesData(), RigTraits.Fixture, RigTraits.PhysicalWebcam)
                        || t.GetMethods(BindingFlags.Public | BindingFlags.Instance)
                            .Any(m => HasTrait(m.GetCustomAttributesData(),
                                RigTraits.Fixture, RigTraits.PhysicalWebcam)))
            .Distinct();

    [Fact]
    public void EveryPhysicalFixtureClass_AlsoCarriesTheIntegrationCategory()
    {
        // Four workflow steps select the non-device suite with `Category!=Integration`. Keeping
        // Category=Integration on the physical-fixture tests is what makes all four exclude them
        // without needing to know the Fixture key exists. Drop it and they would start running on
        // Windows and macOS runners that have never had a webcam attached (#277).
        var classes = PhysicalFixtureClasses.ToList();
        Assert.NotEmpty(classes);

        var stray = classes
            .Where(t => !HasIntegrationTrait(t.GetCustomAttributesData())
                        && !t.GetMethods(BindingFlags.Public | BindingFlags.Instance)
                            .Any(m => HasIntegrationTrait(m.GetCustomAttributesData())))
            .Select(t => t.Name)
            .ToList();

        Assert.True(stray.Count == 0,
            $"these need physical hardware but are not marked Category=Integration, so the "
            + $"`Category!=Integration` filters in build.yml, publish.yml and linux-ci's build job "
            + $"would run them on machines with no webcam: {string.Join(", ", stray)}");
    }


    private static bool HasTrait(IEnumerable<CustomAttributeData> attributes, string key, string value) =>
        attributes.Any(a =>
            a.AttributeType.FullName == "Xunit.TraitAttribute"
            && a.ConstructorArguments.Count == 2
            && (string?)a.ConstructorArguments[0].Value == key
            && (string?)a.ConstructorArguments[1].Value == value);
}
