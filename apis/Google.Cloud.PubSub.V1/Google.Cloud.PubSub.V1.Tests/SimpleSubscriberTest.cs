﻿// Copyright 2017, Google Inc. All rights reserved.
//
// Licensed under the Apache License, Version 2.0 (the "License");
// you may not use this file except in compliance with the License.
// You may obtain a copy of the License at
//
//     http://www.apache.org/licenses/LICENSE-2.0
//
// Unless required by applicable law or agreed to in writing, software
// distributed under the License is distributed on an "AS IS" BASIS,
// WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
// See the License for the specific language governing permissions and
// limitations under the License.

using Google.Api.Gax;
using Google.Api.Gax.Grpc;
using Google.Cloud.PubSub.V1.Tasks;
using Google.Cloud.PubSub.V1.Tests.Tasks;
using Google.Protobuf;
using Grpc.Core;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace Google.Cloud.PubSub.V1.Tests
{
    public class SimpleSubscriberTest
    {
        private struct TimedId
        {
            public TimedId(DateTime time, string id)
            {
                Time = time;
                Id = id;
            }

            public DateTime Time { get; }
            public string Id { get; }

            public override string ToString() => $"{{Time:{Time}; Id:{Id}}}";
        }

        private class ServerAction
        {
            public static ServerAction Data(TimeSpan preInterval, IEnumerable<string> msgs) => new ServerAction(preInterval, msgs, null, null);
            public static ServerAction Inf() => new ServerAction(TimeSpan.FromDays(365), null, null, null);
            public static ServerAction BadMoveNext(TimeSpan preInterval, RpcException ex) => new ServerAction(preInterval, null, ex, null);
            public static ServerAction BadCurrent(TimeSpan preInterval, RpcException ex) => new ServerAction(preInterval, null, null, ex);

            private ServerAction(TimeSpan preInterval, IEnumerable<string> msgs, RpcException moveNextEx, RpcException currentEx)
            {
                PreInterval = preInterval;
                Msgs = msgs;
                MoveNextEx = moveNextEx;
                CurrentEx = currentEx;
            }

            public TimeSpan PreInterval { get; }
            public IEnumerable<string> Msgs { get; }
            public RpcException MoveNextEx { get; }
            public RpcException CurrentEx { get; }
        }

        private class FakeSubscriber : SubscriberClient
        {
            public static ReceivedMessage MakeReceivedMessage(string msgId, string content) =>
                new ReceivedMessage
                {
                    AckId = msgId,
                    Message = new PubsubMessage
                    {
                        MessageId = msgId,
                        Data = ByteString.CopyFromUtf8(content)
                    }
                };

            public static string MakeMsgId(int batchIndex, int msgIndex) => $"{batchIndex:D4}.{msgIndex:D4}";

            private class En : IAsyncEnumerator<StreamingPullResponse>
            {
                public En(IEnumerable<ServerAction> msgs, IScheduler scheduler, TaskHelper taskHelper, IClock clock, bool useMsgAsId, CancellationToken? ct)
                {
                    _msgsEn = msgs.Select((x, i) => (i, x)).GetEnumerator();
                    _scheduler = scheduler;
                    _taskHelper = taskHelper;
                    _clock = clock;
                    _useMsgAsId = useMsgAsId;
                    _ct = ct ?? CancellationToken.None;
                }

                private readonly IScheduler _scheduler;
                private readonly TaskHelper _taskHelper;
                private readonly IClock _clock;
                private readonly bool _useMsgAsId;
                private readonly CancellationToken _ct;

                private readonly IEnumerator<(int Index, ServerAction Action)> _msgsEn;

                public async Task<bool> MoveNext(CancellationToken cancellationToken)
                {
                    if (_msgsEn.MoveNext())
                    {
                        // TODO: This is not correct. The real server cancels the entire call if this cancellationtoken is cancelled.
                        using (var cts = CancellationTokenSource.CreateLinkedTokenSource(_ct, cancellationToken))
                        {
                            var isCancelled = await _taskHelper.ConfigureAwaitHideCancellation(
                                () => _scheduler.Delay(_msgsEn.Current.Action.PreInterval, cts.Token));
                            if (isCancelled)
                            {
                                // This mimics behaviour of a real server
                                throw new RpcException(new Status(StatusCode.Cancelled, "Operation cancelled"));
                            }
                            if (_msgsEn.Current.Action.MoveNextEx != null)
                            {
                                throw _msgsEn.Current.Action.MoveNextEx;
                            }
                            return true;
                        }
                    }
                    return false;
                }

                public StreamingPullResponse Current
                {
                    get
                    {
                        if (_msgsEn.Current.Action.CurrentEx != null)
                        {
                            throw _msgsEn.Current.Action.CurrentEx;
                        }
                        return new StreamingPullResponse
                        {
                            ReceivedMessages = {
                                _msgsEn.Current.Action.Msgs.Select((s, i) =>
                                    MakeReceivedMessage(_useMsgAsId ? s : MakeMsgId(_msgsEn.Current.Index, i), s))
                            }
                        };
                    }
                }

                public void Dispose()
                {
                }
            }

            private class PullStream : StreamingPullStream
            {
                public PullStream(TimeSpan writeAsyncPreDelay,
                    IEnumerable<ServerAction> msgs, List<TimedId> extends, List<TimedId> acks, List<DateTime> writeCompletes,
                    IScheduler scheduler, IClock clock, TaskHelper taskHelper, bool useMsgAsId, CancellationToken? ct)
                {
                    _taskHelper = taskHelper;
                    _scheduler = scheduler;
                    _writeAsyncPreDelay = writeAsyncPreDelay; // delay within the WriteAsync() method. Simulating network or server slowness.
                    _en = new En(msgs, scheduler, taskHelper, clock, useMsgAsId, ct);
                    _clock = clock;
                    _extends = extends;
                    _acks = acks;
                    _writeCompletes = writeCompletes;
                }

                private readonly object _lock = new object();
                private readonly TaskHelper _taskHelper;
                private readonly IScheduler _scheduler;
                private readonly TimeSpan _writeAsyncPreDelay;
                private readonly IAsyncEnumerator<StreamingPullResponse> _en;
                private readonly IClock _clock;
                private readonly List<TimedId> _extends;
                private readonly List<TimedId> _acks;
                private readonly List<DateTime> _writeCompletes;

                public override IAsyncEnumerator<StreamingPullResponse> ResponseStream => _en;

                public override async Task WriteAsync(StreamingPullRequest message)
                {
                    await _taskHelper.ConfigureAwait(_scheduler.Delay(_writeAsyncPreDelay, CancellationToken.None));
                    var now = _clock.GetCurrentDateTimeUtc();
                    lock (_lock)
                    {
                        _extends.AddRange(message.ModifyDeadlineAckIds.Select(id => new TimedId(now, id)).ToList());
                        _acks.AddRange(message.AckIds.Select(id => new TimedId(now, id)).ToList());
                    }
                }

                public override Task WriteCompleteAsync()
                {
                    lock (_lock)
                    {
                        _writeCompletes.Add(_clock.GetCurrentDateTimeUtc());
                        return Task.FromResult(0);
                    }
                }
            }

            public FakeSubscriber(
                IEnumerator<IEnumerable<ServerAction>> msgsEn, IScheduler scheduler, IClock clock,
                TaskHelper taskHelper, TimeSpan writeAsyncPreDelay, bool useMsgAsId)
            {
                _msgsEn = msgsEn;
                _scheduler = scheduler;
                _clock = clock;
                _taskHelper = taskHelper;
                _writeAsyncPreDelay = writeAsyncPreDelay;
                _useMsgAsId = useMsgAsId;
            }

            private readonly IEnumerator<IEnumerable<ServerAction>> _msgsEn;
            private readonly IScheduler _scheduler;
            private readonly IClock _clock;
            private readonly TaskHelper _taskHelper;
            private readonly TimeSpan _writeAsyncPreDelay;
            private readonly bool _useMsgAsId;

            private readonly List<TimedId> _extends = new List<TimedId>();
            private readonly List<TimedId> _acks = new List<TimedId>();
            private readonly List<DateTime> _writeCompletes = new List<DateTime>();

            public IReadOnlyList<TimedId> Extends => _extends;
            public IReadOnlyList<TimedId> Acks => _acks;
            public IReadOnlyList<DateTime> WriteCompletes => _writeCompletes;

            public override StreamingPullStream StreamingPull(
                CallSettings callSettings = null, BidirectionalStreamingSettings streamingSettings = null)
            {
                lock (_msgsEn)
                {
                    if (!_msgsEn.MoveNext())
                    {
                        throw new InvalidOperationException("Test subscriber creation failed. Run out of (fake) data");
                    }
                    var msgs = _msgsEn.Current;
                    return new PullStream(_writeAsyncPreDelay, msgs, _extends, _acks, _writeCompletes,
                        _scheduler, _clock, _taskHelper, _useMsgAsId, callSettings?.CancellationToken);
                }
            }
        }

        private class Fake : IDisposable
        {
            public TestScheduler Scheduler { get; set; }
            public DateTime Time0 { get; set; }
            public TaskHelper TaskHelper { get; set; }
            public List<FakeSubscriber> Subscribers { get; set; }
            public List<DateTime> ClientShutdowns { get; set; }
            public SimpleSubscriberImpl Subscriber { get; set; }

            public static Fake Create(IReadOnlyList<IEnumerable<ServerAction>> msgs,
                TimeSpan? ackDeadline = null, TimeSpan? ackExtendWindow = null,
                int? flowMaxElements = null, int? flowMaxBytes = null,
                int clientCount = 1, int threadCount = 1, TimeSpan? writeAsyncPreDelay = null,
                bool useMsgAsId = false)
            {
                var scheduler = new TestScheduler(threadCount: threadCount);
                TaskHelper taskHelper = scheduler.TaskHelper;
                List<DateTime> clientShutdowns = new List<DateTime>();
                var msgEn = msgs.GetEnumerator();
                var clients = Enumerable.Range(0, clientCount)
                    .Select(_ => new FakeSubscriber(msgEn, scheduler, scheduler.Clock, taskHelper, writeAsyncPreDelay ?? TimeSpan.Zero, useMsgAsId))
                    .ToList();
                var settings = new SimpleSubscriber.Settings
                {
                    Scheduler = scheduler,
                    StreamAckDeadline = ackDeadline,
                    AckExtensionWindow = ackExtendWindow,
                    FlowControlSettings = new FlowControlSettings(flowMaxElements, flowMaxBytes),
                };
                Task Shutdown()
                {
                    clientShutdowns.Locked(() => clientShutdowns.Add(scheduler.Clock.GetCurrentDateTimeUtc()));
                    return Task.FromResult(0);
                }
                var subs = new SimpleSubscriberImpl(new SubscriptionName("projectid", "subscriptionid"), clients, settings, Shutdown, taskHelper);
                return new Fake
                {
                    Scheduler = scheduler,
                    Time0 = scheduler.Clock.GetCurrentDateTimeUtc(),
                    TaskHelper = taskHelper,
                    Subscribers = clients,
                    ClientShutdowns = clientShutdowns,
                    Subscriber = subs,
                };
            }

            public void Dispose()
            {
                Scheduler.Dispose();
            }
        }

        [Theory, CombinatorialData]
        public void ImmediateStop(
            [CombinatorialValues(false, true)] bool hardStop)
        {
            using (var fake = Fake.Create(new[] { new[] { ServerAction.Inf() } }))
            {
                fake.Scheduler.Run(async () =>
                {
                    var doneTask = fake.Subscriber.StartAsync((msg, ct) =>
                    {
                        throw new Exception("Should never get here");
                    });
                    await fake.TaskHelper.ConfigureAwait(fake.Scheduler.Delay(TimeSpan.FromSeconds(1), CancellationToken.None));
                    var isCancelled = await fake.TaskHelper.ConfigureAwaitHideCancellation(
                        () => fake.Subscriber.StopAsync(new CancellationToken(hardStop)));
                    Assert.Equal(hardStop, isCancelled);
                    Assert.Equal(1, fake.Subscribers.Count);
                    Assert.Empty(fake.Subscribers[0].Acks);
                    Assert.Empty(fake.Subscribers[0].Extends);
                    Assert.Equal(new[] { fake.Time0 + TimeSpan.FromSeconds(1) }, fake.Subscribers[0].WriteCompletes);
                    Assert.Equal(new[] { fake.Time0 + TimeSpan.FromSeconds(1) }, fake.ClientShutdowns);
                });
            }
        }
        
        [Theory, PairwiseData]
        public void RecvManyMsgsNoErrors(
            [CombinatorialValues(false, true)] bool hardStop,
            [CombinatorialValues(2, 5, 6, 9, 10)] int batchCount,
            [CombinatorialValues(1, 10, 13, 44, 45)] int batchSize,
            [CombinatorialValues(0, 1, 4, 6, 60)] int interBatchIntervalSeconds,
            [CombinatorialValues(0, 1, 5, 8, 21)] int handlerDelaySeconds,
            [CombinatorialValues(2, 8, 11, 34, 102)] int stopAfterSeconds,
            [CombinatorialValues(1, 2, 3, 4, 5, 7, 13)] int threadCount,
            [CombinatorialValues(1, 2, 3, 4, 8, 16, 33)] int clientCount)
        {
            var expectedCompletedBatches = interBatchIntervalSeconds == 0
                ? (hardStop && stopAfterSeconds < handlerDelaySeconds ? 0 : batchCount)
                : Math.Max(0, stopAfterSeconds - (hardStop ? handlerDelaySeconds : 0)) / interBatchIntervalSeconds;
            var expectedMsgCount = Math.Min(expectedCompletedBatches, batchCount) * batchSize * clientCount;
            var expectedAcks = Enumerable.Range(0, batchCount)
                .SelectMany(batchIndex => Enumerable.Range(0, batchSize).Select(msgIndex => FakeSubscriber.MakeMsgId(batchIndex, msgIndex)))
                .Take(expectedMsgCount / clientCount)
                .OrderBy(x => x);

            var msgss = Enumerable.Range(0, batchCount)
                .Select(batchIndex =>
                    ServerAction.Data(TimeSpan.FromSeconds(interBatchIntervalSeconds), Enumerable.Range(0, batchSize).Select(i => (batchIndex * batchSize + i).ToString())))
                .Concat(new[] { ServerAction.Inf() });
            using (var fake = Fake.Create(Enumerable.Repeat(msgss, clientCount).ToList(), clientCount: clientCount, threadCount: threadCount))
            {
                fake.Scheduler.Run(async () =>
                {
                    List<string> handledMsgs = new List<string>();
                    var doneTask = fake.Subscriber.StartAsync(async (msg, ct) =>
                    {
                        await fake.TaskHelper.ConfigureAwait(fake.Scheduler.Delay(TimeSpan.FromSeconds(handlerDelaySeconds), ct));
                        lock (handledMsgs)
                        {
                            handledMsgs.Add(msg.Data.ToStringUtf8());
                        }
                        return SimpleSubscriber.Reply.Ack;
                    });
                    await fake.TaskHelper.ConfigureAwait(fake.Scheduler.Delay(TimeSpan.FromSeconds(stopAfterSeconds) + TimeSpan.FromSeconds(0.5), CancellationToken.None));
                    var isCancelled = await fake.TaskHelper.ConfigureAwaitHideCancellation(
                        () => fake.Subscriber.StopAsync(new CancellationToken(hardStop)));
                    Assert.Equal(hardStop, isCancelled);
                    Assert.Equal(clientCount, fake.Subscribers.Count);
                    Assert.Equal(expectedMsgCount, handledMsgs.Locked(() => handledMsgs.Count));
                    Assert.Equal(expectedMsgCount, fake.Subscribers.Sum(x => x.Acks.Count));
                    Assert.Equal(Enumerable.Repeat(expectedAcks, clientCount), fake.Subscribers.Select(x => x.Acks.Select(y => y.Id).OrderBy(y => y)));
                    Assert.Equal(Enumerable.Repeat(1, clientCount).ToArray(), fake.Subscribers.Select(x => x.WriteCompletes.Count).ToArray());
                    Assert.Equal(1, fake.ClientShutdowns.Count);
                });
            }
        }

        [Theory, CombinatorialData]
        public void OneClientManyMsgs([CombinatorialValues(1, 2, 3, 4, 5)] int rndSeed)
        {
            // Test sending a large quantity of messages
            const int maxStreamingPull = 14;
            const int maxMoveNext = 14;
            const int maxMsgCount = 80;
            var rnd = new Random(rndSeed);
            // Construct a sequence of sequences of batches of messages:
            // Outer sequence is for each invocation of StreamingPull().
            // Inner sequence is for each invocation of MoveNext() on the StreamingPull response stream.
            // And each call to MoveNext() returns a batch of messages.
            // All sizes vary, to test that are no size-specific dependencies.
            var streamingPullCount = rnd.Next(maxStreamingPull / 2, maxStreamingPull);
            var msgs = Enumerable.Range(0, streamingPullCount).Select(rpcId =>
            {
                var moveNextCount = rnd.Next(maxMoveNext / 2, maxMoveNext);
                return Enumerable.Range(0, moveNextCount).Select(moveNextId =>
                {
                    var msgCount = rnd.Next(1, maxMsgCount);
                    var preInterval = TimeSpan.FromMilliseconds(rnd.NextDouble() * 50.0);
                    return ServerAction.Data(preInterval, Enumerable.Range(0, msgCount).Select(i => $"{rpcId}:{moveNextId}:{i}").ToList());
                }).ToList();
            }).Concat(new[] { new[] { ServerAction.Inf() }.ToList() }).ToList();
            var allMsgIds = msgs.SelectMany(x => x.SelectMany(y => y.Msgs ?? new string[0])).ToList();
            var allMsgIdsSet = new HashSet<string>(allMsgIds);
            var totalMsgCount = allMsgIds.Count;
            using (var fake = Fake.Create(msgs, useMsgAsId: true))
            {
                fake.Scheduler.Run(async () =>
                {
                    var recvedMsgs = new List<string>();
                    var startTask = fake.Subscriber.StartAsync(async (msg, ct) =>
                    {
                        var delay = TimeSpan.FromSeconds(rnd.NextDouble() * 600.0);
                        await fake.TaskHelper.ConfigureAwait(fake.Scheduler.Delay(delay, ct));
                        var msgString = msg.Data.ToStringUtf8();
                        lock (recvedMsgs)
                        {
                            recvedMsgs.Add(msgString);
                            if (recvedMsgs.Count == totalMsgCount)
                            {
                                Task unused = fake.Subscriber.StopAsync(CancellationToken.None);
                            }
                        }
                        return SimpleSubscriber.Reply.Ack;
                    });
                    await fake.TaskHelper.ConfigureAwait(startTask);
                    Assert.Equal(totalMsgCount, recvedMsgs.Count);
                    Assert.Equal(allMsgIdsSet, new HashSet<string>(recvedMsgs));
                    var sub = fake.Subscribers[0];
                    Assert.Equal(totalMsgCount, sub.Acks.Count);
                    Assert.Equal(allMsgIdsSet, new HashSet<string>(sub.Acks.Select(x => x.Id)));
                });
            }
        }
        
        [Theory, PairwiseData]
        public void FlowControl(
            [CombinatorialValues(false, true)] bool hardStop,
            [CombinatorialValues(1, 2, 3)] int clientCount,
            [CombinatorialValues(2, 9, 25, 600)] int stopAfterSeconds,
            [CombinatorialValues(1, 2, 3, 4, 5, 10, 21, 99, 148)] int flowMaxElements,
            [CombinatorialValues(1, 10, 14, 18, 25, 39, 81, 255)] int flowMaxBytes,
            [CombinatorialValues(1, 3, 9, 19)] int threadCount)
        {
            const int msgsPerClient = 100;
            var oneMsgByteCount = FakeSubscriber.MakeReceivedMessage("0000.0000", "0000").CalculateSize();
            var combinedFlowMaxElements = Math.Min(flowMaxElements, flowMaxBytes / oneMsgByteCount + 1);
            var expectedMsgCount = Math.Min(msgsPerClient * clientCount, combinedFlowMaxElements * stopAfterSeconds + (hardStop ? 0 : combinedFlowMaxElements));
            var msgss = Enumerable.Range(0, msgsPerClient)
                .Select(i => ServerAction.Data(TimeSpan.Zero, new[] { i.ToString("D4") }))
                .Concat(new[] { ServerAction.Inf() });
            using (var fake = Fake.Create(Enumerable.Repeat(msgss, clientCount).ToList(),
                flowMaxElements: flowMaxElements, flowMaxBytes: flowMaxBytes, clientCount: clientCount, threadCount: threadCount))
            {
                fake.Scheduler.Run(async () =>
                {
                    List<string> handledMsgs = new List<string>();
                    int concurrentElementCount = 0;
                    int concurrentByteCount = 0;
                    var doneTask = fake.Subscriber.StartAsync(async (msg, ct) =>
                    {
                        var msgSize = msg.CalculateSize();
                        lock (handledMsgs)
                        {
                            Assert.True((concurrentElementCount += 1) <= flowMaxElements, "Flow has exceeded max elements.");
                            // Exceeding the flow byte limit is allowed for individual messages that exceed that size.
                            Assert.True((concurrentByteCount += msgSize) <= flowMaxBytes || concurrentElementCount == 1, "Flow has exceeded max bytes.");
                        }
                        await fake.TaskHelper.ConfigureAwait(fake.Scheduler.Delay(TimeSpan.FromSeconds(1), ct));
                        lock (handledMsgs)
                        {
                            handledMsgs.Add(msg.Data.ToStringUtf8());
                            // Check just for sanity
                            Assert.True((concurrentElementCount -= 1) >= 0);
                            Assert.True((concurrentByteCount -= msgSize) >= 0);
                        }
                        return SimpleSubscriber.Reply.Ack;
                    });
                    await fake.TaskHelper.ConfigureAwait(fake.Scheduler.Delay(TimeSpan.FromSeconds(stopAfterSeconds) + TimeSpan.FromSeconds(0.5), CancellationToken.None));
                    await fake.TaskHelper.ConfigureAwaitHideCancellation(
                        () => fake.Subscriber.StopAsync(new CancellationToken(hardStop)));
                    Assert.Equal(expectedMsgCount, handledMsgs.Count);
                });
            }
        }
        
        [Fact]
        public void UserHandlerFaults()
        {
            var msgs = Enumerable.Repeat(ServerAction.Data(TimeSpan.Zero, new[] { "m" }), 10).Concat(new[] { ServerAction.Inf() });
            using (var fake = Fake.Create(new[] { msgs }))
            {
                fake.Scheduler.Run(async () =>
                {
                    var handledMsgs = new List<string>();
                    int count = 0;
                    var doneTask = fake.Subscriber.StartAsync(async (msg, ct) =>
                    {
                        if (Interlocked.Increment(ref count) % 2 == 0)
                        {
                            throw new NotSupportedException("User handler fault!");
                        }
                        await fake.TaskHelper.ConfigureAwait(fake.Scheduler.Delay(TimeSpan.FromSeconds(1), ct));
                        handledMsgs.Locked(() => handledMsgs.Add(msg.Data.ToStringUtf8()));
                        return SimpleSubscriber.Reply.Ack;
                    });
                    await fake.TaskHelper.ConfigureAwait(fake.Scheduler.Delay(TimeSpan.FromSeconds(10), CancellationToken.None));
                    await fake.TaskHelper.ConfigureAwait(fake.Subscriber.StopAsync(CancellationToken.None));
                    Assert.Equal(Enumerable.Repeat("m", 5), handledMsgs);
                });
            }
        }
        
        [Theory, PairwiseData]
        public void ServerFaultsRecoverable(
            [CombinatorialValues(1, 3, 9, 14)] int threadCount)
        {
            var zero = TimeSpan.Zero;
            var recoverableEx = new RpcException(new Status(StatusCode.DeadlineExceeded, ""), "");
            var msgs = new[]
            {
                new[] { ServerAction.Data(zero, new[] { "1" }), ServerAction.BadMoveNext(zero, recoverableEx) },
                new[] { ServerAction.Data(zero, new[] { "2" }), ServerAction.BadCurrent(zero, recoverableEx) },
                new[] { ServerAction.Data(zero, new[] { "3" }), ServerAction.Inf() }
            };
            using (var fake = Fake.Create(msgs, threadCount: threadCount))
            {
                fake.Scheduler.Run(async () =>
                {
                    var handledMsgs = new List<string>();
                    var doneTask = fake.Subscriber.StartAsync(async (msg, ct) =>
                    {
                        handledMsgs.Locked(() => handledMsgs.Add(msg.Data.ToStringUtf8()));
                        await fake.TaskHelper.ConfigureAwait(fake.Scheduler.Delay(TimeSpan.FromSeconds(1), ct));
                        return SimpleSubscriber.Reply.Ack;
                    });
                    await fake.TaskHelper.ConfigureAwait(fake.Scheduler.Delay(TimeSpan.FromSeconds(10), CancellationToken.None));
                    await fake.TaskHelper.ConfigureAwait(fake.Subscriber.StopAsync(CancellationToken.None));
                    Assert.Equal(new[] { "1", "2", "3" }, handledMsgs);
                    Assert.Equal(3, fake.Subscribers[0].Acks.Count);
                });
            }
        }
        
        [Theory, PairwiseData]
        public void ServerFaultsUnrecoverable(
            [CombinatorialValues(true, false)] bool badMoveNext,
            [CombinatorialValues(1, 2, 3, 4, 10)] int clientCount,
            [CombinatorialValues(1, 3, 9, 14)] int threadCount)
        {
            var zero = TimeSpan.Zero;
            var unrecoverableEx = new RpcException(new Status(StatusCode.Unimplemented, ""), "");
            var failure = badMoveNext ? ServerAction.BadMoveNext(zero, unrecoverableEx) : ServerAction.BadCurrent(zero, unrecoverableEx);
            // Client 1 will recv "1", then crash unrecoverably
            // Client(s) 2(+) will just block; this tests that shutdown occurs
            var msgs = new[] { new[] { ServerAction.Data(zero, new[] { "1" }), failure } }
                .Concat(Enumerable.Repeat<IEnumerable<ServerAction>>(new[] { ServerAction.Inf() }, clientCount - 1))
                .ToList();
            using (var fake = Fake.Create(msgs, clientCount: clientCount, threadCount: threadCount))
            {
                fake.Scheduler.Run(async () =>
                {
                    var handledMsgs = new List<string>();
                    var doneTask = fake.Subscriber.StartAsync(async (msg, ct) =>
                    {
                        handledMsgs.Locked(() => handledMsgs.Add(msg.Data.ToStringUtf8()));
                        await fake.TaskHelper.ConfigureAwait(fake.Scheduler.Delay(TimeSpan.FromSeconds(1), ct));
                        return SimpleSubscriber.Reply.Ack;
                    });
                    await fake.TaskHelper.ConfigureAwait(fake.Scheduler.Delay(TimeSpan.FromSeconds(10), CancellationToken.None));
                    Exception ex = await fake.TaskHelper.ConfigureAwaitHideErrors(
                        () => fake.Subscriber.StopAsync(CancellationToken.None));
                    Assert.Equal(unrecoverableEx, ex.AllExceptions().FirstOrDefault());
                    Assert.Equal(new[] { "1" }, handledMsgs);
                    Assert.Equal(1, fake.ClientShutdowns.Count);
                });
            }
        }
        
        [Fact]
        public void OnlyOneStart()
        {
            var msgs = new[] { new[] { ServerAction.Inf() } };
            using (var fake = Fake.Create(msgs))
            {
                fake.Scheduler.Run(() =>
                {
                    fake.Subscriber.StartAsync((msg, ct) => throw new Exception());
                    // Only allowed to start a SimpleSubscriber once.
                    Assert.Throws<InvalidOperationException>((Action)(() => fake.Subscriber.StartAsync((msg, ct) => throw new Exception())));
                });
            }
        }

        [Fact]
        public void LeaseExtension()
        {
            var msgs = new[] { new[] {
                ServerAction.Data(TimeSpan.Zero, new[] { "1" }),
                ServerAction.Data(TimeSpan.FromSeconds(5), new[] { "2" }),
                ServerAction.Inf()
            } };
            using (var fake = Fake.Create(msgs, ackDeadline: TimeSpan.FromSeconds(30), ackExtendWindow: TimeSpan.FromSeconds(10)))
            {
                fake.Scheduler.Run(async () =>
                {
                    var doneTask = fake.Subscriber.StartAsync(async (msg, ct) =>
                    {
                        await fake.TaskHelper.ConfigureAwait(fake.Scheduler.Delay(TimeSpan.FromSeconds(70), ct));
                        Task unusedTask = fake.Subscriber.StopAsync(CancellationToken.None);
                        return SimpleSubscriber.Reply.Ack;
                    });
                    await fake.TaskHelper.ConfigureAwait(doneTask);
                    var t0 = fake.Time0;
                    DateTime S(int seconds) => t0 + TimeSpan.FromSeconds(seconds);
                    Assert.Equal(1, fake.Subscribers.Count);
                    Assert.Equal(new[] { S(0), S(5), S(20), S(25), S(40), S(45), S(60), S(65) }, fake.Subscribers[0].Extends.Select(x => x.Time));
                    Assert.Equal(new[] { S(70), S(75) }, fake.Subscribers[0].Acks.Select(x => x.Time));
                });
            }
        }

        [Fact]
        public void SlowUplinkThrottlesPull()
        {
            const int msgCount = 20;
            const int flowMaxEls = 5;
            var preDelay = TimeSpan.FromSeconds(10);
            var msgs = new[] {
                Enumerable.Range(0, msgCount)
                    .Select(i => ServerAction.Data(TimeSpan.Zero, new[] { i.ToString() }))
                    .Concat(new[] { ServerAction.Inf() })
            };
            using (var fake = Fake.Create(msgs, flowMaxElements: flowMaxEls, writeAsyncPreDelay: preDelay))
            {
                fake.Scheduler.Run(async () =>
                {
                    var subTask = fake.Subscriber.StartAsync((msg, ct) => Task.FromResult(SimpleSubscriber.Reply.Ack));
                    await fake.TaskHelper.ConfigureAwait(fake.Scheduler.Delay(TimeSpan.FromSeconds(1000), CancellationToken.None));
                    await fake.TaskHelper.ConfigureAwait(fake.Subscriber.StopAsync(CancellationToken.None));
                    await fake.TaskHelper.ConfigureAwait(subTask);
                    var sub = fake.Subscribers[0];
                    Assert.True(sub.Extends.Count <= msgCount); // Difficult to predict, must be <= total message count
                    Assert.Equal(msgCount, sub.Acks.Count);
                    // Difficult to predict the exact timings, so check durations.
                    var expectedMinDuration = TimeSpan.FromTicks(preDelay.Ticks * msgCount / flowMaxEls);
                    Assert.True(sub.Acks.Last().Time - sub.Acks.First().Time >= expectedMinDuration, "Pull not throttled (acks)");
                });
            }
        }
    }
}
