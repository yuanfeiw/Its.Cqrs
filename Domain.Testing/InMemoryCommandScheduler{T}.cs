// Copyright (c) Microsoft. All rights reserved. 
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reactive.Disposables;
using System.Threading;
using System.Threading.Tasks;

namespace Microsoft.Its.Domain.Testing
{
    public class InMemoryCommandScheduler<TAggregate> :
        ICommandScheduler<TAggregate>, 
        IEventHandler,
        IEventHandlerBinder
        where TAggregate : class, IEventSourced
    {
        private readonly IEventSourcedRepository<TAggregate> repository;
        private readonly IHaveConsequencesWhen<IScheduledCommand<TAggregate>> consequenter;
        private readonly ManualResetEventSlim resetEvent = new ManualResetEventSlim();

        /// <summary>
        /// Initializes a new instance of the <see cref="InMemoryCommandScheduler{TAggregate}"/> class.
        /// </summary>
        /// <param name="repository">The repository.</param>
        /// <exception cref="System.ArgumentNullException">repository</exception>
        public InMemoryCommandScheduler(IEventSourcedRepository<TAggregate> repository)
        {
            if (repository == null)
            {
                throw new ArgumentNullException("repository");
            }
            this.repository = repository;

            consequenter = Consequenter.Create<IScheduledCommand<TAggregate>>(e => Schedule(e).Wait());
        }

        /// <summary>
        /// Schedules the specified command.
        /// </summary>
        /// <param name="scheduledCommand">The scheduled command.</param>
        /// <returns>A task that is complete when the command has been successfully scheduled.</returns>
        public async Task Schedule(IScheduledCommand<TAggregate> scheduledCommand)
        {
            var dueTime = scheduledCommand.DueTime;

            var domainNow = Clock.Current.Now();

            if (dueTime == null || dueTime <= domainNow)
            {
                if (!await VerifyPrecondition(scheduledCommand))
                {
                    this.DeliverIfPreconditionIsSatisfiedWithin(
                        TimeSpan.FromSeconds(3),
                        scheduledCommand,
                        Configuration.Current.EventBus);
                }
                else
                {
                    Debug.WriteLine(string.Format("Schedule (applying {1} immediately): @ {0}", domainNow, scheduledCommand.Command.CommandName));

                    resetEvent.Reset();

                    // schedule immediately
                    await Deliver(scheduledCommand);
                }

                return;
            }

            // schedule for later
            VirtualClock.Schedule(scheduledCommand,
                                  dueTime.Value,
                                  (s, command) =>
                                  {
                                      resetEvent.Reset();

                                      try
                                      {
                                          Deliver(command).Wait();
                                      }
                                      catch (Exception exception)
                                      {
                                          Console.WriteLine("InMemoryCommandScheduler caught:\n" + exception);
                                      }

                                      return Disposable.Empty;
                                  });
        }

        private async Task<bool> VerifyPrecondition(IScheduledCommand scheduledCommand)
        {
            var verifier = Configuration.Current.Container.Resolve<ICommandPreconditionVerifier>();
            return await verifier.VerifyPrecondition(scheduledCommand);
        }

        /// <summary>
        /// Delivers the specified scheduled command to the target aggregate.
        /// </summary>
        /// <param name="scheduledCommand">The scheduled command to be applied to the aggregate.</param>
        /// <returns>A task that is complete when the command has been applied.</returns>
        /// <remarks>The scheduler will apply the command and save it, potentially triggering additional consequences.</remarks>
        public async Task Deliver(IScheduledCommand<TAggregate> scheduledCommand)
        {
            using (CommandContext.Establish(scheduledCommand.Command))
            {
                await repository.ApplyScheduledCommand(scheduledCommand);

            }

            resetEvent.Set();
        }

        /// <summary>
        /// Allows awaiting delivery of all commands that are currently due on the command scheduler.
        /// </summary>
        public async Task Done()
        {
            await Task.Yield();
            resetEvent.Wait();
        }

        IEnumerable<IEventHandlerBinder> IEventHandler.GetBinders()
        {
            return new[] { this };
        }

        IDisposable IEventHandlerBinder.SubscribeToBus(object handler, IEventBus bus)
        {
            return bus.Subscribe(consequenter);
        }
    }
}
