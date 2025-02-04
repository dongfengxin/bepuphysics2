﻿using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Dynamic;
using System.Linq;
using System.Numerics;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using BepuUtilities.Memory;

namespace BepuUtilities.TestQueue;

/// <summary>
/// Description of a task to be submitted to a <see cref="TaskQueue"/>.
/// </summary>
public unsafe struct Task
{
    /// <summary>
    /// Function to be executed by the task. Takes as arguments the <see cref="Id"/>, <see cref="Context"/> pointer, and executing worker index.
    /// </summary>
    public delegate*<long, void*, int, IThreadDispatcher, void> Function;
    /// <summary>
    /// Context to be passed into the <see cref="Function"/>.
    /// </summary>
    public void* Context;
    /// <summary>
    /// Continuation to be notified after this task completes, if any.
    /// </summary>
    public ContinuationHandle Continuation;
    /// <summary>
    /// User-provided identifier of this task.
    /// </summary>
    public long Id;

    /// <summary>
    /// Creates a new task.
    /// </summary>
    /// <param name="function">Function to be executed by the task. Takes as arguments the <see cref="Id"/>, <see cref="Context"/> pointer, executing worker index, and executing <see cref="IThreadDispatcher"/>.</param>
    /// <param name="context">Context pointer to pass to the <see cref="Function"/>.</param>
    /// <param name="taskId">Id of this task to be passed into the <see cref="Function"/>.</param>
    /// <param name="continuation">Continuation to notify after the completion of this task, if any.</param>
    public Task(delegate*<long, void*, int, IThreadDispatcher, void> function, void* context = null, long taskId = 0, ContinuationHandle continuation = default)
    {
        Function = function;
        Context = context;
        Continuation = continuation;
        Id = taskId;
    }

    /// <summary>
    /// Creates a task from a function.
    /// </summary>
    /// <param name="function">Function to turn into a task.</param>
    public static implicit operator Task(delegate*<long, void*, int, IThreadDispatcher, void> function) => new(function);

    /// <summary>
    /// Runs the task and, if necessary, notifies the associated continuation of its completion.
    /// </summary>
    /// <param name="workerIndex">Worker index to pass to the function.</param> 
    /// <param name="dispatcher">Dispatcher running this task.</param>
    public void Run(int workerIndex, IThreadDispatcher dispatcher)
    {
        Debug.Assert(!Continuation.Completed);
        Function(Id, Context, workerIndex, dispatcher);
        if (Continuation.Initialized)
            Continuation.NotifyTaskCompleted(workerIndex, dispatcher);
    }
}

/// <summary>
/// Describes the result status of a dequeue attempt.
/// </summary>
public enum DequeueTaskResult
{
    /// <summary>
    /// A task was successfully dequeued.
    /// </summary>
    Success,
    /// <summary>
    /// The queue was empty, but may have more tasks in the future.
    /// </summary>
    Empty,
    /// <summary>
    /// The queue has been terminated and all threads seeking work should stop.
    /// </summary>
    Stop
}

/// <summary>
/// Describes the result of a task enqueue attempt.
/// </summary>
public enum EnqueueTaskResult
{
    /// <summary>
    /// The tasks were successfully enqueued.
    /// </summary>
    Success,
    /// <summary>
    /// The enqueue attempt was blocked by concurrent access.
    /// </summary>
    Contested,
    /// <summary>
    /// The enqueue attempt was blocked because no space remained in the tasks buffer.
    /// </summary>
    Full,
}

/// <summary>
/// Refers to a continuation within a <see cref="TaskQueue"/>.
/// </summary>
public unsafe struct ContinuationHandle : IEquatable<ContinuationHandle>
{
    uint index;
    uint encodedVersion;
    /// <summary>
    /// Set of continuations in the queue.
    /// </summary>
    internal TaskQueueContinuations* Continuations;

    internal ContinuationHandle(uint index, int version, TaskQueueContinuations* continuations)
    {
        this.index = index;
        encodedVersion = (uint)version | 1u << 31;
        Continuations = continuations;
    }

    internal uint Index
    {
        get
        {
            Debug.Assert(Initialized, "If you're trying to pull a continuation id from a continuation handle, it should have been initialized.");
            return index;
        }
    }

    internal int Version
    {
        get
        {
            Debug.Assert(Initialized, "If you're trying to pull a continuation id from a continuation handle, it should have been initialized.");
            return (int)(encodedVersion & ((1u << 31) - 1));
        }
    }

    /// <summary>
    /// Gets whether the tasks associated with this continuation have completed.
    /// </summary>
    public bool Completed => Continuations->IsComplete(this);

    /// <summary>
    /// Retrieves a pointer to the continuation data for <see cref="ContinuationHandle"/>.
    /// </summary>
    /// <returns>Pointer to the continuation backing the given handle.</returns>
    /// <remarks>This should not be used if the continuation handle is not known to be valid. The data pointed to by the data could become invalidated if the continuation completes.</remarks>
    public TaskContinuation* Continuation => Continuations->GetContinuation(this);

    /// <summary>
    /// Gets a null continuation handle.
    /// </summary>
    public static ContinuationHandle Null => default;

    /// <summary>
    /// Gets whether the continuation associated with this handle was allocated and has outstanding tasks.
    /// </summary>
    public bool Exists
    {
        get
        {
            if (!Initialized || Continuations == null)
                return false;
            ref var continuation = ref Continuations->Continuations[Index];
            return continuation.Version == Version && continuation.RemainingTaskCounter > 0;
        }
    }

    /// <summary>
    /// Gets whether this handle ever represented an allocated handle. This does not guarantee that the continuation's associated tasks are active in the <see cref="TaskQueue"/> that it was allocated from.
    /// </summary>
    public bool Initialized => encodedVersion >= 1u << 31;

    /// <summary>
    /// Notifies the continuation that one task was completed.
    /// </summary>
    /// <param name="workerIndex">Worker index to pass to the continuation's delegate, if any.</param>
    /// <param name="dispatcher">Dispatcher to pass to the continuation's delegate, if any.</param>
    public void NotifyTaskCompleted(int workerIndex, IThreadDispatcher dispatcher)
    {
        var continuation = Continuation;
        Debug.Assert(!Completed);
        var counter = Interlocked.Decrement(ref continuation->RemainingTaskCounter);
        Debug.Assert(counter >= 0, "The counter should not go negative. Was notify called too many times?");
        if (counter == 0)
        {
            //This entire job has completed.
            if (continuation->OnCompleted.Function != null)
            {
                continuation->OnCompleted.Function(continuation->OnCompleted.Id, continuation->OnCompleted.Context, workerIndex, dispatcher);
            }
            //Free this continuation slot.
            var waiter = new SpinWait();
            while (Interlocked.CompareExchange(ref Continuations->Locker, 1, 0) != 0)
            {
                waiter.SpinOnce(-1);
            }
            //We have the lock.
            Continuations->IndexPool.ReturnUnsafely((int)Index);
            --Continuations->ContinuationCount;
            Continuations->Locker = 0;
        }
    }

    public bool Equals(ContinuationHandle other) => other.index == index && other.encodedVersion == encodedVersion && other.Continuations == Continuations;

    public override bool Equals([NotNullWhen(true)] object obj) => obj is ContinuationHandle handle && Equals(handle);

    public override int GetHashCode() => (int)(index ^ (encodedVersion << 24));

    public static bool operator ==(ContinuationHandle left, ContinuationHandle right) => left.Equals(right);

    public static bool operator !=(ContinuationHandle left, ContinuationHandle right) => !(left == right);
}

/// <summary>
/// Describes the result of a continuation allocation attempt.
/// </summary>
public enum AllocateTaskContinuationResult
{
    /// <summary>
    /// The continuation was successfully allocated.
    /// </summary>
    Success,
    /// <summary>
    /// The continuation was blocked by concurrent access.
    /// </summary>
    Contested,
    /// <summary>
    /// The queue's continuation buffer is full and can't hold the continuation.
    /// </summary>
    Full
}

internal unsafe struct TaskQueueContinuations
{
    public Buffer<TaskContinuation> Continuations;
    public IdPool IndexPool;
    public int ContinuationCount;
    public volatile int Locker;

    /// <summary>
    /// Retrieves a pointer to the continuation data for <see cref="ContinuationHandle"/>.
    /// </summary>
    /// <param name="continuationHandle">Handle to look up the associated continuation for.</param>
    /// <returns>Pointer to the continuation backing the given handle.</returns>
    /// <remarks>This should not be used if the continuation handle is not known to be valid. The data pointed to by the data could become invalidated if the continuation completes.</remarks>
    public TaskContinuation* GetContinuation(ContinuationHandle continuationHandle)
    {
        Debug.Assert(continuationHandle.Initialized, "This continuation handle was never initialized.");
        Debug.Assert(continuationHandle.Index < Continuations.length, "This continuation refers to an invalid index.");
        if (continuationHandle.Index >= Continuations.length || !continuationHandle.Initialized)
            return null;
        var continuation = Continuations.Memory + continuationHandle.Index;
        Debug.Assert(continuation->Version == continuationHandle.Version, "This continuation no longer refers to an active continuation.");
        if (continuation->Version != continuationHandle.Version)
            return null;
        return Continuations.Memory + continuationHandle.Index;
    }

    /// <summary>
    /// Checks whether all tasks associated with this continuation have completed.
    /// </summary>
    /// <param name="continuationHandle">Continuation to check for completion.</param>
    /// <returns>True if all tasks associated with a continuation have completed, false otherwise.</returns>
    public bool IsComplete(ContinuationHandle continuationHandle)
    {
        Debug.Assert(continuationHandle.Initialized, "This continuation handle was never initialized.");
        Debug.Assert(continuationHandle.Index < Continuations.length, "This continuation refers to an invalid index.");
        if (continuationHandle.Index >= Continuations.length || !continuationHandle.Initialized)
            return false;
        ref var continuation = ref Continuations[continuationHandle.Index];
        return continuation.Version > continuationHandle.Version || continuation.RemainingTaskCounter == 0;
    }
}

/// <summary>
/// Stores data relevant to tracking task completion and reporting completion for a continuation.
/// </summary>
public unsafe struct TaskContinuation
{
    /// <summary>
    /// Task to run upon completion of the associated task.
    /// </summary>
    public Task OnCompleted;
    /// <summary>
    /// Version of this continuation.
    /// </summary>
    public int Version;
    /// <summary>
    /// Number of tasks not yet reported as complete in the continuation.
    /// </summary>
    public int RemainingTaskCounter;
}


/// <summary>
/// Multithreaded task queue 
/// </summary>
public unsafe struct TaskQueue
{
    Buffer<Task> tasks;

    int taskMask, taskShift;

    [StructLayout(LayoutKind.Explicit, Size = 540)]
    struct Padded
    {
        //WrittenTaskIndex and TaskIndex are referenced by dequeues, so keep them together.
        //They're *also* referenced by enqueues, but enqueues are rarer so there's a good chance the local thread can avoid a direct contest.
        [FieldOffset(128)]
        public long WrittenTaskIndex;
        [FieldOffset(136)]
        public long TaskIndex;
        //AllocatedTaskIndex and TaskLocker are only touched by enqueues, so we shove them off into their own padded region.
        [FieldOffset(400)]
        public long AllocatedTaskIndex;
        [FieldOffset(408)]
        public volatile int TaskLocker;
    }

    Padded paddedCounters;

    /// <summary>
    /// Holds the task queue's continuations data in unmanaged memory just in case the queue itself is in unpinned memory.
    /// </summary>
    Buffer<TaskQueueContinuations> continuationsContainer;

    /// <summary>
    /// Constructs a new task queue.
    /// </summary>
    /// <param name="pool">Buffer pool to allocate resources from.</param>
    /// <param name="maximumTaskCapacity">Maximum number of tasks to allocate space for. Tasks are individual chunks of scheduled work. Rounded up to the nearest power of 2.</param>
    /// <param name="maximumContinuationCapacity">Maximum number of continuations to allocate space for. If more continuations exist at any one moment, attempts to create new continuations may have to stall until space is available.</param>
    public TaskQueue(BufferPool pool, int maximumTaskCapacity = 1024, int maximumContinuationCapacity = 256)
    {
        maximumTaskCapacity = (int)BitOperations.RoundUpToPowerOf2((uint)maximumTaskCapacity);
        pool.Take(1, out continuationsContainer);
        ref var continuations = ref continuationsContainer[0];
        pool.Take(maximumContinuationCapacity, out continuations.Continuations);
        continuations.IndexPool = new IdPool(maximumContinuationCapacity, pool);
        continuations.ContinuationCount = 0;
        continuations.Locker = 0;

        pool.Take(maximumTaskCapacity, out tasks);
        taskMask = tasks.length - 1;
        taskShift = BitOperations.TrailingZeroCount(tasks.length);
        paddedCounters.TaskLocker = 0;
        Reset();
    }

    /// <summary>
    /// Returns the task queue to a fresh state without reallocating.
    /// </summary>
    public void Reset()
    {
        paddedCounters.TaskIndex = 0;
        paddedCounters.AllocatedTaskIndex = 0;
        paddedCounters.WrittenTaskIndex = 0;
        Debug.Assert(paddedCounters.TaskLocker == 0, "There appears to be a thread actively working still. That's invalid.");

        ref var continuations = ref continuationsContainer[0];
        continuations.Continuations.Clear(0, continuations.Continuations.Length);
        continuations.ContinuationCount = 0;
        Debug.Assert(continuations.Locker == 0, "There appears to be a thread actively working still. That's invalid.");
    }

    /// <summary>
    /// Returns unmanaged resources held by the <see cref="TaskQueue"/> to a pool.
    /// </summary>
    /// <param name="pool">Buffer pool to return resources to.</param>
    public void Dispose(BufferPool pool)
    {
        continuationsContainer[0].IndexPool.Dispose(pool);
        pool.Return(ref continuationsContainer[0].Continuations);
        pool.Return(ref tasks);
        pool.Return(ref continuationsContainer);
    }

    /// <summary>
    /// Gets the queue's capacity for tasks.
    /// </summary>
    public int TaskCapacity => tasks.length;
    /// <summary>
    /// Gets the queue's capacity for continuations.
    /// </summary>
    public int ContinuationCapacity => continuationsContainer[0].Continuations.length;
    /// <summary>
    /// Gets the number of tasks active in the queue.
    /// </summary>
    /// <remarks>Does not take a lock; if other threads are modifying the task values, the reported count may be invalid.</remarks>
    public int UnsafeTaskCount => (int)(paddedCounters.WrittenTaskIndex - paddedCounters.TaskIndex);
    /// <summary>
    /// Gets the number of tasks active in the queue.
    /// </summary>
    public int TaskCount
    {
        get
        {
            var waiter = new SpinWait();
            while (Interlocked.CompareExchange(ref paddedCounters.TaskLocker, 1, 0) != 0)
            {
                waiter.SpinOnce(-1);
            }
            var result = (int)(paddedCounters.WrittenTaskIndex - paddedCounters.TaskIndex);
            paddedCounters.TaskLocker = 0;
            return result;
        }
    }
    /// <summary>
    /// Gets the number of continuations active in the queue.
    /// </summary>
    public int ContinuationCount => continuationsContainer[0].ContinuationCount;

    /// <summary>
    /// Attempts to allocate a continuation for a set of tasks.
    /// </summary>
    /// <param name="taskCount">Number of tasks associated with the continuation.</param>
    /// <param name="onCompleted">Function to execute upon completing all associated tasks, if any. Any task with a null <see cref="Task.Function"/> will not be executed.</param>
    /// <param name="continuationHandle">Handle of the continuation if allocation is successful.</param>
    /// <returns>Result status of the continuation allocation attempt.</returns>
    public AllocateTaskContinuationResult TryAllocateContinuation(int taskCount, out ContinuationHandle continuationHandle, Task onCompleted = default)
    {
        continuationHandle = default;
        ref var continuations = ref continuationsContainer[0];
        if (Interlocked.CompareExchange(ref continuations.Locker, 1, 0) != 0)
            return AllocateTaskContinuationResult.Contested;
        try
        {
            //We have the lock.
            Debug.Assert(continuations.ContinuationCount <= continuations.Continuations.length);
            if (continuations.ContinuationCount == continuations.Continuations.length)
            {
                //No room.
                return AllocateTaskContinuationResult.Full;
            }
            var index = continuations.IndexPool.Take();
            ++continuations.ContinuationCount;
            ref var continuation = ref continuations.Continuations[index];
            var newVersion = continuation.Version + 1;
            continuation.OnCompleted = onCompleted;
            continuation.Version = newVersion;
            continuation.RemainingTaskCounter = taskCount;
            continuationHandle = new ContinuationHandle((uint)index, newVersion, continuationsContainer.Memory);
            return AllocateTaskContinuationResult.Success;
        }
        finally
        {
            continuations.Locker = 0;
        }
    }

    /// <summary>
    /// Allocates a continuation for a set of tasks.
    /// </summary>
    /// <param name="taskCount">Number of tasks associated with the continuation.</param>
    /// <param name="workerIndex">Worker index to pass to any inline executed tasks if the continuations buffer is full.</param>
    /// <param name="dispatcher">Dispatcher to pass to inline executed tasks if the continuations buffer is full.</param>
    /// <param name="onCompleted">Task to execute upon completing all associated tasks, if any. Any task with a null <see cref="Task.Function"/> will not be executed.</param>
    /// <returns>Handle of the allocated continuation.</returns>
    /// <remarks>Note that this will keep trying until allocation succeeds. If something is blocking allocation, such as insufficient room in the continuations buffer and there are no workers consuming tasks, this will block forever.</remarks>
    public ContinuationHandle AllocateContinuation(int taskCount, int workerIndex, IThreadDispatcher dispatcher, Task onCompleted = default)
    {
        var waiter = new SpinWait();
        ContinuationHandle handle;
        AllocateTaskContinuationResult result;
        while ((result = TryAllocateContinuation(taskCount, out handle, onCompleted)) != AllocateTaskContinuationResult.Success)
        {
            if (result == AllocateTaskContinuationResult.Full)
            {
                var dequeueResult = TryDequeueAndRun(workerIndex, dispatcher);
                Debug.Assert(dequeueResult != DequeueTaskResult.Stop, "We're trying to allocate a continuation, we shouldn't have run into a stop command!");
            }
            else
            {
                waiter.SpinOnce(-1);
            }
        }
        return handle;
    }

    /// <summary>
    /// Attempts to dequeue a task.
    /// </summary>
    /// <param name="task">Dequeued task, if any.</param>
    /// <returns>Result status of the dequeue attempt.</returns>
    public DequeueTaskResult TryDequeue(out Task task)
    {
        task = default;
        while (true)
        {
            long nextTaskIndex, sampledWrittenTaskIndex;
            //Note that there is no lock taken. We sample the currently visible values and treat the dequeue as a transaction.
            //If the transaction fails, we don't make any changes and try again.
            if (Environment.Is64BitProcess) //This branch is compile time (at least where I've tested it).
            {
                //It's fine if we don't get a consistent view of the task index and written task index. Worst case scenario, this will claim that the queue is empty where a lock wouldn't.
                nextTaskIndex = Volatile.Read(ref paddedCounters.TaskIndex);
                sampledWrittenTaskIndex = Volatile.Read(ref paddedCounters.WrittenTaskIndex);
            }
            else
            {
                //Interlocked reads for 32 bit systems.
                nextTaskIndex = Interlocked.Read(ref paddedCounters.TaskIndex);
                sampledWrittenTaskIndex = Interlocked.Read(ref paddedCounters.WrittenTaskIndex);
            }
            if (nextTaskIndex >= sampledWrittenTaskIndex)
                return DequeueTaskResult.Empty;
            ref var taskCandidate = ref tasks[(int)(nextTaskIndex & taskMask)];
            if (taskCandidate.Function == null)
                return DequeueTaskResult.Stop;
            //Unlike enqueues, a dequeue has a fixed contention window on a single value. There's not much point in using a spinwait when there's no reason to expect our next attempt to be blocked.
            if (Interlocked.CompareExchange(ref paddedCounters.TaskIndex, nextTaskIndex + 1, nextTaskIndex) != nextTaskIndex)
                continue;
            //There's an actual job!
            task = taskCandidate;
            Debug.Assert(!task.Continuation.Completed);
            return DequeueTaskResult.Success;
        }
    }

    /// <summary>
    /// Attempts to dequeue a task and run it.
    /// </summary>
    /// <param name="workerIndex">Index of the worker to pass into the task function.</param>
    /// <param name="dispatcher">Thread dispatcher running this task queue.</param>
    /// <returns>Result status of the dequeue attempt.</returns>
    public DequeueTaskResult TryDequeueAndRun(int workerIndex, IThreadDispatcher dispatcher)
    {
        var result = TryDequeue(out var task);
        if (result == DequeueTaskResult.Success)
        {
            task.Run(workerIndex, dispatcher);
        }
        return result;
    }

    EnqueueTaskResult TryEnqueueUnsafelyInternal(Span<Task> tasks, out long taskEndIndex)
    {
        Debug.Assert(tasks.Length > 0, "Probably shouldn't be trying to enqueue zero tasks.");
        Debug.Assert(paddedCounters.WrittenTaskIndex == 0 || this.tasks[(int)((paddedCounters.WrittenTaskIndex - 1) & taskMask)].Function != null, "No more jobs should be written after a stop command.");
        var taskStartIndex = paddedCounters.AllocatedTaskIndex;
        taskEndIndex = taskStartIndex + tasks.Length;
        if (taskEndIndex - paddedCounters.TaskIndex > this.tasks.length)
        {
            //We've run out of space in the ring buffer. If we tried to write, we'd overwrite jobs that haven't yet been completed.
            return EnqueueTaskResult.Full;
        }
        paddedCounters.AllocatedTaskIndex = taskEndIndex;
        Debug.Assert(BitOperations.IsPow2(this.tasks.Length));
        var wrappedInclusiveStartIndex = (int)(taskStartIndex & taskMask);
        //Note - 1; the taskEndIndex is exclusive. Working with the inclusive index means we don't have to worry about an end index of tasks.Length being wrapped to 0.
        var wrappedInclusiveEndIndex = (int)((taskEndIndex - 1) & taskMask);
        if (wrappedInclusiveEndIndex >= wrappedInclusiveStartIndex)
        {
            //We can just copy the whole task block as one blob.
            Unsafe.CopyBlockUnaligned(ref *(byte*)(this.tasks.Memory + wrappedInclusiveStartIndex), ref Unsafe.As<Task, byte>(ref MemoryMarshal.GetReference(tasks)), (uint)(Unsafe.SizeOf<Task>() * tasks.Length));
        }
        else
        {
            //Copy the task block as two blobs.
            ref var startTask = ref tasks[0];
            var firstRegionCount = this.tasks.length - wrappedInclusiveStartIndex;
            ref var secondBlobStartTask = ref tasks[firstRegionCount];
            var secondRegionCount = tasks.Length - firstRegionCount;
            Unsafe.CopyBlockUnaligned(ref *(byte*)(this.tasks.Memory + wrappedInclusiveStartIndex), ref Unsafe.As<Task, byte>(ref startTask), (uint)(Unsafe.SizeOf<Task>() * firstRegionCount));
            Unsafe.CopyBlockUnaligned(ref *(byte*)this.tasks.Memory, ref Unsafe.As<Task, byte>(ref secondBlobStartTask), (uint)(Unsafe.SizeOf<Task>() * secondRegionCount));
        }
        //for (int i = 0; i < tasks.Length; ++i)
        //{
        //    var taskIndex = (int)((i + taskStartIndex) & taskMask);
        //    this.tasks[taskIndex] = tasks[i];
        //}
        return EnqueueTaskResult.Success;
    }
    /// <summary>
    /// Tries to appends a set of tasks to the task queue. Does not acquire a lock; cannot return <see cref="EnqueueTaskResult.Contested"/>.
    /// </summary>
    /// <param name="tasks">Tasks composing the job.</param>
    /// <returns>Result of the enqueue attempt.</returns>
    /// <remarks>This must not be used while other threads could be performing task enqueues or task dequeues.</remarks>
    public EnqueueTaskResult TryEnqueueUnsafely(Span<Task> tasks)
    {
        EnqueueTaskResult result;
        if ((result = TryEnqueueUnsafelyInternal(tasks, out var taskEndIndex)) == EnqueueTaskResult.Success)
        {
            paddedCounters.WrittenTaskIndex = taskEndIndex;
        }
        return result;
    }
    /// <summary>
    /// Tries to appends a task to the task queue. Does not acquire a lock; cannot return <see cref="EnqueueTaskResult.Contested"/>.
    /// </summary>
    /// <param name="task">Task to enqueue</param>
    /// <returns>Result of the enqueue attempt.</returns>
    /// <remarks>This must not be used while other threads could be performing task enqueues or task dequeues.</remarks>
    public EnqueueTaskResult TryEnqueueUnsafely(Task task)
    {
        return TryEnqueueUnsafely(new Span<Task>(&task, 1));
    }

    /// <summary>
    /// Tries to appends a set of tasks to the task queue if the ring buffer is uncontested.
    /// </summary>
    /// <param name="tasks">Tasks composing the job.</param>
    /// <returns>Result of the enqueue attempt.</returns>
    public EnqueueTaskResult TryEnqueue(Span<Task> tasks)
    {
        if (tasks.Length == 0)
            return EnqueueTaskResult.Success;
        if (Interlocked.CompareExchange(ref paddedCounters.TaskLocker, 1, 0) != 0)
            return EnqueueTaskResult.Contested;
        try
        {
            //We have the lock.
            EnqueueTaskResult result;
            if ((result = TryEnqueueUnsafelyInternal(tasks, out var taskEndIndex)) == EnqueueTaskResult.Success)
            {
                if (Environment.Is64BitProcess)
                {
                    Volatile.Write(ref paddedCounters.WrittenTaskIndex, taskEndIndex);
                }
                else
                {
                    Interlocked.Exchange(ref paddedCounters.WrittenTaskIndex, taskEndIndex);
                }
            }
            return result;
        }
        finally
        {
            paddedCounters.TaskLocker = 0;
        }
    }

    /// <summary>
    /// Appends a set of tasks to the queue.
    /// </summary>
    /// <param name="tasks">Tasks composing the job.</param>
    /// <param name="workerIndex">Worker index to pass to inline-executed tasks if the task buffer is full.</param>
    /// <param name="dispatcher">Dispatcher to pass to inline-executed tasks if the task buffer is full.</param>
    /// <remarks>Note that this will keep trying until task submission succeeds. 
    /// If the task queue is full, this will opt to run some tasks inline while waiting for room.</remarks>
    public void Enqueue(Span<Task> tasks, int workerIndex, IThreadDispatcher dispatcher)
    {
        var waiter = new SpinWait();
        EnqueueTaskResult result;
        while ((result = TryEnqueue(tasks)) != EnqueueTaskResult.Success)
        {
            if (result == EnqueueTaskResult.Full)
            {
                //Couldn't enqueue the tasks because the task buffer is full.
                //Clearly there's plenty of work available to execute, so go ahead and try to run one task inline.
                var task = tasks[0];
                task.Run(workerIndex, dispatcher);
                if (tasks.Length == 1)
                    break;
                tasks = tasks[1..];
            }
            else
            {
                waiter.SpinOnce(-1);
            }
        }
    }

    /// <summary>
    /// Appends a set of tasks to the queue. Creates a continuation for the tasks.
    /// </summary>
    /// <param name="tasks">Tasks composing the job. A continuation will be assigned internally; no continuation should be present on any of the provided tasks.</param>
    /// <param name="workerIndex">Worker index to pass to inline-executed tasks if the task buffer is full.</param>
    /// <param name="dispatcher">Dispatcher to pass to inline-executed tasks if the task buffer is full.</param>
    /// <param name="onComplete">Task to run upon completion of all the submitted tasks, if any.</param>
    /// <returns>Handle of the continuation created for these tasks.</returns>
    /// <remarks>Note that this will keep trying until task submission succeeds. 
    /// If the task queue is full, this will opt to run some tasks inline while waiting for room.</remarks>
    public ContinuationHandle AllocateContinuationAndEnqueue(Span<Task> tasks, int workerIndex, IThreadDispatcher dispatcher, Task onComplete = default)
    {
        var continuationHandle = AllocateContinuation(tasks.Length, workerIndex, dispatcher);
        for (int i = 0; i < tasks.Length; ++i)
        {
            ref var task = ref tasks[i];
            Debug.Assert(!task.Continuation.Initialized, "This function creates a continuation for the tasks");
            task.Continuation = continuationHandle;
        }
        Enqueue(tasks, workerIndex, dispatcher);
        return continuationHandle;
    }

    /// <summary>
    /// Waits for a continuation to be completed.
    /// </summary>
    /// <remarks>Instead of spinning the entire time, this may dequeue and execute pending tasks to fill the gap.</remarks>
    /// <param name="continuation">Continuation to wait on.</param>
    /// <param name="workerIndex">Currently executing worker index to pass to any tasks used to fill the wait period.</param>
    /// <param name="dispatcher">Currently executing dispatcher to pass to any tasks used to fill the wait period.</param>
    public void WaitForCompletion(ContinuationHandle continuation, int workerIndex, IThreadDispatcher dispatcher)
    {
        var waiter = new SpinWait();
        Debug.Assert(continuation.Initialized, "This codepath should only run if the continuation was allocated earlier.");
        while (!continuation.Completed)
        {
            //Note that we don't handle the DequeueResult.Stop case; if the job isn't complete yet, there's no way to hit a stop unless we enqueued this job after a stop.
            //Enqueuing after a stop is an error condition and is debug checked for in TryEnqueueJob.
            var dequeueResult = TryDequeue(out var fillerTask);
            if (dequeueResult == DequeueTaskResult.Stop)
            {
                Debug.Assert(dequeueResult != DequeueTaskResult.Stop, "Did you enqueue these tasks *after* some thread enqueued a stop command? That's illegal!");
                return;
            }
            if (dequeueResult == DequeueTaskResult.Success)
            {
                fillerTask.Run(workerIndex, dispatcher);
                waiter.Reset();
            }
            else
            {
                waiter.SpinOnce(-1);
            }
        }
    }

    /// <summary>
    /// Appends a set of tasks to the queue and returns when all tasks are complete.
    /// </summary>
    /// <param name="tasks">Tasks composing the job. A continuation will be assigned internally; no continuation should be present on any of the provided tasks.</param>
    /// <param name="workerIndex">Worker index to pass to inline-executed tasks if the task buffer is full.</param>
    /// <param name="dispatcher">Dispatcher to pass to inline-executed tasks if the task buffer is full.</param>
    /// <remarks>Note that this will keep working until all tasks are run.
    /// If the task queue is full, this will opt to run some tasks inline while waiting for room.</remarks>
    public void RunTasks(Span<Task> tasks, int workerIndex, IThreadDispatcher dispatcher)
    {
        if (tasks.Length == 0)
            return;
        ContinuationHandle continuationHandle = default;
        if (tasks.Length > 1)
        {
            //Note that we only submit tasks to the queue for tasks beyond the first. The current thread is responsible for at least task 0.
            var taskCount = tasks.Length - 1;
            Span<Task> tasksToEnqueue = stackalloc Task[taskCount];
            continuationHandle = AllocateContinuation(taskCount, workerIndex, dispatcher);
            for (int i = 0; i < tasksToEnqueue.Length; ++i)
            {
                var task = tasks[i + 1];
                Debug.Assert(continuationHandle.Continuations == continuationsContainer.Memory);
                Debug.Assert(!task.Continuation.Initialized, $"None of the source tasks should have continuations when provided to {nameof(RunTasks)}.");
                task.Continuation = continuationHandle;
                tasksToEnqueue[i] = task;
            }
            Enqueue(tasksToEnqueue, workerIndex, dispatcher);
        }
        //Tasks [1, count) are submitted to the queue and may now be executing on other workers.
        //The thread calling the for loop should not relinquish its timeslice. It should immediately begin working on task 0.
        var task0 = tasks[0];
        Debug.Assert(!task0.Continuation.Initialized, $"None of the source tasks should have continuations when provided to {nameof(RunTasks)}.");
        task0.Function(task0.Id, task0.Context, workerIndex, dispatcher);

        if (tasks.Length > 1)
        {
            //Task 0 is done; this thread should seek out other work until the job is complete.
            WaitForCompletion(continuationHandle, workerIndex, dispatcher);
        }
    }

    /// <summary>
    /// Tries to enqueues the stop command. 
    /// </summary>
    /// <returns>Result status of the enqueue attempt.</returns>
    public EnqueueTaskResult TryEnqueueStop()
    {
        Span<Task> stopJob = stackalloc Task[1];
        stopJob[0] = new Task { Function = null };
        return TryEnqueue(stopJob);
    }

    /// <summary>
    /// Enqueues the stop command. 
    /// </summary>
    /// <param name="workerIndex">Worker index to pass to any inline executed tasks if the task buffer is full.</param>
    /// <param name="dispatcher">Dispatcher to pass to any inline executed tasks if the task buffer is full.</param>
    /// <remarks>Note that this will keep trying until task submission succeeds.
    /// If the task buffer is full, this may attempt to dequeue tasks to run inline.</remarks>
    public void EnqueueStop(int workerIndex, IThreadDispatcher dispatcher)
    {
        Span<Task> stopJob = stackalloc Task[1];
        stopJob[0] = new Task { Function = null };
        var waiter = new SpinWait();
        EnqueueTaskResult result;
        while ((result = TryEnqueue(stopJob)) != EnqueueTaskResult.Success)
        {
            if (result == EnqueueTaskResult.Full)
            {
                var dequeueResult = TryDequeueAndRun(workerIndex, dispatcher);
                Debug.Assert(dequeueResult != DequeueTaskResult.Stop, "We're trying to enqueue a stop, we shouldn't have found one already present!");
            }
            else
            {
                waiter.SpinOnce(-1);
            }
        }
    }

    /// <summary>
    /// Tries to enqueues the stop command. Does not take a lock; cannot return <see cref="EnqueueTaskResult.Contested"/>.
    /// </summary>
    /// <returns>Result status of the enqueue attempt.</returns>
    /// <remarks>This must not be used while other threads could be performing task enqueues or task dequeues.</remarks>
    public EnqueueTaskResult TryEnqueueStopUnsafely()
    {
        Span<Task> stopJob = stackalloc Task[1];
        stopJob[0] = new Task { Function = null };
        return TryEnqueueUnsafely(stopJob);
    }

    /// <summary>
    /// Enqueues a for loop onto the task queue. Does not take a lock; cannot return <see cref="EnqueueTaskResult.Contested"/>.
    /// </summary>
    /// <param name="function">Function to execute on each iteration of the loop.</param>
    /// <param name="context">Context pointer to pass into each task execution.</param>
    /// <param name="inclusiveStartIndex">Inclusive start index of the loop range.</param>
    /// <param name="iterationCount">Number of iterations to perform.</param>
    /// <param name="continuation">Continuation associated with the loop tasks, if any.</param>
    /// <returns>Status result of the enqueue operation.</returns>
    /// <remarks>This must not be used while other threads could be performing task enqueues or task dequeues.</remarks>
    public EnqueueTaskResult TryEnqueueForUnsafely(delegate*<long, void*, int, IThreadDispatcher, void> function, void* context, int inclusiveStartIndex, int iterationCount, ContinuationHandle continuation = default)
    {
        Span<Task> tasks = stackalloc Task[iterationCount];
        for (int i = 0; i < tasks.Length; ++i)
        {
            tasks[i] = new Task { Function = function, Context = context, Id = i + inclusiveStartIndex, Continuation = continuation };
        }
        return TryEnqueueUnsafely(tasks);
    }

    /// <summary>
    /// Enqueues a for loop onto the task queue.
    /// </summary>
    /// <param name="function">Function to execute on each iteration of the loop.</param>
    /// <param name="context">Context pointer to pass into each task execution.</param>
    /// <param name="inclusiveStartIndex">Inclusive start index of the loop range.</param>
    /// <param name="iterationCount">Number of iterations to perform.</param>
    /// <param name="workerIndex">Worker index to pass to any inline-executed task if the task queue is full.</param>
    /// <param name="dispatcher">Dispatcher to pass to any inline-executed task if the task queue is full.</param>
    /// <param name="continuation">Continuation associated with the loop tasks, if any.</param>
    /// <remarks>This function will not usually attempt to run any iterations of the loop itself. It tries to push the loop tasks onto the queue.<para/>
    /// If the task queue is full, this will opt to run the tasks inline while waiting for room.</remarks>
    public void EnqueueFor(delegate*<long, void*, int, IThreadDispatcher, void> function, void* context, int inclusiveStartIndex, int iterationCount, int workerIndex, IThreadDispatcher dispatcher, ContinuationHandle continuation = default)
    {
        Span<Task> tasks = stackalloc Task[iterationCount];
        for (int i = 0; i < tasks.Length; ++i)
        {
            tasks[i] = new Task { Function = function, Context = context, Id = i + inclusiveStartIndex, Continuation = continuation };
        }
        Enqueue(tasks, workerIndex, dispatcher);
    }

    /// <summary>
    /// Submits a set of tasks representing a for loop over the given indices and returns when all loop iterations are complete.
    /// </summary>
    /// <param name="function">Function to execute on each iteration of the loop.</param>
    /// <param name="context">Context pointer to pass into each iteration of the loop.</param>
    /// <param name="inclusiveStartIndex">Inclusive start index of the loop range.</param>
    /// <param name="iterationCount">Number of iterations to perform.</param>
    /// <param name="workerIndex">Index of the currently executing worker.</param>
    /// <param name="dispatcher">Currently executing thread dispatcher.</param>
    public void For(delegate*<long, void*, int, IThreadDispatcher, void> function, void* context, int inclusiveStartIndex, int iterationCount, int workerIndex, IThreadDispatcher dispatcher)
    {
        if (iterationCount <= 0)
            return;
        Span<Task> tasks = stackalloc Task[iterationCount];
        for (int i = 0; i < tasks.Length; ++i)
        {
            tasks[i] = new Task(function, context, inclusiveStartIndex + i);
        }
        RunTasks(tasks, workerIndex, dispatcher);
    }
}
