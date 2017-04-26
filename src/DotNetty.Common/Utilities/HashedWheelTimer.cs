﻿// Copyright (c) Microsoft. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.


#pragma warning disable 420

namespace DotNetty.Common.Utilities
{
    using System;
    using System.Collections.Generic;
    using System.Diagnostics;
    using System.Diagnostics.Contracts;
    using System.Text;
    using System.Threading;
    using System.Threading.Tasks;
    using DotNetty.Common.Concurrency;
    using DotNetty.Common.Internal;
    using DotNetty.Common.Internal.Logging;

    public sealed class HashedWheelTimer : ITimer
    {
        static readonly IInternalLogger Logger =
            InternalLoggerFactory.GetInstance<HashedWheelTimer>();

        static int instanceCounter;
        static int warnedTooManyInstances;

        const int InstanceCountLimit = 64;

        readonly Worker worker;
        readonly XThread workerThread;
        readonly CancellationTokenSource cancellationTokenSource = new CancellationTokenSource();

        const int WorkerStateInit = 0;
        const int WorkerStateStarted = 1;
        const int WorkerStateShutdown = 2;
        int workerStateVolatile = WorkerStateInit; // 0 - init, 1 - started, 2 - shut down

        readonly long tickDuration;
        readonly HashedWheelBucket[] wheel;
        readonly int mask;
        readonly CountdownEvent startTimeInitialized = new CountdownEvent(1);
        readonly IQueue<HashedWheelTimeout> timeouts = PlatformDependent.NewMpscQueue<HashedWheelTimeout>();
        readonly IQueue<HashedWheelTimeout> cancelledTimeouts = PlatformDependent.NewMpscQueue<HashedWheelTimeout>();
        internal long PendingTimeouts;
        readonly long maxPendingTimeouts;
        long startTimeVolatile;

        public HashedWheelTimer()
            : this(TimeSpan.FromMilliseconds(100), 512, -1)
        {
        }

        /// <summary>
        /// Creates a new timer.
        /// </summary>
        /// <param name="tickDuration">the duration between tick</param>
        /// <param name="ticksPerWheel">the size of the wheel</param>
        /// <param name="maxPendingTimeouts">The maximum number of pending timeouts after which call to
        /// <c>newTimeout</c> will result in <see cref="RejectedExecutionException"/> being thrown.
        /// No maximum pending timeouts limit is assumed if this value is 0 or negative.</param>
        /// <exception cref="ArgumentException">if either of <c>tickDuration</c> and <c>ticksPerWheel</c> is &lt;= 0</exception>
        public HashedWheelTimer(
            TimeSpan tickDuration,
            int ticksPerWheel,
            long maxPendingTimeouts)
        {
            if (tickDuration <= TimeSpan.Zero)
            {
                throw new ArgumentException("tickDuration must be greater than 0: " + tickDuration);
            }
            if (Math.Ceiling(tickDuration.TotalMilliseconds) > int.MaxValue)
            {
                throw new ArgumentException($"{nameof(tickDuration)} must be less than or equal to ${int.MaxValue} ms.");
            }
            if (ticksPerWheel <= 0)
            {
                throw new ArgumentException("ticksPerWheel must be greater than 0: " + ticksPerWheel);
            }

            // Normalize ticksPerWheel to power of two and initialize the wheel.
            this.wheel = CreateWheel(ticksPerWheel);
            this.worker = new Worker(this);
            this.mask = this.wheel.Length - 1;

            this.tickDuration = tickDuration.Ticks;

            // Prevent overflow
            if (this.tickDuration >= long.MaxValue / this.wheel.Length)
            {
                throw new ArgumentException(
                    string.Format(
                        "tickDuration: {0} (expected: 0 < tickDuration in nanos < {1}",
                        tickDuration,
                        long.MaxValue / this.wheel.Length));
            }
            this.workerThread = new XThread(st => this.worker.Run());

            this.maxPendingTimeouts = maxPendingTimeouts;

            if (Interlocked.Increment(ref instanceCounter) > InstanceCountLimit &&
                Interlocked.CompareExchange(ref warnedTooManyInstances, 1, 0) == 0)
            {
                ReportTooManyInstances();
            }
        }

        ~HashedWheelTimer()
        {
            // This object is going to be GCed and it is assumed the ship has sailed to do a proper shutdown. If
            // we have not yet shutdown then we want to make sure we decrement the active instance count.
            if (Interlocked.Exchange(ref this.workerStateVolatile, WorkerStateShutdown) != WorkerStateShutdown)
            {
                Interlocked.Decrement(ref instanceCounter);
            }
        }

        internal CancellationToken CancellationToken => this.cancellationTokenSource.Token;

        PreciseTimeSpan StartTime
        {
            get { return PreciseTimeSpan.FromTicks(Volatile.Read(ref this.startTimeVolatile)); }
            set { Volatile.Write(ref this.startTimeVolatile, value.Ticks); }
        }

        static HashedWheelBucket[] CreateWheel(int ticksPerWheel)
        {
            if (ticksPerWheel <= 0)
            {
                throw new ArgumentException(
                    "ticksPerWheel must be greater than 0: " + ticksPerWheel);
            }
            if (ticksPerWheel > 1073741824)
            {
                throw new ArgumentException(
                    "ticksPerWheel may not be greater than 2^30: " + ticksPerWheel);
            }

            ticksPerWheel = NormalizeTicksPerWheel(ticksPerWheel);
            var wheel = new HashedWheelBucket[ticksPerWheel];
            for (int i = 0; i < wheel.Length; i++)
            {
                wheel[i] = new HashedWheelBucket();
            }
            return wheel;
        }

        static int NormalizeTicksPerWheel(int ticksPerWheel)
        {
            int normalizedTicksPerWheel = 1;
            while (normalizedTicksPerWheel < ticksPerWheel)
            {
                normalizedTicksPerWheel <<= 1;
            }
            return normalizedTicksPerWheel;
        }

        /// <summary>
        /// Starts the background thread explicitly. The background thread will
        /// start automatically on demand even if you did not call this method.
        /// </summary>
        /// <exception cref="InvalidOperationException">if this timer has been
        /// stopped already.</exception>
        public void Start()
        {
            switch (Volatile.Read(ref this.workerStateVolatile))
            {
                case WorkerStateInit:
                    if (Interlocked.CompareExchange(ref this.workerStateVolatile, WorkerStateStarted, WorkerStateInit) == WorkerStateInit)
                    {
                        this.workerThread.Start();
                    }
                    break;
                case WorkerStateStarted:
                    break;
                case WorkerStateShutdown:
                    throw new InvalidOperationException("cannot be started once stopped");
                default:
                    throw new InvalidOperationException("Invalid WorkerState");
            }

            // Wait until the startTime is initialized by the worker.
            while (this.StartTime == PreciseTimeSpan.Zero)
            {
                this.startTimeInitialized.Wait(this.CancellationToken);
            }
        }

        public ISet<ITimeout> Stop()
        {
            if (XThread.CurrentThread == this.workerThread)
            {
                throw new InvalidOperationException($"{nameof(HashedWheelTimer)}.stop() cannot be called from timer task.");
            }

            if (Interlocked.CompareExchange(ref this.workerStateVolatile, WorkerStateShutdown, WorkerStateStarted) != WorkerStateStarted)
            {
                // workerState can be 0 or 2 at this moment - let it always be 2.
                if (Interlocked.Exchange(ref this.workerStateVolatile, WorkerStateShutdown) != WorkerStateShutdown)
                {
                    Interlocked.Decrement(ref instanceCounter);
                }

                return new HashSet<ITimeout>();
            }

            try
            {
                this.cancellationTokenSource.Cancel();
                this.workerThread.Join(100);
            }
            finally
            {
                Interlocked.Decrement(ref instanceCounter);
            }
            return this.worker.UnprocessedTimeouts();
        }

        public ITimeout NewTimeout(ITimerTask task, TimeSpan delay)
        {
            if (task == null)
            {
                throw new ArgumentNullException(nameof(task));
            }
            if (this.ShouldLimitTimeouts())
            {
                long pendingTimeoutsCount = Interlocked.Increment(ref this.PendingTimeouts);
                if (pendingTimeoutsCount > this.maxPendingTimeouts)
                {
                    Interlocked.Decrement(ref this.PendingTimeouts);
                    throw new RejectedExecutionException($"Number of pending timeouts ({pendingTimeoutsCount}) is greater than or equal to maximum allowed pending timeouts ({this.maxPendingTimeouts})");
                }
            }

            this.Start();

            // Add the timeout to the timeout queue which will be processed on the next tick.
            // During processing all the queued HashedWheelTimeouts will be added to the correct HashedWheelBucket.
            TimeSpan deadline = CeilTimeSpanToMilliseconds((PreciseTimeSpan.Deadline(delay) - this.StartTime).ToTimeSpan());
            var timeout = new HashedWheelTimeout(this, task, deadline);
            this.timeouts.TryEnqueue(timeout);
            return timeout;
        }

        bool ShouldLimitTimeouts() => this.maxPendingTimeouts > 0;

        static void ReportTooManyInstances() =>
            Logger.Error($"You are creating too many {nameof(HashedWheelTimer)} instances. {nameof(HashedWheelTimer)} is a shared resource that must be reused across the process,so that only a few instances are created.");

        static TimeSpan CeilTimeSpanToMilliseconds(TimeSpan time)
        {
            long remainder = time.Ticks % TimeSpan.TicksPerMillisecond;
            return remainder == 0 ? time : new TimeSpan(time.Ticks - remainder + TimeSpan.TicksPerMillisecond);
        }

        sealed class Worker : IRunnable
        {
            readonly HashedWheelTimer owner;
            readonly ISet<ITimeout> unprocessedTimeouts = new HashSet<ITimeout>();

            long tick;

            public Worker(HashedWheelTimer owner)
            {
                this.owner = owner;
            }

            public void Run()
            {
                try
                {
                    // Initialize the startTime.
                    this.owner.StartTime = PreciseTimeSpan.FromStart;
                    if (this.owner.StartTime == PreciseTimeSpan.Zero)
                    {
                        // We use 0 as an indicator for the uninitialized value here, so make sure it's not 0 when initialized.
                        this.owner.StartTime = PreciseTimeSpan.FromTicks(1);
                    }

                    // Notify the other threads waiting for the initialization at start().
                    this.owner.startTimeInitialized.Signal();

                    do
                    {
                        TimeSpan deadline = this.WaitForNextTick();
                        if (deadline > TimeSpan.Zero)
                        {
                            int idx = (int)(this.tick & this.owner.mask);
                            this.ProcessCancelledTasks();
                            HashedWheelBucket bucket = this.owner.wheel[idx];
                            this.TransferTimeoutsToBuckets();
                            bucket.ExpireTimeouts(deadline);
                            this.tick++;
                        }
                    }
                    while (Volatile.Read(ref this.owner.workerStateVolatile) == WorkerStateStarted);

                    // Fill the unprocessedTimeouts so we can return them from stop() method.
                    foreach (HashedWheelBucket bucket in this.owner.wheel)
                    {
                        bucket.ClearTimeouts(this.unprocessedTimeouts);
                    }
                    while (true)
                    {
                        HashedWheelTimeout timeout;
                        if (!this.owner.timeouts.TryDequeue(out timeout))
                        {
                            break;
                        }
                        if (!timeout.Cancelled)
                        {
                            this.unprocessedTimeouts.Add(timeout);
                        }
                    }
                    this.ProcessCancelledTasks();
                }
                catch (Exception ex)
                {
                    Logger.Error("Timeout processing failed.", ex);
                }
            }

            void TransferTimeoutsToBuckets()
            {
                // transfer only max. 100000 timeouts per tick to prevent a thread to stale the workerThread when it just
                // adds new timeouts in a loop.
                for (int i = 0; i < 100000; i++)
                {
                    HashedWheelTimeout timeout;
                    if (!this.owner.timeouts.TryDequeue(out timeout))
                    {
                        // all processed
                        break;
                    }
                    if (timeout.State == HashedWheelTimeout.StCancelled)
                    {
                        // Was cancelled in the meantime.
                        continue;
                    }

                    long calculated = (timeout.Deadline.Ticks + this.owner.tickDuration - 1) / this.owner.tickDuration; // ceiling to timeout later rather than earlier
                    timeout.RemainingRounds = (calculated - this.tick) / this.owner.wheel.Length;

                    long ticks = Math.Max(calculated, this.tick); // Ensure we don't schedule for past.
                    int stopIndex = (int)(ticks & this.owner.mask);

                    HashedWheelBucket bucket = this.owner.wheel[stopIndex];
                    bucket.AddTimeout(timeout);
                }
            }

            void ProcessCancelledTasks()
            {
                while (true)
                {
                    HashedWheelTimeout timeout;
                    if (!this.owner.cancelledTimeouts.TryDequeue(out timeout))
                    {
                        // all processed
                        break;
                    }
                    try
                    {
                        timeout.Remove();
                    }
                    catch (Exception ex)
                    {
                        if (Logger.WarnEnabled)
                        {
                            Logger.Warn("An exception was thrown while processing a cancellation task", ex);
                        }
                    }
                }
            }

            /// <summary>
            /// calculate timer firing time from startTime and current tick number,
            /// then wait until that goal has been reached.
            /// </summary>
            /// <returns>long.MinValue if received a shutdown request,
            /// current time otherwise (with long.MinValue changed by +1)
            /// </returns>
            TimeSpan WaitForNextTick()
            {
                long deadline = this.owner.tickDuration * (this.tick + 1);

                while (true)
                {
                    TimeSpan currentTime = (PreciseTimeSpan.FromStart - this.owner.StartTime).ToTimeSpan();
                    TimeSpan sleepTime = CeilTimeSpanToMilliseconds(TimeSpan.FromTicks(deadline - currentTime.Ticks));

                    if (sleepTime <= TimeSpan.Zero)
                    {
                        if (currentTime.Ticks == long.MinValue)
                        {
                            return TimeSpan.FromTicks(-long.MaxValue);
                        }
                        else
                        {
                            return currentTime;
                        }
                    }

                    Task delay = null;
                    try
                    {
                        long sleepTimeMs = sleepTime.Ticks / TimeSpan.TicksPerMillisecond; // we've already rounded so no worries about the remainder > 0 here
                        Contract.Assert(sleepTimeMs <= int.MaxValue);
                        delay = Task.Delay((int)sleepTimeMs, this.owner.CancellationToken);
                        delay.Wait();
                    }
                    catch (AggregateException) when (delay != null && delay.IsCanceled)
                    {
                        if (Volatile.Read(ref this.owner.workerStateVolatile) == WorkerStateShutdown)
                        {
                            return TimeSpan.FromTicks(long.MinValue);
                        }
                    }
                }
            }

            internal ISet<ITimeout> UnprocessedTimeouts() => this.unprocessedTimeouts;
        }

        sealed class HashedWheelTimeout : ITimeout
        {
            const int StInit = 0;
            internal const int StCancelled = 1;
            const int StExpired = 2;

            internal readonly HashedWheelTimer timer;
            internal readonly TimeSpan Deadline;

            volatile int state = StInit;

            // remainingRounds will be calculated and set by Worker.transferTimeoutsToBuckets() before the
            // HashedWheelTimeout will be added to the correct HashedWheelBucket.
            internal long RemainingRounds;

            // This will be used to chain timeouts in HashedWheelTimerBucket via a double-linked-list.
            // As only the workerThread will act on it there is no need for synchronization / volatile.
            internal HashedWheelTimeout Next;

            internal HashedWheelTimeout Prev;

            // The bucket to which the timeout was added
            internal HashedWheelBucket Bucket;

            internal HashedWheelTimeout(HashedWheelTimer timer, ITimerTask task, TimeSpan deadline)
            {
                this.timer = timer;
                this.Task = task;
                this.Deadline = deadline;
            }

            public ITimer Timer => this.timer;

            public ITimerTask Task { get; }

            public bool Cancel()
            {
                // only update the state it will be removed from HashedWheelBucket on next tick.
                if (!this.CompareAndSetState(StInit, StCancelled))
                {
                    return false;
                }
                // If a task should be canceled we put this to another queue which will be processed on each tick.
                // So this means that we will have a GC latency of max. 1 tick duration which is good enough. This way
                // we can make again use of our MpscLinkedQueue and so minimize the locking / overhead as much as possible.
                this.timer.cancelledTimeouts.TryEnqueue(this);
                return true;
            }

            internal void Remove()
            {
                HashedWheelBucket bucket = this.Bucket;
                if (bucket != null)
                {
                    bucket.Remove(this);
                }
                else if (this.timer.ShouldLimitTimeouts())
                {
                    Interlocked.Decrement(ref this.timer.PendingTimeouts);
                }
            }

            bool CompareAndSetState(int expected, int state)
            {
                return Interlocked.CompareExchange(ref this.state, state, expected) == expected;
            }

            internal int State => this.state;

            public bool Cancelled => this.State == StCancelled;

            public bool Expired => this.State == StExpired;

            internal void Expire()
            {
                if (!this.CompareAndSetState(StInit, StExpired))
                {
                    return;
                }

                try
                {
                    this.Task.Run(this);
                }
                catch (Exception t)
                {
                    if (Logger.WarnEnabled)
                    {
                        Logger.Warn($"An exception was thrown by {this.Task.GetType().Name}.", t);
                    }
                }
            }

            public override string ToString()
            {
                PreciseTimeSpan currentTime = PreciseTimeSpan.FromStart - this.timer.StartTime;
                TimeSpan remaining = this.Deadline - currentTime.ToTimeSpan();

                StringBuilder buf = new StringBuilder(192)
                    .Append(this.GetType().Name)
                    .Append('(')
                    .Append("deadline: ");
                if (remaining > TimeSpan.Zero)
                {
                    buf.Append(remaining)
                        .Append(" later");
                }
                else if (remaining < TimeSpan.Zero)
                {
                    buf.Append(-remaining)
                        .Append(" ago");
                }
                else
                {
                    buf.Append("now");
                }

                if (this.Cancelled)
                {
                    buf.Append(", cancelled");
                }

                return buf.Append(", task: ")
                    .Append(this.Task)
                    .Append(')')
                    .ToString();
            }
        }

        /// <summary>
        /// Bucket that stores HashedWheelTimeouts. These are stored in a linked-list like datastructure to allow easy
        /// removal of HashedWheelTimeouts in the middle. Also the HashedWheelTimeout act as nodes themself and so no
        /// extra object creation is needed.
        /// </summary>
        sealed class HashedWheelBucket
        {
            // Used for the linked-list datastructure
            HashedWheelTimeout head;
            HashedWheelTimeout tail;

            /// <summary>
            /// Add {@link HashedWheelTimeout} to this bucket.
            /// </summary>
            public void AddTimeout(HashedWheelTimeout timeout)
            {
                Contract.Assert(timeout.Bucket == null);
                timeout.Bucket = this;
                if (this.head == null)
                {
                    this.head = this.tail = timeout;
                }
                else
                {
                    this.tail.Next = timeout;
                    timeout.Prev = this.tail;
                    this.tail = timeout;
                }
            }

            /// <summary>
            /// Expire all <see cref="HashedWheelTimeout"/>s for the given <c>deadline</c>.
            /// </summary>
            public void ExpireTimeouts(TimeSpan deadline)
            {
                HashedWheelTimeout timeout = this.head;

                // process all timeouts
                while (timeout != null)
                {
                    HashedWheelTimeout next = timeout.Next;
                    if (timeout.RemainingRounds <= 0)
                    {
                        next = this.Remove(timeout);
                        if (timeout.Deadline <= deadline)
                        {
                            timeout.Expire();
                        }
                        else
                        {
                            // The timeout was placed into a wrong slot. This should never happen.
                            throw new InvalidOperationException(
                                string.Format(
                                    "timeout.deadline (%d) > deadline (%d)",
                                    timeout.Deadline,
                                    deadline));
                        }
                    }
                    else if (timeout.Cancelled)
                    {
                        next = this.Remove(timeout);
                    }
                    else
                    {
                        timeout.RemainingRounds--;
                    }
                    timeout = next;
                }
            }

            public HashedWheelTimeout Remove(HashedWheelTimeout timeout)
            {
                HashedWheelTimeout next = timeout.Next;
                // remove timeout that was either processed or cancelled by updating the linked-list
                if (timeout.Prev != null)
                {
                    timeout.Prev.Next = next;
                }
                if (timeout.Next != null)
                {
                    timeout.Next.Prev = timeout.Prev;
                }

                if (timeout == this.head)
                {
                    // if timeout is also the tail we need to adjust the entry too
                    if (timeout == this.tail)
                    {
                        this.tail = null;
                        this.head = null;
                    }
                    else
                    {
                        this.head = next;
                    }
                }
                else if (timeout == this.tail)
                {
                    // if the timeout is the tail modify the tail to be the prev node.
                    this.tail = timeout.Prev;
                }
                // null out prev, next and bucket to allow for GC.
                timeout.Prev = null;
                timeout.Next = null;
                timeout.Bucket = null;
                if (timeout.timer.ShouldLimitTimeouts())
                {
                    Interlocked.Decrement(ref timeout.timer.PendingTimeouts);
                }
                return next;
            }

            /// <summary>
            /// Clear this bucket and return all not expired / cancelled <see cref="ITimeout"/>s.
            /// </summary>
            public void ClearTimeouts(ISet<ITimeout> set)
            {
                while (true)
                {
                    HashedWheelTimeout timeout = this.PollTimeout();
                    if (timeout == null)
                    {
                        return;
                    }
                    if (timeout.Expired || timeout.Cancelled)
                    {
                        continue;
                    }
                    set.Add(timeout);
                }
            }

            HashedWheelTimeout PollTimeout()
            {
                HashedWheelTimeout head = this.head;
                if (head == null)
                {
                    return null;
                }
                HashedWheelTimeout next = head.Next;
                if (next == null)
                {
                    this.tail = this.head = null;
                }
                else
                {
                    this.head = next;
                    next.Prev = null;
                }

                // null out prev and next to allow for GC.
                head.Next = null;
                head.Prev = null;
                head.Bucket = null;
                return head;
            }
        }
    }
}