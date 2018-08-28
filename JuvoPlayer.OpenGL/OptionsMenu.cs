﻿using System.Collections.Generic;
using JuvoLogger;
using JuvoPlayer.Common;

namespace JuvoPlayer.OpenGL
{
    internal unsafe class OptionsMenu
    {
        public bool Visible { get; private set; } = false;
        public bool SubtitlesOn { get; private set; } = false;

        private int _activeOption = -1;
        private int _activeSuboption = -1;
        private int _selectedOption = -1;
        private int _selectedSuboption = -1;

        private class StreamDescriptionsList
        {
            public List<StreamDescription> Descriptions;
            public StreamType StreamType;
            public int Active;
            public int Id;

            public StreamDescriptionsList(StreamType streamType, int id)
            {
                Id = id;
                StreamType = streamType;
                Active = -1;
                Descriptions = new List<StreamDescription>();
                if (streamType == StreamType.Subtitle)
                {
                    Descriptions.Add(new StreamDescription()
                    {
                        Default = true,
                        Description = "off",
                        Id = 0,
                        StreamType = StreamType.Subtitle
                    });
                }
            }
        };

        private List<StreamDescriptionsList> _streams = new List<StreamDescriptionsList>();

        public ILogger Logger { private get; set; }

        public void LoadStreamLists(Player player)
        {
            ClearOptionsMenu();

            if (player == null)
            {
                Logger?.Info($"player null, cannot load stream lists");
                return;
            }
            Logger?.Info($"loading stream lists");

            SubtitlesOn = false;
            foreach (var streamType in new[] { StreamType.Video, StreamType.Audio, StreamType.Subtitle })
            {
                var streamDescriptionsList = new StreamDescriptionsList(streamType, _streams.Count);
                streamDescriptionsList.Descriptions.AddRange(player.GetStreamsDescription(streamType));
                AddSubmenu(streamDescriptionsList, streamType);
                _streams.Add(streamDescriptionsList);
            }

            SetDefaultState();
        }

        private void AddSubmenu(StreamDescriptionsList streamDescriptionsList, StreamType streamType)
        {
            fixed (byte* text = ResourceLoader.GetBytes(streamDescriptionsList.StreamType.ToString()))
                DllImports.AddOption(streamDescriptionsList.Id, text, streamDescriptionsList.StreamType.ToString().Length);
            for (int id = 0; id < streamDescriptionsList.Descriptions.Count; ++id)
            {
                var s = streamDescriptionsList.Descriptions[id];
                Logger?.Info($"stream.Description=\"{s.Description}\", stream.Id=\"{s.Id}\", stream.Type=\"{s.StreamType}\", stream.Default=\"{s.Default}\"");
                if (s.Default)
                {
                    streamDescriptionsList.Active = id;
                    if (streamType == StreamType.Subtitle)
                        SubtitlesOn = true;
                }
                fixed (byte* text = ResourceLoader.GetBytes(s.Description))
                    DllImports.AddSuboption(streamDescriptionsList.Id, id, text, s.Description.Length);
            }
    }

        private void UpdateOptionsSelection()
        {
            Logger?.Info($"activeOption={_activeOption}, activeSuboption={_activeSuboption}, selectedOption={_selectedOption}, selectedSuboption={_selectedSuboption}");
            if (_selectedOption >= 0 && _selectedOption < _streams.Count)
                _activeSuboption = _streams[_selectedOption].Active;
            DllImports.UpdateSelection(Visible ? 1 : 0, _activeOption, _activeSuboption, _selectedOption, _selectedSuboption);
        }

        private void SetDefaultState()
        {
            _activeOption = -1;
            _activeSuboption = -1;
            _selectedOption = 0;
            _selectedSuboption = -1;
            Visible = false;
            SubtitlesOn = false;
            UpdateOptionsSelection();
        }

        public void ClearOptionsMenu()
        {
            _activeOption = -1;
            _activeSuboption = -1;
            _selectedOption = -1;
            _selectedSuboption = -1;
            Visible = false;
            SubtitlesOn = false;
            _streams = new List<StreamDescriptionsList>();
            DllImports.ClearOptions();
        }

        public void ControlLeft()
        {
            if (_selectedSuboption == -1)
                Hide();
            else
            {
                _selectedSuboption = -1;
                UpdateOptionsSelection();
            }
        }

        public void ControlRight()
        {
            if (_selectedSuboption == -1 && _selectedOption >= 0 && _selectedOption < _streams.Count && _streams[_selectedOption].Descriptions.Count > 0)
                _selectedSuboption =
                    _streams[_selectedOption].Active >= 0 && _streams[_selectedOption].Active <
                    _streams[_selectedOption].Descriptions.Count
                        ? _streams[_selectedOption].Active
                        : _streams[_selectedOption].Descriptions.Count - 1;
            UpdateOptionsSelection();
        }

        public void ControlUp()
        {
            if (_selectedSuboption == -1)
            {
                if (_selectedOption > 0)
                    --_selectedOption;
            }
            else
            {
                if (_selectedSuboption > 0)
                    --_selectedSuboption;
            }

            UpdateOptionsSelection();
        }

        public void ControlDown()
        {
            if (_selectedSuboption == -1)
            {
                if (_selectedOption < _streams.Count - 1)
                    ++_selectedOption;
            }
            else
            {
                if (_selectedSuboption < _streams[_selectedOption].Descriptions.Count - 1)
                    ++_selectedSuboption;
            }

            UpdateOptionsSelection();
        }

        public bool ProperSelection()
        {
            int selectedStreamTypeIndex = _selectedOption;
            int selectedStreamIndex = _selectedSuboption;
            return selectedStreamIndex >= 0 && selectedStreamIndex < _streams[selectedStreamTypeIndex].Descriptions.Count;
        }

        public void ControlSelect(Player player)
        {
            if (player == null)
                return;

            int selectedStreamTypeIndex = _selectedOption;
            int selectedStreamIndex = _selectedSuboption;

            if (selectedStreamIndex >= 0 && selectedStreamIndex < _streams[selectedStreamTypeIndex].Descriptions.Count)
            {
                if (_streams[selectedStreamTypeIndex].Descriptions[selectedStreamIndex].StreamType == StreamType.Subtitle && selectedStreamIndex == 0) // special subtitles:off suboption
                {
                    SubtitlesOn = false;
                    player.DeactivateStream(StreamType.Subtitle);
                }
                else
                {
                    if (_streams[selectedStreamTypeIndex].Descriptions[selectedStreamIndex].StreamType == StreamType.Subtitle)
                        SubtitlesOn = true;
                    player.ChangeActiveStream(_streams[selectedStreamTypeIndex].Descriptions[selectedStreamIndex]);
                }

                _streams[selectedStreamTypeIndex].Active = selectedStreamIndex;
                _activeSuboption = selectedStreamIndex;
            }

            UpdateOptionsSelection();
        }

        public void Show()
        {
            _selectedOption = _streams.Count - 1;
            _selectedSuboption = -1;
            Visible = true;
            UpdateOptionsSelection();
        }

        public void Hide()
        {
            Visible = false;
            UpdateOptionsSelection();
        }
    }
};