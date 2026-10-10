using System;
using System.Linq;
using System.Reflection;
using NUnit.Framework;

public sealed class ReliabilityBoundaryTests
{
    [Test]
    public void BoundedTextBuffer_AppendLineKeepsItsConfiguredLimit()
    {
        Type bufferType = RuntimeType("PointCloudWorkbench.BoundedTextBuffer");
        object buffer = Activator.CreateInstance(bufferType, new object[] { 48, 16 });
        bufferType.GetMethod("AppendLine", new[] { typeof(string) }).Invoke(buffer, new object[] { "start" });
        bufferType.GetMethod("Append", new[] { typeof(string) }).Invoke(buffer, new object[] { new string('x', 80) });

        int length = (int)bufferType.GetProperty("Length").GetValue(buffer);
        string text = buffer.ToString();

        Assert.That(length, Is.LessThanOrEqualTo(48));
        Assert.That(text, Does.Contain("...[truncated"));
        Assert.That(text, Does.Contain("start"));
    }

    [Test]
    public void AtomicResourceFactory_ReleasesCandidateWhenInitializationFails()
    {
        Type factoryType = RuntimeType("PointCloudWorkbench.AtomicResourceFactory");
        object resource = new object();
        bool released = false;
        MethodInfo factory = factoryType.GetMethods(BindingFlags.Public | BindingFlags.Static)
            .Single(method => method.Name == "CreateInitialized")
            .MakeGenericMethod(typeof(int), typeof(object));

        TargetInvocationException exception = Assert.Throws<TargetInvocationException>(() => factory.Invoke(null,
            new object[]
            {
                7,
                (Func<object>)(() => resource),
                (Action<object, int>)((_, __) => throw new InvalidOperationException("injected")),
                (Action<object>)(_ => released = true)
            }));

        Assert.That(exception.InnerException, Is.TypeOf<InvalidOperationException>());
        Assert.That(released, Is.True);
    }

    private static Type RuntimeType(string fullName)
    {
        Type type = AppDomain.CurrentDomain.GetAssemblies()
            .Select(assembly => assembly.GetType(fullName, false))
            .FirstOrDefault(candidate => candidate != null);
        Assert.That(type, Is.Not.Null, "Runtime type was not loaded: " + fullName);
        return type;
    }
}
