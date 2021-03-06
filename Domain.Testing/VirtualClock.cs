// Copyright (c) Microsoft. All rights reserved. 
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reactive.Concurrency;
using System.Reactive.Subjects;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Its.Domain.Sql;
using Microsoft.Its.Recipes;

namespace Microsoft.Its.Domain.Testing
{
    /// <summary>
    /// A virtual domain clock that can be used for testing time-dependent operations.
    /// </summary>
    [DebuggerStepThrough]
    public class VirtualClock :
        IClock,
        IDisposable
    {
        private readonly Subject<DateTimeOffset> movements = new Subject<DateTimeOffset>();
        private readonly RxScheduler Scheduler;
        private readonly HashSet<string> schedulerClocks = new HashSet<string>();

        private VirtualClock(DateTimeOffset now)
        {
            Scheduler = new RxScheduler(now);
        }

        /// <summary>
        /// Gets the current clock as a <see cref="VirtualClock" />. If the current clock is not a <see cref="VirtualClock" />, it throws.
        /// </summary>
        /// <value>
        /// The current.
        /// </value>
        /// <exception cref="System.InvalidOperationException">Clock.Current must be a VirtualClock in order to use this method.</exception>
        public static VirtualClock Current
        {
            get
            {
                var clock = Clock.Current as VirtualClock;
                if (clock == null)
                {
                    throw new InvalidOperationException("Clock.Current must be a VirtualClock in order to use this method.");
                }
                return clock;
            }
        }

        /// <summary>
        /// Gets the current time.
        /// </summary>
        public DateTimeOffset Now()
        {
            return Scheduler.Clock;
        }

        /// <summary>
        /// Advances the clock to the specified time.
        /// </summary>
        public void AdvanceTo(DateTimeOffset time)
        {
            Scheduler.AdvanceTo(time);
            movements.OnNext(Scheduler.Now);
            WaitForScheduler();
        }

        /// <summary>
        /// Advances the clock by the specified amount of time.
        /// </summary>
        public void AdvanceBy(TimeSpan time)
        {
            Scheduler.AdvanceBy(time);
            movements.OnNext(Scheduler.Now);
            WaitForScheduler();
        }

        private void WaitForScheduler()
        {
            Scheduler.Done().Wait();

            if (schedulerClocks.Any())
            {
                var configuration = Configuration.Current;
                if (configuration.UsesSqlCommandScheduling())
                {
                    foreach (var clockName in schedulerClocks)
                    {
                        var sqlCommandScheduler = configuration.SqlCommandScheduler();
                        sqlCommandScheduler.AdvanceClock(clockName, Clock.Now()).Wait();
                    }
                }
            }
        }

        /// <summary>
        /// Performs application-defined tasks associated with freeing, releasing, or resetting unmanaged resources.
        /// </summary>
        /// <filterpriority>2</filterpriority>
        public void Dispose()
        {
            Clock.Reset();
        }

        /// <summary>
        /// Notifies the provider that an observer is to receive notifications.
        /// </summary>
        /// <returns>
        /// A reference to an interface that allows observers to stop receiving notifications before the provider has finished sending them.
        /// </returns>
        /// <param name="observer">The object that is to receive notifications.</param>
        public IDisposable Subscribe(IObserver<DateTimeOffset> observer)
        {
            return movements.Subscribe(observer);
        }

        /// <summary>
        /// Replaces the domain clock with a virtual clock that can be used to control the current time.
        /// </summary>
        /// <param name="now">The time to which the virtual clock is set.</param>
        /// <returns></returns>
        /// <exception cref="System.InvalidOperationException">You must dispose the current VirtualClock before starting another.</exception>
        public static VirtualClock Start(DateTimeOffset? now = null)
        {
            if (Clock.Current is VirtualClock)
            {
                throw new InvalidOperationException("You must dispose the current VirtualClock before starting another.");
            }

            var virtualClock = new VirtualClock(now ?? DateTimeOffset.Now);
            Clock.Current = virtualClock;
            return virtualClock;
        }

        internal static IDisposable Schedule<TAggregate>(
            IScheduledCommand<TAggregate> scheduledCommand,
            DateTimeOffset dueTime,
            Func<IScheduler, IScheduledCommand<TAggregate>, IDisposable> func)
            where TAggregate : IEventSourced
        {
            var clock =
                Clock.Current
                     .IfTypeIs<VirtualClock>()
                     .Else(() => CommandContext.Current.Root.Clock as VirtualClock);

            if (clock == null)
            {
                throw new InvalidOperationException("In-memory command scheduling can only be performed when a VirtualClock is active.");
            }

            return clock.Scheduler.Schedule(scheduledCommand, dueTime, func);
        }

        public async Task Done()
        {
            await Scheduler.Done();
        }

        public override string ToString()
        {
            return GetType() + ": " + Now().ToString("O");
        }

        private class RxScheduler : HistoricalScheduler
        {
            private readonly IDictionary<IScheduledCommand, DateTimeOffset> pending = new ConcurrentDictionary<IScheduledCommand, DateTimeOffset>();

            private readonly ManualResetEventSlim resetEvent = new ManualResetEventSlim(true);

            public RxScheduler(DateTimeOffset initialClock) : base(initialClock)
            {
            }

            public override IDisposable ScheduleAbsolute<TState>(TState state, DateTimeOffset dueTime, Func<IScheduler, TState, IDisposable> action)
            {
                var schedule = base.ScheduleAbsolute(state, dueTime, (scheduler, command) =>
                {
                    var cancel = action(scheduler, command);

                    pending.Remove((IScheduledCommand) command);

                    resetEvent.Set();

                    return cancel;
                });

                resetEvent.Reset();

                pending.Add((IScheduledCommand) state, dueTime);

                return schedule;
            }

            public async Task Done()
            {
                await Task.Yield();

                while (CommandsAreDue)
                {
                    resetEvent.Wait();
                }
            }

            private bool CommandsAreDue
            {
                get
                {
                    return pending.Any(p => p.Value <= Now);
                }
            }
        }

        internal void OnAdvanceTriggerSchedulerClock(string clockName)
        {
            schedulerClocks.Add(clockName);
        }
    }
}
