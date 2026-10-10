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

    [Test]
    public void SparseHistory_UndoRemovesOnlyTheLastBrushStroke_AndSupportsRedo()
    {
        Type deltaType = RuntimeType("PointCloudWorkbench.PointLabelDelta");
        Type historyType = RuntimeType("PointCloudWorkbench.PointLabelEditHistory");
        object history = Activator.CreateInstance(historyType);
        Array points = NewPointData(5, 0);

        RecordToggle(deltaType, historyType, history, points, new[] { 0 }, 0x10000, 16, 0);
        RecordToggle(deltaType, historyType, history, points, new[] { 1 }, 0x10000, 16, 0);
        // One mouse-down/up stroke may contain many changed points, but remains one history item.
        RecordToggle(deltaType, historyType, history, points, new[] { 2, 3 }, 0x10000, 16, 0);

        ApplyHistory(historyType, history, points, undo: true);
        Assert.That(GetLabel(points, 0) & 0x10000, Is.Not.Zero);
        Assert.That(GetLabel(points, 1) & 0x10000, Is.Not.Zero);
        Assert.That(GetLabel(points, 2) & 0x10000, Is.Zero);
        Assert.That(GetLabel(points, 3) & 0x10000, Is.Zero);

        ApplyHistory(historyType, history, points, undo: false);
        Assert.That(GetLabel(points, 2) & 0x10000, Is.Not.Zero);
        Assert.That(GetLabel(points, 3) & 0x10000, Is.Not.Zero);
    }

    [Test]
    public void SparseHistory_NewEditAfterUndoClearsRedo_AndNoiseBitsArePreserved()
    {
        Type deltaType = RuntimeType("PointCloudWorkbench.PointLabelDelta");
        Type historyType = RuntimeType("PointCloudWorkbench.PointLabelEditHistory");
        object history = Activator.CreateInstance(historyType);
        Array points = NewPointData(3, 0);
        SetLabel(points, 0, 5 | 0x10000 | 0x80000);

        RecordToggle(deltaType, historyType, history, points, new[] { 0 }, 0x10000, 16, 1);
        SetLabel(points, 0, GetLabel(points, 0) | 0x40000 | 0x500000);
        ApplyHistory(historyType, history, points, undo: true);
        Assert.That(GetLabel(points, 0) & 0x10000, Is.Not.Zero, "selection should be restored");
        Assert.That(GetLabel(points, 0) & (0xff | 0x7c0000), Is.EqualTo(5 | 0x40000 | 0x80000 | 0x500000));

        RecordToggle(deltaType, historyType, history, points, new[] { 1 }, 0x10000, 16, 0);
        bool canRedo = (bool)historyType.GetProperty("CanRedo").GetValue(history);
        Assert.That(canRedo, Is.False, "a new edit after Undo discards the old Redo branch");
    }

    [Test]
    public void SparseHistory_ClassificationAndDeletionUndoIndependentlyWithoutLosingOtherBits()
    {
        Type deltaType = RuntimeType("PointCloudWorkbench.PointLabelDelta");
        Type historyType = RuntimeType("PointCloudWorkbench.PointLabelEditHistory");
        object history = Activator.CreateInstance(historyType);
        Array points = NewPointData(2, 0);
        SetLabel(points, 0, 2 | 0x10000 | 0x80000);
        SetLabel(points, 1, 0 | 0x10000);

        byte[] oldClasses = { 2, 0 };
        object assign = deltaType.GetMethod("ClassAssignment").Invoke(null,
            new object[] { new[] { 0, 1 }, oldClasses, (byte)4 });
        ApplyAndRecord(deltaType, historyType, history, points, assign);

        RecordToggle(deltaType, historyType, history, points, new[] { 1 }, 0x10000, 16, 0);
        object delete = deltaType.GetMethod("ToggleConstant").Invoke(null,
            new object[] { new[] { 1 }, 0x30000, 16, (byte)1 });
        ApplyAndRecord(deltaType, historyType, history, points, delete);

        ApplyHistory(historyType, history, points, undo: true);
        Assert.That(GetLabel(points, 1) & (0xff | 0x30000), Is.EqualTo(0x10000 | 4));
        ApplyHistory(historyType, history, points, undo: true);
        Assert.That(GetLabel(points, 1) & (0xff | 0x30000), Is.EqualTo(4));
        ApplyHistory(historyType, history, points, undo: true);
        Assert.That(GetLabel(points, 0) & (0xff | 0x10000), Is.EqualTo(2 | 0x10000));
        Assert.That(GetLabel(points, 1) & (0xff | 0x10000), Is.EqualTo(0 | 0x10000));
        Assert.That(GetLabel(points, 0) & 0x80000, Is.Not.Zero);

        ApplyHistory(historyType, history, points, undo: false);
        ApplyHistory(historyType, history, points, undo: false);
        ApplyHistory(historyType, history, points, undo: false);
        Assert.That(GetLabel(points, 1) & (0xff | 0x30000), Is.EqualTo(4 | 0x20000));
    }

    [Test]
    public void MaskedHistory_UndoAndRedoPreserveSelectionDeletionAndClassBits()
    {
        Type deltaType = RuntimeType("PointCloudWorkbench.PointLabelDelta");
        Type historyType = RuntimeType("PointCloudWorkbench.PointLabelEditHistory");
        object history = Activator.CreateInstance(historyType);
        Array points = NewPointData(1, 3 | 0x10000 | 0x20000);

        object noiseDelta = deltaType.GetMethod("MaskedValues").Invoke(null,
            new object[] { new[] { 0 }, 0x7c0000, 18, new byte[] { 0 }, new byte[] { 0x15 } });
        ApplyAndRecord(deltaType, historyType, history, points, noiseDelta);
        Assert.That(GetLabel(points, 0) & 0x7c0000, Is.EqualTo(0x540000));

        ApplyHistory(historyType, history, points, undo: true);
        Assert.That(GetLabel(points, 0) & 0x7c0000, Is.Zero);
        Assert.That(GetLabel(points, 0) & (0xff | 0x30000), Is.EqualTo(3 | 0x30000));

        ApplyHistory(historyType, history, points, undo: false);
        Assert.That(GetLabel(points, 0) & 0x7c0000, Is.EqualTo(0x540000));
        Assert.That(GetLabel(points, 0) & (0xff | 0x30000), Is.EqualTo(3 | 0x30000));
    }

    [Test]
    public void SparseHistory_EnforcesPerStackMemoryLimitBeforeAcceptingAnEdit()
    {
        Type deltaType = RuntimeType("PointCloudWorkbench.PointLabelDelta");
        Type historyType = RuntimeType("PointCloudWorkbench.PointLabelEditHistory");
        object history = Activator.CreateInstance(historyType, new object[] { 10, 100L });
        object tooLarge = deltaType.GetMethod("ToggleConstant").Invoke(null,
            new object[] { Enumerable.Range(0, 20).ToArray(), 0x10000, 16, (byte)0 });

        bool canRecord = (bool)historyType.GetMethod("CanRecord").Invoke(history, new[] { tooLarge });
        bool recorded = (bool)historyType.GetMethod("Record").Invoke(history, new[] { tooLarge });
        Assert.That(canRecord, Is.False);
        Assert.That(recorded, Is.False);
        Assert.That((bool)historyType.GetProperty("CanUndo").GetValue(history), Is.False);
    }

    private static Type RuntimeType(string fullName)
    {
        Type type = AppDomain.CurrentDomain.GetAssemblies()
            .Select(assembly => assembly.GetType(fullName, false))
            .FirstOrDefault(candidate => candidate != null);
        Assert.That(type, Is.Not.Null, "Runtime type was not loaded: " + fullName);
        return type;
    }

    private static Array NewPointData(int count, int initialLabel)
    {
        Type pointType = RuntimeType("PointCloudWorkbench.PointData");
        Array points = Array.CreateInstance(pointType, count);
        ConstructorInfo constructor = pointType.GetConstructor(new[]
            { typeof(UnityEngine.Vector3), typeof(UnityEngine.Color32), typeof(int), typeof(float) });
        for (int i = 0; i < count; i++)
            points.SetValue(constructor.Invoke(new object[]
            {
                UnityEngine.Vector3.zero, new UnityEngine.Color32(1, 2, 3, 255), initialLabel, 0f
            }), i);
        return points;
    }

    private static void SetLabel(Array points, int index, int label)
    {
        object point = points.GetValue(index);
        point.GetType().GetField("label").SetValue(point, label);
        points.SetValue(point, index);
    }

    private static int GetLabel(Array points, int index) =>
        (int)points.GetValue(index).GetType().GetField("label").GetValue(points.GetValue(index));

    private static void RecordToggle(Type deltaType, Type historyType, object history, Array points,
        int[] indices, int mask, int shift, byte before)
    {
        object delta = deltaType.GetMethod("ToggleConstant").Invoke(null,
            new object[] { indices, mask, shift, before });
        ApplyAndRecord(deltaType, historyType, history, points, delta);
    }

    private static void ApplyAndRecord(Type deltaType, Type historyType, object history, Array points, object delta)
    {
        deltaType.GetMethod("Apply").Invoke(delta, new object[] { points, true });
        Assert.That((bool)historyType.GetMethod("Record").Invoke(history, new[] { delta }), Is.True);
    }

    private static void ApplyHistory(Type historyType, object history, Array points, bool undo)
    {
        string peekName = undo ? "PeekUndo" : "PeekRedo";
        string completeName = undo ? "CompleteUndo" : "CompleteRedo";
        object delta = historyType.GetMethod(peekName).Invoke(history, null);
        Assert.That(delta, Is.Not.Null);
        delta.GetType().GetMethod("Apply").Invoke(delta, new object[] { points, !undo });
        Assert.That((bool)historyType.GetMethod(completeName).Invoke(history, null), Is.True);
    }
}
