using System;
using PointCloudWorkbench;

namespace PointCloudWorkbench
{
    public enum PointLabelHistoryDomain
    {
        Annotation,
        Noise
    }

    public sealed class PointLabelDelta
    {
        private enum DeltaKind
        {
            ToggleBits,
            MaskedBytes,
            ClassAssignment
        }

        private readonly DeltaKind kind;
        private readonly int[] indices;
        private readonly int mask;
        private readonly int shift;
        private readonly byte[] beforeValues;
        private readonly byte[] afterValues;
        private readonly byte beforeConstant;
        private readonly byte afterConstant;
        private readonly byte newClassId;

        public int PointCount => indices.Length;
        public long EstimatedBytes { get; }

        private PointLabelDelta(DeltaKind kind, int[] indices, int mask, int shift,
            byte[] beforeValues, byte[] afterValues, byte beforeConstant, byte afterConstant,
            byte newClassId)
        {
            this.kind = kind;
            this.indices = indices ?? throw new ArgumentNullException(nameof(indices));
            this.mask = mask;
            this.shift = shift;
            this.beforeValues = beforeValues;
            this.afterValues = afterValues;
            this.beforeConstant = beforeConstant;
            this.afterConstant = afterConstant;
            this.newClassId = newClassId;
            EstimatedBytes = 32L + (long)indices.Length * sizeof(int) +
                (beforeValues != null ? beforeValues.Length : 0) +
                (afterValues != null ? afterValues.Length : 0);
        }

        public static PointLabelDelta ToggleConstant(int[] indices, int mask, int shift, byte beforeValue)
        {
            ValidateEncodedMask(mask, shift);
            byte toggle = (byte)((mask >> shift) & 0xff);
            return new PointLabelDelta(DeltaKind.ToggleBits, indices, mask, shift,
                null, null, beforeValue, (byte)(beforeValue ^ toggle), 0);
        }

        public static PointLabelDelta ToggleValues(int[] indices, int mask, int shift, byte[] beforeValues)
        {
            ValidateEncodedMask(mask, shift);
            ValidateValues(indices, beforeValues);
            return new PointLabelDelta(DeltaKind.ToggleBits, indices, mask, shift,
                beforeValues, null, 0, 0, 0);
        }

        public static PointLabelDelta MaskedValues(int[] indices, int mask, int shift,
            byte[] beforeValues, byte[] afterValues)
        {
            ValidateEncodedMask(mask, shift);
            ValidateValues(indices, beforeValues);
            ValidateValues(indices, afterValues);
            return new PointLabelDelta(DeltaKind.MaskedBytes, indices, mask, shift,
                beforeValues, afterValues, 0, 0, 0);
        }

        public static PointLabelDelta ClassAssignment(int[] indices, byte[] beforeClasses, byte newClassId)
        {
            ValidateValues(indices, beforeClasses);
            return new PointLabelDelta(DeltaKind.ClassAssignment, indices, 0xff, 0,
                beforeClasses, null, 0, 0, newClassId);
        }

        public bool MatchesExpected(PointData[] points, bool forward)
        {
            if (points == null) return false;
            for (int i = 0; i < indices.Length; i++)
            {
                int index = indices[i];
                if ((uint)index >= (uint)points.Length) return false;
                int label = points[index].label;
                if (kind == DeltaKind.ClassAssignment)
                {
                    byte expectedClass = forward ? beforeValues[i] : newClassId;
                    bool expectedSelected = forward;
                    if ((label & 0xff) != expectedClass || ((label & 0x10000) != 0) != expectedSelected)
                        return false;
                    continue;
                }

                byte current = (byte)((label & mask) >> shift);
                byte expected;
                if (kind == DeltaKind.ToggleBits)
                {
                    byte before = beforeValues != null ? beforeValues[i] : beforeConstant;
                    byte after = beforeValues != null
                        ? (byte)(before ^ ((mask >> shift) & 0xff))
                        : afterConstant;
                    expected = forward ? before : after;
                }
                else
                {
                    expected = forward ? beforeValues[i] : afterValues[i];
                }
                if (current != expected) return false;
            }
            return true;
        }

        public void Apply(PointData[] points, bool forward)
        {
            if (points == null) throw new ArgumentNullException(nameof(points));
            if (!MatchesExpected(points, forward))
                throw new InvalidOperationException("点ラベルが履歴の想定状態と異なるため適用できません。");

            for (int i = 0; i < indices.Length; i++)
            {
                int index = indices[i];
                PointData point = points[index];
                if (kind == DeltaKind.ToggleBits)
                {
                    point.label ^= mask;
                }
                else if (kind == DeltaKind.MaskedBytes)
                {
                    byte value = forward ? afterValues[i] : beforeValues[i];
                    point.label = (point.label & ~mask) | ((value << shift) & mask);
                }
                else
                {
                    point.label = (point.label & ~0xff) | (forward ? newClassId : beforeValues[i]);
                    if (forward) point.label &= ~0x10000;
                    else point.label |= 0x10000;
                }
                points[index] = point;
            }
        }

        private static void ValidateEncodedMask(int mask, int shift)
        {
            if (mask == 0 || shift < 0 || shift > 24 || (mask >> shift) > 0xff)
                throw new ArgumentOutOfRangeException(nameof(mask));
        }

        private static void ValidateValues(int[] indices, byte[] values)
        {
            if (indices == null) throw new ArgumentNullException(nameof(indices));
            if (values == null) throw new ArgumentNullException(nameof(values));
            if (indices.Length != values.Length) throw new ArgumentException("履歴の点数が一致しません。");
        }
    }

    public sealed class PointLabelEditHistory
    {
        public const int DefaultCapacity = 100;
        public const long DefaultMaxBytesPerStack = 128L * 1024 * 1024;

        private sealed class Entry
        {
            public readonly PointLabelDelta Delta;
            public readonly PointLabelHistoryDomain Domain;

            public Entry(PointLabelDelta delta, PointLabelHistoryDomain domain)
            {
                Delta = delta;
                Domain = domain;
            }
        }

        private readonly BoundedHistory<Entry> undo;
        private readonly BoundedHistory<Entry> redo;

        public bool CanUndo => undo.Count > 0;
        public bool CanRedo => redo.Count > 0;
        public long UndoBytes => undo.RetainedBytes;
        public long RedoBytes => redo.RetainedBytes;
        public long MaxBytesPerStack { get; }
        public PointLabelHistoryDomain UndoDomain => CanUndo ? undo.Peek().Domain : PointLabelHistoryDomain.Annotation;
        public PointLabelHistoryDomain RedoDomain => CanRedo ? redo.Peek().Domain : PointLabelHistoryDomain.Annotation;

        public PointLabelEditHistory()
            : this(DefaultCapacity, DefaultMaxBytesPerStack)
        {
        }

        public PointLabelEditHistory(int capacity, long maxBytesPerStack)
        {
            MaxBytesPerStack = maxBytesPerStack;
            undo = new BoundedHistory<Entry>(capacity, maxBytesPerStack, entry => entry.Delta.EstimatedBytes);
            redo = new BoundedHistory<Entry>(capacity, maxBytesPerStack, entry => entry.Delta.EstimatedBytes);
        }

        public bool CanRecord(PointLabelDelta delta) => delta != null && delta.EstimatedBytes <= MaxBytesPerStack;

        public bool Record(PointLabelDelta delta) => Record(delta, PointLabelHistoryDomain.Annotation);

        public bool Record(PointLabelDelta delta, PointLabelHistoryDomain domain)
        {
            if (!CanRecord(delta) || !undo.Push(new Entry(delta, domain))) return false;
            redo.Clear();
            return true;
        }

        public PointLabelDelta PeekUndo() => CanUndo ? undo.Peek().Delta : null;
        public PointLabelDelta PeekRedo() => CanRedo ? redo.Peek().Delta : null;

        public bool CompleteUndo()
        {
            if (!CanUndo || !redo.Push(undo.Peek())) return false;
            undo.Pop();
            return true;
        }

        public bool CompleteRedo()
        {
            if (!CanRedo || !undo.Push(redo.Peek())) return false;
            redo.Pop();
            return true;
        }

        public void ClearRedo() => redo.Clear();

        public void ClearDomain(PointLabelHistoryDomain domain)
        {
            RemoveDomain(undo, domain);
            RemoveDomain(redo, domain);
        }

        public void Clear()
        {
            undo.Clear();
            redo.Clear();
        }

        private static void RemoveDomain(BoundedHistory<Entry> history, PointLabelHistoryDomain domain)
        {
            var retained = new System.Collections.Generic.List<Entry>();
            while (history.Count > 0)
            {
                Entry entry = history.Pop();
                if (entry.Domain != domain) retained.Add(entry);
            }

            for (int i = retained.Count - 1; i >= 0; i--)
                history.Push(retained[i]);
        }
    }
}
