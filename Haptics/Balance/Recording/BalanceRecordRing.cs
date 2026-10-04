using System;

namespace DivebombLogistics.Haptics.Balance.Recording;

/// <summary>
/// Fixed-capacity FIFO of <see cref="BalanceRecord"/> shared between the data thread (producer) and the
/// recorder's writer thread (consumer). When full, the oldest record is overwritten and counted as dropped,
/// so a stalled disk never blocks or grows memory on the data thread.
/// </summary>
/// <remarks>
/// A plain lock keeps this simple and correct: both sides hold it only for a struct copy, and
/// <c>Monitor.Enter</c> does not allocate managed memory.
/// </remarks>
internal sealed class BalanceRecordRing
{
    private readonly object gate = new object();
    private readonly BalanceRecord[] buffer;
    private int head;   // index of the oldest record
    private int count;
    private long dropped;

    public BalanceRecordRing(int capacity)
    {
        if (capacity <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(capacity), "capacity must be positive");
        }

        buffer = new BalanceRecord[capacity];
    }

    public int Capacity => buffer.Length;

    public int Count
    {
        get
        {
            lock (gate)
            {
                return count;
            }
        }
    }

    /// <summary>Records overwritten before the consumer drained them (since construction or <see cref="Clear"/>).</summary>
    public long Dropped
    {
        get
        {
            lock (gate)
            {
                return dropped;
            }
        }
    }

    /// <summary>Appends one tick (allocation-free); drops the oldest record when full.</summary>
    public void Add(VehicleState state, BalanceOutputs outputs)
    {
        lock (gate)
        {
            int index;
            if (count == buffer.Length)
            {
                index = head;
                head = (head + 1) % buffer.Length;
                dropped++;
            }
            else
            {
                index = (head + count) % buffer.Length;
                count++;
            }

            buffer[index].CopyFrom(state, outputs);
        }
    }

    /// <summary>Moves up to <c>destination.Length</c> oldest records into <paramref name="destination"/>; returns how many.</summary>
    public int Drain(BalanceRecord[] destination)
    {
        lock (gate)
        {
            int n = Math.Min(count, destination.Length);
            for (int i = 0; i < n; i++)
            {
                destination[i] = buffer[(head + i) % buffer.Length];
            }

            head = (head + n) % buffer.Length;
            count -= n;
            return n;
        }
    }

    /// <summary>Discards all records and resets the drop counter.</summary>
    public void Clear()
    {
        lock (gate)
        {
            head = 0;
            count = 0;
            dropped = 0;
        }
    }
}
