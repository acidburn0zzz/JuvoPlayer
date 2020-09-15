﻿/*!
 * https://github.com/SamsungDForum/JuvoPlayer
 * Copyright 2020, Samsung Electronics Co., Ltd
 * Licensed under the MIT license
 *
 * THIS SOFTWARE IS PROVIDED BY THE COPYRIGHT HOLDERS AND CONTRIBUTORS "AS IS"
 * AND ANY EXPRESS OR IMPLIED WARRANTIES, INCLUDING, BUT NOT LIMITED TO, THE
 * IMPLIED WARRANTIES OF MERCHANTABILITY AND FITNESS FOR A PARTICULAR PURPOSE
 * ARE DISCLAIMED. IN NO EVENT SHALL THE COPYRIGHT HOLDER BE LIABLE FOR ANY
 * DIRECT, INDIRECT, INCIDENTAL, SPECIAL, EXEMPLARY, OR CONSEQUENTIAL DAMAGES
 * (INCLUDING, BUT NOT LIMITED TO, PROCUREMENT OF SUBSTITUTE GOODS OR SERVICES;
 * LOSS OF USE, DATA, OR PROFITS; OR BUSINESS INTERRUPTION) HOWEVER CAUSED AND
 * ON ANY THEORY OF LIABILITY, WHETHER IN CONTRACT, STRICT LIABILITY, OR TORT
 * (INCLUDING NEGLIGENCE OR OTHERWISE) ARISING IN ANY WAY OUT OF THE USE OF THIS
 * SOFTWARE, EVEN IF ADVISED OF THE POSSIBILITY OF SUCH DAMAGE.
 */

using System;
using System.Collections.Generic;
using System.Linq;
using System.Reactive.Linq;
using System.Threading;
using System.Threading.Tasks;
using JuvoLogger;
using JuvoPlayer.Common;

namespace JuvoPlayer.Players
{
    public class Player : IPlayer
    {
        private readonly Configuration _configuration;

        private readonly ILogger _logger = LoggerManager.GetInstance().GetLogger("JuvoPlayer");
        private readonly Func<IPlatformPlayer> _platformPlayerFactory;
        private readonly IWindow _window;
        private readonly Clock _clock;
        private CancellationTokenSource _cancellationTokenSource;
        private Period _currentPeriod;
        private IPlatformPlayer _platformPlayer;
        private Segment _segment;
        private Dictionary<ContentType, StreamHolder> _streamHolders;
        private IStreamProvider _streamProvider;

        public Player(
            Func<IPlatformPlayer> platformPlayerFactory,
            Clock clock,
            IWindow window,
            Configuration configuration)
        {
            _platformPlayerFactory = platformPlayerFactory;
            this._clock = clock;
            _window = window;
            _configuration = configuration;
        }

        public TimeSpan? Duration => _streamProvider?.GetDuration();

        public TimeSpan? Position
        {
            get
            {
                try
                {
                    return _platformPlayer?.GetPosition();
                }
                catch (Exception)
                {
                    return _segment.Start;
                }
            }
        }

        public PlayerState State
        {
            get
            {
                try
                {
                    return _platformPlayer?.GetState() ?? PlayerState.None;
                }
                catch (Exception)
                {
                    return PlayerState.None;
                }
            }
        }

        public async Task Prepare()
        {
            _logger.Info();
            _cancellationTokenSource = new CancellationTokenSource();
            _platformPlayer = _platformPlayerFactory.Invoke();
            _currentPeriod = await PrepareStreamProvider();
            var streamGroups = SelectDefaultStreamsGroups(_currentPeriod);
            CreateStreamHolders(
                streamGroups,
                new IStreamSelector[streamGroups.Length]);
            await PrepareStreams();
            var startTime = _currentPeriod.StartTime ?? TimeSpan.Zero;
            if (_configuration.StartTime != null)
            {
                var requestedStartTime = _configuration.StartTime.Value;
                if (requestedStartTime > startTime)
                    startTime = requestedStartTime;
            }

            UpdateSegment(startTime);
            LoadChunks();

            var streamConfigs =
                await Task.WhenAll(GetStreamConfigs());
            await PreparePlayer(streamConfigs);
        }

        public async Task Seek(TimeSpan position)
        {
            _logger.Info($"Seek to {position}");
            if (_platformPlayer == null)
                throw new InvalidOperationException("Prepare not called");

            var stateBeforeSeek = _platformPlayer.GetState();
            await StopStreaming();

            UpdateSegment(position);
            position = _segment.Start;
            LoadChunks();

            await SeekPlayer(position);

            if (stateBeforeSeek == PlayerState.Playing)
                _clock.Start();
        }

        public StreamGroup[] GetStreamGroups()
        {
            return _streamProvider.GetStreamGroups(_currentPeriod);
        }

        public async Task SetStreamGroups(StreamGroup[] streamGroups, IStreamSelector[] selectors)
        {
            _logger.Info();
            if (_platformPlayer == null)
                throw new InvalidOperationException("Prepare not called");
            VerifyStreamGroups(streamGroups, selectors);

            var previousState = _platformPlayer.GetState();
            if (previousState == PlayerState.Playing)
                _platformPlayer.Pause();
            var position = _platformPlayer.GetState() == PlayerState.Ready
                ? _segment.Start
                : _platformPlayer.GetPosition();
            await StopStreaming();

            // Create new streams
            var streamHolders = new Dictionary<ContentType, StreamHolder>();
            var shallRecreatePlayer = false;
            for (var index = 0; index < streamGroups.Length; ++index)
            {
                var streamGroup = streamGroups[index];
                var selector = selectors[index];
                var contentType = streamGroup.ContentType;

                // Add new stream
                if (!_streamHolders.ContainsKey(contentType))
                {
                    shallRecreatePlayer = true;
                    streamHolders[contentType] = CreateStreamHolder(
                        streamGroup,
                        selector);
                    continue;
                }

                var oldStreamHolder = _streamHolders[contentType];
                var oldStreamGroup = oldStreamHolder.StreamGroup;
                // Replace streams
                if (streamGroup != oldStreamGroup)
                {
                    // TODO: Consider to check if stream groups are 'compatible'
                    shallRecreatePlayer = true;
                    DisposeStreamHolder(oldStreamHolder);
                    streamHolders[contentType] = CreateStreamHolder(
                        streamGroup,
                        selector);
                    continue;
                }

                var platformCapabilities = Platform.Current.Capabilities;
                var supportsSeamlessAudioChange =
                    platformCapabilities.SupportsSeamlessAudioChange;
                if (!supportsSeamlessAudioChange
                    && contentType == ContentType.Audio)
                {
                    var oldStreamSelector = oldStreamHolder.StreamSelector;
                    if (!Equals(oldStreamSelector, selector))
                    {
                        shallRecreatePlayer = true;
                        DisposeStreamHolder(oldStreamHolder);
                        streamHolders[contentType] = CreateStreamHolder(
                            streamGroup,
                            selector);
                        continue;
                    }
                }

                // Reuse previously selected stream and update IStreamSelector only
                _streamHolders.Remove(contentType);
                oldStreamHolder.StreamSelector = selector;
                streamHolders[contentType] = oldStreamHolder;
                var stream = oldStreamHolder.Stream;
                _streamProvider.UpdateStream(stream, selector);
            }

            // If we still have old streamHolders,
            // that means a client selected less streams than previously.
            // We have to dispose them and recreate the player.
            if (_streamHolders.Count > 0)
            {
                shallRecreatePlayer = true;
                foreach (var streamHolder in _streamHolders.Values)
                    DisposeStreamHolder(streamHolder);
            }

            _streamHolders = streamHolders;
            await PrepareStreams();

            UpdateSegment(position);

            LoadChunks();

            if (shallRecreatePlayer)
            {
                _platformPlayer.Dispose();
                _platformPlayer = _platformPlayerFactory.Invoke();
                var streamConfigs =
                    await Task.WhenAll(GetStreamConfigs());
                await PreparePlayer(streamConfigs);
            }
            else
            {
                await SeekPlayer(_segment.Start);
            }

            // Restore previous state
            if (previousState == PlayerState.Playing)
            {
                if (shallRecreatePlayer)
                    _platformPlayer.Start();
                else
                    _platformPlayer.Resume();
                _clock.Start();
            }
        }

        public void Play()
        {
            var state = _platformPlayer.GetState();
            switch (state)
            {
                case PlayerState.Ready:
                    _platformPlayer.Start();
                    break;
                case PlayerState.Paused:
                    _platformPlayer.Resume();
                    break;
            }

            _clock.Start();
            foreach (var streamHolder in _streamHolders.Values)
            {
                streamHolder.StartPushingPackets(
                    _segment,
                    _platformPlayer,
                    _cancellationTokenSource.Token);
            }
        }

        public async Task Pause()
        {
            _platformPlayer.Pause();
            _clock.Stop();
            foreach (var streamHolder in _streamHolders.Values)
                await streamHolder.StopPushingPackets();
        }

        public IObservable<IEvent> OnEvent()
        {
            return Observable.Empty<IEvent>();
        }

        public async Task DisposeAsync()
        {
            await StopStreaming();
            _cancellationTokenSource?.Dispose();
            _platformPlayer?.Dispose();
        }

        private Task PrepareStreams()
        {
            return Task.WhenAll(
                _streamHolders.Values.Select(holder =>
                    holder.Stream.Prepare()));
        }

        private void UpdateSegment(TimeSpan startTime)
        {
            if (_streamHolders.ContainsKey(ContentType.Video))
            {
                var videoStream = _streamHolders[ContentType.Video]
                    .Stream;
                startTime = videoStream?.GetAdjustedSeekPosition(startTime) ?? startTime;
            }

            _logger.Info($"{startTime}");

            _segment = new Segment {Base = _clock.Elapsed, Start = startTime, Stop = TimeSpan.MinValue};
        }

        private StreamGroup[] SelectDefaultStreamsGroups(Period period)
        {
            var allStreamGroups =
                _streamProvider.GetStreamGroups(period);
            var selectedStreamGroups = new List<StreamGroup>();
            var audioStreamGroup = SelectAudioStreamGroup(
                allStreamGroups);
            if (audioStreamGroup != null)
                selectedStreamGroups.Add(audioStreamGroup);
            var videoStreamGroup = SelectDefaultStreamGroup(
                allStreamGroups,
                ContentType.Video);
            if (videoStreamGroup != null)
                selectedStreamGroups.Add(videoStreamGroup);
            return selectedStreamGroups.ToArray();
        }

        private StreamGroup SelectDefaultStreamGroup(
            StreamGroup[] streamGroups,
            ContentType contentType)
        {
            var filteredStreamGroups = streamGroups
                .Where(streamGroup => streamGroup.ContentType == contentType)
                .ToArray();
            return filteredStreamGroups
                       .FirstOrDefault(streamGroup => streamGroup.Streams.Any(stream =>
                           stream.Format.SelectionFlags == SelectionFlags.Default ||
                           stream.Format.RoleFlags == RoleFlags.Main))
                   ?? filteredStreamGroups.FirstOrDefault();
        }

        private StreamGroup SelectAudioStreamGroup(StreamGroup[] streamGroups)
        {
            var filteredStreamGroups = streamGroups
                .Where(streamGroup => streamGroup.ContentType == ContentType.Audio);
            if (_configuration.PreferredAudioLanguage != null)
            {
                filteredStreamGroups = filteredStreamGroups.Where(streamGroup =>
                    streamGroup.Streams.Any(stream =>
                        stream.Format.Language == _configuration.PreferredAudioLanguage));
            }

            return filteredStreamGroups.FirstOrDefault()
                   ?? SelectDefaultStreamGroup(streamGroups, ContentType.Audio);
        }

        private async Task<Period> PrepareStreamProvider()
        {
            var timeline = await _streamProvider.Prepare();
            return timeline.Periods[0];
        }

        private async Task PreparePlayer(StreamConfig[] streamConfigs)
        {
            _logger.Info();
            _platformPlayer.Open(_window, streamConfigs);
            var synchronizationContext = SynchronizationContext.Current;
            var cancellationToken = _cancellationTokenSource.Token;
            await _platformPlayer.PrepareAsync(contentType =>
            {
                _logger.Info($"{contentType}");
                var holder = _streamHolders[contentType];
                synchronizationContext.Post(_ =>
                {
                    if (!cancellationToken.IsCancellationRequested)
                    {
                        holder.StartPushingPackets(
                            _segment,
                            _platformPlayer,
                            cancellationToken);
                    }
                }, cancellationToken);
            });
        }

        private void LoadChunks()
        {
            _logger.Info();
            foreach (var holder in _streamHolders.Values)
            {
                var streamListener = holder.StreamRenderer;
                holder.LoadChunks(_segment,
                    streamListener,
                    _cancellationTokenSource.Token);
            }
        }

        private IEnumerable<Task<StreamConfig>> GetStreamConfigs()
        {
            return _streamHolders.Values.Select(holder =>
            {
                var stream = holder.Stream;
                return stream.GetStreamConfig(_cancellationTokenSource.Token);
            });
        }

        private void CreateStreamHolders(
            IReadOnlyList<StreamGroup> streamGroups,
            IReadOnlyList<IStreamSelector> streamSelectors)
        {
            _streamHolders = new Dictionary<ContentType, StreamHolder>();
            for (var i = 0; i < streamGroups.Count; i++)
            {
                var streamGroup = streamGroups[i];
                var streamSelector = streamSelectors[i];
                var contentType = streamGroup.ContentType;
                _streamHolders[contentType] = CreateStreamHolder(
                    streamGroup,
                    streamSelector);
            }
        }

        private StreamHolder CreateStreamHolder(
            StreamGroup streamGroup,
            IStreamSelector streamSelector)
        {
            var stream = _streamProvider.CreateStream(
                _currentPeriod,
                streamGroup,
                streamSelector);
            var packetSynchronizer = new PacketSynchronizer {Clock = _clock, Offset = TimeSpan.FromSeconds(1)};
            var streamRenderer = new StreamRenderer(
                packetSynchronizer);
            return new StreamHolder(
                stream,
                streamRenderer,
                streamGroup,
                streamSelector);
        }

        private void DisposeStreamHolder(StreamHolder streamHolder)
        {
            var stream = streamHolder.Stream;
            _streamProvider.ReleaseStream(stream);
            stream.Dispose();
            var contentType = streamHolder
                .StreamGroup
                .ContentType;
            _streamHolders.Remove(contentType);
        }

        private async Task StopStreaming()
        {
            _clock.Stop();
            _cancellationTokenSource.Cancel();
            _cancellationTokenSource = new CancellationTokenSource();
            await Task.WhenAll(
                _streamHolders.Values.Select(
                    holder => holder.FinishLoadingChunks()));
            foreach (var streamHolder in _streamHolders.Values)
            {
                await streamHolder.StopPushingPackets();
                streamHolder.Flush();
            }
        }

        private async Task SeekPlayer(TimeSpan position)
        {
            var synchronizationContext = SynchronizationContext.Current;
            var cancellationToken = _cancellationTokenSource.Token;
            await _platformPlayer.SeekAsync(position, contentType =>
            {
                _logger.Info($"{contentType}");
                var holder = _streamHolders[contentType];
                synchronizationContext.Post(_ =>
                {
                    if (!cancellationToken.IsCancellationRequested)
                    {
                        holder.StartPushingPackets(
                            _segment,
                            _platformPlayer,
                            cancellationToken);
                    }
                }, cancellationToken);
            });
        }

        private static void VerifyStreamGroups(StreamGroup[] streamGroups, IStreamSelector[] streamSelectors)
        {
            if (streamGroups == null)
                throw new ArgumentNullException(nameof(streamGroups));
            if (streamGroups.Length == 0)
                throw new ArgumentException($"{nameof(streamGroups)} argument is empty");
            var audioStreamGroupsCount = 0;
            var videoStreamGroupsCount = 0;
            var capabilities = Platform.Current.Capabilities;
            var supportsSeamlessAudioChange =
                capabilities.SupportsSeamlessAudioChange;

            for (var index = 0; index < streamGroups.Length; index++)
            {
                var streamGroup = streamGroups[index];
                var selector = streamSelectors[index];
                var contentType = streamGroup.ContentType;

                if (contentType == ContentType.Audio)
                {
                    ++audioStreamGroupsCount;

                    if (supportsSeamlessAudioChange)
                        continue;
                    if (selector != null && selector.GetType() == typeof(ThroughputHistoryStreamSelector))
                    {
                        throw new ArgumentException(
                            "Cannot select ThroughputHistoryStreamSelector for audio StreamGroup. " +
                            "Platform doesn't support it");
                    }
                }
                else if (contentType == ContentType.Video)
                {
                    ++videoStreamGroupsCount;
                }
                else
                {
                    throw new ArgumentException($"{contentType} is not supported");
                }
            }

            if (audioStreamGroupsCount > 1)
            {
                throw new ArgumentException(
                    $"{nameof(streamGroups)} contains more than 1 audio stream group. Allowed 0 or 1");
            }

            if (videoStreamGroupsCount > 1)
            {
                throw new ArgumentException(
                    $"{nameof(streamGroups)} contains more than 1 video stream group. Allowed 0 or 1");
            }
        }

        internal void SetStreamProvider(IStreamProvider streamProvider)
        {
            _streamProvider = streamProvider;
        }

        private class StreamHolder
        {
            private readonly ILogger _logger = LoggerManager.GetInstance().GetLogger("JuvoPlayer");
            private Task _loadChunksTask;
            private Task _pushPacketsTask;

            public StreamHolder(
                IStream stream,
                StreamRenderer streamRenderer,
                StreamGroup streamGroup,
                IStreamSelector streamSelector)
            {
                Stream = stream;
                StreamRenderer = streamRenderer;
                StreamGroup = streamGroup;
                StreamSelector = streamSelector;
            }

            public IStream Stream { get; }
            public StreamRenderer StreamRenderer { get; }
            public StreamGroup StreamGroup { get; }
            public IStreamSelector StreamSelector { get; set; }

            public void StartPushingPackets(
                Segment segment,
                IPlatformPlayer platformPlayer,
                CancellationToken cancellationToken)
            {
                _logger.Info();
                if (!StreamRenderer.IsPushingPackets)
                {
                    _pushPacketsTask = StreamRenderer.StartPushingPackets(
                        segment,
                        platformPlayer,
                        cancellationToken);
                }
            }

            public async Task StopPushingPackets()
            {
                _logger.Info();
                StreamRenderer.StopPushingPackets();

                if (_pushPacketsTask == null)
                    return;

                try
                {
                    await _pushPacketsTask;
                }
                catch (Exception)
                {
                    // ignored
                }
            }

            public void Flush()
            {
                _logger.Info();
                StreamRenderer.Flush();
            }

            public void LoadChunks(
                Segment segment,
                IStreamRenderer streamRenderer,
                CancellationToken cancellationToken)
            {
                _loadChunksTask = Stream.LoadChunks(segment,
                    streamRenderer,
                    cancellationToken);
            }

            public async Task FinishLoadingChunks()
            {
                if (_loadChunksTask == null)
                    return;
                try
                {
                    await _loadChunksTask;
                }
                catch (Exception)
                {
                    // ignored
                }
            }
        }
    }
}